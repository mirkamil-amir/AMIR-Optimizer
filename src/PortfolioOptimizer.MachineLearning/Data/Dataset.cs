using PortfolioOptimizer.MachineLearning.Features;
using PortfolioOptimizer.MachineLearning.Labels;

namespace PortfolioOptimizer.MachineLearning.Data;

/// <summary>
/// One (stock, decision date) observation. <see cref="Label"/> uses prices up to <see cref="LabelAvailableIndex"/> = t + h
/// and is NaN for decisions whose horizon has not elapsed yet (those are only used for live prediction).
/// <see cref="Outcome"/> is the realized forward excess return over the same horizon, used to evaluate rankings.
/// </summary>
public sealed record Sample(int Asset, int DecisionIndex, double[] Features, double Label, double Outcome, int LabelAvailableIndex)
{
    public bool IsLabeled => !double.IsNaN(Label);
}

public sealed record DatasetSummary(int DecisionDates, int Samples, int LabeledSamples, int DroppedForMissingFeatures, int AssetsWithoutUsableHistory);

/// <summary>Samples of one stock: usable, labeled (horizon elapsed) and dropped for missing features.</summary>
public sealed record AssetSampleCount(int Samples, int LabeledSamples, int DroppedForMissingFeatures);

/// <summary>Panel samples on a regular decision grid (every <c>sampleStep</c> trading days).</summary>
public sealed class Dataset
{
    public const double MaxMissingFeatureFraction = 0.3;

    private Dataset(MarketPanel panel, FeatureEngine features, PredictionTarget target, int horizon, IReadOnlyList<Sample> samples, DatasetSummary summary,
        IReadOnlyList<AssetSampleCount> perAsset)
    {
        PerAsset = perAsset;
        Panel = panel;
        Features = features;
        Target = target;
        Horizon = horizon;
        Samples = samples;
        Summary = summary;
    }

    public MarketPanel Panel { get; }
    public FeatureEngine Features { get; }
    public PredictionTarget Target { get; }
    public int Horizon { get; }
    public IReadOnlyList<Sample> Samples { get; }
    public DatasetSummary Summary { get; }

    /// <summary>Sample counts per asset, indexed like <see cref="MarketPanel.Symbols"/>.</summary>
    public IReadOnlyList<AssetSampleCount> PerAsset { get; }

    public static Dataset Build(MarketPanel panel, FeatureEngine features, PredictionTarget target, int horizon, int sampleStep, int firstDecisionIndex = 0)
    {
        if (horizon < TargetCatalog.MinimumHorizon(target)) throw new ArgumentException($"The horizon must be at least {TargetCatalog.MinimumHorizon(target)} days for {target}.");
        if (sampleStep < 1) throw new ArgumentException("The sample step must be at least one day.");
        var start = Math.Max(features.RequiredHistory, firstDecisionIndex);
        var decisionIndices = new List<int>();
        for (var t = start; t < panel.Length; t += sampleStep) decisionIndices.Add(t);
        if (decisionIndices.Count > 0 && decisionIndices[^1] != panel.Length - 1 && panel.Length - 1 >= start) decisionIndices.Add(panel.Length - 1);

        var samples = new List<Sample>();
        var dropped = 0;
        var droppedPerAsset = new int[panel.AssetCount];
        var samplesPerAsset = new int[panel.AssetCount];
        var labeledPerAsset = new int[panel.AssetCount];
        var usableAssets = new HashSet<int>();
        foreach (var t in decisionIndices)
        {
            for (var a = 0; a < panel.AssetCount; a++)
            {
                var x = features.Compute(a, t);
                if (x is null) continue;
                var missing = x.Count(double.IsNaN);
                if (missing > MaxMissingFeatureFraction * x.Length) { dropped++; droppedPerAsset[a]++; continue; }
                var label = LabelEngine.Compute(panel, a, t, target, horizon);
                var outcome = LabelEngine.ForwardExcessReturn(panel, a, t, horizon);
                samples.Add(new Sample(a, t, x, label, outcome, t + horizon));
                samplesPerAsset[a]++;
                if (!double.IsNaN(label)) labeledPerAsset[a]++;
                usableAssets.Add(a);
            }
        }
        var summary = new DatasetSummary(decisionIndices.Count, samples.Count, samples.Count(s => s.IsLabeled), dropped, panel.AssetCount - usableAssets.Count);
        var perAsset = Enumerable.Range(0, panel.AssetCount).Select(a => new AssetSampleCount(samplesPerAsset[a], labeledPerAsset[a], droppedPerAsset[a])).ToList();
        return new Dataset(panel, features, target, horizon, samples, summary, perAsset);
    }
}
