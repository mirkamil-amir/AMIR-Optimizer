namespace PortfolioOptimizer.MachineLearning.Evaluation;

/// <summary>A scored observation: the model's prediction, the realized label and the realized investment outcome.</summary>
public readonly record struct ScoredObservation(int DecisionIndex, int Asset, double Prediction, double Label, double Outcome);

/// <summary>
/// Regression quality. <see cref="OutOfSampleR2"/> compares the squared error with a naive forecast equal to the
/// training-period mean (Campbell–Thompson); it is negative when the model is worse than that baseline.
/// </summary>
public sealed record RegressionMetrics(
    int Count,
    double Mae,
    double Rmse,
    double? OutOfSampleR2,
    double? PooledRankCorrelation,
    double? DirectionalAccuracy);

public sealed record CalibrationBin(double MeanPredicted, double ObservedFrequency, int Count);

/// <summary>Classification quality at the 0.5 threshold plus probability scoring and calibration.</summary>
public sealed record ClassificationMetrics(
    int Count,
    double BaseRate,
    double MeanPredicted,
    double? RocAuc,
    double Brier,
    double BaseRateBrier,
    double? BrierSkillScore,
    double LogLoss,
    double Accuracy,
    double? Precision,
    double? Recall,
    IReadOnlyList<CalibrationBin> Calibration);

/// <summary>
/// Cross-sectional ranking quality per decision date, using the realized forward excess return as the outcome for every
/// target (so models with different targets are judged on the same investment outcome). The mean IC uses every date;
/// its t-statistic uses only dates at least one horizon apart, because overlapping forward windows make consecutive ICs
/// dependent and would overstate significance.
/// </summary>
public sealed record RankingMetrics(
    int DatesEvaluated,
    int TopK,
    double? MeanInformationCoefficient,
    double? InformationCoefficientStdDev,
    double? InformationCoefficientTStat,
    int NonOverlappingDates,
    double? TopKMeanOutcome,
    double? UniverseMeanOutcome,
    double? TopKExcessOverUniverse,
    double? TopKHitRate,
    double? TopMinusBottomSpread,
    double? RankStability);

public static class MetricCalculator
{
    public static RegressionMetrics Regression(IReadOnlyList<ScoredObservation> obs, double trainingMean, bool directional)
    {
        if (obs.Count == 0) return new RegressionMetrics(0, double.NaN, double.NaN, null, null, null);
        double sae = 0, sse = 0, sseNaive = 0;
        var hits = 0;
        foreach (var o in obs)
        {
            var e = o.Prediction - o.Label;
            sae += Math.Abs(e);
            sse += e * e;
            var naive = trainingMean - o.Label;
            sseNaive += naive * naive;
            if (Math.Sign(o.Prediction) == Math.Sign(o.Label)) hits++;
        }
        return new RegressionMetrics(
            obs.Count,
            sae / obs.Count,
            Math.Sqrt(sse / obs.Count),
            sseNaive > 0 ? 1 - sse / sseNaive : null,
            Spearman(obs.Select(o => o.Prediction).ToArray(), obs.Select(o => o.Label).ToArray()),
            directional ? (double)hits / obs.Count : null);
    }

    public static ClassificationMetrics Classification(IReadOnlyList<ScoredObservation> obs, double trainingBaseRate)
    {
        var n = obs.Count;
        var baseRate = n == 0 ? double.NaN : obs.Average(o => o.Label);
        double brier = 0, brierBase = 0, logLoss = 0;
        int tp = 0, fp = 0, fn = 0, correct = 0;
        foreach (var o in obs)
        {
            var p = Math.Clamp(o.Prediction, 1e-12, 1 - 1e-12);
            brier += (o.Prediction - o.Label) * (o.Prediction - o.Label);
            brierBase += (trainingBaseRate - o.Label) * (trainingBaseRate - o.Label);
            logLoss -= o.Label * Math.Log(p) + (1 - o.Label) * Math.Log(1 - p);
            var predicted = o.Prediction >= 0.5 ? 1 : 0;
            if (predicted == (int)o.Label) correct++;
            if (predicted == 1 && o.Label == 1) tp++;
            if (predicted == 1 && o.Label == 0) fp++;
            if (predicted == 0 && o.Label == 1) fn++;
        }
        var calibration = obs.OrderBy(o => o.Prediction).Select((o, i) => (o, bin: (int)((long)i * 10 / Math.Max(1, n))))
            .GroupBy(x => x.bin)
            .Select(g => new CalibrationBin(g.Average(x => x.o.Prediction), g.Average(x => x.o.Label), g.Count()))
            .ToList();
        return new ClassificationMetrics(
            n,
            baseRate,
            n == 0 ? double.NaN : obs.Average(o => o.Prediction),
            RocAuc(obs),
            n == 0 ? double.NaN : brier / n,
            n == 0 ? double.NaN : brierBase / n,
            brierBase > 0 ? 1 - brier / brierBase : null,
            n == 0 ? double.NaN : logLoss / n,
            n == 0 ? double.NaN : (double)correct / n,
            tp + fp > 0 ? (double)tp / (tp + fp) : null,
            tp + fn > 0 ? (double)tp / (tp + fn) : null,
            calibration);
    }

