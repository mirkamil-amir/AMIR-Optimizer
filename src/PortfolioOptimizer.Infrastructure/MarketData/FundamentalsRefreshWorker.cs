using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.Infrastructure.MarketData;

/// <summary>
/// Fetches queued listings' reported figures one after another (<see cref="FundamentalsService.RefreshAsync"/>), outside the
/// user's request. The queue is in memory and bounded; what is stored and when it was fetched stays in
/// <c>FundamentalsFetchStates</c>, so after a restart the next page view queues missing listings again. It does not use
/// the shared job queue, which runs long backtests and ML experiments one after another.
/// </summary>
public sealed class FundamentalsRefreshWorker(IServiceScopeFactory scopes, ILogger<FundamentalsRefreshWorker> logger)
    : BackgroundService, IFundamentalsRefreshScheduler
{
    /// <summary>At most this many listings wait at once (several pages of 200); more are queued by a later page view.</summary>
    public const int MaxQueued = 2000;

    private readonly ConcurrentQueue<int> _queue = new();
    private readonly ConcurrentDictionary<int, byte> _pending = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public bool IsQueued(int assetId) => _pending.ContainsKey(assetId);

    public void Schedule(IEnumerable<int> assetIds)
    {
        var added = 0;
        foreach (var id in assetIds)
        {
            if (_pending.Count >= MaxQueued) break;
            if (!_pending.TryAdd(id, 0)) continue;
            _queue.Enqueue(id);
            added++;
        }
        if (added > 0) _signal.Release(added);
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
            if (!_queue.TryDequeue(out var assetId)) continue;
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
