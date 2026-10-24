using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.MachineLearning.Backtesting;
using PortfolioOptimizer.MachineLearning.Explainability;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.MachineLearning.Strategy;
using PortfolioOptimizer.MachineLearning.Training;
using PortfolioOptimizer.MachineLearning.Validation;
using PortfolioOptimizer.Optimization.Backtesting;

namespace PortfolioOptimizer.Application.Dtos;

/// <summary>
/// Submits an ML experiment. Model parameters (family, target, horizon, features, validation) are separate from
/// portfolio-construction parameters (top-K, weighting, caps, turnover, costs). Fractions: 0.2 = 20%.
/// </summary>
public sealed record CreateMlExperimentRequest : IValidatableObject
{
    [StringLength(100)] public string? Name { get; init; }

    /// <summary>Stock universe (e.g. the current stock selection).</summary>
    /// <example>["AAPL","MSFT","NVDA","JPM","XOM","JNJ","PG","AMZN","GOOGL","META"]</example>
    [Required, MinLength(5), MaxLength(MaxUniverse)] public List<string> Universe { get; init; } = [];

    /// <example>SPY</example>
    [Required, StringLength(15)] public string BenchmarkSymbol { get; init; } = "SPY";
    /// <summary>Analysis currency (ISO 4217); stocks in other currencies are converted with dated FX rates. Default: the selection's own currency, or the application default when it mixes currencies.</summary>
    [RegularExpression("^[A-Z]{3}$")] public string? Currency { get; init; }

    // ---- model
    public ModelFamily ModelFamily { get; init; } = ModelFamily.RidgeRegression;
    public PredictionTarget Target { get; init; } = PredictionTarget.ForwardExcessReturn;
    /// <summary>Prediction horizon in trading days (21 ≈ 1 month).</summary>
    [Range(5, 126)] public int HorizonDays { get; init; } = 21;
    /// <summary>Years of history loaded for features, training, validation and testing.</summary>
    [Range(3, 20)] public int HistoryYears { get; init; } = 8;
    /// <summary>Feature names (empty = all features in the catalogue).</summary>
    public List<string> Features { get; init; } = [];
    [Range(1, 21)] public int SampleStepDays { get; init; } = 5;
    [Range(0.3, 0.8)] public double TrainFraction { get; init; } = 0.6;
    [Range(0.1, 0.4)] public double ValidationFraction { get; init; } = 0.2;
    [Range(0, 63)] public int EmbargoDays { get; init; }
    /// <summary>Advanced: fixed hyperparameters instead of the default search (see GET /api/v1/ml/models).</summary>
    public Dictionary<string, double>? Hyperparameters { get; init; }
    [Range(0, int.MaxValue)] public int Seed { get; init; } = 42;

    // ---- portfolio construction & backtest
    [Range(1, 100)] public int TopK { get; init; } = 10;
    public WeightingMethod Weighting { get; init; } = WeightingMethod.EqualWeight;
    [Range(0.01, 1)] public double MaxWeight { get; init; } = 0.2;
    [Range(0, 0.5)] public double CashReserve { get; init; }
    /// <summary>Minimum score to be selected (probability for the outperformance target; predicted value otherwise; −volatility for the volatility target).</summary>
    public double? MinimumScore { get; init; }
    [Range(0, 1)] public double? MaxTurnover { get; init; }
    public Dictionary<string, double> SectorLimits { get; init; } = [];
    public RebalanceKind Rebalance { get; init; } = RebalanceKind.Monthly;
    /// <summary>Retrain during the walk-forward backtest every N rebalances (0 = keep the evaluation model).</summary>
    [Range(0, 24)] public int RetrainEveryRebalances { get; init; } = 3;
    /// <summary>Rolling training window in trading days for walk-forward retraining (null = expanding).</summary>
    [Range(126, 5040)] public int? TrainingWindowDays { get; init; }
    [Range(0, 500)] public double TransactionCostBps { get; init; } = 10;
    [Range(1_000, 1_000_000_000)] public double InitialCapital { get; init; } = 100_000;
    [Range(-0.05, 0.25)] public double RiskFreeRate { get; init; } = 0.04;

    public const int MaxUniverse = 150;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TrainFraction + ValidationFraction > 0.9)
            yield return new ValidationResult("Train and validation fractions must leave at least 10% for the test period.", [nameof(TrainFraction), nameof(ValidationFraction)]);
        if (Rebalance == RebalanceKind.None)
            yield return new ValidationResult("The ML strategy needs a rebalancing frequency (Monthly or Quarterly).", [nameof(Rebalance)]);
        if (TopK * MaxWeight < 1 - CashReserve - 1e-9)
            yield return new ValidationResult($"With top {TopK} and a {MaxWeight:P0} cap at most {TopK * MaxWeight:P0} can be invested; the rest stays in cash.", [nameof(MaxWeight)]);
        if (Target == PredictionTarget.ForwardVolatility && HorizonDays < 5)
            yield return new ValidationResult("The volatility target needs a horizon of at least 5 days.", [nameof(HorizonDays)]);
        foreach (var (sector, limit) in SectorLimits)
            if (limit is < 0 or > 1) yield return new ValidationResult($"Sector limit for '{sector}' must be between 0 and 1.", [nameof(SectorLimits)]);
    }
}

public sealed record MlExperimentSummaryDto(
    Guid Id,
    string Name,
    JobStatus Status,
    int ProgressPercent,
    string? ProgressMessage,
    ModelFamily ModelFamily,
    PredictionTarget Target,
    int HorizonDays,
    int UniverseSize,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    bool UsesSimulatedData,
    bool IsExploratory,
    double? TestRankCorrelation,
    double? TestOutOfSampleR2,
    double? TestRocAuc,
    double? BacktestAnnualizedReturn,
    string? Error);