    /// <summary>Per-date ranking metrics. <paramref name="higherIsBetter"/> = false inverts the score (e.g. predicted volatility).</summary>
    public static RankingMetrics Ranking(IReadOnlyList<ScoredObservation> obs, int topK, bool higherIsBetter, int horizon = 1)
    {
        var byDate = obs.Where(o => !double.IsNaN(o.Outcome)).GroupBy(o => o.DecisionIndex).OrderBy(g => g.Key)
            .Select(g => g.ToList()).Where(g => g.Count >= 4).ToList(); // at least two stocks in each of the top and bottom groups
        if (byDate.Count == 0)
            return new RankingMetrics(0, topK, null, null, null, 0, null, null, null, null, null, null);

        var ics = new List<(int Date, double Ic)>();
        var topMeans = new List<double>();
        var universeMeans = new List<double>();
        var bottomMeans = new List<double>();
        var hitRates = new List<double>();
        var stability = new List<double>();
        Dictionary<int, double>? previous = null;
        foreach (var day in byDate)
        {
            var score = day.Select(o => higherIsBetter ? o.Prediction : -o.Prediction).ToArray();
            var outcome = day.Select(o => o.Outcome).ToArray();
            if (Spearman(score, outcome) is { } ic) ics.Add((day[0].DecisionIndex, ic));
            var k = Math.Min(topK, day.Count / 2);
            var ordered = day.Zip(score).OrderByDescending(x => x.Second).ThenBy(x => x.First.Asset).Select(x => x.First).ToList();
            var top = ordered.Take(k).ToList();
            var bottom = ordered.TakeLast(k).ToList();
            var median = Median(outcome);
            topMeans.Add(top.Average(o => o.Outcome));
            bottomMeans.Add(bottom.Average(o => o.Outcome));
            universeMeans.Add(outcome.Average());
            hitRates.Add(top.Count(o => o.Outcome > median) / (double)top.Count);

            var current = day.Zip(score).ToDictionary(x => x.First.Asset, x => x.Second);
            if (previous is not null)
            {
                var common = current.Keys.Intersect(previous.Keys).ToArray();
                if (common.Length >= 3 && Spearman(common.Select(a => current[a]).ToArray(), common.Select(a => previous[a]).ToArray()) is { } s)
                    stability.Add(s);
            }
            previous = current;
        }

        double? meanIc = ics.Count > 0 ? ics.Average(x => x.Ic) : null;
        double? sdIc = ics.Count > 1 ? Math.Sqrt(ics.Sum(x => (x.Ic - meanIc!.Value) * (x.Ic - meanIc.Value)) / (ics.Count - 1)) : null;
        // t-statistic on dates spaced at least one horizon apart (independent forward windows).
        var spaced = new List<double>();
        int? lastDate = null;
        foreach (var (date, ic) in ics)
        {
            if (lastDate is { } previousDate && date - previousDate < horizon) continue;
            spaced.Add(ic);
            lastDate = date;
        }
        double? tStat = null;
        if (spaced.Count > 2)
        {
            var mean = spaced.Average();
            var sd = Math.Sqrt(spaced.Sum(x => (x - mean) * (x - mean)) / (spaced.Count - 1));
            tStat = sd > 0 ? mean / sd * Math.Sqrt(spaced.Count) : null;
        }
        return new RankingMetrics(
            byDate.Count,
            topK,
            meanIc,
            sdIc,
            tStat,
            spaced.Count,
            topMeans.Average(),
            universeMeans.Average(),
            topMeans.Average() - universeMeans.Average(),
            hitRates.Average(),
            topMeans.Average() - bottomMeans.Average(),
            stability.Count > 0 ? stability.Average() : null);
    }

    /// <summary>Spearman rank correlation with average ranks for ties; null when undefined (fewer than 3 values or constant input).</summary>
    public static double? Spearman(double[] x, double[] y)
    {
        if (x.Length != y.Length) throw new ArgumentException("Lengths differ.");
        if (x.Length < 3) return null;
        var rx = Ranks(x);
        var ry = Ranks(y);
        return Pearson(rx, ry);
    }

    public static double? Pearson(double[] x, double[] y)
    {
        var n = x.Length;
        var mx = x.Average();
        var my = y.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        return sxx <= 1e-18 || syy <= 1e-18 ? null : sxy / Math.Sqrt(sxx * syy);
    }

    public static double[] Ranks(double[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var ranks = new double[values.Length];
        var i0 = 0;
        while (i0 < order.Length)
        {
            var i1 = i0;
            while (i1 + 1 < order.Length && values[order[i1 + 1]] == values[order[i0]]) i1++;
            var avg = (i0 + i1) / 2.0 + 1;
            for (var k = i0; k <= i1; k++) ranks[order[k]] = avg;
            i0 = i1 + 1;
        }
        return ranks;
    }

    /// <summary>ROC-AUC via the Mann–Whitney statistic (ties count ½); null when only one class is present.</summary>
    public static double? RocAuc(IReadOnlyList<ScoredObservation> obs)
    {
        var positives = obs.Count(o => o.Label >= 0.5);
        var negatives = obs.Count - positives;
        if (positives == 0 || negatives == 0) return null;
        var ranks = Ranks(obs.Select(o => o.Prediction).ToArray());
        var sumPositiveRanks = obs.Select((o, i) => o.Label >= 0.5 ? ranks[i] : 0).Sum();
        return (sumPositiveRanks - positives * (positives + 1) / 2.0) / ((double)positives * negatives);
    }

    private static double Median(double[] v)
    {
        var s = v.OrderBy(x => x).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}
