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

    public BacktestResult Run(BacktestInput input, BacktestSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.End <= settings.Start) throw new ArgumentException("End date must be after start date.");
        if (settings.InitialCapital <= 0) throw new ArgumentException("Initial capital must be positive.");
        if (input.AssetPrices.Count == 0) throw new ArgumentException("At least one asset is required.");

        var warnings = new List<string>();
        var symbols = input.AssetPrices.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var priceMaps = symbols.ToDictionary(s => s, s => input.AssetPrices[s]
            .Where(p => p.Price > 0 && double.IsFinite(p.Price))
            .GroupBy(p => p.Date).ToDictionary(g => g.Key, g => g.Last().Price));
        var sortedDates = symbols.ToDictionary(s => s, s => priceMaps[s].Keys.OrderBy(d => d).ToArray());

        var calendar = priceMaps.Values.SelectMany(m => m.Keys)
            .Where(d => d >= settings.Start && d <= settings.End)
            .Distinct().OrderBy(d => d).ToArray();
        if (calendar.Length < 3) throw new ArgumentException("Fewer than three trading days of price data in the backtest period.");

        var signalIndices = SignalIndices(calendar, settings.Frequency);

        var strategy = Simulate(symbols, priceMaps, sortedDates, calendar, signalIndices, settings, warnings, cancellationToken);
        var buyHold = Simulate(symbols, priceMaps, sortedDates, calendar, [0], settings, null, cancellationToken);
        var equal = Simulate(symbols, priceMaps, sortedDates, calendar, [0], settings with { Objective = OptimizationObjective.EqualWeight }, null, cancellationToken);

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

    private sealed record SimulationResult(double[] Values, List<RebalanceEvent> Events);

    private SimulationResult Simulate(
        string[] symbols,
        Dictionary<string, Dictionary<DateOnly, double>> priceMaps,
        Dictionary<string, DateOnly[]> sortedDates,
        DateOnly[] calendar,
        IReadOnlyList<int> signalIndices,
        BacktestSettings settings,
        List<string>? warnings,
        CancellationToken ct)
    {
        var n = symbols.Length;
        var units = new double[n];
        var lastPrice = new double?[n];
        var cash = settings.InitialCapital;
        var values = new double[calendar.Length];
        var events = new List<RebalanceEvent>();
        var signalSet = signalIndices.ToHashSet();
        (DateOnly Signal, double[]? Weights, List<string> Eligible, bool Ok, string Message)? pending = null;

        for (var t = 0; t < calendar.Length; t++)
        {
            ct.ThrowIfCancellationRequested();
            var date = calendar[t];
            for (var i = 0; i < n; i++)
            {
                if (priceMaps[symbols[i]].TryGetValue(date, out var p)) lastPrice[i] = p;
                else if (units[i] > 0 && warnings is not null)
                    warnings.Add($"{symbols[i]} has no price on {date:yyyy-MM-dd}; last available price carried forward for valuation.");
            }

            // 1) Execute orders decided at the previous signal date, at today's close.
            if (pending is { } order)
            {
                pending = null;
                var value = cash + Enumerable.Range(0, n).Sum(i => units[i] * (lastPrice[i] ?? 0));
                if (order.Weights is not null)
                {
                    var target = (double[])order.Weights.Clone();
                    for (var i = 0; i < n; i++)
                    {
                        if (target[i] > 0 && !priceMaps[symbols[i]].ContainsKey(date))
                        {
                            warnings?.Add($"{symbols[i]} could not be traded on {date:yyyy-MM-dd} (no price); its target weight was left in cash.");
                            target[i] = 0;
                        }
                    }

                    var currentW = Enumerable.Range(0, n).Select(i => value > 0 ? units[i] * (lastPrice[i] ?? 0) / value : 0).ToArray();
                    var bps = settings.TransactionCostBps / 10_000d;
                    // Costs reduce the investable value; two fixed-point passes are sufficient for bps-level costs.
                    var costs = 0d;
                    for (var pass = 0; pass < 3; pass++)
                    {
                        var net = value - costs;
                        costs = bps * Enumerable.Range(0, n).Sum(i => Math.Abs(net * target[i] - units[i] * (lastPrice[i] ?? 0)));
                    }
                    var investable = value - costs;
                    for (var i = 0; i < n; i++)
                        units[i] = target[i] > 0 ? investable * target[i] / lastPrice[i]!.Value : 0;
                    cash = investable - Enumerable.Range(0, n).Sum(i => units[i] * (lastPrice[i] ?? 0));
                    var newW = Enumerable.Range(0, n).Select(i => value > 0 ? units[i] * (lastPrice[i] ?? 0) / value : 0).ToArray();

                    events.Add(new RebalanceEvent(order.Signal, date,
                        Enumerable.Range(0, n).Where(i => target[i] > 0).ToDictionary(i => symbols[i], i => target[i]),
                        order.Eligible, PerformanceMetrics.Turnover(currentW, newW), costs, value, order.Ok, order.Message));
                }
                else
                {
                    events.Add(new RebalanceEvent(order.Signal, date, new Dictionary<string, double>(), order.Eligible, 0, 0, value, false, order.Message));
                }
            }

            // 2) Compute the signal at today's close with data dated ≤ today; execute tomorrow.
            if (signalSet.Contains(t) && t < calendar.Length - 1)
            {
                var (weights, eligible, ok, message) = Signal(symbols, priceMaps, sortedDates, date, settings, units, lastPrice, cash);
                pending = (date, weights, eligible, ok, message);
            }

            values[t] = cash + Enumerable.Range(0, n).Sum(i => units[i] * (lastPrice[i] ?? 0));
        }

        return new SimulationResult(values, events);
    }

    private (double[]? Weights, List<string> Eligible, bool Ok, string Message) Signal(
        string[] symbols,
        Dictionary<string, Dictionary<DateOnly, double>> priceMaps,
        Dictionary<string, DateOnly[]> sortedDates,
        DateOnly signalDate,
        BacktestSettings settings,
        double[] units,
        double?[] lastPrice,
        double cash)
    {
        var lookback = settings.LookbackDays;
        var eligible = new List<string>();
        var windows = new Dictionary<string, IReadOnlyList<PricePoint>>();
        foreach (var s in symbols)
        {
            var dates = sortedDates[s];
            var end = UpperBound(dates, signalDate); // count of dates ≤ signal
            if (end == 0 || dates[end - 1] != signalDate) continue; // must trade on the signal date
            if (end < lookback + 1) continue; // insufficient history: not yet investable
            var window = new List<PricePoint>(lookback + 1);
            for (var k = end - lookback - 1; k < end; k++) window.Add(new PricePoint(dates[k], priceMaps[s][dates[k]]));
            windows[s] = window;
            eligible.Add(s);
        }

        if (eligible.Count == 0)
            return (null, eligible, false, "No asset had a full lookback window; the portfolio stays in cash.");

        var matrix = ReturnMatrix.Build(windows, eligible);
        if (matrix.Observations < settings.MinimumObservations)
            return (null, eligible, false, $"Only {matrix.Observations} aligned observations (minimum {settings.MinimumObservations}); holdings unchanged.");

        var est = CovarianceEstimator.Estimate(matrix.Returns, settings.CovarianceMethod, conventions);
        var value = cash + Enumerable.Range(0, symbols.Length).Sum(i => units[i] * (lastPrice[i] ?? 0));
        var current = eligible.Select(s =>
        {
            var i = Array.IndexOf(symbols, s);
            return value > 0 ? units[i] * (lastPrice[i] ?? 0) / value : 0;
        }).ToArray();

        var result = optimizer.Optimize(new OptimizationInput(
            eligible, est.ExpectedReturns, est.Covariance, settings.Objective, settings.Constraints, settings.RiskFreeRate, current));
        if (!result.Succeeded)
            return (null, eligible, false, $"Optimization failed ({result.Status}): {result.Message} Holdings unchanged.");

        var full = new double[symbols.Length];
        for (var k = 0; k < eligible.Count; k++) full[Array.IndexOf(symbols, eligible[k])] = result.Weights![k];
        return (full, eligible, true, result.Diagnostics.Count > 0 ? string.Join(" ", result.Diagnostics) : "OK");
    }

    private static int[] SignalIndices(DateOnly[] calendar, RebalanceFrequency frequency)
    {
        var list = new List<int> { 0 };
        if (frequency == RebalanceFrequency.None) return [.. list];
        for (var t = 1; t < calendar.Length - 1; t++)
        {
            var d = calendar[t];
            var next = calendar[t + 1];
            var periodEnd = frequency switch
            {
                RebalanceFrequency.Monthly => d.Month != next.Month,
                RebalanceFrequency.Quarterly => (d.Month - 1) / 3 != (next.Month - 1) / 3 || d.Year != next.Year,
                _ => false,
            };
            if (periodEnd) list.Add(t);
        }
        return [.. list];
    }

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

    private static int UpperBound(DateOnly[] dates, DateOnly value)
    {
        int lo = 0, hi = dates.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (dates[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
