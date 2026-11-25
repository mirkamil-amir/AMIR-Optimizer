using System.Diagnostics;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;
using Xunit.Abstractions;

namespace PortfolioOptimizer.UnitTests.Optimization;

/// <summary>
/// Universes of realistic size (AMIR-44): the allocation-free Ledoit–Wolf sum against the direct day-by-day definition,
/// pinned results of a seeded 120-stock universe (maximum Sharpe and the efficient frontier, so a speed change cannot
/// silently move a result), and an opt-in timing benchmark (<c>PERF_BENCH=1</c>).
/// </summary>
public sealed class LargeUniverseTests(ITestOutputHelper output)
{
    /// <summary>Seeded one-factor daily returns: market β plus idiosyncratic noise and a per-stock drift.</summary>
    private static double[,] FactorReturns(int days, int stocks, int seed)
    {
        var rng = new Random(seed);
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var beta = Enumerable.Range(0, stocks).Select(_ => 0.5 + rng.NextDouble()).ToArray();
        var drift = Enumerable.Range(0, stocks).Select(_ => (rng.NextDouble() - 0.3) * 0.002).ToArray();
        var vol = Enumerable.Range(0, stocks).Select(_ => 0.008 + rng.NextDouble() * 0.02).ToArray();
        var r = new double[days, stocks];
        for (var t = 0; t < days; t++)
        {
            var market = 0.0004 + 0.01 * Normal();
            for (var i = 0; i < stocks; i++) r[t, i] = drift[i] + beta[i] * market + vol[i] * Normal();
        }
        return r;
    }

    private static OptimizationInput Input(double[,] returns, OptimizationObjective objective)
    {
        var est = CovarianceEstimator.Estimate(returns, CovarianceMethod.LedoitWolf, AnalyticsConventions.Default);
        var symbols = Enumerable.Range(0, returns.GetLength(1)).Select(i => $"S{i:000}").ToList();
        return new OptimizationInput(symbols, est.ExpectedReturns, est.Covariance, objective,
            new PortfolioConstraints { MaxWeight = 0.35, CashReserve = 0.01 }, 0.04);
    }

