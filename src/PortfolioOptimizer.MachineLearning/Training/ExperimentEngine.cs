using PortfolioOptimizer.MachineLearning.Backtesting;
using PortfolioOptimizer.MachineLearning.Data;
using PortfolioOptimizer.MachineLearning.Evaluation;
using PortfolioOptimizer.MachineLearning.Explainability;
using PortfolioOptimizer.MachineLearning.Features;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.MachineLearning.Strategy;
using PortfolioOptimizer.MachineLearning.Validation;
using PortfolioOptimizer.Optimization.Backtesting;

namespace PortfolioOptimizer.MachineLearning.Training;

/// <summary>
/// Runs one ML experiment end to end:
/// <list type="number">
/// <item>Features (≤ t) and labels (t+1 … t+h) on a regular decision grid.</item>
/// <item>Chronological train/validation/test split with purging (no random splits).</item>
/// <item>Hyperparameter search: models trained on the training period, compared on the validation period only.</item>
/// <item>Evaluation model: refit on train + validation (labels realized before the test period), evaluated once on the
/// untouched test period, with naive baselines.</item>
/// <item>Explainability: permutation importance on the test period, model-native coefficients/importances.</item>
/// <item>Deployment model: refit on all realized labels for current predictions (its own out-of-sample record is the
/// evaluation model's test result).</item>
/// <item>Walk-forward backtest over the test period through the shared portfolio simulator.</item>
/// </list>
/// </summary>
public sealed class ExperimentEngine
{
    public const string BacktestMethodology =
        "Walk-forward over the test period: at each rebalance close the strategy uses only data dated on or before that day; the model " +
        "is retrained on samples whose labels were already realized (label end ≤ decision date) with the hyperparameters selected on " +
        "the validation period; orders fill at the next trading day's close net of proportional costs; cash earns 0%; fractional units. " +
        "The universe is the current selection (survivorship bias). Hypothetical results, not a forecast.";

