using PortfolioOptimizer.Optimization.Backtesting;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.UnitTests.Optimization;

public class BacktestTests
{
    private static readonly DateOnly Origin = new(2020, 1, 1);

    /// <summary>Deterministic business-day price series with sinusoidal noise.</summary>
    private static List<PricePoint> Series(double start, double drift, double amp, double phase, int days, DateOnly? from = null)
    {
        var list = new List<PricePoint>();
        var d = from ?? Origin;
        var price = start;
        while (list.Count < days)
        {
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                list.Add(new PricePoint(d, price));
                price *= 1 + drift + amp * Math.Sin(list.Count * 0.7 + phase);
            }
            d = d.AddDays(1);
        }
        return list;
    }

    private static BacktestInput Input(Func<string, List<PricePoint>, List<PricePoint>>? mutate = null)
    {
        var assets = new Dictionary<string, List<PricePoint>>
        {
            ["AAA"] = Series(100, 0.0006, 0.012, 0.0, 900),
            ["BBB"] = Series(50, 0.0003, 0.008, 1.3, 900),
            ["CCC"] = Series(20, 0.0008, 0.020, 2.1, 900),
        };
        if (mutate is not null) assets = assets.ToDictionary(kv => kv.Key, kv => mutate(kv.Key, kv.Value));
        return new BacktestInput(
            assets.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<PricePoint>)kv.Value),
            "BENCH",
            Series(300, 0.0004, 0.01, 0.5, 900));
    }

    private static BacktestSettings Settings => new()
    {
        Start = new DateOnly(2021, 3, 1),
        End = new DateOnly(2023, 6, 30),
        Frequency = RebalanceFrequency.Monthly,
        Objective = OptimizationObjective.MinimumVariance,
        LookbackDays = 120,
        MinimumObservations = 60,
        TransactionCostBps = 10,
        Constraints = new PortfolioConstraints { MaxWeight = 0.6 },
    };

    [Fact]
    public void Backtest_ProducesConsistentSeriesAndMetrics()
    {
        var result = new BacktestEngine().Run(Input(), Settings);

        Assert.Equal(result.Dates.Count, result.Values[BacktestEngine.Strategy].Count);
        Assert.Equal(100_000, result.Values[BacktestEngine.Strategy][0], 6);
        Assert.True(result.Rebalances.Count > 20);
        Assert.All(result.Rebalances, e => Assert.True(e.ExecutionDate > e.SignalDate));
        Assert.All(result.Rebalances.Where(e => e.OptimizationSucceeded),
            e => Assert.Equal(1.0, e.TargetWeights.Values.Sum(), 6));
        Assert.Contains(result.Metrics, m => m.Name.StartsWith("Benchmark"));
        Assert.True(result.Metrics.Single(m => m.Name == BacktestEngine.Strategy).TotalTransactionCosts > 0);
        Assert.True(result.Beta.IsAvailable);
    }

    [Fact]
    public void Backtest_HasNoLookAheadBias()
    {
        var settings = Settings;
        var baseline = new BacktestEngine().Run(Input(), settings);
        var cutoff = baseline.Rebalances[10].SignalDate;

        // Perturb every price strictly after the 11th signal date.
        var perturbed = new BacktestEngine().Run(
            Input((_, s) => s.Select(p => p.Date > cutoff ? p with { Price = p.Price * (1 + 0.3 * Math.Sin(p.Date.DayNumber)) } : p).ToList()),
            settings);

        for (var k = 0; k <= 10; k++)
        {
            var a = baseline.Rebalances[k].TargetWeights;
            var b = perturbed.Rebalances[k].TargetWeights;
            Assert.Equal(a.Keys.OrderBy(x => x), b.Keys.OrderBy(x => x));
            foreach (var key in a.Keys) Assert.Equal(a[key], b[key], 10);
        }

        // Values up to and including the signal date are unaffected.
        var idx = baseline.Dates.ToList().IndexOf(cutoff);
        for (var t = 0; t <= idx; t++)
            Assert.Equal(baseline.Values[BacktestEngine.Strategy][t], perturbed.Values[BacktestEngine.Strategy][t], 8);
    }

    [Fact]
    public void Backtest_ExcludesAssetsWithoutFullLookback()
    {
        var input = Input();
        var late = Series(10, 0.001, 0.01, 0.2, 400, new DateOnly(2021, 6, 1));
        var assets = input.AssetPrices.ToDictionary(kv => kv.Key, kv => kv.Value);
        assets["NEW"] = late;
        var result = new BacktestEngine().Run(input with { AssetPrices = assets }, Settings);

        var firstEligible = late[Settings.LookbackDays].Date;
        Assert.All(result.Rebalances.Where(e => e.SignalDate < firstEligible), e => Assert.DoesNotContain("NEW", e.EligibleSymbols));
        Assert.Contains(result.Rebalances, e => e.EligibleSymbols.Contains("NEW"));
    }

    [Fact]
    public void BuyAndHold_HasSingleAllocation()
    {
        var result = new BacktestEngine().Run(Input(), Settings with { Frequency = RebalanceFrequency.None });
        Assert.Single(result.Rebalances);
    }
}
