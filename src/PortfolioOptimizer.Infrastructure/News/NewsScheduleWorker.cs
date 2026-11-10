using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.News;

namespace PortfolioOptimizer.Infrastructure.News;

/// <summary>
/// Fetches due news sources on a timer (<c>News:Schedule</c>), so every Wire feed has its own supply before anyone opens
/// it: all feeds (central banks, statistics offices, market instruments) and the scheduled company set. Each run fetches
/// only what is due; due-ness lives in <c>NewsFetchStates</c>, so a restart loses nothing. Separate from the job queue,
/// which runs long backtests and ML experiments.
/// </summary>
public sealed class NewsScheduleWorker(IServiceScopeFactory scopes, IOptions<NewsOptions> options, TimeProvider clock, ILogger<NewsScheduleWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled || !o.Schedule.Enabled || o.Schedule.TickMinutes <= 0)
        {
            logger.LogInformation("Scheduled news fetching is disabled.");
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, o.Schedule.InitialDelayMinutes)), clock, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(o.Schedule.TickMinutes), clock);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<NewsService>().RefreshScheduledAsync(ct);
            logger.LogInformation("Scheduled news run: {Due} of {Companies} companies due, {Reclassified} articles reclassified, {Linked} linked to entities.",
                run.Due, run.Companies, run.Reclassified, run.Linked);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Each source records its own failure in NewsFetchStates; this only catches unexpected errors of the run itself.
            logger.LogWarning(ex, "Scheduled news run failed");
        }
    }
}
