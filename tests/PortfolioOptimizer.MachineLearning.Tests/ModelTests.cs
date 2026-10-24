using System.Reflection;
using PortfolioOptimizer.MachineLearning.Evaluation;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.MachineLearning.Strategy;
using PortfolioOptimizer.MachineLearning.Training;

namespace PortfolioOptimizer.MachineLearning.Tests;

public class ModelTests
{
    private static readonly string[] Names = ["x1", "x2", "noise"];

    private static TrainingData Linear(int n, int seed, Func<double, double, double> f)
    {
        var random = new Random(seed);
        var x = new List<double[]>();
        var y = new List<double>();
        for (var i = 0; i < n; i++)
        {
            var row = new[] { random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() };
            x.Add(row);
            y.Add(f(row[0], row[1]));
        }
        return new TrainingData(x, y, Names);
    }

    [Fact]
    public void Ridge_WithTinyPenalty_RecoversAnExactLinearRelationship()
    {
        var data = Linear(500, 1, (a, b) => 0.5 + 2 * a - 3 * b);
        var model = new RidgeRegressionTrainer().Train(data, Hyperparameters.Of(("lambda", 0)), TaskKind.Regression, 1, CancellationToken.None);
        var predictions = model.Predict([[0.2, -0.4, 0.5], [-1, 1, 0]]);
        Assert.Equal(0.5 + 0.4 + 1.2, predictions[0], 6);
        Assert.Equal(0.5 - 2 - 3, predictions[1], 6);
        var coefficients = model.GlobalExplanation().Values.ToDictionary(v => v.Name, v => v.Value);
        Assert.True(coefficients["x1"] > 0 && coefficients["x2"] < 0);
        Assert.Equal(0, coefficients["noise"], 6);
    }

    [Fact]
    public void Ridge_ShrinksCoefficientsAsPenaltyGrows()
    {
        var data = Linear(300, 2, (a, b) => a + b);
        double Norm(double lambda) => new RidgeRegressionTrainer().Train(data, Hyperparameters.Of(("lambda", lambda)), TaskKind.Regression, 1, CancellationToken.None)
            .GlobalExplanation().Values.Sum(v => Math.Abs(v.Value));
        Assert.True(Norm(0.001) > Norm(1) && Norm(1) > Norm(100));
    }

    [Fact]
    public void LinearContributions_AreExactAndAdditive()
    {
        var data = Linear(300, 3, (a, b) => 1 + a - b);
        var model = new RidgeRegressionTrainer().Train(data, Hyperparameters.Of(("lambda", 0.01)), TaskKind.Regression, 1, CancellationToken.None);
        var row = new[] { 0.3, 0.1, 0.4 };
        var state = ((LinearModel)model).State;
        var sum = model.Explain([row], top: 3)[0].Sum(c => c.Contribution);
        Assert.Equal(model.Predict([row])[0], state.Intercept + sum, 10);
    }

    [Fact]
    public void Logistic_RecoversDirectionAndProducesProbabilities()
    {
        var random = new Random(4);
        var x = new List<double[]>();
        var y = new List<double>();
        for (var i = 0; i < 3000; i++)
        {
            var row = new[] { random.NextDouble() * 4 - 2, random.NextDouble() * 4 - 2, random.NextDouble() };
            var p = 1 / (1 + Math.Exp(-(0.3 + 1.5 * row[0] - 1.0 * row[1])));
            x.Add(row);
            y.Add(random.NextDouble() < p ? 1 : 0);
        }
        var model = new LogisticRegressionTrainer().Train(new TrainingData(x, y, Names), Hyperparameters.Of(("lambda", 0.0001)), TaskKind.BinaryClassification, 1, CancellationToken.None);
        var probability = model.Predict([[1, -1, 0.5]])[0];
        Assert.InRange(probability, 0.88, 0.97); // true value σ(0.3 + 1.5 + 1.0) = 0.943
        var scored = x.Select((r, i) => new ScoredObservation(0, i, model.Predict([r])[0], y[i], 0)).ToList();
        Assert.True(MetricCalculator.RocAuc(scored) > 0.75);
    }

