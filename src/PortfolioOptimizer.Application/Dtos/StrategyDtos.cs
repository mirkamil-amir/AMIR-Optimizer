using System.ComponentModel.DataAnnotations;

namespace PortfolioOptimizer.Application.Dtos;

/// <summary>The two kinds of optimization a portfolio can have: a mean-variance run or an ML experiment.</summary>
public enum OptimizationKind { Mathematical, MachineLearning }

/// <summary>A stored optimization of a portfolio: an optimization run (Mathematical) or an ML experiment (MachineLearning).</summary>
public sealed record StrategyRef
{
    public OptimizationKind Kind { get; init; }
    [Required] public Guid Id { get; init; }
}

/// <summary>One optimization of a portfolio that can be backtested or compared (completed successfully).</summary>
/// <param name="HistoryDaysBeforeStart">Calendar days of prices a simulation needs before its start (lookback or ML training history).</param>
public sealed record PortfolioStrategyDto(OptimizationKind Kind, Guid Id, string Label, string Detail, DateTime CreatedAtUtc, int Stocks,
    string Currency, bool UsesSimulatedData, IReadOnlyList<string> Universe, string BenchmarkSymbol, int HistoryDaysBeforeStart);

/// <summary>
/// Frozen copy of an optimization's strategy settings, taken when a backtest or comparison is submitted, so it can be
/// re-simulated later even if the run or experiment is deleted. Mathematical: the run's settings and objective, re-optimized
/// at every rebalance. MachineLearning: the experiment's configuration with the evaluation model's selected hyperparameters
/// and features, retrained walk-forward.
/// </summary>
public sealed record StrategySnapshotDto(
    OptimizationKind Kind,
    Guid SourceId,
    string Label,
    DateTime SourceCreatedAtUtc,
    string Currency,
    IReadOnlyList<string> Universe,
    string BenchmarkSymbol,
    PortfolioSettingsDto? MathSettings = null,
    string? MathObjective = null,
    CreateMlExperimentRequest? MlConfiguration = null,
    IReadOnlyDictionary<string, double>? MlHyperparameters = null,
    IReadOnlyList<string>? MlFeatures = null,
    DateOnly? MlValidationEnd = null,
    DateOnly? MlTestStart = null,
    string? MlFeatureSetVersion = null,
    Guid? MlModelVersionId = null);
