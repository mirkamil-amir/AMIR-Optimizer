using System.IO.Compression;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;
using PortfolioOptimizer.MachineLearning.Labels;

namespace PortfolioOptimizer.MachineLearning.Models;

internal sealed class MlRow
{
    public float[] Features { get; set; } = [];
    public float Label { get; set; }
    public bool BoolLabel { get; set; }
}

internal sealed class ScoreRow
{
    public float Score { get; set; }
}

internal sealed class ProbabilityRow
{
    public float Probability { get; set; }
}

/// <summary>Tree ensemble trained with ML.NET. Preprocessing: median imputation fitted on the training rows.</summary>
public sealed class TreeModel : ITrainedModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly MLContext _ml;
    private readonly ITransformer _transformer;
    private readonly FeaturePreprocessor _pre;
    private readonly bool _probabilityColumn;
    private readonly IReadOnlyList<NamedValue> _gainImportance;

    internal TreeModel(ModelFamily family, TaskKind task, IReadOnlyList<string> featureNames, Hyperparameters hyperparameters, MLContext ml,
        ITransformer transformer, FeaturePreprocessor pre, bool probabilityColumn, IReadOnlyList<NamedValue> gainImportance)
    {
        Family = family;
        Task = task;
        FeatureNames = featureNames;
        Hyperparameters = hyperparameters;
        _ml = ml;
        _transformer = transformer;
        _pre = pre;
        _probabilityColumn = probabilityColumn;
        _gainImportance = gainImportance;
    }

    public ModelFamily Family { get; }
    public TaskKind Task { get; }
    public IReadOnlyList<string> FeatureNames { get; }
    public Hyperparameters Hyperparameters { get; }

    public string ContributionMethod =>
        "Occlusion: the change in the prediction when the feature is replaced by its training-set median, all other features unchanged. " +
        "Not additive; interactions between features are not separated.";

    public double[] Predict(IReadOnlyList<double[]> rawFeatures)
    {
        if (rawFeatures.Count == 0) return [];
        var data = TreeTraining.ToDataView(_ml, rawFeatures.Select(_pre.Transform).ToList(), null, FeatureNames.Count);
        var scored = _transformer.Transform(data);
        double[] values = _probabilityColumn
            ? [.. _ml.Data.CreateEnumerable<ProbabilityRow>(scored, reuseRowObject: false).Select(r => (double)r.Probability)]
            : [.. _ml.Data.CreateEnumerable<ScoreRow>(scored, reuseRowObject: false).Select(r => (double)r.Score)];
        // A regression forest on a 0/1 target estimates a probability; keep it inside [0, 1].
        return Task == TaskKind.BinaryClassification ? values.Select(v => Math.Clamp(v, 0, 1)).ToArray() : values;
    }

    public IReadOnlyList<FeatureContribution>[] Explain(IReadOnlyList<double[]> rawFeatures, int top)
    {
        var m = FeatureNames.Count;
        var batch = new List<double[]>(rawFeatures.Count * (m + 1));
        foreach (var row in rawFeatures)
        {
            batch.Add(row);
            for (var j = 0; j < m; j++)
            {
                var occluded = (double[])row.Clone();
                occluded[j] = _pre.Medians[j];
                batch.Add(occluded);
            }
        }
        var predictions = Predict(batch);
        var result = new IReadOnlyList<FeatureContribution>[rawFeatures.Count];
        for (var r = 0; r < rawFeatures.Count; r++)
        {
            var baseIndex = r * (m + 1);
            var full = predictions[baseIndex];
            result[r] = Enumerable.Range(0, m)
                .Select(j => new FeatureContribution(FeatureNames[j], double.IsNaN(rawFeatures[r][j]) ? null : rawFeatures[r][j], full - predictions[baseIndex + 1 + j]))
                .OrderByDescending(c => Math.Abs(c.Contribution)).Take(top).ToList();
        }
        return result;
    }

    public ModelExplanation GlobalExplanation() => new(
        "Split-gain importance",
        _gainImportance,
        "Total loss reduction from splits on each feature, normalized to sum to 1 (in-sample, training data). High importance means the trees " +
        "use the feature often, not that it causes returns; correlated features share importance.");

    public void Save(ZipArchive archive)
    {
        using (var s = archive.CreateEntry("preprocessor.json").Open())
            JsonSerializer.Serialize(s, new TreeState(_pre.Medians, _probabilityColumn, _gainImportance), Json);
        var data = TreeTraining.ToDataView(_ml, [new double[FeatureNames.Count]], null, FeatureNames.Count);
        using var model = archive.CreateEntry("mlnet-model.zip").Open();
        using var buffer = new MemoryStream();
        _ml.Model.Save(_transformer, data.Schema, buffer);
        buffer.Position = 0;
        buffer.CopyTo(model);
    }

    public static TreeModel Load(ZipArchive archive, ModelFamily family, TaskKind task, IReadOnlyList<string> featureNames, Hyperparameters hp, int seed)
    {
        TreeState state;
        using (var s = (archive.GetEntry("preprocessor.json") ?? throw new InvalidDataException("Artifact has no preprocessor.json.")).Open())
            state = JsonSerializer.Deserialize<TreeState>(s, Json) ?? throw new InvalidDataException("Invalid tree model state.");
        if (state.Medians.Length != featureNames.Count) throw new InvalidDataException("Preprocessor does not match the feature list.");
        var ml = new MLContext(seed);
        using var buffer = new MemoryStream();
        using (var s = (archive.GetEntry("mlnet-model.zip") ?? throw new InvalidDataException("Artifact has no ML.NET model.")).Open()) s.CopyTo(buffer);
        buffer.Position = 0;
        var transformer = ml.Model.Load(buffer, out _);
        var pre = new FeaturePreprocessor { Medians = state.Medians, Means = new double[state.Medians.Length], StandardDeviations = new double[state.Medians.Length] };
        return new TreeModel(family, task, featureNames, hp, ml, transformer, pre, state.ProbabilityColumn, state.GainImportance);
    }

    private sealed record TreeState(double[] Medians, bool ProbabilityColumn, IReadOnlyList<NamedValue> GainImportance);
}

