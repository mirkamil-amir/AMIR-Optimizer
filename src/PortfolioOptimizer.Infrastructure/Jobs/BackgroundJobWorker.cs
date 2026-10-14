using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Backtesting;
using PortfolioOptimizer.Application.MachineLearning;
using PortfolioOptimizer.Application.Comparisons;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.Jobs;

/// <summary>
/// Processes queued market-data syncs, quote refreshes, backtests, ML experiments and comparisons one at a time (bounded CPU use, predictable ordering). On start-up, jobs left
/// "Running" by a previous process are marked failed and "Queued" jobs are re-enqueued, so no work is silently lost.
/// </summary>
public sealed class BackgroundJobWorker(
    IBackgroundJobQueue queue,
    IServiceScopeFactory scopes,
    ILogger<BackgroundJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (var job in queue.DequeueAllAsync(stoppingToken))
        {
            using var scope = scopes.CreateScope();
            try
            {
                switch (job.Kind)
                {
                    case BackgroundJobKind.MarketDataSync:
                        await scope.ServiceProvider.GetRequiredService<MarketDataService>().ExecuteSyncJobAsync(job.Id, stoppingToken);
                        break;
                    case BackgroundJobKind.Backtest:
                        await scope.ServiceProvider.GetRequiredService<BacktestService>().ExecuteAsync(job.Id, stoppingToken);
                        break;
                    case BackgroundJobKind.MlExperiment:
                        await scope.ServiceProvider.GetRequiredService<MlExperimentRunner>().ExecuteAsync(job.Id, stoppingToken);
                        break;
                    case BackgroundJobKind.StrategyComparison:
                        await scope.ServiceProvider.GetRequiredService<StrategyComparisonRunner>().ExecuteAsync(job.Id, stoppingToken);
                        break;
                    case BackgroundJobKind.QuoteRefresh:
                        await scope.ServiceProvider.GetRequiredService<QuoteRefreshService>().ExecuteCycleAsync(job.Id, stoppingToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background job {Kind} {JobId} failed", job.Kind, job.Id);
                await MarkFailedAsync(scope.ServiceProvider.GetRequiredService<IApplicationDbContext>(), job, ex.Message);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        try
        {
            var now = DateTime.UtcNow;
            await db.SyncJobs.Where(j => j.Status == JobStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed).SetProperty(j => j.Message, "Interrupted by application restart.").SetProperty(j => j.CompletedAtUtc, now), ct);
            await db.Backtests.Where(b => b.Status == JobStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, JobStatus.Failed).SetProperty(b => b.Error, "Interrupted by application restart.").SetProperty(b => b.CompletedAtUtc, now), ct);
            await db.MlExperiments.Where(e => e.Status == JobStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, JobStatus.Failed).SetProperty(e => e.Error, "Interrupted by application restart.").SetProperty(e => e.CompletedAtUtc, now), ct);
            await db.QuoteRefreshRuns.Where(r => r.Status == JobStatus.Running || r.Status == JobStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobStatus.Failed).SetProperty(r => r.Message, "Interrupted by application restart.").SetProperty(r => r.CompletedAtUtc, now), ct);
            await db.StrategyComparisons.Where(c => c.Status == JobStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, JobStatus.Failed).SetProperty(c => c.Error, "Interrupted by application restart.").SetProperty(c => c.CompletedAtUtc, now), ct);

            foreach (var id in await db.SyncJobs.Where(j => j.Status == JobStatus.Queued).OrderBy(j => j.RequestedAtUtc).Select(j => j.Id).ToListAsync(ct))
                await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.MarketDataSync, id), ct);
            foreach (var id in await db.Backtests.Where(b => b.Status == JobStatus.Queued).OrderBy(b => b.CreatedAtUtc).Select(b => b.Id).ToListAsync(ct))
                await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.Backtest, id), ct);
            foreach (var id in await db.MlExperiments.Where(e => e.Status == JobStatus.Queued).OrderBy(e => e.CreatedAtUtc).Select(e => e.Id).ToListAsync(ct))
                await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.MlExperiment, id), ct);
            foreach (var id in await db.StrategyComparisons.Where(c => c.Status == JobStatus.Queued).OrderBy(c => c.CreatedAtUtc).Select(c => c.Id).ToListAsync(ct))
                await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.StrategyComparison, id), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job recovery failed");
        }
    }

    private async Task MarkFailedAsync(IApplicationDbContext db, BackgroundJob job, string message)
    {
        try
        {
            var now = DateTime.UtcNow;
            switch (job.Kind)
            {
                case BackgroundJobKind.MarketDataSync:
                    await db.SyncJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed).SetProperty(j => j.Message, message).SetProperty(j => j.CompletedAtUtc, now));
                    break;
                case BackgroundJobKind.Backtest:
                    await db.Backtests.Where(b => b.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, JobStatus.Failed).SetProperty(b => b.Error, message).SetProperty(b => b.CompletedAtUtc, now));
                    break;
                case BackgroundJobKind.MlExperiment:
                    await db.MlExperiments.Where(e => e.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, JobStatus.Failed).SetProperty(e => e.Error, "Unexpected error; see server logs.").SetProperty(e => e.CompletedAtUtc, now));
                    break;
                case BackgroundJobKind.StrategyComparison:
                    await db.StrategyComparisons.Where(c => c.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, JobStatus.Failed).SetProperty(c => c.Error, "Unexpected error; see server logs.").SetProperty(c => c.CompletedAtUtc, now));
                    break;
                case BackgroundJobKind.QuoteRefresh:
                    await db.QuoteRefreshRuns.Where(r => r.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, JobStatus.Failed).SetProperty(r => r.Message, "Unexpected error; see server logs.").SetProperty(r => r.CompletedAtUtc, now));
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark job {JobId} as failed", job.Id);
        }
    }
}
