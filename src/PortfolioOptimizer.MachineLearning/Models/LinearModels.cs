using System.IO.Compression;
using System.Text.Json;
using MathNet.Numerics.LinearAlgebra;
using PortfolioOptimizer.MachineLearning.Labels;

namespace PortfolioOptimizer.MachineLearning.Models;

/// <summary>Parameters of a linear model on standardized features (shared by ridge and logistic regression).</summary>
public sealed record LinearModelState(double Intercept, double[] Coefficients, double[] Medians, double[] Means, double[] StandardDeviations);

/// <summary>
/// A linear predictor ŷ = b + Σ βⱼ zⱼ on z-scored features (z = (x − mean)/sd, means/sds from the training rows). For the
/// logistic model the probability is σ(ŷ). Contributions βⱼ zⱼ are exact (additive on the linear or log-odds scale).
/// Coefficients describe associations in the training data, not causal effects.
/// </summary>
public sealed class LinearModel : ITrainedModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly FeaturePreprocessor _pre;

    public LinearModel(ModelFamily family, TaskKind task, IReadOnlyList<string> featureNames, Hyperparameters hyperparameters, LinearModelState state)
    {
        Family = family;
        Task = task;
        FeatureNames = featureNames;
        Hyperparameters = hyperparameters;
        State = state;
        _pre = new FeaturePreprocessor { Medians = state.Medians, Means = state.Means, StandardDeviations = state.StandardDeviations, Standardize = true };
    }

    public ModelFamily Family { get; }
    public TaskKind Task { get; }
    public IReadOnlyList<string> FeatureNames { get; }
    public Hyperparameters Hyperparameters { get; }
    public LinearModelState State { get; }

    public string ContributionMethod => Task == TaskKind.BinaryClassification
        ? "Exact additive contributions βⱼ·zⱼ on the log-odds scale (z = standardized feature value)."
        : "Exact additive contributions βⱼ·zⱼ to the prediction (z = standardized feature value).";

    public double[] Predict(IReadOnlyList<double[]> rawFeatures) =>
        rawFeatures.Select(r =>
        {
            var eta = Linear(_pre.Transform(r));
            return Task == TaskKind.BinaryClassification ? Sigmoid(eta) : eta;
        }).ToArray();

    public IReadOnlyList<FeatureContribution>[] Explain(IReadOnlyList<double[]> rawFeatures, int top) =>
        rawFeatures.Select(r =>
        {
            var z = _pre.Transform(r);
            return (IReadOnlyList<FeatureContribution>)Enumerable.Range(0, z.Length)
                .Select(j => new FeatureContribution(FeatureNames[j], double.IsNaN(r[j]) ? null : r[j], State.Coefficients[j] * z[j]))
                .OrderByDescending(c => Math.Abs(c.Contribution)).Take(top).ToList();
        }).ToArray();

    public ModelExplanation GlobalExplanation() => new(
        "Standardized coefficients",
        FeatureNames.Select((n, j) => new NamedValue(n, State.Coefficients[j])).OrderByDescending(v => Math.Abs(v.Value)).ToList(),
        Task == TaskKind.BinaryClassification
            ? "Change in log-odds of outperformance per one-standard-deviation increase of the feature, holding the others fixed. Associations, not causes."
            : "Change in the predicted target per one-standard-deviation increase of the feature, holding the others fixed. Associations, not causes.");

    public void Save(ZipArchive archive)
    {
        var entry = archive.CreateEntry("linear.json");
        using var s = entry.Open();
        JsonSerializer.Serialize(s, State, Json);
    }

    public static LinearModel Load(ZipArchive archive, ModelFamily family, TaskKind task, IReadOnlyList<string> featureNames, Hyperparameters hp)
    {
        var entry = archive.GetEntry("linear.json") ?? throw new InvalidDataException("Artifact has no linear.json.");
        using var s = entry.Open();
        var state = JsonSerializer.Deserialize<LinearModelState>(s, Json) ?? throw new InvalidDataException("Invalid linear model.");
        if (state.Coefficients.Length != featureNames.Count) throw new InvalidDataException("Coefficient count does not match the feature list.");
        return new LinearModel(family, task, featureNames, hp, state);
    }

    private double Linear(double[] z)
    {
        var eta = State.Intercept;
        for (var j = 0; j < z.Length; j++) eta += State.Coefficients[j] * z[j];
        return eta;
    }

    internal static double Sigmoid(double x) => x >= 0 ? 1 / (1 + Math.Exp(-x)) : Math.Exp(x) / (1 + Math.Exp(x));
}

/// <summary>
/// Ridge regression on standardized features: β = (ZᵀZ + λ·n·I)⁻¹ Zᵀ(y − ȳ), intercept ȳ (not penalized). λ is scaled by
/// the sample count so the same value means the same amount of shrinkage regardless of dataset size.
/// </summary>
public sealed class RidgeRegressionTrainer : IModelTrainer
{
    public ModelFamily Family => ModelFamily.RidgeRegression;
    public bool Supports(TaskKind task) => task == TaskKind.Regression;

