using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.UnitTests.Optimization;

public class MetricsTests
{
    private readonly PerformanceMetrics _m = new();

    [Fact]
    public void SimpleReturns_AreComputedFromConsecutivePrices()
    {
        var r = ReturnCalculator.SimpleReturns([100, 110, 99]);
        Assert.Equal(0.10, r[0], 12);
        Assert.Equal(-0.10, r[1], 12);
    }

    [Fact]
    public void LogReturns_SumToLogOfTotalGrowth()
    {
        var r = ReturnCalculator.LogReturns([100, 120, 90, 135]);
        Assert.Equal(Math.Log(1.35), r.Sum(), 12);
    }

    [Fact]
    public void SimpleReturns_RejectNonPositivePrices()
    {
        Assert.Throws<ArgumentException>(() => ReturnCalculator.SimpleReturns([100, 0, 50]));
    }

    [Fact]
    public void CumulativeReturn_Compounds()
    {
        Assert.Equal(1.1 * 0.9 - 1, ReturnCalculator.CumulativeReturn([0.10, -0.10]), 12);
    }

    [Fact]
    public void AnnualizedVolatility_UsesSampleStdAndSqrt252()
    {
        double[] r = [0.01, -0.01, 0.02, -0.02];
        var mean = r.Average();
        var var = r.Sum(x => (x - mean) * (x - mean)) / (r.Length - 1);
        Assert.Equal(Math.Sqrt(var * 252), _m.AnnualizedVolatility(r).Value!.Value, 12);
    }

    [Fact]
    public void AnnualizedReturn_IsGeometric()
    {
        // 252 days of constant 0.1% → (1.001)^252 − 1
        var r = Enumerable.Repeat(0.001, 252).ToArray();
        Assert.Equal(Math.Pow(1.001, 252) - 1, _m.AnnualizedReturn(r).Value!.Value, 10);
        Assert.Equal(0.001 * 252, _m.AnnualizedMeanReturn(r).Value!.Value, 12);
    }

