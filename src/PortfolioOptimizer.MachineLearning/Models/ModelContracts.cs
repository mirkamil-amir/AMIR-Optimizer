using System.Globalization;
using System.IO.Compression;
using PortfolioOptimizer.MachineLearning.Labels;

namespace PortfolioOptimizer.MachineLearning.Models;

public enum ModelFamily
{
    /// <summary>L2-regularized linear regression (closed form) – transparent baseline for return/volatility targets.</summary>
    RidgeRegression,
    /// <summary>L2-regularized logistic regression (Newton/IRLS) – probability of outperformance.</summary>
    LogisticRegression,
    /// <summary>Random forest of regression trees (ML.NET FastForest); on a 0/1 target it estimates the probability.</summary>
    RandomForest,
    /// <summary>Gradient-boosted decision trees (ML.NET FastTree, MART).</summary>
    GradientBoosting,
}

/// <summary>Named hyperparameter values (all numeric), ordered for stable display and hashing.</summary>
public sealed record Hyperparameters(IReadOnlyDictionary<string, double> Values)
{
    public double this[string name] => Values.TryGetValue(name, out var v) ? v : throw new KeyNotFoundException($"Hyperparameter '{name}' is missing.");

    public override string ToString() =>
        string.Join(", ", Values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value.ToString("G6", CultureInfo.InvariantCulture)}"));

    public static Hyperparameters Of(params (string Name, double Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));
}

/// <summary>Raw (un-imputed, unscaled) feature rows with their labels.</summary>
public sealed record TrainingData(IReadOnlyList<double[]> Features, IReadOnlyList<double> Labels, IReadOnlyList<string> FeatureNames)
{
    public int Count => Labels.Count;
}

/// <summary>A prediction driver. <see cref="FeatureValue"/> is the raw value (null when missing and imputed for the model).</summary>
public sealed record FeatureContribution(string Feature, double? FeatureValue, double Contribution);

public sealed record NamedValue(string Name, double Value);

/// <summary>Model-native global explanation (coefficients or split-gain importance) with how to read it.</summary>
public sealed record ModelExplanation(string Method, IReadOnlyList<NamedValue> Values, string Interpretation);

/// <summary>A fitted model including its own preprocessing (fitted on its training rows only).</summary>
public interface ITrainedModel
{
    ModelFamily Family { get; }
    TaskKind Task { get; }
    IReadOnlyList<string> FeatureNames { get; }
    Hyperparameters Hyperparameters { get; }

    /// <summary>Predicted value (regression) or estimated probability of class 1 (classification) for raw feature rows.</summary>
    double[] Predict(IReadOnlyList<double[]> rawFeatures);

    /// <summary>Per-row drivers: the largest contributions by absolute value.</summary>
    IReadOnlyList<FeatureContribution>[] Explain(IReadOnlyList<double[]> rawFeatures, int top);

    /// <summary>How <see cref="Explain"/> attributes predictions (exact additive vs. occlusion).</summary>
    string ContributionMethod { get; }

    ModelExplanation GlobalExplanation();

    /// <summary>Writes the model's parameters into an artifact archive.</summary>
    void Save(ZipArchive archive);
}

/// <summary>Trains one model family. Implementations must be deterministic for a given seed.</summary>
public interface IModelTrainer
{
    ModelFamily Family { get; }
    bool Supports(TaskKind task);

    /// <summary>Small default search space evaluated on the validation period.</summary>
    IReadOnlyList<Hyperparameters> DefaultGrid(TaskKind task);

    ITrainedModel Train(TrainingData data, Hyperparameters hyperparameters, TaskKind task, int seed, CancellationToken ct);
}

/// <summary>Median imputation and (optionally) z-score scaling, fitted on training rows only.</summary>
public sealed class FeaturePreprocessor
{
    public required double[] Medians { get; init; }
    public required double[] Means { get; init; }
    public required double[] StandardDeviations { get; init; }
    public bool Standardize { get; init; }

    public static FeaturePreprocessor Fit(IReadOnlyList<double[]> rows, bool standardize)
    {
        if (rows.Count == 0) throw new ArgumentException("No training rows.");
        var m = rows[0].Length;
        var medians = new double[m];
        for (var j = 0; j < m; j++)
        {
            var values = rows.Select(r => r[j]).Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
            medians[j] = values.Length == 0 ? 0 : values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
        }
        var means = new double[m];
        var sds = new double[m];
        for (var j = 0; j < m; j++)
        {
            var col = rows.Select(r => double.IsNaN(r[j]) ? medians[j] : r[j]).ToArray();
            means[j] = col.Average();
            var variance = col.Length > 1 ? col.Sum(v => (v - means[j]) * (v - means[j])) / (col.Length - 1) : 0;
            sds[j] = Math.Sqrt(variance);
        }
        return new FeaturePreprocessor { Medians = medians, Means = means, StandardDeviations = sds, Standardize = standardize };
    }

    public double[] Transform(double[] raw)
    {
        var x = new double[raw.Length];
        for (var j = 0; j < raw.Length; j++)
        {
            var v = double.IsNaN(raw[j]) ? Medians[j] : raw[j];
            x[j] = !Standardize ? v : StandardDeviations[j] > 1e-12 ? (v - Means[j]) / StandardDeviations[j] : 0;
        }
        return x;
    }
}
