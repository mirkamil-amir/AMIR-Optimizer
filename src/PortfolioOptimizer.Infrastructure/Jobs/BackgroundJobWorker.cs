using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Backtesting;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.Jobs;

/// <summary>
/// Processes queued market-data syncs and backtests one at a time (single SQLite writer). On start-up, jobs left
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

            foreach (var id in await db.SyncJobs.Where(j => j.Status == JobStatus.Queued).OrderBy(j => j.RequestedAtUtc).Select(j => j.Id).ToListAsync(ct))
                await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.MarketDataSync, id), ct);
            foreach (var id in await db.Backtests.Where(b => b.Status == JobStatus.Queued).OrderBy(b => b.CreatedAtUtc).Select(b => b.Id).ToListAsync(ct))
                await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.Backtest, id), ct);
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
            if (job.Kind == BackgroundJobKind.MarketDataSync)
                await db.SyncJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed).SetProperty(j => j.Message, message).SetProperty(j => j.CompletedAtUtc, now));
            else
                await db.Backtests.Where(b => b.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, JobStatus.Failed).SetProperty(b => b.Error, message).SetProperty(b => b.CompletedAtUtc, now));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark job {JobId} as failed", job.Id);
        }
    }
}