    [Theory]
    [InlineData(ModelFamily.RandomForest, TaskKind.Regression)]
    [InlineData(ModelFamily.GradientBoosting, TaskKind.Regression)]
    [InlineData(ModelFamily.RandomForest, TaskKind.BinaryClassification)]
    [InlineData(ModelFamily.GradientBoosting, TaskKind.BinaryClassification)]
    public void TreeModels_LearnNonLinearSignal_AndRoundTripThroughArtifacts(ModelFamily family, TaskKind task)
    {
        var data = Linear(2000, 5, (a, b) => task == TaskKind.Regression ? (a > 0 ? 1 : -1) * Math.Abs(b) : (a * b > 0 ? 1 : 0));
        var trainer = ModelRegistry.Get(family);
        var model = trainer.Train(data, trainer.DefaultGrid(task)[0], task, seed: 7, CancellationToken.None);
        var test = Linear(500, 6, (a, b) => task == TaskKind.Regression ? (a > 0 ? 1 : -1) * Math.Abs(b) : (a * b > 0 ? 1 : 0));
        var predictions = model.Predict(test.Features);
        if (task == TaskKind.Regression)
            Assert.True(MetricCalculator.Spearman(predictions, [.. test.Labels]) > 0.7);
        else
        {
            Assert.All(predictions, p => Assert.InRange(p, 0, 1));
            Assert.True(MetricCalculator.RocAuc(test.Labels.Select((l, i) => new ScoredObservation(0, i, predictions[i], l, 0)).ToList()) > 0.85);
        }

        var manifest = Manifest(family, task, model);
        var bytes = ModelArtifact.Serialize(model, manifest);
        var (loadedManifest, loaded) = ModelArtifact.Deserialize(bytes, ModelArtifact.Sha256(bytes));
        Assert.Equal(predictions, loaded.Predict(test.Features));
        Assert.Equal(manifest.TrainedThrough, loadedManifest.TrainedThrough);
        Assert.Equal(model.Explain(test.Features.Take(3).ToList(), 2)[0].Select(c => c.Feature), loaded.Explain(test.Features.Take(3).ToList(), 2)[0].Select(c => c.Feature));
    }

    [Fact]
    public void Artifact_WithWrongChecksum_IsNeverLoaded()
    {
        var data = Linear(200, 9, (a, b) => a);
        var model = new RidgeRegressionTrainer().Train(data, Hyperparameters.Of(("lambda", 0.1)), TaskKind.Regression, 1, CancellationToken.None);
        var bytes = ModelArtifact.Serialize(model, Manifest(ModelFamily.RidgeRegression, TaskKind.Regression, model));
        var checksum = ModelArtifact.Sha256(bytes);
        bytes[^10] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => ModelArtifact.Deserialize(bytes, checksum));
    }

    [Fact]
    public void Trainers_AreDeterministicForASeed()
    {
        var data = Linear(800, 10, (a, b) => a * b);
        foreach (var family in new[] { ModelFamily.RandomForest, ModelFamily.GradientBoosting })
        {
            var trainer = ModelRegistry.Get(family);
            var a = trainer.Train(data, trainer.DefaultGrid(TaskKind.Regression)[1], TaskKind.Regression, 3, CancellationToken.None).Predict(data.Features);
            var b = trainer.Train(data, trainer.DefaultGrid(TaskKind.Regression)[1], TaskKind.Regression, 3, CancellationToken.None).Predict(data.Features);
            Assert.Equal(a, b);
        }
    }

    private static ModelManifest Manifest(ModelFamily family, TaskKind task, ITrainedModel model) => new()
    {
        Family = family,
        Task = task,
        Target = task == TaskKind.Regression ? PredictionTarget.ForwardReturn : PredictionTarget.OutperformBenchmark,
        HorizonDays = 21,
        FeatureSetVersion = "test",
        FeatureNames = model.FeatureNames,
        Hyperparameters = model.Hyperparameters.Values,
        TrainedFrom = new DateOnly(2020, 1, 1),
        TrainedThrough = new DateOnly(2023, 12, 29),
        TrainingSamples = 2000,
        Seed = 7,
        Role = "Evaluation",
        CreatedAtUtc = DateTime.UtcNow,
    };
}

public class PortfolioConstructionTests
{
    private static List<Candidate> Candidates(int n, Func<int, string?>? sector = null) =>
        Enumerable.Range(0, n).Select(i => new Candidate($"S{i:D2}", sector?.Invoke(i) ?? "A", Score: n - i, TrailingVolatility: 0.1 + 0.02 * i)).ToList();

