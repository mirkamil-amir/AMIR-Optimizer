using PortfolioOptimizer.Optimization.Backtesting;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.UnitTests.Optimization;

/// <summary>
/// Portfolios that mix markets with different holidays (formula reference F-BT-001, F-BT-002). Before 2026-10-28 an execution
/// day on which one exchange was closed (e.g. 1 May in Europe while the US trades) sold that market's positions at the
/// previous close and kept the proceeds in cash until the next rebalance; a signal date on which it was closed left its
/// stocks out of the optimization.
/// </summary>
public sealed class ExchangeCalendarTests
{
    private static List<PricePoint> Weekdays(DateOnly from, DateOnly to, double start, double dailyGrowth, ISet<DateOnly>? closed = null)
    {
        var list = new List<PricePoint>();
        var price = start;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            price *= 1 + dailyGrowth;
            if (closed?.Contains(d) != true) list.Add(new PricePoint(d, price));
        }
        return list;
    }

    private static PriceGrid Grid(ISet<DateOnly>? euClosed = null, DateOnly? euLast = null)
    {
        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2024, 3, 29);
        var series = new Dictionary<string, IReadOnlyList<PricePoint>>
        {
            ["EU"] = Weekdays(from, euLast ?? to, 50, 0.002, euClosed),
            ["US"] = Weekdays(from, to, 100, 0.0005),
        };
        return PriceGrid.Build(series, from, to);
    }

    /// <summary>Always 50/50 (grid symbols are ordinal: EU, US).</summary>
    private static PortfolioDecision Half(SignalContext ctx) => new([0.5, 0.5], ctx.Symbols, true, "50/50");

    private static RebalanceEvent After(SimulationResult r, DateOnly signal) => r.Events.Single(e => e.SignalDate == signal);

    [Fact]
    public void ExecutionOnAHoliday_WaitsForTheExchange_InsteadOfSellingIntoCash()
    {
        var grid = Grid(new HashSet<DateOnly> { new(2024, 2, 1) }); // EU closed on the execution day of the January signal
        var warnings = new List<string>();
        var r = PortfolioSimulator.Run(grid, grid.SignalIndices(RebalanceFrequency.Monthly), 100_000, 10, Half, warnings);

        var e = After(r, new DateOnly(2024, 1, 31));
        Assert.Equal(new DateOnly(2024, 2, 2), e.ExecutionDate);
        Assert.Equal(0.5, e.TargetWeights["EU"], 9);
        Assert.Equal(1.0, e.TargetWeights.Values.Sum(), 9);
        Assert.DoesNotContain(warnings, w => w.Contains("left in cash"));
    }

    [Fact]
    public void LongClosure_TradesAroundTheClosedExchange_KeepingItsPositionAndNeverBorrowing()
    {
        var closed = new HashSet<DateOnly>();
        for (var d = new DateOnly(2024, 2, 1); d <= new DateOnly(2024, 2, 9); d = d.AddDays(1)) closed.Add(d); // 7 trading days
        var grid = Grid(closed);
        var warnings = new List<string>();
        var r = PortfolioSimulator.Run(grid, grid.SignalIndices(RebalanceFrequency.Monthly), 100_000, 10, Half, warnings);

        var e = After(r, new DateOnly(2024, 1, 31));
        Assert.Equal(new DateOnly(2024, 2, 8), e.ExecutionDate); // waited MaxExecutionDelayDays = 5 trading days
        // EU kept its units: its weight is what it had drifted to (it grew faster than US), not the 50 % target and not 0.
        Assert.True(e.TargetWeights["EU"] > 0.5);
        Assert.True(e.TargetWeights["US"] < 0.5);
        Assert.InRange(e.TargetWeights.Values.Sum(), 0.99, 1 + 1e-9); // no negative cash
        Assert.Contains(warnings, w => w.Contains("EU could not be traded on 2024-02-08") && w.Contains("kept unchanged"));
        Assert.All(r.Values, v => Assert.True(double.IsFinite(v) && v > 0));
    }

    [Fact]
    public void StoppedPrices_AreTreatedAsNoLongerTrading_AndSoldAtTheLastPrice()
    {
        var grid = Grid(euLast: new DateOnly(2024, 1, 15));
        var warnings = new List<string>();
        var r = PortfolioSimulator.Run(grid, grid.SignalIndices(RebalanceFrequency.Monthly), 100_000, 10, Half, warnings);

        var e = After(r, new DateOnly(2024, 1, 31));
        Assert.Equal(new DateOnly(2024, 2, 1), e.ExecutionDate); // nothing to wait for
        Assert.False(e.TargetWeights.ContainsKey("EU"));
        Assert.Equal(0.5, e.TargetWeights["US"], 9);
        Assert.Contains(warnings, w => w.Contains("EU has no price within 10 days") && w.Contains("sold at its last price"));
    }

    [Fact]
    public void SignalOnAHoliday_KeepsTheClosedMarketInTheOptimization()
    {
        var from = new DateOnly(2023, 1, 2);
        var to = new DateOnly(2024, 6, 28);
        var holiday = new DateOnly(2024, 4, 30); // last weekday of April: EU closed, US trades → it is a signal date
        var input = new BacktestInput(new Dictionary<string, IReadOnlyList<PricePoint>>
        {
            ["EU"] = Weekdays(from, to, 50, 0.0004, new HashSet<DateOnly> { holiday }).Select((p, i) => p with { Price = p.Price * (1 + 0.01 * Math.Sin(i)) }).ToList(),
            ["US"] = Weekdays(from, to, 100, 0.0003).Select((p, i) => p with { Price = p.Price * (1 + 0.01 * Math.Cos(1.3 * i)) }).ToList(),
        }, null, null);
        var settings = new BacktestSettings
        {
            Start = new DateOnly(2024, 1, 2), End = to, Objective = OptimizationObjective.MinimumVariance, LookbackDays = 120,
            Constraints = new PortfolioConstraints { MaxWeight = 0.8 },
        };

        var result = new BacktestEngine().Run(input, settings);

        var e = result.Rebalances.Single(x => x.SignalDate == holiday);
        Assert.Contains("EU", e.EligibleSymbols);
        Assert.True(e.OptimizationSucceeded, e.Message);
        Assert.True(e.TargetWeights.GetValueOrDefault("EU") > 0.2); // with EU left out, the 80 % cap would have been infeasible
    }
}