internal static class TreeTraining
{
    public static IDataView ToDataView(MLContext ml, IReadOnlyList<double[]> rows, IReadOnlyList<double>? labels, int featureCount)
    {
        var schema = SchemaDefinition.Create(typeof(MlRow));
        schema[nameof(MlRow.Features)].ColumnType = new VectorDataViewType(NumberDataViewType.Single, featureCount);
        var data = rows.Select((r, i) => new MlRow
        {
            Features = r.Select(v => (float)v).ToArray(),
            Label = labels is null ? 0 : (float)labels[i],
            BoolLabel = labels is not null && labels[i] >= 0.5,
        }).ToList();
        return ml.Data.LoadFromEnumerable(data, schema);
    }

    public static IReadOnlyList<NamedValue> GainImportance(TreeEnsembleModelParameters model, IReadOnlyList<string> names)
    {
        VBuffer<float> weights = default;
        model.GetFeatureWeights(ref weights);
        var dense = weights.DenseValues().Select(v => (double)v).ToArray();
        var total = dense.Sum();
        return names.Select((n, j) => new NamedValue(n, total > 0 && j < dense.Length ? dense[j] / total : 0))
            .OrderByDescending(v => v.Value).ToList();
    }
}

/// <summary>
/// Random forest (bagged regression trees with feature subsampling, ML.NET FastForest). For the binary target it is
/// trained as a regression forest on the 0/1 label, so its output is an estimated probability (clipped to [0, 1]);
/// calibration is checked on validation and test data rather than assumed.
/// </summary>
public sealed class RandomForestTrainer : IModelTrainer
{
    public ModelFamily Family => ModelFamily.RandomForest;
    public bool Supports(TaskKind task) => true;

    public IReadOnlyList<Hyperparameters> DefaultGrid(TaskKind task) =>
    [
        Hyperparameters.Of(("trees", 200), ("leaves", 16), ("minLeaf", 20), ("featureFraction", 0.6)),
        Hyperparameters.Of(("trees", 200), ("leaves", 16), ("minLeaf", 100), ("featureFraction", 0.6)),
        Hyperparameters.Of(("trees", 200), ("leaves", 64), ("minLeaf", 20), ("featureFraction", 0.6)),
        Hyperparameters.Of(("trees", 200), ("leaves", 64), ("minLeaf", 100), ("featureFraction", 0.6)),
    ];

