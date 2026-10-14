using PortfolioOptimizer.MachineLearning.Data;

namespace PortfolioOptimizer.MachineLearning.Labels;

public enum PredictionTarget
{
    /// <summary>Total return over the next h trading days.</summary>
    ForwardReturn,
    /// <summary>Total return over the next h trading days minus the benchmark's.</summary>
    ForwardExcessReturn,
    /// <summary>1 if the stock's next-h-day total return exceeds the benchmark's, else 0 (probability of outperformance).</summary>
    OutperformBenchmark,
    /// <summary>Annualized realized volatility of daily returns over the next h trading days.</summary>
    ForwardVolatility,
}

public enum TaskKind { Regression, BinaryClassification }

public static class TargetCatalog
{
    public static TaskKind TaskOf(PredictionTarget target) =>
        target == PredictionTarget.OutperformBenchmark ? TaskKind.BinaryClassification : TaskKind.Regression;

    /// <summary>Whether a higher prediction is more attractive (lower predicted volatility is preferred).</summary>
    public static bool HigherIsBetter(PredictionTarget target) => target != PredictionTarget.ForwardVolatility;

    public static int MinimumHorizon(PredictionTarget target) => target == PredictionTarget.ForwardVolatility ? 5 : 1;

    /// <summary>Precise label definition (decision at the close of day t; uses prices of days t+1 … t+h only).</summary>
    public static string Describe(PredictionTarget target, int horizon, string benchmark) => target switch
    {
        PredictionTarget.ForwardReturn =>
            $"y = P[t+{horizon}] / P[t] − 1: the stock's total return (adjusted prices) over the next {horizon} trading days.",
        PredictionTarget.ForwardExcessReturn =>
            $"y = (P[t+{horizon}] / P[t] − 1) − (B[t+{horizon}] / B[t] − 1): total return over the next {horizon} trading days minus {benchmark}'s.",
        PredictionTarget.OutperformBenchmark =>
            $"y = 1 if the stock's next-{horizon}-day total return exceeds {benchmark}'s next-{horizon}-day total return, otherwise 0. " +
            "The model estimates P(y = 1); a probability of outperformance is not an expected-return forecast.",
        PredictionTarget.ForwardVolatility =>
            $"y = annualized standard deviation (√252 scaling) of the stock's daily returns on days t+1 … t+{horizon}. Lower is preferred when ranking.",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
}

/// <summary>Label construction. A label for decision index t becomes known at index t+h (its availability index).</summary>
public static class LabelEngine
{
    /// <returns>The label, or NaN when the horizon extends beyond the data or a required price is missing.</returns>
    public static double Compute(MarketPanel panel, int asset, int t, PredictionTarget target, int horizon)
    {
        var end = t + horizon;
        if (end >= panel.Length) return double.NaN;
        var p = panel.Prices[asset];
        if (!Valid(p[t]) || !Valid(p[end])) return double.NaN;
        var stock = p[end] / p[t] - 1;
        var bench = panel.Benchmark[end] / panel.Benchmark[t] - 1;
        return target switch
        {
            PredictionTarget.ForwardReturn => stock,
            PredictionTarget.ForwardExcessReturn => stock - bench,
            PredictionTarget.OutperformBenchmark => stock > bench ? 1 : 0,
            PredictionTarget.ForwardVolatility => RealizedVolatility(p, t + 1, end),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    /// <summary>Forward excess return over the horizon (the investment outcome used by ranking metrics for every target).</summary>
    public static double ForwardExcessReturn(MarketPanel panel, int asset, int t, int horizon) =>
        Compute(panel, asset, t, PredictionTarget.ForwardExcessReturn, horizon);

    private static double RealizedVolatility(double[] p, int from, int to)
    {
        var returns = new List<double>();
        for (var i = from; i <= to; i++)
        {
            if (!Valid(p[i]) || !Valid(p[i - 1])) return double.NaN;
            returns.Add(p[i] / p[i - 1] - 1);
        }
        if (returns.Count < 2) return double.NaN;
        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        return Math.Sqrt(variance * 252);
    }

    private static bool Valid(double x) => !double.IsNaN(x) && x > 0;
}
