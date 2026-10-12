using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.Jobs;

/// <summary>Periodically enqueues an incremental sync of all enabled assets from the active provider.</summary>
public sealed class ScheduledMarketDataRefresh(
    IServiceScopeFactory scopes,
    IOptions<MarketDataOptions> options,
    TimeProvider clock,
    ILogger<ScheduledMarketDataRefresh> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.ScheduledRefreshEnabled || o.ScheduledRefreshIntervalHours <= 0)
        {
            logger.LogInformation("Scheduled market-data refresh is disabled.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(o.ScheduledRefreshInitialDelayMinutes), clock, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(o.ScheduledRefreshIntervalHours), clock);
            do
            {
                await EnqueueAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task EnqueueAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            if (await db.SyncJobs.AnyAsync(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running, ct))
            {
                logger.LogInformation("Skipping scheduled refresh: a sync job is already pending.");
                return;
            }
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var job = await scope.ServiceProvider.GetRequiredService<MarketDataService>()
                .EnqueueSyncAsync(new SyncRequest { StartDate = today.AddYears(-options.Value.DefaultHistoryYears) }, ct);
            logger.LogInformation("Scheduled market-data refresh enqueued: {JobId} ({Count} assets)", job.Id, job.AssetsTotal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scheduled market-data refresh could not be enqueued.");
        }
    }
}