    public ExperimentResult Run(MarketPanel panel, ExperimentConfiguration config, IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
        var warnings = new List<string>(panel.Notes);
        var trainer = ModelRegistry.Get(config.Family);
        var task = TargetCatalog.TaskOf(config.Target);
        if (!trainer.Supports(task))
            throw new ArgumentException($"{config.Family} does not support the {config.Target} target ({task}).");
        var features = (config.Features.Count == 0 ? FeatureCatalog.All : config.Features.Select(FeatureCatalog.Get)).ToList();
        if (panel.Volumes.All(v => v.All(double.IsNaN)) && features.Any(f => f.RequiresVolume))
        {
            features = features.Where(f => !f.RequiresVolume).ToList();
            warnings.Add("No volume data is available from the data source; volume-based features were removed.");
        }

        progress?.Report((5, "Computing features and labels"));
        var engine = new FeatureEngine(panel, features);
        var dataset = Dataset.Build(panel, engine, config.Target, config.HorizonDays, config.SampleStepDays);
        ct.ThrowIfCancellationRequested();
        if (dataset.Summary.DroppedForMissingFeatures > 0)
            warnings.Add($"{dataset.Summary.DroppedForMissingFeatures} sample(s) dropped because more than {Dataset.MaxMissingFeatureFraction:P0} of their features were missing.");
        if (dataset.Summary.AssetsWithoutUsableHistory > 0)
            warnings.Add($"{dataset.Summary.AssetsWithoutUsableHistory} stock(s) never had enough history for features and were not used.");

        var split = ChronologicalSplitter.Split(dataset, config.Split);
        warnings.AddRange(split.Warnings);
        var topK = config.RankingTopK ?? config.Portfolio.TopK;
        var higherIsBetter = TargetCatalog.HigherIsBetter(config.Target);

        // ---- Hyperparameter selection on the validation period (the test period is not touched).
        progress?.Report((15, "Selecting hyperparameters on the validation period"));
        var grid = config.Hyperparameters is { Count: > 0 } fixedHp ? [new Hyperparameters(fixedHp)] : trainer.DefaultGrid(task);
        var trainData = ToData(split.Train, engine);
        var trials = new List<HyperparameterTrial>();
        Hyperparameters? best = null;
        var bestLoss = double.PositiveInfinity;
        for (var g = 0; g < grid.Count; g++)
        {
            ct.ThrowIfCancellationRequested();
            var model = trainer.Train(trainData, grid[g], task, config.Seed, ct);
            var scored = Score(model, split.Validation);
            var loss = Loss(task, scored);
            var ic = MetricCalculator.Spearman(scored.Select(s => s.Prediction).ToArray(), scored.Select(s => s.Label).ToArray());
            trials.Add(new HyperparameterTrial(grid[g].Values, loss, ic));
            if (loss < bestLoss) { bestLoss = loss; best = grid[g]; }
            progress?.Report((15 + 30 * (g + 1) / grid.Count, $"Validated {g + 1} of {grid.Count} hyperparameter sets"));
        }
        var selected = best ?? throw new InvalidOperationException("No hyperparameter set could be evaluated.");
        var trainMean = split.Train.Average(s => s.Label);

        // In-sample and validation metrics of the selected configuration (trained on the training period only).
        var selectedOnTrain = trainer.Train(trainData, selected, task, config.Seed, ct);
        var trainMetrics = Evaluate("Training (in-sample)", task, selectedOnTrain, split.Train, trainMean, topK, higherIsBetter, config);
        var validationMetrics = Evaluate("Validation (model selection)", task, selectedOnTrain, split.Validation, trainMean, topK, higherIsBetter, config);

        // ---- Evaluation model: train + validation, evaluated once on the test period.
        progress?.Report((50, "Fitting the evaluation model and scoring the test period"));
        var refitSamples = split.TrainAndValidation(config.Split.EmbargoDays);
        var evaluationModel = trainer.Train(ToData(refitSamples, engine), selected, task, config.Seed, ct);
        var refitMean = refitSamples.Average(s => s.Label);
        var testMetrics = Evaluate("Test (out-of-sample)", task, evaluationModel, split.Test, refitMean, topK, higherIsBetter, config);
        var baselines = Baselines(split, refitSamples, task, config, topK, engine);

        progress?.Report((60, "Computing permutation importance"));
        var importance = PermutationImportance.Compute(evaluationModel, split.Test.Select(s => s.Features).ToList(), split.Test.Select(s => s.Label).ToList(),
            repeats: 3, seed: config.Seed, ct);

        // ---- Deployment model on every realized label; current predictions with it.
        progress?.Report((65, "Fitting the deployment model and predicting"));
        var allLabeled = dataset.Samples.Where(s => s.IsLabeled).ToList();
        var deploymentModel = trainer.Train(ToData(allLabeled, engine), selected, task, config.Seed, ct);
        (double Low, double High)? residualQuantiles = task == TaskKind.Regression ? ResidualQuantiles(evaluationModel, split.Test) : null;
        var latest = PredictLatest(dataset, deploymentModel, config, residualQuantiles);

        // ---- Walk-forward backtest over the test period.
        progress?.Report((70, "Running the walk-forward backtest"));
        var backtest = RunBacktest(dataset, trainer, selected, config, panel.Dates[split.TestStartIndex], panel.Dates[^1],
            evaluationModel, panel.Dates[refitSamples.Max(s => s.LabelAvailableIndex)], refitSamples.Count, ct);
        warnings.AddRange(backtest.Warnings.Take(20));

        var exploratory = split.Warnings.Count > 0 || split.Test.Select(s => s.DecisionIndex).Distinct().Count() < 12;
        if (panel.Length < 3 * 252) warnings.Add("Less than three years of history: model estimates and out-of-sample metrics are unstable.");
        warnings.Add("The universe is today's selection of stocks, so delisted and replaced companies are missing (survivorship bias).");
        progress?.Report((95, "Finalizing"));

        return new ExperimentResult
        {
            Configuration = config,
            TargetDefinition = TargetCatalog.Describe(config.Target, config.HorizonDays, panel.BenchmarkSymbol),
            FeatureSetVersion = FeatureCatalog.Version,
            FeatureNames = engine.Names,
            DataSnapshotHash = panel.SnapshotHash,
            TrainPeriod = split.TrainPeriod,
            ValidationPeriod = split.ValidationPeriod,
            TestPeriod = split.TestPeriod,
            Trials = trials,
            SelectedHyperparameters = selected.Values,
            TrainMetrics = trainMetrics,
            ValidationMetrics = validationMetrics,
            TestMetrics = testMetrics,
            Baselines = baselines,
            PermutationImportance = importance,
            NativeExplanation = evaluationModel.GlobalExplanation(),
            ContributionMethod = deploymentModel.ContributionMethod,
            EvaluationModel = new TrainedModelInfo("Evaluation", evaluationModel, panel.Dates[refitSamples.Min(s => s.DecisionIndex)],
                panel.Dates[refitSamples.Max(s => s.LabelAvailableIndex)], refitSamples.Count),
            DeploymentModel = new TrainedModelInfo("Deployment", deploymentModel, panel.Dates[allLabeled.Min(s => s.DecisionIndex)],
                panel.Dates[allLabeled.Max(s => s.LabelAvailableIndex)], allLabeled.Count),
            LatestPredictions = latest,
            Backtest = backtest,
            Warnings = warnings.Distinct().ToList(),
            IsExploratory = exploratory,
            TotalSamples = dataset.Summary.Samples,
            DroppedSamples = dataset.Summary.DroppedForMissingFeatures,
        };
    }

