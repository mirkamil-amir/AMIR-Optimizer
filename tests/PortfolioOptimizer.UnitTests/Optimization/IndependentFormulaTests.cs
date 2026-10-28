using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Backtesting;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.UnitTests.Optimization;

/// <summary>
/// Formulas checked against independent re-implementations written from their published definitions or against values
/// computed by hand – never against the production code path itself. IDs refer to docs/quant/formula-reference.md.
/// </summary>
public sealed class IndependentFormulaTests
{
    private static readonly double[,] Returns =
    {
        { 0.010, -0.004, 0.002 }, { -0.006, 0.003, 0.001 }, { 0.004, 0.006, -0.003 }, { 0.012, -0.010, 0.004 },
        { -0.008, 0.002, -0.002 }, { 0.003, 0.004, 0.005 }, { -0.002, -0.007, 0.000 }, { 0.007, 0.001, -0.004 },
    };

    /// <summary>F-COV-002: Ledoit &amp; Wolf (2004), Lemma 3.2/3.3 and Theorem 3.1, with the paper's normalized Frobenius norm ‖A‖² = tr(AAᵀ)/p.</summary>
    [Fact]
    public void LedoitWolf_MatchesThePaperWithNormalizedNorm()
    {
        int t = Returns.GetLength(0), p = Returns.GetLength(1);
        var x = new double[t, p];
        for (var j = 0; j < p; j++)
        {
            var mean = Enumerable.Range(0, t).Average(i => Returns[i, j]);
            for (var i = 0; i < t; i++) x[i, j] = Returns[i, j] - mean;
        }
        var s = new double[p, p];
        for (var a = 0; a < p; a++) for (var b = 0; b < p; b++) for (var i = 0; i < t; i++) s[a, b] += x[i, a] * x[i, b] / t;
        double Norm2(Func<int, int, double> m) { var sum = 0d; for (var a = 0; a < p; a++) for (var b = 0; b < p; b++) sum += m(a, b) * m(a, b); return sum / p; }
        var mu = Enumerable.Range(0, p).Sum(a => s[a, a]) / p;
        var d2 = Norm2((a, b) => s[a, b] - (a == b ? mu : 0));
        var b2Bar = Enumerable.Range(0, t).Sum(k => Norm2((a, b) => x[k, a] * x[k, b] - s[a, b])) / ((double)t * t);
        var delta = Math.Min(b2Bar, d2) / d2;

        var est = CovarianceEstimator.Estimate(Returns, CovarianceMethod.LedoitWolf, AnalyticsConventions.Default);

        Assert.Equal(delta, est.ShrinkageIntensity, 12);
        for (var a = 0; a < p; a++)
        for (var b = 0; b < p; b++)
        {
            var expected = (delta * (a == b ? mu : 0) + (1 - delta) * s[a, b]) * t / (t - 1) * 252; // n−1 rescaling, × 252
            Assert.Equal(expected, est.Covariance[a, b], 14);
        }
    }

    /// <summary>F-COV-001 / F-COV-003: annualized sample covariance is symmetric and positive semi-definite; correlation has a unit diagonal.</summary>
    [Fact]
    public void SampleCovariance_IsSymmetricPsd_AndAnnualizedBy252()
    {
        var est = CovarianceEstimator.Estimate(Returns, CovarianceMethod.Sample, AnalyticsConventions.Default);
        var col0 = Enumerable.Range(0, 8).Select(i => Returns[i, 0]).ToArray();
        var m = col0.Average();
        Assert.Equal(col0.Sum(r => (r - m) * (r - m)) / 7 * 252, est.Covariance[0, 0], 14);
        Assert.Equal(m * 252, est.ExpectedReturns[0], 14);
        var rng = new Random(7);
        for (var k = 0; k < 200; k++)
        {
            var w = Enumerable.Range(0, 3).Select(_ => rng.NextDouble() * 2 - 1).ToArray();
            var q = 0d;
            for (var a = 0; a < 3; a++) for (var b = 0; b < 3; b++) q += w[a] * est.Covariance[a, b] * w[b];
            Assert.True(q >= -1e-15, $"wᵀΣw = {q}");
        }
        for (var a = 0; a < 3; a++)
        {
            Assert.Equal(1, est.Correlation[a, a]);
            for (var b = 0; b < 3; b++) Assert.Equal(est.Covariance[a, b], est.Covariance[b, a]);
        }
    }

    /// <summary>F-RISK-003: Euler allocation – Σᵢ wᵢ ∂σ/∂wᵢ = σ, so the risk shares sum to 1 and share × σ is each asset's volatility contribution.</summary>
    [Fact]
    public void RiskContributions_SatisfyEulerIdentity_ForNumericalGradient()
    {
        var sigma = new[,] { { 0.04, 0.006, 0.002 }, { 0.006, 0.09, -0.01 }, { 0.002, -0.01, 0.0225 } };
        double[] w = [0.3, 0.2, 0.5];
        var vol = PortfolioMath.Volatility(w, sigma);
        var shares = PortfolioMath.RiskContributions(w, sigma)!;
        for (var i = 0; i < 3; i++)
        {
            var up = (double[])w.Clone(); up[i] += 1e-7;
            var down = (double[])w.Clone(); down[i] -= 1e-7;
            var gradient = (PortfolioMath.Volatility(up, sigma) - PortfolioMath.Volatility(down, sigma)) / 2e-7;
            Assert.Equal(w[i] * gradient / vol, shares[i], 7);
        }
        Assert.Equal(1, shares.Sum(), 12);
    }