    public IReadOnlyList<Hyperparameters> DefaultGrid(TaskKind task) =>
        [.. new[] { 0.001, 0.01, 0.1, 1.0 }.Select(l => Hyperparameters.Of(("lambda", l)))];

    public ITrainedModel Train(TrainingData data, Hyperparameters hyperparameters, TaskKind task, int seed, CancellationToken ct)
    {
        if (task != TaskKind.Regression) throw new ArgumentException("Ridge regression supports regression targets only.");
        var lambda = hyperparameters["lambda"];
        if (lambda < 0) throw new ArgumentException("lambda must be non-negative.");
        var pre = FeaturePreprocessor.Fit(data.Features, standardize: true);
        var n = data.Count;
        var m = data.FeatureNames.Count;
        var rows = data.Features.Select(pre.Transform).ToArray();
        var z = Matrix<double>.Build.Dense(n, m, (i, j) => rows[i][j]);
        var yMean = data.Labels.Average();
        var y = Vector<double>.Build.Dense(n, i => data.Labels[i] - yMean);
        var gram = z.TransposeThisAndMultiply(z) + Matrix<double>.Build.DenseIdentity(m) * (lambda * n + 1e-10);
        var beta = gram.Cholesky().Solve(z.TransposeThisAndMultiply(y));
        ct.ThrowIfCancellationRequested();
        return new LinearModel(Family, task, data.FeatureNames, hyperparameters,
            new LinearModelState(yMean, beta.ToArray(), pre.Medians, pre.Means, pre.StandardDeviations));
    }
}

/// <summary>
/// Logistic regression with an L2 penalty λ·n/2·‖β‖² (intercept unpenalized) on standardized features, fitted by Newton's
/// method (iteratively reweighted least squares). The penalty keeps the solution finite under (quasi-)separation.
/// </summary>
public sealed class LogisticRegressionTrainer : IModelTrainer
{
    public const int MaxIterations = 100;

    public ModelFamily Family => ModelFamily.LogisticRegression;
    public bool Supports(TaskKind task) => task == TaskKind.BinaryClassification;

    public IReadOnlyList<Hyperparameters> DefaultGrid(TaskKind task) =>
        [.. new[] { 0.001, 0.01, 0.1, 1.0 }.Select(l => Hyperparameters.Of(("lambda", l)))];

    public ITrainedModel Train(TrainingData data, Hyperparameters hyperparameters, TaskKind task, int seed, CancellationToken ct)
    {
        if (task != TaskKind.BinaryClassification) throw new ArgumentException("Logistic regression supports binary targets only.");
        if (data.Labels.Any(y => y is not (0 or 1))) throw new ArgumentException("Labels must be 0 or 1.");
        var lambda = hyperparameters["lambda"];
        var pre = FeaturePreprocessor.Fit(data.Features, standardize: true);
        var n = data.Count;
        var m = data.FeatureNames.Count;
        // Design matrix with a leading intercept column.
        var rows = data.Features.Select(pre.Transform).ToArray();
        var x = Matrix<double>.Build.Dense(n, m + 1, (i, j) => j == 0 ? 1 : rows[i][j - 1]);
        var y = Vector<double>.Build.Dense(n, i => data.Labels[i]);
        var penalty = Vector<double>.Build.Dense(m + 1, j => j == 0 ? 0 : lambda * n);
        var baseRate = Math.Clamp(y.Average(), 1e-6, 1 - 1e-6);
        var beta = Vector<double>.Build.Dense(m + 1, j => j == 0 ? Math.Log(baseRate / (1 - baseRate)) : 0);

        for (var iter = 0; iter < MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            var p = (x * beta).Map(LinearModel.Sigmoid);
            var w = p.Map(v => Math.Max(v * (1 - v), 1e-10));
            var gradient = x.TransposeThisAndMultiply(y - p) - penalty.PointwiseMultiply(beta);
            // XᵀWX without materializing the n×n weight matrix: scale each row of X by its weight.
            var weighted = x.Clone();
            for (var i = 0; i < n; i++) weighted.SetRow(i, x.Row(i) * w[i]);
            var hessian = x.TransposeThisAndMultiply(weighted) + Matrix<double>.Build.DenseOfDiagonalVector(penalty)
                          + Matrix<double>.Build.DenseIdentity(m + 1) * 1e-10;
            var step = hessian.Cholesky().Solve(gradient);
            beta += step;
            if (step.InfinityNorm() < 1e-9) break;
        }

        var coefficients = beta.SubVector(1, m).ToArray();
        return new LinearModel(Family, task, data.FeatureNames, hyperparameters,
            new LinearModelState(beta[0], coefficients, pre.Medians, pre.Means, pre.StandardDeviations));
    }
}