    [Fact]
    public void Sharpe_MatchesManualCalculation()
    {
        double[] r = [0.01, -0.005, 0.007, 0.002, -0.003];
        const double rf = 0.0252; // 0.0001 per day
        var mean = r.Average();
        var sd = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / (r.Length - 1));
        var expected = (mean - 0.0001) / sd * Math.Sqrt(252);
        Assert.Equal(expected, _m.SharpeRatio(r, rf).Value!.Value, 10);
    }

    [Fact]
    public void Sharpe_WithZeroVolatility_IsUnavailable()
    {
        var s = _m.SharpeRatio([0.001, 0.001, 0.001], 0.02);
        Assert.False(s.IsAvailable);
        Assert.Contains("zero", s.UnavailableReason);
    }

    [Fact]
    public void Metrics_WithInsufficientHistory_AreUnavailable()
    {
        Assert.False(_m.AnnualizedVolatility([0.01]).IsAvailable);
        Assert.False(_m.SharpeRatio([], 0).IsAvailable);
        Assert.False(PerformanceMetrics.MaxDrawdown([100]).IsAvailable);
    }

    [Fact]
    public void Sortino_UsesDownsideDeviationOnly()
    {
        double[] r = [0.02, -0.01, 0.03, -0.02];
        var dd = Math.Sqrt((0.01 * 0.01 + 0.02 * 0.02) / 4);
        Assert.Equal(r.Average() / dd * Math.Sqrt(252), _m.SortinoRatio(r, 0).Value!.Value, 10);
    }

    [Fact]
    public void MaxDrawdown_FindsPeakToTrough()
    {
        var mdd = PerformanceMetrics.MaxDrawdown([100, 120, 90, 110, 80, 130]);
        Assert.Equal(80.0 / 120 - 1, mdd.Value!.Value, 12);
    }

    [Fact]
    public void HistoricalVaRAndCVaR_UseEmpiricalTail()
    {
        var r = Enumerable.Range(1, 100).Select(i => (i - 50) / 1000.0).ToArray(); // −0.049 … 0.050
        var var95 = PerformanceMetrics.HistoricalVaR(r, 0.95).Value!.Value;
        Assert.Equal(-(-0.049 + 0.05 * 99 / 1000.0), var95, 10); // type-7 quantile at p = 0.05
        var cvar = PerformanceMetrics.HistoricalCVaR(r, 0.95).Value!.Value;
        Assert.True(cvar > var95);
    }

    [Fact]
    public void VaR_WithTooFewObservations_IsUnavailable()
    {
        Assert.False(PerformanceMetrics.HistoricalVaR([0.01, 0.02], 0.99).IsAvailable);
    }

    [Fact]
    public void Beta_OfScaledBenchmark_IsScale()
    {
        double[] b = [0.01, -0.02, 0.015, 0.003, -0.007];
        var p = b.Select(x => 1.5 * x).ToArray();
        Assert.Equal(1.5, PerformanceMetrics.Beta(p, b).Value!.Value, 12);
        Assert.Equal(0, _m.TrackingError(b, b).Value!.Value, 12);
        Assert.False(_m.InformationRatio(b, b).IsAvailable);
    }

    [Fact]
    public void Turnover_IsHalfSumOfAbsoluteChanges()
    {
        Assert.Equal(0.3, PerformanceMetrics.Turnover([0.5, 0.5, 0], [0.2, 0.5, 0.3]), 12);
    }

    [Fact]
    public void SampleCovariance_MatchesManualCalculation()
    {
        var returns = new double[,] { { 0.01, 0.02 }, { -0.01, 0.00 }, { 0.03, 0.01 } };
        var est = CovarianceEstimator.Estimate(returns, CovarianceMethod.Sample, AnalyticsConventions.Default);
        double[] x = [0.01, -0.01, 0.03], y = [0.02, 0.00, 0.01];
        var mx = x.Average(); var my = y.Average();
        var cxy = x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum() / 2;
        Assert.Equal(cxy * 252, est.Covariance[0, 1], 12);
        Assert.Equal(est.Covariance[0, 1], est.Covariance[1, 0], 15);
        Assert.Equal(mx * 252, est.ExpectedReturns[0], 12);
        Assert.Equal(1.0, est.Correlation[0, 0]);
    }

    [Fact]
    public void LedoitWolf_ShrinkageIsBoundedAndKeepsMatrixPositiveDefinite()
    {
        var rng = new Random(42);
        var t = 30; var n = 10;
        var data = new double[t, n];
        for (var i = 0; i < t; i++) for (var j = 0; j < n; j++) data[i, j] = (rng.NextDouble() - 0.5) * 0.04;
        var est = CovarianceEstimator.Estimate(data, CovarianceMethod.LedoitWolf, AnalyticsConventions.Default);
        Assert.InRange(est.ShrinkageIntensity, 0, 1);
        var m = MathNet.Numerics.LinearAlgebra.Matrix<double>.Build.DenseOfArray(est.Covariance);
        Assert.All(m.Evd().EigenValues, e => Assert.True(e.Real > 0));
    }

    [Fact]
    public void ReturnMatrix_AlignsOnCommonDates()
    {
        var d = new DateOnly(2024, 1, 1);
        var series = new Dictionary<string, IReadOnlyList<PricePoint>>
        {
            ["A"] = [new(d, 100), new(d.AddDays(1), 101), new(d.AddDays(2), 102), new(d.AddDays(3), 103)],
            ["B"] = [new(d, 50), new(d.AddDays(2), 51), new(d.AddDays(3), 52)],
        };
        var m = ReturnMatrix.Build(series, ["A", "B"]);
        Assert.Equal(3, m.PriceDates.Count);
        Assert.Equal(2, m.Observations);
        Assert.Equal(102.0 / 100 - 1, m.Returns[0, 0], 12); // A return spans the missing day of B
        Assert.Equal(1, m.DroppedObservations["A"]);
    }
}