    /// <summary>F-PERF-007 / F-PERF-008: tracking error and information ratio by hand (period means × 252, sample sd × √252).</summary>
    [Fact]
    public void TrackingErrorAndInformationRatio_ByHand()
    {
        double[] p = [0.010, -0.002, 0.004, 0.006];
        double[] b = [0.008, 0.001, 0.002, 0.003];
        // Active returns 0.002, −0.003, 0.002, 0.003: mean 0.001, sample variance (0.000001+0.000016+0.000001+0.000004)/3.
        var sd = Math.Sqrt(0.000022 / 3);
        var metrics = new PerformanceMetrics();
        Assert.Equal(sd * Math.Sqrt(252), metrics.TrackingError(p, b).Value!.Value, 12);
        Assert.Equal(0.001 * 252 / (sd * Math.Sqrt(252)), metrics.InformationRatio(p, b).Value!.Value, 10);
    }

    /// <summary>F-PERF-004: Sharpe with a non-zero risk-free rate converts it to a daily rate (rf/252), not a compounded one.</summary>
    [Fact]
    public void Sharpe_UsesSimpleDailyRiskFreeRate()
    {
        double[] r = [0.01, -0.005, 0.007, 0.002, -0.001];
        var mean = r.Average();
        var sd = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / 4);
        Assert.Equal((mean - 0.05 / 252) / sd * Math.Sqrt(252), new PerformanceMetrics().SharpeRatio(r, 0.05).Value!.Value, 12);
    }

    /// <summary>F-RISK-001: empirical quantile type 7 (Hyndman &amp; Fan 1996) – h = (n−1)p, linear interpolation.</summary>
    [Fact]
    public void HistoricalVaR_UsesType7Quantile()
    {
        var r = Enumerable.Range(1, 40).Select(i => (i - 20) / 1000d).ToArray(); // −0.019 … 0.020
        // p = 0.05 → h = 39 × 0.05 = 1.95 → x(1) + 0.95 (x(2) − x(1)) with 0-based order statistics x(1) = −0.018, x(2) = −0.017.
        Assert.Equal(-(-0.018 + 0.95 * 0.001), PerformanceMetrics.HistoricalVaR(r, 0.95).Value!.Value, 12);
        // CVaR: mean of returns ≤ that quantile: −0.019 and −0.018.
        Assert.Equal(0.0185, PerformanceMetrics.HistoricalCVaR(r, 0.95).Value!.Value, 12);
    }

    /// <summary>F-OPT-003: with a cash reserve c, the maximum Sharpe weights maximize (μᵀw − r_f)/σ over Σw = 1 − c (checked against a dense search).</summary>
    [Fact]
    public void MaximumSharpe_WithCashReserve_BeatsEveryFeasibleMix()
    {
        string[] symbols = ["A", "B"];
        double[] mu = [0.08, 0.13];
        var sigma = new[,] { { 0.04, 0.012 }, { 0.012, 0.09 } };
        var input = new OptimizationInput(symbols, mu, sigma, OptimizationObjective.MaximumSharpe, new PortfolioConstraints { CashReserve = 0.1 }, 0.03);
        var r = new MeanVarianceOptimizer().Optimize(input);
        Assert.True(r.Succeeded, r.Message);
        Assert.Equal(0.9, r.Weights!.Sum(), 9);

        double Sharpe(double a) { double[] w = [a, 0.9 - a]; return (PortfolioMath.ExpectedReturn(w, mu) - 0.03) / PortfolioMath.Volatility(w, sigma); }
        var best = Enumerable.Range(0, 9001).Select(k => k / 10_000d).Max(Sharpe);
        Assert.True(r.Statistics!.SharpeRatio >= best - 1e-9, $"{r.Statistics.SharpeRatio} < {best}");
    }

    /// <summary>F-PERF-013: monthly return chains: Π(1 + monthly) − 1 equals the total return of the value series.</summary>
    [Fact]
    public void MonthlyReturns_ChainToTheTotalReturn()
    {
        var dates = new List<DateOnly>();
        var values = new List<double>();
        var v = 100d;
        for (var d = new DateOnly(2024, 1, 2); d <= new DateOnly(2024, 5, 31); d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            dates.Add(d);
            values.Add(v);
            v *= 1 + 0.003 * Math.Sin(d.DayNumber);
        }
        var monthly = StrategyMetrics.MonthlyReturns(dates, values);
        Assert.Equal(5, monthly.Count);
        Assert.Equal(values[^1] / values[0] - 1, monthly.Aggregate(1d, (g, m) => g * (1 + m.Return)) - 1, 12);
    }

    /// <summary>F-PERF-006 vs F-PERF-002: the arithmetic annualization (optimizer input) exceeds the geometric one for volatile series.</summary>
    [Fact]
    public void ArithmeticAnnualReturn_IsAtLeastTheGeometric()
    {
        double[] r = [0.05, -0.04, 0.03, -0.02, 0.04, -0.05];
        var m = new PerformanceMetrics();
        Assert.True(m.AnnualizedMeanReturn(r).Value > m.AnnualizedReturn(r).Value);
    }
}
