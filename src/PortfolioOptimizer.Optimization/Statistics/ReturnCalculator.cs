namespace PortfolioOptimizer.Optimization.Statistics;

public static class ReturnCalculator
{
    /// <summary>Simple returns r[t] = p[t] / p[t-1] - 1. Requires strictly positive prices.</summary>
    public static double[] SimpleReturns(IReadOnlyList<double> prices)
    {
        ValidatePrices(prices);
        var result = new double[Math.Max(0, prices.Count - 1)];
        for (var t = 1; t < prices.Count; t++)
            result[t - 1] = prices[t] / prices[t - 1] - 1d;
        return result;
    }

    /// <summary>Log returns ln(p[t] / p[t-1]).</summary>
    public static double[] LogReturns(IReadOnlyList<double> prices)
    {
        ValidatePrices(prices);
        var result = new double[Math.Max(0, prices.Count - 1)];
        for (var t = 1; t < prices.Count; t++)
            result[t - 1] = Math.Log(prices[t] / prices[t - 1]);
        return result;
    }

    /// <summary>Compounded cumulative return of a return series: prod(1 + r) - 1.</summary>
    public static double CumulativeReturn(IReadOnlyList<double> returns)
    {
        var growth = 1d;
        foreach (var r in returns) growth *= 1d + r;
        return growth - 1d;
    }

    /// <summary>Running wealth index starting at <paramref name="start"/>.</summary>
    public static double[] WealthIndex(IReadOnlyList<double> returns, double start = 1d)
    {
        var result = new double[returns.Count + 1];
        result[0] = start;
        for (var i = 0; i < returns.Count; i++) result[i + 1] = result[i] * (1d + returns[i]);
        return result;
    }

    private static void ValidatePrices(IReadOnlyList<double> prices)
    {
        for (var i = 0; i < prices.Count; i++)
        {
            if (!double.IsFinite(prices[i]) || prices[i] <= 0)
                throw new ArgumentException($"Price at index {i} is not a positive finite number ({prices[i]}).", nameof(prices));
        }
    }
}
