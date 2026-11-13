namespace PortfolioOptimizer.Application.Abstractions;

public enum BackgroundJobKind { MarketDataSync, Backtest, MlExperiment, StrategyComparison, QuoteRefresh }

public sealed record BackgroundJob(BackgroundJobKind Kind, Guid Id);

/// <summary>In-process queue for long-running work (data imports, backtests) so HTTP requests return immediately.</summary>
public interface IBackgroundJobQueue
{
    ValueTask EnqueueAsync(BackgroundJob job, CancellationToken cancellationToken = default);

    IAsyncEnumerable<BackgroundJob> DequeueAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Queue of Matrix Pilot decision cycles. Separate from <see cref="IBackgroundJobQueue"/> so that a cycle waiting minutes for
/// model answers never holds up imports, backtests or ML runs (and vice versa).
/// </summary>
public interface IPilotCycleQueue
{
    ValueTask EnqueueAsync(Guid cycleId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}
