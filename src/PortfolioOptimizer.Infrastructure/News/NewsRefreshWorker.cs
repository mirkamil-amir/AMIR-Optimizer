using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.News;

namespace PortfolioOptimizer.Infrastructure.News;

/// <summary>
/// Background refresher for due news sources (see <see cref="INewsRefreshScheduler"/>). One refresh runs at a time; requests
/// arriving meanwhile are merged into the next run, so many concurrent viewers cause one provider fetch per source. It does not
/// use the shared job queue because that queue runs long backtests and ML experiments one after another, which would delay news.
/// </summary>
public sealed class NewsRefreshWorker(IServiceScopeFactory scopes, ILogger<NewsRefreshWorker> logger) : BackgroundService, INewsRefreshScheduler
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly ConcurrentDictionary<string, string?> _companies = new(StringComparer.OrdinalIgnoreCase);
    private int _feeds;
    private int _running;

    public bool IsBusy => Volatile.Read(ref _running) == 1 || Volatile.Read(ref _feeds) == 1 || !_companies.IsEmpty;

    public void Schedule(NewsRefreshRequest request)
    {
        if (request.Feeds) Interlocked.Exchange(ref _feeds, 1);
        foreach (var (symbol, name) in request.Companies) _companies.TryAdd(symbol, name);
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled by a concurrent request: the pending work is picked up by that run.
        }
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
            Interlocked.Exchange(ref _running, 1);
            try
            {
                var feeds = Interlocked.Exchange(ref _feeds, 0) == 1;
                var companies = new List<(string, string?)>();
                foreach (var symbol in _companies.Keys.ToList())
                    if (_companies.TryRemove(symbol, out var name)) companies.Add((symbol, name));
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<NewsService>().RefreshDueAsync(new NewsRefreshRequest(feeds, companies), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Each source records its own failure in NewsFetchStates; this only catches unexpected errors of the run itself.
                logger.LogWarning(ex, "Background news refresh failed");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }
}