    /// <summary>Scores and ranks the latest decision date with a stored model (used for "predict again" without retraining).</summary>
    public static PredictionSnapshot PredictLatest(Dataset dataset, ITrainedModel model, ExperimentConfiguration config, (double Low, double High)? residualQuantiles)
    {
        var panel = dataset.Panel;
        var t = panel.Length - 1;
        var rows = new List<(int Asset, double[] X)>();
        for (var a = 0; a < panel.AssetCount; a++)
        {
            var x = dataset.Features.Compute(a, t);
            if (x is not null && x.Count(double.IsNaN) <= Dataset.MaxMissingFeatureFraction * x.Length) rows.Add((a, x));
        }
        if (rows.Count == 0)
            return new PredictionSnapshot(panel.Dates[t], config.HorizonDays, [], 1, ["No stock has complete features on the latest date."], null);

        var predictions = model.Predict(rows.Select(r => r.X).ToList());
        var drivers = model.Explain(rows.Select(r => r.X).ToList(), top: 5);
        var higherIsBetter = TargetCatalog.HigherIsBetter(config.Target);
        var candidates = rows.Select((r, i) => new Candidate(panel.Symbols[r.Asset], panel.Sectors[r.Asset],
            higherIsBetter ? predictions[i] : -predictions[i], dataset.Features.TrailingVolatility(r.Asset, t))).ToList();
        var built = PortfolioConstructor.Build(candidates, config.Portfolio);
        var order = candidates.Select((c, i) => (c, i)).OrderByDescending(x => x.c.Score).ThenBy(x => x.c.Symbol, StringComparer.Ordinal)
            .Select((x, rank) => (x.i, rank: rank + 1)).ToDictionary(x => x.i, x => x.rank);

        var list = rows.Select((r, i) => new StockPrediction(
            panel.Symbols[r.Asset], panel.Sectors[r.Asset], predictions[i], candidates[i].Score, order[i],
            built.Selected.Contains(panel.Symbols[r.Asset]), built.Weights.GetValueOrDefault(panel.Symbols[r.Asset]),
            residualQuantiles is { } q ? predictions[i] + q.Low : null,
            residualQuantiles is { } q2 ? predictions[i] + q2.High : null,
            drivers[i])).OrderBy(p => p.Rank).ToList();
        return new PredictionSnapshot(panel.Dates[t], config.HorizonDays, list, built.CashWeight, built.Notes,
            residualQuantiles is null ? null
                : "Empirical 10th–90th percentile range of the evaluation model's out-of-sample errors (test period), added to the prediction. " +
                  "Not a statistical confidence interval of this specific forecast.");
    }

