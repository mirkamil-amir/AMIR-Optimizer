namespace PortfolioOptimizer.Optimization.Portfolio;

public static class PortfolioMath
{
    /// <summary>Portfolio expected return wᵀμ (cash earns zero in the optimizer's convention).</summary>
    public static double ExpectedReturn(IReadOnlyList<double> w, IReadOnlyList<double> mu)
    {
        var s = 0d;
        for (var i = 0; i < w.Count; i++) s += w[i] * mu[i];
        return s;
    }

    /// <summary>Portfolio variance wᵀΣw.</summary>
    public static double Variance(IReadOnlyList<double> w, double[,] sigma)
    {
        var n = w.Count;
        var s = 0d;
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            s += w[i] * sigma[i, j] * w[j];
        return Math.Max(0d, s);
    }

    public static double Volatility(IReadOnlyList<double> w, double[,] sigma) => Math.Sqrt(Variance(w, sigma));

    public static double? Sharpe(double expectedReturn, double volatility, double riskFreeRate) =>
        volatility > 1e-12 ? (expectedReturn - riskFreeRate) / volatility : null;

    public static PortfolioStatistics Statistics(IReadOnlyList<double> w, IReadOnlyList<double> mu, double[,] sigma, double rf)
    {
        var er = ExpectedReturn(w, mu);
        var vol = Volatility(w, sigma);
        return new PortfolioStatistics(er, vol, Sharpe(er, vol, rf));
    }

    /// <summary>
    /// Percentage contribution of each asset to portfolio volatility: RCᵢ = wᵢ(Σw)ᵢ / (wᵀΣw). Sums to 1.
    /// Returns null if portfolio variance is zero.
    /// </summary>
    public static double[]? RiskContributions(IReadOnlyList<double> w, double[,] sigma)
    {
        var n = w.Count;
        var variance = Variance(w, sigma);
        if (variance < 1e-16) return null;
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            var marginal = 0d;
            for (var j = 0; j < n; j++) marginal += sigma[i, j] * w[j];
            result[i] = w[i] * marginal / variance;
        }
        return result;
    }
}