    public ITrainedModel Train(TrainingData data, Hyperparameters hp, TaskKind task, int seed, CancellationToken ct)
    {
        var ml = new MLContext(seed);
        var pre = FeaturePreprocessor.Fit(data.Features, standardize: false);
        var view = TreeTraining.ToDataView(ml, data.Features.Select(pre.Transform).ToList(), data.Labels, data.FeatureNames.Count);
        ct.ThrowIfCancellationRequested();
        var trainer = ml.Regression.Trainers.FastForest(new FastForestRegressionTrainer.Options
        {
            LabelColumnName = nameof(MlRow.Label),
            FeatureColumnName = nameof(MlRow.Features),
            NumberOfTrees = (int)hp["trees"],
            NumberOfLeaves = (int)hp["leaves"],
            MinimumExampleCountPerLeaf = (int)hp["minLeaf"],
            FeatureFraction = hp["featureFraction"],
            BaggingExampleFraction = 0.7,
            NumberOfThreads = 1, // deterministic
            Seed = seed,
        });
        var model = trainer.Fit(view);
        return new TreeModel(Family, task, data.FeatureNames, hp, ml, model, pre, probabilityColumn: false,
            TreeTraining.GainImportance(model.Model, data.FeatureNames));
    }
}

/// <summary>
/// Gradient-boosted decision trees (ML.NET FastTree, MART). Regression targets use squared loss; the binary target uses
/// logistic loss with ML.NET's Platt calibration (fitted on the training data), whose calibration is then checked
/// out of sample.
/// </summary>
public sealed class GradientBoostingTrainer : IModelTrainer
{
    public ModelFamily Family => ModelFamily.GradientBoosting;
    public bool Supports(TaskKind task) => true;

    public IReadOnlyList<Hyperparameters> DefaultGrid(TaskKind task) =>
    [
        Hyperparameters.Of(("trees", 100), ("leaves", 8), ("learningRate", 0.05), ("minLeaf", 50)),
        Hyperparameters.Of(("trees", 300), ("leaves", 8), ("learningRate", 0.05), ("minLeaf", 50)),
        Hyperparameters.Of(("trees", 100), ("leaves", 16), ("learningRate", 0.05), ("minLeaf", 50)),
        Hyperparameters.Of(("trees", 300), ("leaves", 16), ("learningRate", 0.05), ("minLeaf", 50)),
    ];

    public ITrainedModel Train(TrainingData data, Hyperparameters hp, TaskKind task, int seed, CancellationToken ct)
    {
        var ml = new MLContext(seed);
        var pre = FeaturePreprocessor.Fit(data.Features, standardize: false);
        var view = TreeTraining.ToDataView(ml, data.Features.Select(pre.Transform).ToList(), data.Labels, data.FeatureNames.Count);
        ct.ThrowIfCancellationRequested();
        if (task == TaskKind.BinaryClassification)
        {
            var binary = ml.BinaryClassification.Trainers.FastTree(new FastTreeBinaryTrainer.Options
            {
                LabelColumnName = nameof(MlRow.BoolLabel),
                FeatureColumnName = nameof(MlRow.Features),
                NumberOfTrees = (int)hp["trees"],
                NumberOfLeaves = (int)hp["leaves"],
                LearningRate = hp["learningRate"],
                MinimumExampleCountPerLeaf = (int)hp["minLeaf"],
                NumberOfThreads = 1,
                Seed = seed,
            }).Fit(view);
            return new TreeModel(Family, task, data.FeatureNames, hp, ml, binary, pre, probabilityColumn: true,
                TreeTraining.GainImportance(binary.Model.SubModel, data.FeatureNames));
        }

        var regression = ml.Regression.Trainers.FastTree(new FastTreeRegressionTrainer.Options
        {
            LabelColumnName = nameof(MlRow.Label),
            FeatureColumnName = nameof(MlRow.Features),
            NumberOfTrees = (int)hp["trees"],
            NumberOfLeaves = (int)hp["leaves"],
            LearningRate = hp["learningRate"],
            MinimumExampleCountPerLeaf = (int)hp["minLeaf"],
            NumberOfThreads = 1,
            Seed = seed,
        }).Fit(view);
        return new TreeModel(Family, task, data.FeatureNames, hp, ml, regression, pre, probabilityColumn: false,
            TreeTraining.GainImportance(regression.Model, data.FeatureNames));
    }
}
