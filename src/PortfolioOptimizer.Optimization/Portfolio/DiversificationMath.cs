using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Optimization.Portfolio;

public sealed record SectorWeight(string Sector, double Weight, int Holdings);

/// <summary>Weight concentration of the invested part (weights renormalized to sum to 1).</summary>
/// <param name="Hhi">Herfindahl–Hirschman index Σ wᵢ² of the normalized weights (1/N for equal weights, 1 for one stock).</param>
/// <param name="EffectiveHoldings">1 / HHI: the number of equally weighted positions with the same concentration.</param>
public sealed record ConcentrationProfile(
    int Holdings,
    double LargestWeight,
    string LargestSymbol,
    double TopThreeWeight,
    double Hhi,
    double EffectiveHoldings,
    IReadOnlyList<SectorWeight> Sectors);

/// <param name="Volatility">Ex-ante annualized volatility √(wᵀΣw).</param>
/// <param name="RiskShares">Share of variance per asset wᵢ(Σw)ᵢ / wᵀΣw (sums to 1; negative = diversifying).</param>
/// <param name="DiversificationRatio">Σ wᵢσᵢ / σ_p (1 = no diversification benefit).</param>
/// <param name="EffectiveRiskContributors">1 / Σ RCᵢ² over non-negative shares (null when a share is negative).</param>
public sealed record RiskProfile(
    double Volatility,
    double[] RiskShares,
    double DiversificationRatio,
    double? EffectiveRiskContributors,
    double? AverageCorrelation,
    (string A, string B, double Correlation)? HighestCorrelation);

/// <summary>Historical behaviour of fixed weights over a return window (daily rebalanced to the weights).</summary>
public sealed record HistoricalProfile(
    int Days,
    double[] DailyReturns,
    double CumulativeReturn,
    MetricValue AnnualizedVolatility,
    MetricValue MaxDrawdown,
    MetricValue ValueAtRisk95,
    MetricValue ExpectedShortfall95,
    double WorstDay,
    double? Worst20DayReturn);

/// <summary>Average pairwise correlation on all days vs. on the stress days (the worst decile of an equal-weight basket).</summary>
public sealed record StressCorrelation(double? AllDays, double? StressDays, int StressDayCount);

/// <summary>
/// Diversification and risk diagnostics of a weight vector (pure functions; inputs are not modified). Positions below
/// <see cref="HoldingThreshold"/> count as not held.
/// </summary>
public static class DiversificationMath
{
    public const double HoldingThreshold = 0.005;

    public static ConcentrationProfile Concentration(IReadOnlyList<string> symbols, IReadOnlyList<double> weights, IReadOnlyDictionary<string, string> sectors)
    {
        Validate(symbols, weights);
        var total = weights.Sum();
        if (total <= 0) throw new ArgumentException("Weights must sum to a positive value.", nameof(weights));
        var w = weights.Select(x => x / total).ToArray();
        var order = Enumerable.Range(0, w.Length).OrderByDescending(i => w[i]).ToArray();
        var hhi = w.Sum(x => x * x);
        var bySector = Enumerable.Range(0, w.Length).Where(i => w[i] >= HoldingThreshold)
            .GroupBy(i => sectors.TryGetValue(symbols[i], out var s) ? s : "Unclassified")
            .Select(g => new SectorWeight(g.Key, g.Sum(i => w[i]), g.Count()))
            .OrderByDescending(s => s.Weight).ToList();
        return new ConcentrationProfile(w.Count(x => x >= HoldingThreshold), w[order[0]], symbols[order[0]], order.Take(3).Sum(i => w[i]), hhi, 1 / hhi, bySector);
    }

    public static RiskProfile Risk(IReadOnlyList<string> symbols, IReadOnlyList<double> weights, double[,] covariance)
    {
        Validate(symbols, weights);
        var n = weights.Count;
        if (covariance.GetLength(0) != n || covariance.GetLength(1) != n) throw new ArgumentException("Covariance size does not match the weights.", nameof(covariance));
        var vol = PortfolioMath.Volatility(weights, covariance);
        var shares = PortfolioMath.RiskContributions(weights, covariance) ?? new double[n];
        var weightedVol = 0d;
        for (var i = 0; i < n; i++) weightedVol += weights[i] * Math.Sqrt(Math.Max(0, covariance[i, i]));
        double? effective = shares.All(s => s >= -1e-12) && shares.Sum(s => s * s) > 0 ? 1 / shares.Sum(s => s * s) : null;

        var corr = CovarianceEstimator.ToCorrelation(covariance);
        var held = Enumerable.Range(0, n).Where(i => weights[i] >= HoldingThreshold).ToArray();
        double? avg = null;
        (string, string, double)? highest = null;
        if (held.Length >= 2)
        {
            var sum = 0d;
            var count = 0;
            foreach (var i in held)
                foreach (var j in held.Where(j => j > i))
                {
                    sum += corr[i, j];
                    count++;
                    if (highest is null || corr[i, j] > highest.Value.Item3) highest = (symbols[i], symbols[j], corr[i, j]);
                }
            avg = sum / count;
        }
        return new RiskProfile(vol, shares, vol > 0 ? weightedVol / vol : 1, effective, avg, highest);
    }

