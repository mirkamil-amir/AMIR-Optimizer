using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Optimization.Backtesting;

public enum RebalanceFrequency
{
    /// <summary>Allocate once at the start and hold.</summary>
    None,
    Monthly,
    Quarterly,
}

public sealed record BacktestSettings
{
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    public double InitialCapital { get; init; } = 100_000;
    public RebalanceFrequency Frequency { get; init; } = RebalanceFrequency.Monthly;
    public OptimizationObjective Objective { get; init; } = OptimizationObjective.MaximumSharpe;
    public PortfolioConstraints Constraints { get; init; } = new();
    /// <summary>Estimation window in trading days ending at (and including) the signal date.</summary>
    public int LookbackDays { get; init; } = 252;
    /// <summary>Minimum aligned return observations required for an asset set to be optimized.</summary>
    public int MinimumObservations { get; init; } = 60;
    public double TransactionCostBps { get; init; } = 5;
    public double RiskFreeRate { get; init; } = 0.04;
    public CovarianceMethod CovarianceMethod { get; init; } = CovarianceMethod.LedoitWolf;
}

public sealed record BacktestInput(
    IReadOnlyDictionary<string, IReadOnlyList<PricePoint>> AssetPrices,
    string? BenchmarkSymbol,
    IReadOnlyList<PricePoint>? BenchmarkPrices);

public sealed record SeriesPoint(DateOnly Date, double Value);

public sealed record RebalanceEvent(
    DateOnly SignalDate,
    DateOnly ExecutionDate,
    IReadOnlyDictionary<string, double> TargetWeights,
    IReadOnlyList<string> EligibleSymbols,
    double Turnover,
    double TransactionCosts,
    double PortfolioValue,
    bool OptimizationSucceeded,
    string Message);

public sealed record SeriesMetrics(
    string Name,
    double TotalReturn,
    MetricValue AnnualizedReturn,
    MetricValue AnnualizedVolatility,
    MetricValue SharpeRatio,
    MetricValue SortinoRatio,
    MetricValue MaxDrawdown,
    double TotalTurnover,
    double TotalTransactionCosts);

public sealed record PeriodReturn(int Year, int Month, double? Strategy, double? Benchmark);