public sealed record MlModelVersionDto(
    Guid Id,
    int Version,
    string Role,
    string ModelFamily,
    DateOnly TrainedFrom,
    DateOnly TrainedThrough,
    int TrainingSamples,
    IReadOnlyDictionary<string, double> Hyperparameters,
    IReadOnlyList<string> FeatureNames,
    string FeatureSetVersion,
    string ArtifactSha256,
    long ArtifactSizeBytes,
    DateTime CreatedAtUtc);

/// <summary>Stored results of a completed experiment (everything except model parameters and the backtest series).</summary>
public sealed record MlResultDocument(
    string TargetDefinition,
    IReadOnlyList<string> FeatureNames,
    IReadOnlyList<HyperparameterTrial> Trials,
    IReadOnlyDictionary<string, double> SelectedHyperparameters,
    PeriodMetrics TrainMetrics,
    PeriodMetrics ValidationMetrics,
    PeriodMetrics TestMetrics,
    IReadOnlyList<BaselineMetrics> Baselines,
    IReadOnlyList<PermutationImportanceResult> PermutationImportance,
    ModelExplanation NativeExplanation,
    string ContributionMethod,
    PeriodInfo TrainPeriod,
    PeriodInfo ValidationPeriod,
    PeriodInfo TestPeriod,
    int TotalSamples,
    int DroppedSamples,
    string ValidationMethodology,
    IReadOnlyList<AssetEligibilityDto>? Eligibility = null);

/// <summary>
/// One stock's path through an experiment: stored prices → prices on the benchmark's span → without exchange rate → usable
/// prices → samples → latest prediction → selection and weight. <see cref="Stage"/> is the last step reached, <see cref="Reason"/>
/// why it stopped there. Null counts: the stock did not reach that step. (Absent in experiments run before 2026-10-23.)
/// </summary>
public sealed record AssetEligibilityDto(
    string Symbol,
    int StoredPrices,
    int PricesInBenchmarkSpan,
    int PricesWithoutFx,
    int UsablePrices,
    int? Samples,
    int? LabeledSamples,
    int? DroppedSamples,
    int? Rank,
    double TargetWeight,
    string Stage,
    string? Reason);

public sealed record MlBacktestDocument(
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Values,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Drawdowns,
    IReadOnlyList<StrategyPerformance> Performance,
    IReadOnlyList<MlRebalanceRecord> Rebalances,
    IReadOnlyList<MonthlyReturnPoint> MonthlyReturns,
    IReadOnlyList<string> Warnings,
    string Methodology);

public sealed record MonthlyReturnPoint(string Series, int Year, int Month, double Return);

public sealed record MlPredictionSetDto(
    Guid Id,
    Guid ModelVersionId,
    int ModelVersion,
    string ModelRole,
    DateOnly ModelTrainedThrough,
    DateOnly AsOfDate,
    DateTime CreatedAtUtc,
    int HorizonDays,
    double CashWeight,
    string? IntervalMethod,
    IReadOnlyList<string> Notes,
    bool UsesSimulatedData,
    IReadOnlyList<MlPredictionDto> Predictions);

public sealed record MlPredictionDto(
    string Symbol,
    string? Name,
    string? Sector,
    decimal? LatestPrice,
    DateOnly? LatestPriceDate,
    double PredictedValue,
    double Score,
    int Rank,
    bool Selected,
    double TargetWeight,
    double? IntervalLow,
    double? IntervalHigh,
    IReadOnlyList<FeatureContribution> Drivers);

public sealed record MlExperimentDto(
    MlExperimentSummaryDto Summary,
    CreateMlExperimentRequest Configuration,
    string FeatureSetVersion,
    string BenchmarkSymbol,
    IReadOnlyList<string> Symbols,
    string PriceSource,
    DateOnly HistoryStart,
    DateOnly HistoryEnd,
    string? DataSnapshotHash,
    DateTime? StartedAtUtc,
    double? DurationMs,
    IReadOnlyList<string> Warnings,
    MlResultDocument? Results,
    IReadOnlyList<MlModelVersionDto> ModelVersions,
    MlPredictionSetDto? LatestPredictions,
    string Disclaimer,
    /// <summary>Currency of all prices, features and results (stocks converted with dated FX rates).</summary>
    string Currency = "USD");

public sealed record NamedOption(string Value, string Label, string Description);

public sealed record MlFeatureInfo(string Name, string Group, string Definition, int WindowDays, bool RequiresVolume, bool RequiresBenchmark, bool RequiresSectorPeers);

public sealed record MlModelFamilyInfo(string Value, string Label, string Description, IReadOnlyList<string> SupportedTargets,
    IReadOnlyList<IReadOnlyDictionary<string, double>> DefaultSearchSpaceRegression, IReadOnlyList<IReadOnlyDictionary<string, double>> DefaultSearchSpaceClassification);

/// <summary>Everything the ML workspace needs to build its configuration form.</summary>
public sealed record MlCatalogDto(
    IReadOnlyList<MlModelFamilyInfo> ModelFamilies,
    IReadOnlyList<NamedOption> Targets,
    IReadOnlyList<MlFeatureInfo> Features,
    string FeatureSetVersion,
    string MissingValuePolicy,
    IReadOnlyList<NamedOption> WeightingMethods,
    string ValidationMethodology,
    string BacktestMethodology,
    int MaxUniverse);
