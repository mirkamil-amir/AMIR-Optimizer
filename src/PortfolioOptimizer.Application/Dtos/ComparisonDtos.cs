using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization.Backtesting;

namespace PortfolioOptimizer.Application.Dtos;

public enum ComparisonUniverse
{
    /// <summary>Only stocks in both the optimization run and the ML experiment (default, like-for-like).</summary>
    Intersection,
    /// <summary>All stocks of either; every strategy may invest in the same, larger universe.</summary>
    Union,
}

/// <summary>Compares a completed optimization run's strategy with a completed ML experiment's strategy on shared rules.</summary>
public sealed record CreateStrategyComparisonRequest : IValidatableObject
{
    [StringLength(100)] public string? Name { get; init; }
    [Required] public Guid OptimizationRunId { get; init; }
    [Required] public Guid MlExperimentId { get; init; }

    /// <summary>First evaluation date; default = start of the ML experiment's test period (out-of-sample for both).</summary>
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    [Range(1_000, 1_000_000_000)] public double InitialCapital { get; init; } = 100_000;
    [Required, StringLength(15)] public string BenchmarkSymbol { get; init; } = "SPY";
    [Range(0, 500)] public double TransactionCostBps { get; init; } = 10;
    public RebalanceKind Rebalance { get; init; } = RebalanceKind.Monthly;
    public ComparisonUniverse Universe { get; init; } = ComparisonUniverse.Intersection;
    /// <summary>Also evaluate a clearly labelled hybrid: ML top-K screen, then mean-variance weights.</summary>
    public bool IncludeHybrid { get; init; }
    [Range(-0.05, 0.25)] public double? RiskFreeRate { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate is { } s && EndDate is { } e && e <= s.AddMonths(3))
            yield return new ValidationResult("The evaluation period must be longer than three months.", [nameof(EndDate)]);
        if (Rebalance == RebalanceKind.None)
            yield return new ValidationResult("Choose Monthly or Quarterly rebalancing.", [nameof(Rebalance)]);
    }
}

public enum StrategyKind { Mathematical, MachineLearning, Hybrid, EqualWeight, Benchmark }

public sealed record ComparisonRebalanceDto(DateOnly SignalDate, DateOnly ExecutionDate, double Turnover, double TransactionCosts, int Holdings, bool Succeeded, string Message);

public sealed record ComparisonStrategyDto(
    string Key,
    string Name,
    StrategyKind Kind,
    string Description,
    IReadOnlyList<double> Values,
    IReadOnlyList<double> Drawdowns,
    IReadOnlyList<double?> RollingSharpe,
    StrategyPerformance Performance,
    IReadOnlyList<MonthlyReturnPoint> MonthlyReturns,
    IReadOnlyDictionary<string, double> FinalWeights,
    IReadOnlyDictionary<string, double> AverageWeights,
    IReadOnlyDictionary<string, double> AverageSectorExposure,
    IReadOnlyList<ComparisonRebalanceDto> Rebalances);

public sealed record HoldingsOverlapDto(
    string StrategyA,
    string StrategyB,
    IReadOnlyList<string> CommonHoldings,
    IReadOnlyList<string> OnlyA,
    IReadOnlyList<string> OnlyB,
    double JaccardSimilarity,
    double WeightOverlap,
    double AverageWeightOverlap);

public sealed record FairnessAssessment(bool IsFair, IReadOnlyList<string> SharedConventions, IReadOnlyList<string> Issues);

public sealed record StrategyComparisonResultDto(
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<ComparisonStrategyDto> Strategies,
    IReadOnlyList<HoldingsOverlapDto> Overlaps,
    IReadOnlyList<string> Universe,
    IReadOnlyList<string> UniverseNotes,
    FairnessAssessment Fairness,
    IReadOnlyList<string>? RankingBySharpe,
    int RollingSharpeWindow,
    string Methodology,
    IReadOnlyList<string> Warnings);

public sealed record StrategyComparisonSummaryDto(
    Guid Id,
    string Name,
    JobStatus Status,
    int ProgressPercent,
    string? ProgressMessage,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    DateOnly EvaluationStart,
    DateOnly EvaluationEnd,
    Guid? OptimizationRunId,
    Guid? MlExperimentId,
    bool UsesSimulatedData,
    bool? IsFair,
    string? Error);

/// <summary>Resolved, frozen inputs of a comparison (copied from the referenced run and experiment at submission).</summary>
public sealed record ComparisonConfigurationDto(
    CreateStrategyComparisonRequest Request,
    PortfolioSettingsDto OptimizationSettings,
    string OptimizationObjective,
    DateTime OptimizationRunCreatedAtUtc,
    CreateMlExperimentRequest MlConfiguration,
    IReadOnlyDictionary<string, double> MlHyperparameters,
    IReadOnlyList<string> MlFeatures,
    DateOnly? MlValidationEnd,
    DateOnly? MlTestStart,
    string MlFeatureSetVersion);

public sealed record StrategyComparisonDto(
    StrategyComparisonSummaryDto Summary,
    ComparisonConfigurationDto Configuration,
    string BenchmarkSymbol,
    string PriceSource,
    string? DataSnapshotHash,
    IReadOnlyList<string> Warnings,
    StrategyComparisonResultDto? Result,
    string Disclaimer,
    /// <summary>Currency of both strategies, the benchmark and all results.</summary>
    string Currency = "USD");