    [Fact]
    public void TopK_EqualWeight_SelectsHighestScores()
    {
        var r = PortfolioConstructor.Build(Candidates(10), new PortfolioConstructionSettings { TopK = 4, MaxWeight = 0.5, CashReserve = 0.02 });
        Assert.Equal(["S00", "S01", "S02", "S03"], r.Selected);
        Assert.All(r.Weights.Values, w => Assert.Equal(0.245, w, 10));
        Assert.Equal(0.02, r.CashWeight, 10);
    }

    [Fact]
    public void RankAndInverseVolatilityWeighting_FollowTheirRules()
    {
        var rank = PortfolioConstructor.Build(Candidates(5), new PortfolioConstructionSettings { TopK = 3, Weighting = WeightingMethod.RankWeighted, MaxWeight = 1 });
        Assert.Equal(3.0 / 6, rank.Weights["S00"], 10);
        Assert.Equal(1.0 / 6, rank.Weights["S02"], 10);
        var inv = PortfolioConstructor.Build(Candidates(5), new PortfolioConstructionSettings { TopK = 2, Weighting = WeightingMethod.InverseVolatility, MaxWeight = 1 });
        Assert.Equal(1 / 0.1 / (1 / 0.1 + 1 / 0.12), inv.Weights["S00"], 10);
    }

    [Fact]
    public void Caps_AreRespected_AndExcessIsRedistributedOrHeldInCash()
    {
        var capped = PortfolioConstructor.Build(Candidates(10), new PortfolioConstructionSettings { TopK = 5, Weighting = WeightingMethod.RankWeighted, MaxWeight = 0.25 });
        Assert.All(capped.Weights.Values, w => Assert.True(w <= 0.25 + 1e-12));
        Assert.Equal(1, capped.Weights.Values.Sum(), 9);

        var infeasible = PortfolioConstructor.Build(Candidates(10), new PortfolioConstructionSettings { TopK = 3, MaxWeight = 0.2 });
        Assert.Equal(0.6, infeasible.Weights.Values.Sum(), 9);
        Assert.Equal(0.4, infeasible.CashWeight, 9);
        Assert.Contains(infeasible.Notes, n => n.Contains("cash"));
    }

    [Fact]
    public void SectorLimits_AreEnforced()
    {
        var r = PortfolioConstructor.Build(Candidates(10, i => i < 6 ? "Tech" : "Energy"),
            new PortfolioConstructionSettings { TopK = 8, MaxWeight = 0.3, SectorLimits = new Dictionary<string, double> { ["Tech"] = 0.4 } });
        Assert.True(r.Weights.Where(kv => int.Parse(kv.Key[1..]) < 6).Sum(kv => kv.Value) <= 0.4 + 1e-9);
        Assert.True(r.Weights.Values.Sum() <= 1 + 1e-9);
    }

    [Fact]
    public void ThresholdAndTurnoverLimit_AreApplied()
    {
        var none = PortfolioConstructor.Build(Candidates(5), new PortfolioConstructionSettings { MinimumScore = 100 });
        Assert.Empty(none.Weights);
        Assert.Equal(1, none.CashWeight);

        var current = new Dictionary<string, double> { ["S09"] = 0.5, ["S08"] = 0.5 };
        var limited = PortfolioConstructor.Build(Candidates(10), new PortfolioConstructionSettings { TopK = 2, MaxWeight = 0.5, MaxTurnover = 0.2 }, current);
        Assert.Equal(0.2, limited.Turnover!.Value, 9);
        var turnover = limited.Weights.Keys.Union(current.Keys).Sum(k => Math.Abs(limited.Weights.GetValueOrDefault(k) - current.GetValueOrDefault(k))) / 2;
        Assert.Equal(0.2, turnover, 9);
    }
}

public class MetricTests
{
    [Fact]
    public void Spearman_HandlesTiesAndMonotoneTransforms()
    {
        Assert.Equal(1, MetricCalculator.Spearman([1, 2, 3, 4], [10, 20, 30, 1000])!.Value, 12);
        Assert.Equal(-1, MetricCalculator.Spearman([1, 2, 3, 4], [4, 3, 2, 1])!.Value, 12);
        Assert.Equal([1.5, 1.5, 3], MetricCalculator.Ranks([5, 5, 7]));
        Assert.Null(MetricCalculator.Spearman([1, 1, 1], [1, 2, 3]));
    }

