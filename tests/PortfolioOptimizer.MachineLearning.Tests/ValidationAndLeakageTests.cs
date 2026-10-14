using PortfolioOptimizer.MachineLearning.Data;
using PortfolioOptimizer.MachineLearning.Features;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.MachineLearning.Strategy;
using PortfolioOptimizer.MachineLearning.Training;
using PortfolioOptimizer.MachineLearning.Validation;
using PortfolioOptimizer.Optimization.Backtesting;

namespace PortfolioOptimizer.MachineLearning.Tests;

public class ValidationAndLeakageTests
{
    private static Dataset Data(int stocks = 20, int days = 1300, double momentum = 0, int seed = 5, int horizon = 21) =>
        DatasetFor(SyntheticMarket.Panel(stocks, days, momentum, seed), horizon);

    private static Dataset DatasetFor(MarketPanel panel, int horizon = 21, PredictionTarget target = PredictionTarget.ForwardExcessReturn) =>
        Dataset.Build(panel, new FeatureEngine(panel, FeatureCatalog.All), target, horizon, 5);

    [Fact]
    public void Split_IsChronological_WithPurgedBoundaries()
    {
        var dataset = Data();
        var split = ChronologicalSplitter.Split(dataset, new SplitSettings { EmbargoDays = 3 });
        Assert.True(split.Train.Max(s => s.DecisionIndex) < split.Validation.Min(s => s.DecisionIndex));
        Assert.True(split.Validation.Max(s => s.DecisionIndex) < split.Test.Min(s => s.DecisionIndex));
        // Purging: a training label must be fully realized before the validation period starts (minus the embargo).
        Assert.All(split.Train, s => Assert.True(s.LabelAvailableIndex < split.ValidationStartIndex - 3));
        Assert.All(split.Validation, s => Assert.True(s.LabelAvailableIndex < split.TestStartIndex - 3));
        Assert.True(split.TrainPeriod.Purged > 0 && split.ValidationPeriod.Purged > 0);
        Assert.All(split.TrainAndValidation(3), s => Assert.True(s.LabelAvailableIndex < split.TestStartIndex - 3));
        Assert.All(split.Test, s => Assert.True(s.IsLabeled));
    }

