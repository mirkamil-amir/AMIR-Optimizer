using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PortfolioOptimizer.MachineLearning.Labels;

namespace PortfolioOptimizer.MachineLearning.Models;

/// <summary>Everything needed to interpret a stored model; written as manifest.json inside the artifact.</summary>
public sealed record ModelManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required ModelFamily Family { get; init; }
    public required TaskKind Task { get; init; }
    public required PredictionTarget Target { get; init; }
    public required int HorizonDays { get; init; }
    public required string FeatureSetVersion { get; init; }
    public required IReadOnlyList<string> FeatureNames { get; init; }
    public required IReadOnlyDictionary<string, double> Hyperparameters { get; init; }
    public required DateOnly TrainedFrom { get; init; }
    public required DateOnly TrainedThrough { get; init; }
    public required int TrainingSamples { get; init; }
    public required int Seed { get; init; }
    public required string Role { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public string? DataSnapshotHash { get; init; }
}

/// <summary>
/// Model artifact format: a ZIP archive with manifest.json plus the family's parameters (linear.json, or preprocessor.json and
/// an ML.NET model). Artifacts are created only by this server; loading verifies the SHA-256 checksum recorded in the
/// database before anything is deserialized, and checks the manifest against the expected experiment.
/// </summary>
public static class ModelArtifact
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static byte[] Serialize(ITrainedModel model, ModelManifest manifest)
    {
        if (!manifest.FeatureNames.SequenceEqual(model.FeatureNames)) throw new ArgumentException("Manifest features do not match the model.");
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = archive.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(s, manifest, Json);
            model.Save(archive);
        }
        return buffer.ToArray();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Verifies the checksum, then reads the manifest and parameters.</summary>
    /// <exception cref="InvalidDataException">Checksum mismatch or malformed artifact.</exception>
    public static (ModelManifest Manifest, ITrainedModel Model) Deserialize(byte[] bytes, string expectedSha256)
    {
        var actual = Sha256(bytes);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Model artifact checksum mismatch (expected {expectedSha256}, found {actual}); refusing to load.");
        using var buffer = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        ModelManifest manifest;
        using (var s = (archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Artifact has no manifest.")).Open())
            manifest = JsonSerializer.Deserialize<ModelManifest>(s, Json) ?? throw new InvalidDataException("Invalid manifest.");
        if (manifest.FormatVersion != ModelManifest.CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported artifact format version {manifest.FormatVersion}.");
        var hp = new Hyperparameters(manifest.Hyperparameters);
        ITrainedModel model = manifest.Family switch
        {
            ModelFamily.RidgeRegression or ModelFamily.LogisticRegression => LinearModel.Load(archive, manifest.Family, manifest.Task, manifest.FeatureNames, hp),
            ModelFamily.RandomForest or ModelFamily.GradientBoosting => TreeModel.Load(archive, manifest.Family, manifest.Task, manifest.FeatureNames, hp, manifest.Seed),
            _ => throw new InvalidDataException($"Unknown model family {manifest.Family}."),
        };
        return (manifest, model);
    }
}

public static class ModelRegistry
{
    public static readonly IReadOnlyList<IModelTrainer> Trainers =
        [new RidgeRegressionTrainer(), new LogisticRegressionTrainer(), new RandomForestTrainer(), new GradientBoostingTrainer()];

    public static IModelTrainer Get(ModelFamily family) => Trainers.Single(t => t.Family == family);

    public static string Describe(ModelFamily family) => family switch
    {
        ModelFamily.RidgeRegression => "Ridge regression: L2-regularized linear model on standardized features (closed form). Transparent baseline; coefficients are associations, not causes. Regression targets.",
        ModelFamily.LogisticRegression => "Logistic regression: L2-regularized linear classifier (Newton/IRLS) estimating the probability of outperforming the benchmark.",
        ModelFamily.RandomForest => "Random forest: 200 bagged regression trees with feature subsampling (ML.NET FastForest). Captures non-linearities; on the binary target it estimates a probability.",
        ModelFamily.GradientBoosting => "Gradient boosting: sequentially fitted shallow trees (ML.NET FastTree / MART) with learning-rate shrinkage. Most flexible; most prone to overfitting – compare with the simpler models out of sample.",
        _ => family.ToString(),
    };
}