public sealed record BacktestResult(
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Values,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Drawdowns,
    IReadOnlyList<RebalanceEvent> Rebalances,
    IReadOnlyList<SeriesMetrics> Metrics,
    IReadOnlyList<PeriodReturn> MonthlyReturns,
    MetricValue Beta,
    MetricValue TrackingError,
    MetricValue InformationRatio,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Event-driven daily backtest with explicit timing:
/// <list type="number">
/// <item>Signal: at the close of a rebalance date s, weights are estimated using only adjusted closes dated ≤ s.</item>
/// <item>Execution: orders are filled at the adjusted close of the next trading day e &gt; s, net of proportional costs.</item>
/// <item>Returns: the new holdings earn returns from e onwards.</item>
/// </list>
/// Assets enter the investable set only once they have a full lookback window; there is no back-filling of missing history.
/// Valuation uses adjusted closes (total return, dividends reinvested) and fractional units. Cash earns zero.
/// </summary>
public sealed class BacktestEngine(IPortfolioOptimizer optimizer, AnalyticsConventions conventions)
{
    public const string Strategy = "Strategy";
    public const string BuyAndHold = "Buy & hold (initial allocation)";
    public const string EqualWeight = "Equal weight (buy & hold)";

    public BacktestEngine() : this(new MeanVarianceOptimizer(), AnalyticsConventions.Default) { }

    /// <param name="strategyRule">
    /// Decision rule of the strategy when it is not this engine's mean-variance optimization (e.g. a walk-forward ML strategy).
    /// It decides at the same signal dates with the same execution, costs and valuation; the buy-and-hold baseline then holds
    /// its first allocation. The equal-weight baseline is unchanged.
    /// </param>
    public BacktestResult Run(BacktestInput input, BacktestSettings settings, CancellationToken cancellationToken = default,
        Func<SignalContext, PortfolioDecision>? strategyRule = null)
    {
        if (settings.End <= settings.Start) throw new ArgumentException("End date must be after start date.");
        if (settings.InitialCapital <= 0) throw new ArgumentException("Initial capital must be positive.");
        if (input.AssetPrices.Count == 0) throw new ArgumentException("At least one asset is required.");

        var warnings = new List<string>();
        var grid = PriceGrid.Build(input.AssetPrices, settings.Start, settings.End);
        var calendar = grid.Calendar;
        var signalIndices = grid.SignalIndices(settings.Frequency);

        var rule = strategyRule ?? (ctx => Signal(grid, ctx, settings));
        var strategy = PortfolioSimulator.Run(grid, signalIndices, settings.InitialCapital, settings.TransactionCostBps, rule, warnings, cancellationToken);
        var buyHold = PortfolioSimulator.Run(grid, [0], settings.InitialCapital, settings.TransactionCostBps, rule, null, cancellationToken);
        var equalSettings = settings with { Objective = OptimizationObjective.EqualWeight };
        var equal = PortfolioSimulator.Run(grid, [0], settings.InitialCapital, settings.TransactionCostBps,
            ctx => Signal(grid, ctx, equalSettings), null, cancellationToken);

        var values = new Dictionary<string, IReadOnlyList<double>>
        {
            [Strategy] = strategy.Values,
            [BuyAndHold] = buyHold.Values,
            [EqualWeight] = equal.Values,
        };
        var turnover = new Dictionary<string, (double Turnover, double Costs)>
        {
            [Strategy] = (strategy.Events.Sum(e => e.Turnover), strategy.Events.Sum(e => e.TransactionCosts)),
            [BuyAndHold] = (buyHold.Events.Sum(e => e.Turnover), buyHold.Events.Sum(e => e.TransactionCosts)),
            [EqualWeight] = (equal.Events.Sum(e => e.Turnover), equal.Events.Sum(e => e.TransactionCosts)),
        };

        double[]? bench = null;
        if (input.BenchmarkPrices is { Count: > 0 } bp)
        {
            bench = BenchmarkSeries(bp, calendar, settings.InitialCapital, warnings, input.BenchmarkSymbol ?? "Benchmark");
            if (bench is not null)
            {
                values[$"Benchmark ({input.BenchmarkSymbol})"] = bench;
                turnover[$"Benchmark ({input.BenchmarkSymbol})"] = (0, 0);
            }
        }

        var metrics = new PerformanceMetrics(conventions);
        var seriesMetrics = values.Select(kv =>
        {
            var r = DailyReturns(kv.Value);
            return new SeriesMetrics(
                kv.Key,
                kv.Value[^1] / kv.Value[0] - 1,
                metrics.AnnualizedReturn(r),
                metrics.AnnualizedVolatility(r),
                metrics.SharpeRatio(r, settings.RiskFreeRate),
                metrics.SortinoRatio(r, settings.RiskFreeRate),
                PerformanceMetrics.MaxDrawdown(kv.Value),
                turnover[kv.Key].Turnover,
                turnover[kv.Key].Costs);
        }).ToList();

        var stratReturns = DailyReturns(strategy.Values);
        MetricValue beta, te, ir;
        if (bench is not null)
        {
            var benchReturns = DailyReturns(bench);
            beta = PerformanceMetrics.Beta(stratReturns, benchReturns);
            te = metrics.TrackingError(stratReturns, benchReturns);
            ir = metrics.InformationRatio(stratReturns, benchReturns);
        }
        else
        {
            beta = te = ir = MetricValue.Unavailable("No benchmark data.");
        }

        return new BacktestResult(
            calendar,
            values,
            values.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)PerformanceMetrics.DrawdownSeries(kv.Value)),
            strategy.Events,
            seriesMetrics,
            MonthlyReturns(calendar, strategy.Values, bench),
            beta, te, ir,
            warnings.Distinct().ToList());
    }

    /// <summary>Mean-variance signal: estimates on the trailing lookback window ending at the signal date, then optimizes.</summary>
    private PortfolioDecision Signal(PriceGrid grid, SignalContext ctx, BacktestSettings settings, ISet<string>? allowed = null)
    {
        var symbols = grid.Symbols;
        var lookback = settings.LookbackDays;
        var eligible = new List<string>();
        var windows = new Dictionary<string, IReadOnlyList<PricePoint>>();
        foreach (var s in symbols)
        {
            if (allowed is not null && !allowed.Contains(s)) continue;
            var dates = grid.SortedDates[s];
            var end = grid.CountUpTo(s, ctx.Date); // count of dates ≤ signal
            if (end == 0 || dates[end - 1] != ctx.Date) continue; // must trade on the signal date
            if (end < lookback + 1) continue; // insufficient history: not yet investable
            var window = new List<PricePoint>(lookback + 1);
            for (var k = end - lookback - 1; k < end; k++) window.Add(new PricePoint(dates[k], grid.Prices[s][dates[k]]));
            windows[s] = window;
            eligible.Add(s);
        }

        if (eligible.Count == 0)
            return PortfolioDecision.Hold(eligible, "No asset had a full lookback window; the portfolio stays in cash.");

        var matrix = ReturnMatrix.Build(windows, eligible);
        if (matrix.Observations < settings.MinimumObservations)
            return PortfolioDecision.Hold(eligible, $"Only {matrix.Observations} aligned observations (minimum {settings.MinimumObservations}); holdings unchanged.");

        var est = CovarianceEstimator.Estimate(matrix.Returns, settings.CovarianceMethod, conventions);
        var current = eligible.Select(s => ctx.CurrentWeights[Array.IndexOf(symbols, s)]).ToArray();

        var result = optimizer.Optimize(new OptimizationInput(
            eligible, est.ExpectedReturns, est.Covariance, settings.Objective, settings.Constraints, settings.RiskFreeRate, current));
        if (!result.Succeeded)
            return PortfolioDecision.Hold(eligible, $"Optimization failed ({result.Status}): {result.Message} Holdings unchanged.");

        var full = new double[symbols.Length];
        for (var k = 0; k < eligible.Count; k++) full[Array.IndexOf(symbols, eligible[k])] = result.Weights![k];
        return new PortfolioDecision(full, eligible, true, result.Diagnostics.Count > 0 ? string.Join(" ", result.Diagnostics) : "OK");
    }

    /// <summary>The mean-variance decision rule at one signal date (used by the strategy comparison engine).</summary>
    public Func<SignalContext, PortfolioDecision> CreateDecisionRule(PriceGrid grid, BacktestSettings settings) => ctx => Signal(grid, ctx, settings);

    /// <summary>The mean-variance rule restricted to symbols chosen at each signal date (e.g. a pre-screened set); null = no restriction.</summary>
    public Func<SignalContext, PortfolioDecision> CreateDecisionRule(PriceGrid grid, BacktestSettings settings, Func<SignalContext, ISet<string>?> allowed) =>
        ctx =>
        {
            var set = allowed(ctx); // evaluated once: the screen may be stateful
            return set is { Count: 0 }
                ? PortfolioDecision.Hold([], "The screen selected no stocks; holdings unchanged.")
                : Signal(grid, ctx, settings, set);
        };

    private static double[]? BenchmarkSeries(IReadOnlyList<PricePoint> prices, DateOnly[] calendar, double capital, List<string> warnings, string name)
    {
        var map = prices.Where(p => p.Price > 0).GroupBy(p => p.Date).ToDictionary(g => g.Key, g => g.Last().Price);
        if (!map.TryGetValue(calendar[0], out var first))
        {
            warnings.Add($"Benchmark {name} has no price on the first backtest date; benchmark comparison omitted.");
            return null;
        }
        var units = capital / first;
        var last = first;
        var result = new double[calendar.Length];
        for (var t = 0; t < calendar.Length; t++)
        {
            if (map.TryGetValue(calendar[t], out var p)) last = p;
            result[t] = units * last;
        }
        return result;
    }

    private static List<PeriodReturn> MonthlyReturns(DateOnly[] dates, double[] strategy, double[]? bench)
    {
        var result = new List<PeriodReturn>();
        var startIdx = 0;
        for (var t = 1; t <= dates.Length; t++)
        {
            if (t == dates.Length || dates[t].Month != dates[t - 1].Month)
            {
                var end = t - 1;
                var baseIdx = startIdx == 0 ? 0 : startIdx - 1; // previous month-end close
                double? s = strategy[baseIdx] > 0 ? strategy[end] / strategy[baseIdx] - 1 : null;
                double? b = bench is not null && bench[baseIdx] > 0 ? bench[end] / bench[baseIdx] - 1 : null;
                result.Add(new PeriodReturn(dates[end].Year, dates[end].Month, s, b));
                startIdx = t;
            }
        }
        return result;
    }

    private static double[] DailyReturns(IReadOnlyList<double> values)
    {
        var r = new double[Math.Max(0, values.Count - 1)];
        for (var i = 1; i < values.Count; i++) r[i - 1] = values[i - 1] > 0 ? values[i] / values[i - 1] - 1 : 0;
        return r;
    }
}