    [Fact]
    public void Split_RefusesTooLittleData()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ChronologicalSplitter.Split(Data(stocks: 3, days: 400), new SplitSettings()));
        Assert.Contains("period", ex.Message);
    }

    [Fact]
    public void Preprocessing_IsFittedOnTrainingRowsOnly()
    {
        var dataset = Data();
        var split = ChronologicalSplitter.Split(dataset, new SplitSettings());
        var trainer = new RidgeRegressionTrainer();
        var data = new TrainingData(split.Train.Select(s => s.Features).ToList(), split.Train.Select(s => s.Label).ToList(), dataset.Features.Names);
        var model = (LinearModel)trainer.Train(data, Hyperparameters.Of(("lambda", 0.01)), TaskKind.Regression, 1, CancellationToken.None);
        var trainMeans = Enumerable.Range(0, data.FeatureNames.Count)
            .Select(j => split.Train.Select(s => double.IsNaN(s.Features[j]) ? model.State.Medians[j] : s.Features[j]).Average()).ToArray();
        for (var j = 0; j < trainMeans.Length; j++) Assert.Equal(trainMeans[j], model.State.Means[j], 10);
    }

    [Fact]
    public void ModelSelection_DoesNotDependOnTheTestPeriod()
    {
        var market = SyntheticMarket.Generate(20, 1300, 0.1, seed: 9);
        var panel = MarketPanel.Build(market.Assets, market.Benchmark);
        var config = new ExperimentConfiguration { Family = ModelFamily.RidgeRegression, Target = PredictionTarget.ForwardExcessReturn };
        var dataset = DatasetFor(panel);
        var split = ChronologicalSplitter.Split(dataset, config.Split);
        // Rewrite every price inside the test period: the hyperparameter search and validation metrics must not change.
        var perturbed = SyntheticMarket.PerturbAfter(market, panel.Dates[split.TestStartIndex - 1]);
        var a = new ExperimentEngine().Run(panel, config, null, CancellationToken.None);
        var b = new ExperimentEngine().Run(MarketPanel.Build(perturbed.Assets, perturbed.Benchmark), config, null, CancellationToken.None);

        Assert.Equal(a.SelectedHyperparameters, b.SelectedHyperparameters);
        Assert.Equal(a.Trials.Select(t => t.ValidationLoss), b.Trials.Select(t => t.ValidationLoss));
        Assert.Equal(a.ValidationMetrics.Regression!.Rmse, b.ValidationMetrics.Regression!.Rmse, 12);
        Assert.NotEqual(a.TestMetrics.Regression!.Rmse, b.TestMetrics.Regression!.Rmse); // the test period itself did change
    }

    [Fact]
    public void OnARandomWalk_NoModelShowsOutOfSampleSkill()
    {
        // Zero drift, no momentum: every stock has the same (zero) expected forward excess return, so nothing is
        // predictable from the past. A leaking pipeline (future data in features, test data in preprocessing or
        // selection) would show a clearly positive out-of-sample rank correlation here.
        foreach (var seed in new[] { 21, 22 })
        foreach (var family in new[] { ModelFamily.RidgeRegression, ModelFamily.RandomForest, ModelFamily.GradientBoosting })
        {
            var panel = SyntheticMarket.Panel(30, 1500, momentumStrength: 0, seed: seed, marketDrift: 0);
            var result = new ExperimentEngine().Run(panel, new ExperimentConfiguration { Family = family }, null, CancellationToken.None);
            var ic = result.TestMetrics.Ranking.MeanInformationCoefficient!.Value;
            // Sampling noise of the mean IC is ≈ 0.19/√(independent dates) ≈ 0.05 here; bound the IC loosely and its
            // t-statistic (computed on non-overlapping dates) tightly.
            Assert.InRange(ic, -0.12, 0.12);
            Assert.InRange(result.TestMetrics.Ranking.InformationCoefficientTStat!.Value, -3, 3);
            Assert.True(result.TestMetrics.Regression!.OutOfSampleR2 < 0.02, $"{family}: out-of-sample R² {result.TestMetrics.Regression.OutOfSampleR2} on pure noise");
        }
    }

    [Fact]
    public void WithAPlantedSignal_ModelsFindItOutOfSample()
    {
        var panel = SyntheticMarket.Panel(30, 1500, momentumStrength: 0.25, seed: 21);
        var ridge = new ExperimentEngine().Run(panel, new ExperimentConfiguration { Family = ModelFamily.RidgeRegression }, null, CancellationToken.None);
        Assert.True(ridge.TestMetrics.Ranking.MeanInformationCoefficient > 0.05, $"IC {ridge.TestMetrics.Ranking.MeanInformationCoefficient}");
        Assert.True(ridge.TestMetrics.Ranking.InformationCoefficientTStat > 2, $"t {ridge.TestMetrics.Ranking.InformationCoefficientTStat}");
        Assert.True(ridge.TestMetrics.Ranking.TopKExcessOverUniverse > 0);
        // The planted effect is momentum: a momentum feature must matter most to the linear model.
        var top = ridge.PermutationImportance.Take(3).Select(p => p.Feature).ToList();
        Assert.Contains(top, f => f.StartsWith("mom") || f.Contains("ma_gap") || f.Contains("excess_mom") || f.Contains("sector_rel"));
    }

    [Fact]
    public void WalkForwardBacktest_DecisionsUseOnlyPastData()
    {
        var market = SyntheticMarket.Generate(15, 1300, 0.1, seed: 4);
        var panel = MarketPanel.Build(market.Assets, market.Benchmark);
        var config = new ExperimentConfiguration
        {
            Family = ModelFamily.RidgeRegression,
            Portfolio = new PortfolioConstructionSettings { TopK = 5, MaxWeight = 0.3 },
            WalkForward = new Backtesting.WalkForwardSettings { RetrainEveryRebalances = 2 },
        };
        var baseline = new ExperimentEngine().Run(panel, config, null, CancellationToken.None);
        var cutoff = baseline.Backtest.Rebalances[6].SignalDate;
        var perturbed = SyntheticMarket.PerturbAfter(market, cutoff);
        var other = new ExperimentEngine().Run(MarketPanel.Build(perturbed.Assets, perturbed.Benchmark), config, null, CancellationToken.None);

        // The model selected before the test period is identical; every decision up to the cutoff is identical.
        for (var k = 0; k <= 6; k++)
        {
            var x = baseline.Backtest.Rebalances[k];
            var y = other.Backtest.Rebalances[k];
            Assert.Equal(x.ModelTrainedThrough, y.ModelTrainedThrough);
            Assert.True(x.ModelTrainedThrough <= x.SignalDate);
            Assert.Equal(x.TargetWeights.OrderBy(w => w.Key), y.TargetWeights.OrderBy(w => w.Key));
        }
        // Retraining happens during the test period, always with labels realized before the signal.
        Assert.Contains(baseline.Backtest.Rebalances, r => r.Retrained);
        Assert.All(baseline.Backtest.Rebalances, r => Assert.True(r.ModelTrainedThrough <= r.SignalDate));
    }

    [Fact]
    public void Experiment_IsDeterministic_ForAFixedSeed()
    {
        var panel = SyntheticMarket.Panel(15, 1100, 0.1, seed: 8);
        var config = new ExperimentConfiguration { Family = ModelFamily.RandomForest };
        var a = new ExperimentEngine().Run(panel, config, null, CancellationToken.None);
        var b = new ExperimentEngine().Run(panel, config, null, CancellationToken.None);
        Assert.Equal(a.LatestPredictions.Predictions.Select(p => p.PredictedValue), b.LatestPredictions.Predictions.Select(p => p.PredictedValue));
        Assert.Equal(a.TestMetrics.Regression!.Rmse, b.TestMetrics.Regression!.Rmse);
        Assert.Equal(a.Backtest.Values.Values.First()[^1], b.Backtest.Values.Values.First()[^1]);
    }

    [Fact]
    public void Experiment_TransactionCostsAreAppliedToEveryStrategy()
    {
        var panel = SyntheticMarket.Panel(12, 1100, 0.1, seed: 13);
        var cheap = new ExperimentEngine().Run(panel, new ExperimentConfiguration { TransactionCostBps = 0 }, null, CancellationToken.None);
        var costly = new ExperimentEngine().Run(panel, new ExperimentConfiguration { TransactionCostBps = 50 }, null, CancellationToken.None);
        foreach (var (a, b) in cheap.Backtest.Performance.Zip(costly.Backtest.Performance).Where(x => !x.First.Name.StartsWith("Benchmark")))
        {
            Assert.Equal(0, a.TotalTransactionCosts, 9);
            Assert.True(b.TotalTransactionCosts > 0, b.Name);
        }
    }

    [Fact]
    public void Experiment_RejectsUnsupportedFamilyTargetCombinations()
    {
        var panel = SyntheticMarket.Panel(10, 900, 0, seed: 1);
        Assert.Throws<ArgumentException>(() => new ExperimentEngine().Run(panel,
            new ExperimentConfiguration { Family = ModelFamily.RidgeRegression, Target = PredictionTarget.OutperformBenchmark }, null, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => new ExperimentEngine().Run(panel,
            new ExperimentConfiguration { Family = ModelFamily.LogisticRegression, Target = PredictionTarget.ForwardReturn }, null, CancellationToken.None));
    }

    [Fact]
    public void Classification_ProducesCalibratedProbabilitiesAndBaseline()
    {
        var panel = SyntheticMarket.Panel(30, 1500, 0.25, seed: 33);
        var result = new ExperimentEngine().Run(panel,
            new ExperimentConfiguration { Family = ModelFamily.LogisticRegression, Target = PredictionTarget.OutperformBenchmark }, null, CancellationToken.None);
        var test = result.TestMetrics.Classification!;
        Assert.InRange(test.MeanPredicted, 0.2, 0.8);
        Assert.NotEmpty(test.Calibration);
        Assert.All(result.LatestPredictions.Predictions, p => Assert.InRange(p.PredictedValue, 0, 1));
        Assert.Contains(result.Baselines, b => b.Name == "Base rate");
        Assert.True(test.RocAuc > 0.5, $"AUC {test.RocAuc}");
    }
}
