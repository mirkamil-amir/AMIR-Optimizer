using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.Infrastructure.MarketData;

/// <summary>
/// Fetches queued listings' reported figures one after another (<see cref="FundamentalsService.RefreshAsync"/>), outside the
/// user's request. Two in-memory queues: listings of a page someone is looking at always go first, the scheduled full
/// refresh (<see cref="ScheduledFundamentalsRefresh"/>) fills the rest. What is stored and when it was fetched stays in
/// <c>FundamentalsFetchStates</c>, so after a restart the next page view or scheduled run queues missing listings again.
/// It does not use the shared job queue, which runs long backtests and ML experiments one after another.
/// </summary>
public sealed class FundamentalsRefreshWorker(IServiceScopeFactory scopes, ILogger<FundamentalsRefreshWorker> logger)
    : BackgroundService, IFundamentalsRefreshScheduler
{
    /// <summary>At most this many listings wait at once (well above the catalogue's companies).</summary>
    public const int MaxQueued = 20_000;

    private readonly ConcurrentQueue<int> _page = new();
    private readonly ConcurrentQueue<int> _background = new();
    /// <summary>Queued or in-progress listings: 0 = page queue, 1 = background queue, 2 = being fetched.</summary>
    private readonly ConcurrentDictionary<int, byte> _pending = new();
    /// <summary>Listings already waiting in the background queue that a page view moved ahead (skipped when reached there).</summary>
    private readonly ConcurrentDictionary<int, byte> _promoted = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public bool IsQueued(int assetId) => _pending.ContainsKey(assetId);

    public void Schedule(IEnumerable<int> assetIds, bool background = false)
    {
        var added = 0;
        foreach (var id in assetIds)
        {
            if (_pending.TryAdd(id, background ? (byte)1 : (byte)0))
            {
                if (_pending.Count > MaxQueued)
                {
                    _pending.TryRemove(id, out _);
                    break;
                }
                (background ? _background : _page).Enqueue(id);
                added++;
            }
            else if (!background && _pending.TryGetValue(id, out var where) && where == 1 && _pending.TryUpdate(id, 0, 1))
            {
                // Waiting in the background queue: a page view needs it now.
                _promoted.TryAdd(id, 0);
                _page.Enqueue(id);
                added++;
            }
        }
        if (added > 0) _signal.Release(added);
    }

    /// <summary>Next listing to fetch: page requests first, then the full refresh; false when both are empty.</summary>
    public bool TryTakeNext(out int assetId)
    {
        // Marked "in progress" (2) so a page view does not move a listing that is already being fetched.
        if (_page.TryDequeue(out assetId))
        {
            _pending[assetId] = 2;
            return true;
        }
        while (_background.TryDequeue(out assetId))
        {
            if (_promoted.TryRemove(assetId, out _)) continue; // already fetched as a page request
            _pending[assetId] = 2;
            return true;
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (!TryTakeNext(out var assetId)) continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FundamentalsService>().RefreshAsync(assetId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                // Provider failures are recorded per listing in FundamentalsFetchStates; this only catches unexpected errors.
                logger.LogWarning(ex, "Fundamentals refresh of asset {AssetId} failed", assetId);
            }
            finally
            {
                _pending.TryRemove(assetId, out _);
            }
        }
    }
}

public sealed class ScheduledFundamentalsOptions
{
    public const string Section = "MarketData:Fundamentals:ScheduledRefresh";

    /// <summary>Fetch every company's figures in the background (off in tests; the e2e stack keeps it off too).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Wait after start before the first run, so start-up work (migrations, quote refresh) goes first.</summary>
    public int InitialDelaySeconds { get; set; } = 60;
    /// <summary>How often to look for missing or due figures; what is due follows the service's refresh ages (1 day).</summary>
    public int IntervalHours { get; set; } = 6;
}

/// <summary>
/// Queues every company whose figures are missing or due (<see cref="FundamentalsService.ScheduleDueAsync"/>) shortly after
/// start and then every few hours, so the stock list shows figures for every page without waiting. The first run fetches
/// the whole catalogue (about 2,200 companies at 4 per second ≈ 10 minutes); later runs only refresh figures older than a day.
/// </summary>
public sealed class ScheduledFundamentalsRefresh(IServiceScopeFactory scopes, IOptions<ScheduledFundamentalsOptions> options,
    TimeProvider clock, ILogger<ScheduledFundamentalsRefresh> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, o.InitialDelaySeconds)), clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var queued = await scope.ServiceProvider.GetRequiredService<FundamentalsService>().ScheduleDueAsync(stoppingToken);
                    if (queued > 0) logger.LogInformation("Scheduled fundamentals refresh queued {Count} companies", queued);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ExceptionFilters.IsRecoverable(ex))
                {
                    logger.LogWarning(ex, "Scheduled fundamentals refresh failed");
                }
                await Task.Delay(TimeSpan.FromHours(Math.Max(1, o.IntervalHours)), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