    [Fact]
    public void LedoitWolf_EqualsTheDirectDayByDaySum()
    {
        // Reference: b̄² = (1/t²) Σ_k ‖x_k x_kᵀ − S‖²_F built element by element per day (the textbook form), d² = ‖S − μI‖²_F.
        var returns = FactorReturns(300, 60, 7);
        int t = returns.GetLength(0), n = returns.GetLength(1);
        var means = Enumerable.Range(0, n).Select(j => Enumerable.Range(0, t).Average(k => returns[k, j])).ToArray();
        var x = new double[t, n];
        for (var k = 0; k < t; k++) for (var j = 0; j < n; j++) x[k, j] = returns[k, j] - means[j];
        var s = new double[n, n];
        for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) { var v = 0d; for (var k = 0; k < t; k++) v += x[k, i] * x[k, j]; s[i, j] = v / t; }
        var mu = Enumerable.Range(0, n).Sum(i => s[i, i]) / n;
        var d2 = 0d;
        for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) { var v = s[i, j] - (i == j ? mu : 0); d2 += v * v; }
        var b2 = 0d;
        for (var k = 0; k < t; k++) for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) { var v = x[k, i] * x[k, j] - s[i, j]; b2 += v * v; }
        b2 /= (double)t * t;
        var shrink = Math.Min(b2, d2) / d2;

        var est = CovarianceEstimator.Estimate(returns, CovarianceMethod.LedoitWolf, AnalyticsConventions.Default);
        Assert.Equal(shrink, est.ShrinkageIntensity, 12);
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                var expected = ((i == j ? mu : 0) * shrink + s[i, j] * (1 - shrink)) * t / (t - 1) * 252;
                Assert.Equal(expected, est.Covariance[i, j], 12);
            }
    }

    [Fact]
    public void Universe120_MaxSharpeAndFrontier_ArePinned_AndEveryPointIsValid()
    {
        var returns = FactorReturns(378, 120, 42);
        var optimizer = new MeanVarianceOptimizer();
        var input = Input(returns, OptimizationObjective.MaximumSharpe);

        var best = optimizer.Optimize(input);
        Assert.True(best.Succeeded, best.Message);
        // Pinned on 2026-11-25 (AMIR-44): a performance change must not move these beyond 1e-9.
        Assert.Equal(PinnedSharpe.ExpectedReturn, best.Statistics!.ExpectedReturn, 9);
        Assert.Equal(PinnedSharpe.Volatility, best.Statistics.Volatility, 9);

        var frontier = optimizer.EfficientFrontier(input with { Objective = OptimizationObjective.MinimumVariance }, 30);
        Assert.Equal(PinnedFrontier.Length, frontier.Count);
        for (var k = 0; k < frontier.Count; k++)
        {
            Assert.Equal(PinnedFrontier[k].Return, frontier[k].ExpectedReturn, 9);
            Assert.Equal(PinnedFrontier[k].Volatility, frontier[k].Volatility, 9);
            var check = AllocationValidator.Validate(input.Symbols, frontier[k].Weights, input.Constraints);
            Assert.True(check.IsValid, string.Join("; ", check.Violations));
        }
    }

    [Fact]
    public void Benchmark_Universe230_TimesEachStage()
    {
        if (Environment.GetEnvironmentVariable("PERF_BENCH") != "1") return; // opt-in timing, never a pass/fail threshold
        var returns = FactorReturns(378, 230, 11);
        var optimizer = new MeanVarianceOptimizer();
        var sw = Stopwatch.StartNew();
        var input = Input(returns, OptimizationObjective.MaximumSharpe);
        var estimate = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var best = optimizer.Optimize(input);
        var solve = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var frontier = optimizer.EfficientFrontier(input with { Objective = OptimizationObjective.MinimumVariance }, 30);
        var frontierMs = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"230 stocks × 378 days: estimate {estimate:F0} ms, max Sharpe {solve:F0} ms ({best.SolverIterations} iterations), frontier {frontierMs:F0} ms ({frontier.Count} points)");
    }

    private static readonly (double ExpectedReturn, double Volatility) PinnedSharpe = (0.8106104471125165, 0.13082123528076248);

    /// <summary>Expected return and volatility of each frontier point (identical with the code before AMIR-44).</summary>
    private static readonly (double Return, double Volatility)[] PinnedFrontier =
    [
        (0.25161979870276474, 0.09210039983573723),
        (0.2809214813291657, 0.09218435619985328),
        (0.31022316395556676, 0.09243501670551314),
        (0.3395248465819676, 0.0928540843358121),
        (0.3688265292083683, 0.09344385883513943),
        (0.3981282118347696, 0.09421869561739822),
        (0.42742989446117013, 0.09521143679609705),
        (0.4567315770875713, 0.09642829224051672),
        (0.486033259713972, 0.09787683138192711),
        (0.515334942340373, 0.09955325301634534),
        (0.5446366249667741, 0.10146015125145205),
        (0.573938307593175, 0.10360338553721733),
        (0.6032399902195756, 0.10597696108490146),
        (0.632541672845977, 0.10857500030196168),
        (0.6618433554723777, 0.11142130259597716),
        (0.6911450380987786, 0.11451496417425429),
        (0.7204467207251791, 0.11789143829595539),
        (0.7497484033515803, 0.12164022885906972),
        (0.7790500859779816, 0.1258094666759791),
        (0.8083517686043822, 0.13043980908519678),
        (0.8376534512307835, 0.135690301430405),
        (0.8669551338571843, 0.14168273791986757),
        (0.8962568164835846, 0.1487387173901515),
        (0.9255584991099858, 0.1572360688065718),
        (0.9548601817363869, 0.1671107819344641),
        (0.9841618643627879, 0.17832569279825694),
        (1.0134635469891886, 0.19069153107768572),
        (1.0427652296155898, 0.20471460313758122),
        (1.0720669122419904, 0.22274348689176973),
        (1.1013685948683913, 0.2567119994024678),
    ];
}
