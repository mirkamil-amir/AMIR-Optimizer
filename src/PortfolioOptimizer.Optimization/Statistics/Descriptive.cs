namespace PortfolioOptimizer.Optimization.Statistics;

internal static class Descriptive
{
    public static double Mean(IReadOnlyList<double> x)
    {
        if (x.Count == 0) throw new ArgumentException("Empty series.", nameof(x));
        var s = 0d;
        for (var i = 0; i < x.Count; i++) s += x[i];
        return s / x.Count;
    }

    /// <summary>Sample variance with n-1 denominator.</summary>
    public static double SampleVariance(IReadOnlyList<double> x)
    {
        if (x.Count < 2) throw new ArgumentException("At least two observations are required.", nameof(x));
        var m = Mean(x);
        var s = 0d;
        for (var i = 0; i < x.Count; i++) s += (x[i] - m) * (x[i] - m);
        return s / (x.Count - 1);
    }

    public static double SampleCovariance(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count) throw new ArgumentException("Series lengths differ.");
        if (x.Count < 2) throw new ArgumentException("At least two observations are required.");
        var mx = Mean(x);
        var my = Mean(y);
        var s = 0d;
        for (var i = 0; i < x.Count; i++) s += (x[i] - mx) * (y[i] - my);
        return s / (x.Count - 1);
    }

    /// <summary>Empirical quantile with linear interpolation between order statistics (type 7).</summary>
    public static double Quantile(IReadOnlyList<double> x, double p)
    {
        if (x.Count == 0) throw new ArgumentException("Empty series.", nameof(x));
        var sorted = x.OrderBy(v => v).ToArray();
        var h = (sorted.Length - 1) * p;
        var lo = (int)Math.Floor(h);
        var hi = (int)Math.Ceiling(h);
        return sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
    }
}
