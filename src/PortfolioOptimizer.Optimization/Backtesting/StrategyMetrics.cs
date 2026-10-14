using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Optimization.Backtesting;

/// <summary>Performance, risk, trading and concentration statistics of one simulated strategy.</summary>
public sealed record StrategyPerformance(
    string Name,
    double TotalReturn,
    MetricValue AnnualizedReturn,
    MetricValue AnnualizedVolatility,
    MetricValue SharpeRatio,
    MetricValue SortinoRatio,
    MetricValue MaxDrawdown,
    MetricValue HistoricalVaR95,
    MetricValue ExpectedShortfall95,
    double TotalTurnover,
    double TotalTransactionCosts,
    double TransactionCostsFraction,
    int Rebalances,
    double AverageHoldings,
    double AverageMaxWeight,
    double AverageEffectiveHoldings,
    MetricValue ExcessAnnualizedReturn,
    MetricValue Beta,
    MetricValue TrackingError,
    MetricValue InformationRatio);

/// <summary>Derived series and statistics computed identically for every strategy in a backtest or comparison.</summary>
public static class StrategyMetrics
{
    public static StrategyPerformance Compute(string name, IReadOnlyList<double> values, IReadOnlyList<RebalanceEvent> events, double riskFreeRate,
        IReadOnlyList<double>? benchmarkValues, AnalyticsConventions? conventions = null)
    {
        var metrics = new PerformanceMetrics(conventions ?? AnalyticsConventions.Default);
        var r = DailyReturns(values);
        var invested = events.Where(e => e.TargetWeights.Count > 0).ToList();
        double Avg(Func<RebalanceEvent, double> f) => invested.Count == 0 ? 0 : invested.Average(f);

        MetricValue excess, beta, te, ir;
        if (benchmarkValues is { Count: > 1 } b && b.Count == values.Count)
        {
            var rb = DailyReturns(b);
            var cagr = metrics.AnnualizedReturn(r);
            var benchCagr = metrics.AnnualizedReturn(rb);
            excess = cagr.IsAvailable && benchCagr.IsAvailable ? MetricValue.Of(cagr.Value!.Value - benchCagr.Value!.Value) : MetricValue.Unavailable("Annualized return unavailable.");
            beta = PerformanceMetrics.Beta(r, rb);
            te = metrics.TrackingError(r, rb);
            ir = metrics.InformationRatio(r, rb);
        }
        else
        {
            excess = beta = te = ir = MetricValue.Unavailable("No benchmark series over the same dates.");
        }

        return new StrategyPerformance(
            name,
            values.Count > 1 && values[0] > 0 ? values[^1] / values[0] - 1 : 0,
            metrics.AnnualizedReturn(r),
            metrics.AnnualizedVolatility(r),
            metrics.SharpeRatio(r, riskFreeRate),
            metrics.SortinoRatio(r, riskFreeRate),
            PerformanceMetrics.MaxDrawdown(values),
            r.Length >= 20 ? PerformanceMetrics.HistoricalVaR(r, 0.95) : MetricValue.Unavailable("Fewer than 20 daily returns."),
            r.Length >= 20 ? PerformanceMetrics.HistoricalCVaR(r, 0.95) : MetricValue.Unavailable("Fewer than 20 daily returns."),
            events.Sum(e => e.Turnover),
            events.Sum(e => e.TransactionCosts),
            values.Count > 0 && values[0] > 0 ? events.Sum(e => e.TransactionCosts) / values[0] : 0,
            invested.Count,
            Avg(e => e.TargetWeights.Count),
            Avg(e => e.TargetWeights.Values.Max()),
            Avg(e =>
            {
                var hhi = e.TargetWeights.Values.Sum(w => w * w);
                var sum = e.TargetWeights.Values.Sum();
                return hhi > 0 ? sum * sum / hhi : 0; // effective number of positions = (Σw)²/Σw²
            }),
            excess, beta, te, ir);
    }

    public static double[] DailyReturns(IReadOnlyList<double> values)
    {
        var r = new double[Math.Max(0, values.Count - 1)];
        for (var i = 1; i < values.Count; i++) r[i - 1] = values[i - 1] > 0 ? values[i] / values[i - 1] - 1 : 0;
        return r;
    }

    /// <summary>Annualized Sharpe ratio over a trailing window of daily returns (null until the window is filled or when volatility is zero).</summary>
    public static double?[] RollingSharpe(IReadOnlyList<double> values, int window, double riskFreeRate, AnalyticsConventions? conventions = null)
    {
        var c = conventions ?? AnalyticsConventions.Default;
        var r = DailyReturns(values);
        var result = new double?[values.Count];
        var rf = c.DailyRate(riskFreeRate);
        for (var t = window; t < values.Count; t++)
        {
            var slice = new double[window];
            Array.Copy(r, t - window, slice, 0, window);
            var mean = slice.Average();
            var sd = Math.Sqrt(slice.Sum(x => (x - mean) * (x - mean)) / (window - 1));
            result[t] = sd > 1e-14 ? (mean - rf) / sd * Math.Sqrt(c.TradingDaysPerYear) : null;
        }
        return result;
    }

    /// <summary>Calendar-month returns from a value series (first month measured from the first value).</summary>
    public static IReadOnlyList<(int Year, int Month, double Return)> MonthlyReturns(IReadOnlyList<DateOnly> dates, IReadOnlyList<double> values)
    {
        var result = new List<(int, int, double)>();
        var startIdx = 0;
        for (var t = 1; t <= dates.Count; t++)
        {
            if (t == dates.Count || dates[t].Month != dates[t - 1].Month)
            {
                var end = t - 1;
                var baseIdx = startIdx == 0 ? 0 : startIdx - 1;
                if (values[baseIdx] > 0) result.Add((dates[end].Year, dates[end].Month, values[end] / values[baseIdx] - 1));
                startIdx = t;
            }
        }
        return result;
    }

    /// <summary>Buy-and-hold value of one instrument from the first calendar date (null if it has no price on that date).</summary>
    public static double[]? BuyAndHold(PriceGrid grid, string symbol, double capital)
    {
        if (!grid.Prices.TryGetValue(symbol, out var map) || !map.TryGetValue(grid.Calendar[0], out var first)) return null;
        var units = capital / first;
        var last = first;
        var result = new double[grid.Calendar.Length];
        for (var t = 0; t < grid.Calendar.Length; t++)
        {
            if (map.TryGetValue(grid.Calendar[t], out var p)) last = p;
            result[t] = units * last;
        }
        return result;
    }
}
