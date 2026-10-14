using PortfolioOptimizer.MachineLearning.Backtesting;
using PortfolioOptimizer.MachineLearning.Evaluation;
using PortfolioOptimizer.MachineLearning.Explainability;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.MachineLearning.Strategy;
using PortfolioOptimizer.MachineLearning.Validation;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Backtesting;

namespace PortfolioOptimizer.MachineLearning.Training;

/// <summary>Complete, reproducible definition of an ML experiment (stored with the experiment).</summary>
public sealed record ExperimentConfiguration
{
    public ModelFamily Family { get; init; } = ModelFamily.RidgeRegression;
    public PredictionTarget Target { get; init; } = PredictionTarget.ForwardExcessReturn;
    public int HorizonDays { get; init; } = 21;

    /// <summary>Feature names (empty = the full catalogue).</summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>Spacing of decision dates in trading days (overlapping horizons make samples dependent; larger steps reduce this).</summary>
    public int SampleStepDays { get; init; } = 5;

    public SplitSettings Split { get; init; } = new();
    public int Seed { get; init; } = 42;

    /// <summary>Optional fixed hyperparameters (advanced). When set, no search is performed.</summary>
    public IReadOnlyDictionary<string, double>? Hyperparameters { get; init; }

    public PortfolioConstructionSettings Portfolio { get; init; } = new();
    public RebalanceFrequency Rebalance { get; init; } = RebalanceFrequency.Monthly;
    public WalkForwardSettings WalkForward { get; init; } = new();
    public double TransactionCostBps { get; init; } = 10;
    public double InitialCapital { get; init; } = 100_000;
    public double RiskFreeRate { get; init; } = 0.04;

    /// <summary>Top-K used for ranking metrics (defaults to the portfolio's TopK).</summary>
    public int? RankingTopK { get; init; }
}

public sealed record HyperparameterTrial(IReadOnlyDictionary<string, double> Hyperparameters, double ValidationLoss, double? ValidationRankCorrelation);

/// <summary>Metrics for one evaluation period (regression or classification block populated according to the task).</summary>
public sealed record PeriodMetrics(
    string Period,
    RegressionMetrics? Regression,
    ClassificationMetrics? Classification,
    RankingMetrics Ranking);

/// <summary>Naive reference predictors evaluated on the test period.</summary>
public sealed record BaselineMetrics(string Name, string Description, RegressionMetrics? Regression, ClassificationMetrics? Classification, RankingMetrics? Ranking);

public sealed record StockPrediction(
    string Symbol,
    string? Sector,
    double PredictedValue,
    double Score,
    int Rank,
    bool Selected,
    double TargetWeight,
    double? IntervalLow,
    double? IntervalHigh,
    IReadOnlyList<FeatureContribution> Drivers);

public sealed record PredictionSnapshot(
    DateOnly AsOf,
    int HorizonDays,
    IReadOnlyList<StockPrediction> Predictions,
    double CashWeight,
    IReadOnlyList<string> ConstructionNotes,
    string? IntervalMethod);

public sealed record SeriesPoint(DateOnly Date, double Value);

public sealed record MlBacktestResult(
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Values,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Drawdowns,
    IReadOnlyList<StrategyPerformance> Performance,
    IReadOnlyList<MlRebalanceRecord> Rebalances,
    IReadOnlyList<string> Warnings,
    string Methodology);

public sealed record TrainedModelInfo(
    string Role,
    ITrainedModel Model,
    DateOnly TrainedFrom,
    DateOnly TrainedThrough,
    int TrainingSamples);

public sealed record ExperimentResult
{
    public required ExperimentConfiguration Configuration { get; init; }
    public required string TargetDefinition { get; init; }
    public required string FeatureSetVersion { get; init; }
    public required IReadOnlyList<string> FeatureNames { get; init; }
    public required string DataSnapshotHash { get; init; }
    public required PeriodInfo TrainPeriod { get; init; }
    public required PeriodInfo ValidationPeriod { get; init; }
    public required PeriodInfo TestPeriod { get; init; }
    public required IReadOnlyList<HyperparameterTrial> Trials { get; init; }
    public required IReadOnlyDictionary<string, double> SelectedHyperparameters { get; init; }
    public required PeriodMetrics TrainMetrics { get; init; }
    public required PeriodMetrics ValidationMetrics { get; init; }
    public required PeriodMetrics TestMetrics { get; init; }
    public required IReadOnlyList<BaselineMetrics> Baselines { get; init; }
    public required IReadOnlyList<PermutationImportanceResult> PermutationImportance { get; init; }
    public required ModelExplanation NativeExplanation { get; init; }
    public required string ContributionMethod { get; init; }
    public required TrainedModelInfo EvaluationModel { get; init; }
    public required TrainedModelInfo DeploymentModel { get; init; }
    public required PredictionSnapshot LatestPredictions { get; init; }
    public required MlBacktestResult Backtest { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required bool IsExploratory { get; init; }
    public required int TotalSamples { get; init; }
    public required int DroppedSamples { get; init; }
}