    [Fact]
    public void Auc_Brier_AndCalibration_MatchDefinitions()
    {
        var perfect = new[] { (0.9, 1.0), (0.8, 1.0), (0.2, 0.0), (0.1, 0.0) }.Select((x, i) => new ScoredObservation(0, i, x.Item1, x.Item2, 0)).ToList();
        Assert.Equal(1, MetricCalculator.RocAuc(perfect));
        var m = MetricCalculator.Classification(perfect, trainingBaseRate: 0.5);
        Assert.Equal((0.01 + 0.04 + 0.04 + 0.01) / 4, m.Brier, 12);
        Assert.Equal(0.25, m.BaseRateBrier, 12);
        Assert.Equal(1, m.Accuracy);
        var reversed = perfect.Select(o => o with { Prediction = 1 - o.Prediction }).ToList();
        Assert.Equal(0, MetricCalculator.RocAuc(reversed));
    }

    [Fact]
    public void OutOfSampleR2_IsRelativeToTheTrainingMean()
    {
        var obs = new[] { (1.0, 1.0), (2.0, 2.0), (3.0, 3.0) }.Select((x, i) => new ScoredObservation(0, i, x.Item1, x.Item2, 0)).ToList();
        Assert.Equal(1, MetricCalculator.Regression(obs, trainingMean: 2, directional: true).OutOfSampleR2);
        var naive = obs.Select(o => o with { Prediction = 2 }).ToList();
        Assert.Equal(0, MetricCalculator.Regression(naive, trainingMean: 2, directional: false).OutOfSampleR2!.Value, 12);
    }

    [Fact]
    public void Ranking_TopKAndSpread_UseRealizedOutcomes()
    {
        var obs = Enumerable.Range(0, 10).Select(i => new ScoredObservation(1, i, Prediction: i, Label: 0, Outcome: i / 100.0)).ToList();
        var r = MetricCalculator.Ranking(obs, topK: 3, higherIsBetter: true);
        Assert.Equal(1, r.MeanInformationCoefficient!.Value, 12);
        Assert.Equal(0.08, r.TopKMeanOutcome!.Value, 12);
        Assert.Equal(0.045, r.UniverseMeanOutcome!.Value, 12);
        Assert.Equal(1, r.TopKHitRate);
        var inverted = MetricCalculator.Ranking(obs, topK: 3, higherIsBetter: false);
        Assert.Equal(-1, inverted.MeanInformationCoefficient!.Value, 12);
    }
}

public class ArchitectureTests
{
    [Fact]
    public void MachineLearning_NeverUsesTheMeanVarianceOptimizer()
    {
        var ml = typeof(ExperimentEngine).Assembly;
        var forbidden = new[] { "PortfolioOptimizer.Optimization.Portfolio.MeanVarianceOptimizer", "PortfolioOptimizer.Optimization.Portfolio.IPortfolioOptimizer",
            "PortfolioOptimizer.Optimization.Backtesting.BacktestEngine" };
        var used = ml.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
                .Concat(t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Select(f => f.FieldType)))
            .Select(t => t.FullName ?? "").ToHashSet();
        Assert.DoesNotContain(used, forbidden.Contains);
    }

    [Fact]
    public void Optimization_DoesNotReferenceMachineLearning()
    {
        var references = typeof(Optimization.Backtesting.PortfolioSimulator).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("PortfolioOptimizer.MachineLearning", references);
        Assert.DoesNotContain("Microsoft.ML", references);
    }
}

public class InvalidScoreTests
{
    [Fact]
    public void NonFiniteScores_AreNotEligible_AndSaidSo()
    {
        var candidates = new List<Candidate>
        {
            new("A", "X", 0.9, 0.2), new("B", "X", double.NaN, 0.2), new("C", "X", double.PositiveInfinity, 0.2), new("D", "X", 0.5, 0.2),
        };
        var r = PortfolioConstructor.Build(candidates, new PortfolioConstructionSettings { TopK = 4, MaxWeight = 1 });
        Assert.Equal(["A", "D"], r.Selected);
        Assert.Contains(r.Notes, n => n.Contains("No valid score") && n.Contains("B") && n.Contains("C"));
    }
}