    /// <summary>Daily returns rₚ = Σ wᵢ rᵢ of the allocation as given (a cash reserve, i.e. 1 − Σw, earns nothing).</summary>
    public static double[] PortfolioReturns(double[,] returns, IReadOnlyList<double> weights)
    {
        var t = returns.GetLength(0);
        if (returns.GetLength(1) != weights.Count) throw new ArgumentException("Return matrix width does not match the weights.", nameof(returns));
        var result = new double[t];
        for (var d = 0; d < t; d++)
        {
            var s = 0d;
            for (var j = 0; j < weights.Count; j++) s += weights[j] * returns[d, j];
            result[d] = s;
        }
        return result;
    }

    public static HistoricalProfile History(double[] dailyReturns, AnalyticsConventions conventions)
    {
        var wealth = ReturnCalculator.WealthIndex(dailyReturns);
        var metrics = new PerformanceMetrics(conventions);
        double? worst20 = null;
        for (var i = 20; i < wealth.Length; i++)
        {
            var r = wealth[i] / wealth[i - 20] - 1;
            if (worst20 is null || r < worst20) worst20 = r;
        }
        return new HistoricalProfile(dailyReturns.Length, dailyReturns, ReturnCalculator.CumulativeReturn(dailyReturns),
            metrics.AnnualizedVolatility(dailyReturns), PerformanceMetrics.MaxDrawdown(wealth),
            PerformanceMetrics.HistoricalVaR(dailyReturns, 0.95), PerformanceMetrics.HistoricalCVaR(dailyReturns, 0.95),
            dailyReturns.Length == 0 ? 0 : dailyReturns.Min(), worst20);
    }

    /// <summary>
    /// Average pairwise correlation of the given columns over all days and over the worst <paramref name="tailShare"/> of
    /// days of their equal-weight basket. Correlations computed on few stress days are noisy (≥ 20 required).
    /// </summary>
    public static StressCorrelation CorrelationUnderStress(double[,] returns, IReadOnlyList<int> columns, double tailShare = 0.1)
    {
        var t = returns.GetLength(0);
        if (columns.Count < 2 || t < 40) return new StressCorrelation(null, null, 0);
        var basket = new double[t];
        for (var d = 0; d < t; d++) basket[d] = columns.Average(c => returns[d, c]);
        var threshold = Descriptive.Quantile(basket, tailShare);
        var stressDays = Enumerable.Range(0, t).Where(d => basket[d] <= threshold).ToArray();
        return new StressCorrelation(AveragePairwise(returns, columns, Enumerable.Range(0, t).ToArray()),
            stressDays.Length >= 20 ? AveragePairwise(returns, columns, stressDays) : null, stressDays.Length);
    }

    private static double? AveragePairwise(double[,] returns, IReadOnlyList<int> columns, int[] days)
    {
        var sum = 0d;
        var count = 0;
        for (var a = 0; a < columns.Count; a++)
            for (var b = a + 1; b < columns.Count; b++)
            {
                var x = days.Select(d => returns[d, columns[a]]).ToArray();
                var y = days.Select(d => returns[d, columns[b]]).ToArray();
                var sx = Math.Sqrt(Descriptive.SampleVariance(x));
                var sy = Math.Sqrt(Descriptive.SampleVariance(y));
                if (sx < 1e-12 || sy < 1e-12) continue; // constant series: correlation undefined
                sum += Descriptive.SampleCovariance(x, y) / (sx * sy);
                count++;
            }
        return count == 0 ? null : sum / count;
    }

    private static void Validate(IReadOnlyList<string> symbols, IReadOnlyList<double> weights)
    {
        if (symbols.Count == 0 || symbols.Count != weights.Count) throw new ArgumentException("Symbols and weights must be non-empty and of equal length.");
        if (weights.Any(w => !double.IsFinite(w) || w < -1e-9)) throw new ArgumentException("Weights must be finite and non-negative (long-only).");
    }
}
