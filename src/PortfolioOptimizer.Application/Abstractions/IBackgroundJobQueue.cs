namespace PortfolioOptimizer.Application.Abstractions;

public enum BackgroundJobKind { MarketDataSync, Backtest }

public sealed record BackgroundJob(BackgroundJobKind Kind, Guid Id);

/// <summary>In-process queue for long-running work (data imports, backtests) so HTTP requests return immediately.</summary>
public interface IBackgroundJobQueue
{
    ValueTask EnqueueAsync(BackgroundJob job, CancellationToken cancellationToken = default);

    IAsyncEnumerable<BackgroundJob> DequeueAllAsync(CancellationToken cancellationToken);
}