    private static MlBacktestResult RunBacktest(Dataset dataset, IModelTrainer trainer, Hyperparameters hp, ExperimentConfiguration config,
        DateOnly start, DateOnly end, ITrainedModel initialModel, DateOnly initialTrainedThrough, int initialSamples, CancellationToken ct)
    {
        var panel = dataset.Panel;
        var series = new Dictionary<string, IReadOnlyList<Optimization.Statistics.PricePoint>>();
        for (var a = 0; a < panel.AssetCount; a++)
            series[panel.Symbols[a]] = Enumerable.Range(0, panel.Length).Where(t => !double.IsNaN(panel.Prices[a][t]))
                .Select(t => new Optimization.Statistics.PricePoint(panel.Dates[t], panel.Prices[a][t])).ToList();
        var warnings = new List<string>();
        if (end <= start) throw new InvalidOperationException("The test period is too short for a backtest.");
        var grid = PriceGrid.Build(series, start, end);
        var signals = grid.SignalIndices(config.Rebalance);

        var strategy = new MlStrategy(dataset, trainer, hp, config.Portfolio, config.WalkForward, config.Seed, initialModel, initialTrainedThrough, initialSamples);
        var ml = PortfolioSimulator.Run(grid, signals, config.InitialCapital, config.TransactionCostBps, strategy.Decide, warnings, ct);
        var equal = PortfolioSimulator.Run(grid, signals, config.InitialCapital, config.TransactionCostBps,
            EqualWeightRule.Create(dataset, config.Portfolio.CashReserve), null, ct);

        // Benchmark buy-and-hold on the same calendar.
        var benchmark = new double[grid.Calendar.Length];
        var b0 = panel.IndexOnOrBefore(grid.Calendar[0]);
        for (var t = 0; t < grid.Calendar.Length; t++)
            benchmark[t] = config.InitialCapital * panel.Benchmark[panel.IndexOnOrBefore(grid.Calendar[t])] / panel.Benchmark[b0];

        var mlName = $"ML strategy ({config.Family})";
        var equalName = "Equal weight (same universe, same rebalancing)";
        var benchName = $"Benchmark ({panel.BenchmarkSymbol}, buy & hold)";
        var values = new Dictionary<string, IReadOnlyList<double>> { [mlName] = ml.Values, [equalName] = equal.Values, [benchName] = benchmark };
        var performance = new List<StrategyPerformance>
        {
            StrategyMetrics.Compute(mlName, ml.Values, ml.Events, config.RiskFreeRate, benchmark),
            StrategyMetrics.Compute(equalName, equal.Values, equal.Events, config.RiskFreeRate, benchmark),
            StrategyMetrics.Compute(benchName, benchmark, [], config.RiskFreeRate, benchmark),
        };
        return new MlBacktestResult(grid.Calendar, values,
            values.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)Optimization.Statistics.PerformanceMetrics.DrawdownSeries(kv.Value)),
            performance, strategy.Records, warnings.Distinct().ToList(), BacktestMethodology);
    }

    private static PeriodMetrics Evaluate(string period, TaskKind task, ITrainedModel model, IReadOnlyList<Sample> samples, double trainingMean, int topK,
        bool higherIsBetter, ExperimentConfiguration config)
    {
        var scored = Score(model, samples);
        return new PeriodMetrics(
            period,
            task == TaskKind.Regression ? MetricCalculator.Regression(scored, trainingMean, directional: config.Target != PredictionTarget.ForwardVolatility) : null,
            task == TaskKind.BinaryClassification ? MetricCalculator.Classification(scored, trainingMean) : null,
            MetricCalculator.Ranking(scored, topK, higherIsBetter, config.HorizonDays));
    }

    /// <summary>Naive references on the test period: constant training mean / base rate, and 12-1 momentum ranking.</summary>
    private static IReadOnlyList<BaselineMetrics> Baselines(ChronologicalSplit split, IReadOnlyList<Sample> refit, TaskKind task, ExperimentConfiguration config,
        int topK, FeatureEngine engine)
    {
        var mean = refit.Average(s => s.Label);
        var constant = split.Test.Select(s => new ScoredObservation(s.DecisionIndex, s.Asset, mean, s.Label, s.Outcome)).ToList();
        var list = new List<BaselineMetrics>
        {
            task == TaskKind.Regression
                ? new BaselineMetrics("Training mean", "Predicts the training-period average label for every stock (out-of-sample R² = 0 by construction relative to itself).",
                    MetricCalculator.Regression(constant, mean, directional: false), null, null)
                : new BaselineMetrics("Base rate", $"Predicts the training-period outperformance frequency ({mean:P1}) for every stock.",
                    null, MetricCalculator.Classification(constant, mean), null),
        };
        var momIndex = engine.Names.ToList().IndexOf("mom_12_1");
        if (momIndex >= 0)
        {
            var momentum = split.Test.Where(s => !double.IsNaN(s.Features[momIndex]))
                .Select(s => new ScoredObservation(s.DecisionIndex, s.Asset, s.Features[momIndex], s.Label, s.Outcome)).ToList();
            list.Add(new BaselineMetrics("12-1 momentum rank", "Ranks stocks by their 12-1 month momentum without any model (classic factor baseline).",
                null, null, MetricCalculator.Ranking(momentum, topK, higherIsBetter: config.Target != PredictionTarget.ForwardVolatility, config.HorizonDays)));
        }
        return list;
    }

    private static (double Low, double High) ResidualQuantiles(ITrainedModel model, IReadOnlyList<Sample> test)
    {
        var predictions = model.Predict(test.Select(s => s.Features).ToList());
        var residuals = test.Select((s, i) => s.Label - predictions[i]).OrderBy(r => r).ToArray();
        static double Q(double[] sorted, double p)
        {
            var h = (sorted.Length - 1) * p;
            var lo = (int)Math.Floor(h);
            var hi = (int)Math.Ceiling(h);
            return sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
        }
        return (Q(residuals, 0.1), Q(residuals, 0.9));
    }

    private static List<ScoredObservation> Score(ITrainedModel model, IReadOnlyList<Sample> samples)
    {
        var predictions = model.Predict(samples.Select(s => s.Features).ToList());
        return samples.Select((s, i) => new ScoredObservation(s.DecisionIndex, s.Asset, predictions[i], s.Label, s.Outcome)).ToList();
    }

    private static double Loss(TaskKind task, IReadOnlyList<ScoredObservation> scored) => task == TaskKind.Regression
        ? scored.Average(s => (s.Prediction - s.Label) * (s.Prediction - s.Label))
        : scored.Average(s =>
        {
            var p = Math.Clamp(s.Prediction, 1e-12, 1 - 1e-12);
            return -(s.Label * Math.Log(p) + (1 - s.Label) * Math.Log(1 - p));
        });

    private static TrainingData ToData(IReadOnlyList<Sample> samples, FeatureEngine engine) =>
        new(samples.Select(s => s.Features).ToList(), samples.Select(s => s.Label).ToList(), engine.Names);
}
