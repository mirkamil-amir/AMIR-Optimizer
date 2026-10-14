using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;

namespace PortfolioOptimizer.MachineLearning.Explainability;

public sealed record PermutationImportanceResult(string Feature, double MeanLossIncrease, double StdDev);

/// <summary>
/// Model-agnostic permutation importance on held-out data: the increase in loss (MSE for regression, log loss for
/// classification) when one feature column is randomly shuffled, averaged over repeats. It measures how much the fitted
/// model relies on the feature for this data – an association, not a causal effect; correlated features can mask each other.
/// </summary>
public static class PermutationImportance
{
    public static IReadOnlyList<PermutationImportanceResult> Compute(ITrainedModel model, IReadOnlyList<double[]> features, IReadOnlyList<double> labels,
        int repeats, int seed, CancellationToken ct)
    {
        if (features.Count == 0) return [];
        var baseline = Loss(model.Task, model.Predict(features), labels);
        var random = new Random(seed);
        var m = model.FeatureNames.Count;
        var results = new List<PermutationImportanceResult>(m);
        for (var j = 0; j < m; j++)
        {
            ct.ThrowIfCancellationRequested();
            var increases = new double[repeats];
            for (var r = 0; r < repeats; r++)
            {
                var permutation = Enumerable.Range(0, features.Count).ToArray();
                random.Shuffle(permutation);
                var shuffled = features.Select((row, i) =>
                {
                    var copy = (double[])row.Clone();
                    copy[j] = features[permutation[i]][j];
                    return copy;
                }).ToList();
                increases[r] = Loss(model.Task, model.Predict(shuffled), labels) - baseline;
            }
            var mean = increases.Average();
            var sd = repeats > 1 ? Math.Sqrt(increases.Sum(x => (x - mean) * (x - mean)) / (repeats - 1)) : 0;
            results.Add(new PermutationImportanceResult(model.FeatureNames[j], mean, sd));
        }
        return results.OrderByDescending(r => r.MeanLossIncrease).ToList();
    }

    private static double Loss(TaskKind task, double[] predictions, IReadOnlyList<double> labels)
    {
        double sum = 0;
        for (var i = 0; i < predictions.Length; i++)
        {
            if (task == TaskKind.Regression)
            {
                var e = predictions[i] - labels[i];
                sum += e * e;
            }
            else
            {
                var p = Math.Clamp(predictions[i], 1e-12, 1 - 1e-12);
                sum -= labels[i] * Math.Log(p) + (1 - labels[i]) * Math.Log(1 - p);
            }
        }
        return sum / predictions.Length;
    }
}
