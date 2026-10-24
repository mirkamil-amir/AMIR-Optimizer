using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization.Backtesting;

namespace PortfolioOptimizer.Application.Dtos;

public enum ComparisonUniverse
{
    /// <summary>Only stocks in both strategies' universes (default, like-for-like).</summary>
    Intersection,
    /// <summary>All stocks of either; every strategy may invest in the same, larger universe.</summary>
    Union,
}

/// <summary>
/// Compares two optimizations of one portfolio on shared rules: mathematical vs mathematical, ML vs ML or mathematical vs ML.
/// Both must belong to <see cref="PortfolioId"/> (one of the caller's portfolios).
/// </summary>
public sealed record CreateStrategyComparisonRequest : IValidatableObject
{
    [StringLength(100)] public string? Name { get; init; }
    [Required] public Guid PortfolioId { get; init; }
    [Required] public StrategyRef StrategyA { get; init; } = new();
    [Required] public StrategyRef StrategyB { get; init; } = new();

    /// <summary>First evaluation date; default = the latest start of an ML strategy's test period (out-of-sample), or three years ago.</summary>
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
        if (StrategyA.Kind == StrategyB.Kind && StrategyA.Id == StrategyB.Id)
            yield return new ValidationResult("Choose two different optimizations.", [nameof(StrategyB)]);
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
    string? Error,
    Guid? PortfolioId = null,
    IReadOnlyList<string>? StrategyLabels = null);

/// <summary>
/// Resolved, frozen inputs of a comparison (copied from the referenced optimizations at submission). Comparisons created
/// before 2026-10-24 stored one optimization run and one ML experiment in the Optimization*/Ml* fields; they are returned
/// with <see cref="Strategies"/> filled from those fields.
/// </summary>
public sealed record ComparisonConfigurationDto(
    CreateStrategyComparisonRequest Request,
    IReadOnlyList<StrategySnapshotDto>? Strategies = null,
    PortfolioSettingsDto? OptimizationSettings = null,
    string? OptimizationObjective = null,
    DateTime? OptimizationRunCreatedAtUtc = null,
    CreateMlExperimentRequest? MlConfiguration = null,
    IReadOnlyDictionary<string, double>? MlHyperparameters = null,
    IReadOnlyList<string>? MlFeatures = null,
    DateOnly? MlValidationEnd = null,
    DateOnly? MlTestStart = null,
    string? MlFeatureSetVersion = null)
{
    /// <summary>The compared strategies, also for comparisons stored in the earlier one-run-one-experiment format.</summary>
    public IReadOnlyList<StrategySnapshotDto> Resolve(Guid? legacyRunId, Guid? legacyExperimentId, string currency, string benchmark)
    {
        if (Strategies is { Count: > 0 }) return Strategies;
        var list = new List<StrategySnapshotDto>();
        if (OptimizationSettings is not null)
            list.Add(new StrategySnapshotDto(OptimizationKind.Mathematical, legacyRunId ?? Guid.Empty, $"{OptimizationObjective} · {OptimizationRunCreatedAtUtc:yyyy-MM-dd HH:mm} UTC",
                OptimizationRunCreatedAtUtc ?? default, currency, OptimizationSettings.Universe, benchmark, MathSettings: OptimizationSettings, MathObjective: OptimizationObjective));
        if (MlConfiguration is not null)
            list.Add(new StrategySnapshotDto(OptimizationKind.MachineLearning, legacyExperimentId ?? Guid.Empty, MlConfiguration.Name ?? MlConfiguration.ModelFamily.ToString(),
                default, currency, MlConfiguration.Universe, benchmark, MlConfiguration: MlConfiguration, MlHyperparameters: MlHyperparameters, MlFeatures: MlFeatures,
                MlValidationEnd: MlValidationEnd, MlTestStart: MlTestStart, MlFeatureSetVersion: MlFeatureSetVersion));
        return list;
    }
}

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
