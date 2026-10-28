using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Optimization.Backtesting;

/// <summary>Aligned daily prices of a set of symbols over a backtest window.</summary>
public sealed class PriceGrid
{
    private PriceGrid(string[] symbols, DateOnly[] calendar, Dictionary<string, Dictionary<DateOnly, double>> prices, Dictionary<string, DateOnly[]> dates)
    {
        Symbols = symbols;
        Calendar = calendar;
        Prices = prices;
        SortedDates = dates;
    }

    /// <summary>Symbols in ordinal order (stable indices for weight vectors).</summary>
    public string[] Symbols { get; }

    /// <summary>Trading days in [start, end]: the union of all symbols' observation dates in the window.</summary>
    public DateOnly[] Calendar { get; }

    /// <summary>All valid (positive, finite) prices per symbol, including history before the window (for lookbacks).</summary>
    public Dictionary<string, Dictionary<DateOnly, double>> Prices { get; }

    public Dictionary<string, DateOnly[]> SortedDates { get; }

    public static PriceGrid Build(IReadOnlyDictionary<string, IReadOnlyList<PricePoint>> assetPrices, DateOnly start, DateOnly end)
    {
        if (end <= start) throw new ArgumentException("End date must be after start date.");
        if (assetPrices.Count == 0) throw new ArgumentException("At least one asset is required.");
        var symbols = assetPrices.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var prices = symbols.ToDictionary(s => s, s => assetPrices[s]
            .Where(p => p.Price > 0 && double.IsFinite(p.Price))
            .GroupBy(p => p.Date).ToDictionary(g => g.Key, g => g.Last().Price));
        var dates = symbols.ToDictionary(s => s, s => prices[s].Keys.OrderBy(d => d).ToArray());
        var calendar = prices.Values.SelectMany(m => m.Keys).Where(d => d >= start && d <= end).Distinct().OrderBy(d => d).ToArray();
        if (calendar.Length < 3) throw new ArgumentException("Fewer than three trading days of price data in the backtest period.");
        return new PriceGrid(symbols, calendar, prices, dates);
    }

    /// <summary>
    /// A symbol whose latest price is more than this many calendar days old counts as no longer trading (suspended, delisted
    /// or before its listing) rather than closed for a holiday. Covers a week-long exchange closure (Golden Week, Lunar New Year).
    /// </summary>
    public const int StaleAfterDays = 10;

    /// <summary>Signal dates: the first trading day, then the last trading day of each month/quarter (never the final day).</summary>
    public int[] SignalIndices(RebalanceFrequency frequency)
    {
        var list = new List<int> { 0 };
        if (frequency == RebalanceFrequency.None) return [.. list];
        for (var t = 1; t < Calendar.Length - 1; t++)
        {
            var d = Calendar[t];
            var next = Calendar[t + 1];
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

    /// <summary>Number of a symbol's observations dated on or before <paramref name="date"/>.</summary>
    public int CountUpTo(string symbol, DateOnly date)
    {
        var dates = SortedDates[symbol];
        int lo = 0, hi = dates.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (dates[mid] <= date) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

/// <summary>State visible to a strategy at the close of a signal date. Only information dated ≤ <see cref="Date"/> may be used.</summary>
public sealed record SignalContext(int Index, DateOnly Date, IReadOnlyList<string> Symbols, double[] CurrentWeights, double PortfolioValue);

/// <summary>A strategy's target allocation (fractions of portfolio value aligned with the grid's symbols; the rest is cash).</summary>
public sealed record PortfolioDecision(double[]? Weights, IReadOnlyList<string> EligibleSymbols, bool Succeeded, string Message)
{
    public static PortfolioDecision Hold(IReadOnlyList<string> eligible, string message) => new(null, eligible, false, message);
}

public sealed record SimulationResult(double[] Values, IReadOnlyList<RebalanceEvent> Events);

/// <summary>
/// Engine-agnostic event-driven portfolio accounting shared by every strategy (classical optimizer, ML, equal weight,
/// benchmark) so that comparisons use identical conventions:
/// <list type="number">
/// <item>Signal at the close of a signal date (the strategy sees only data dated ≤ that date).</item>
/// <item>Orders fill at the next trading day's close net of proportional costs (bps of traded value). An exchange can be
/// closed on that day while others trade (holidays differ between markets): the rebalance then waits for the first day on
/// which every asset to be bought, sold or kept has a price, at most <see cref="MaxExecutionDelayDays"/> trading days.
/// After that, assets that are still closed keep their units (nothing can be traded on a closed exchange) and the other
/// assets are traded towards their targets within the remaining value. An asset whose latest price is more than
/// <see cref="PriceGrid.StaleAfterDays"/> days old counts as no longer trading: it is not bought, and a held position is
/// sold at its last price.</item>
/// <item>Holdings earn returns from the execution day; cash earns zero; fractional units; missing prices are carried
/// forward for valuation only.</item>
/// </list>
/// </summary>
public static class PortfolioSimulator
{
    /// <summary>Trading days an execution may wait for closed exchanges to reopen before it trades around them.</summary>
    public const int MaxExecutionDelayDays = 5;

    public static SimulationResult Run(
        PriceGrid grid,
        IReadOnlyList<int> signalIndices,
        double initialCapital,
        double transactionCostBps,
        Func<SignalContext, PortfolioDecision> decide,
        List<string>? warnings = null,
        CancellationToken ct = default)
    {
        if (initialCapital <= 0) throw new ArgumentException("Initial capital must be positive.");
        var symbols = grid.Symbols;
        var calendar = grid.Calendar;
        var n = symbols.Length;
        var units = new double[n];
        var lastPrice = new double?[n];
        var lastDate = new DateOnly?[n];
        var cash = initialCapital;
        var values = new double[calendar.Length];
        var events = new List<RebalanceEvent>();
        var signalSet = signalIndices.ToHashSet();
        (DateOnly Signal, PortfolioDecision Decision, int Delay)? pending = null;

        for (var t = 0; t < calendar.Length; t++)
        {
            ct.ThrowIfCancellationRequested();
            var date = calendar[t];
            var tradable = new bool[n];
            for (var i = 0; i < n; i++)
            {
                if (grid.Prices[symbols[i]].TryGetValue(date, out var p)) { lastPrice[i] = p; lastDate[i] = date; tradable[i] = true; }
                else if (units[i] > 0) warnings?.Add($"{symbols[i]} has no price on {date:yyyy-MM-dd}; last available price carried forward for valuation.");
            }
            // No price today but a recent one: its exchange is closed today (a holiday), the asset still trades.
            bool Closed(int i) => !tradable[i] && lastDate[i] is { } d && date.DayNumber - d.DayNumber <= PriceGrid.StaleAfterDays;

            // 1) Execute the decision taken at the previous signal date, at today's close.
            if (pending is { } order)
            {
                pending = null;
                var value = Value(cash, units, lastPrice);
                if (order.Decision.Weights is { } weights)
                {
                    var involved = Enumerable.Range(0, n).Where(i => weights[i] > 0 || units[i] > 0).ToList();
                    if (involved.Any(Closed) && order.Delay < MaxExecutionDelayDays && t < calendar.Length - 1)
                    {
                        pending = order with { Delay = order.Delay + 1 }; // wait for the closed exchanges to reopen
                    }
                    else
                    {
                        var target = (double[])weights.Clone();
                        var frozen = new bool[n];
                        for (var i = 0; i < n; i++)
                        {
                            if (tradable[i]) continue;
                            if (Closed(i))
                            {
                                frozen[i] = units[i] > 0;
                                if (frozen[i] || target[i] > 0)
                                    warnings?.Add($"{symbols[i]} could not be traded on {date:yyyy-MM-dd} (its exchange was closed); " +
                                                  (frozen[i] ? "the position was kept unchanged." : "its target weight was left in cash."));
                            }
                            else if (target[i] > 0 || units[i] > 0)
                            {
                                warnings?.Add($"{symbols[i]} has no price within {PriceGrid.StaleAfterDays} days of {date:yyyy-MM-dd} (no longer trading); " +
                                              "it was not bought and a held position was sold at its last price.");
                            }
                            target[i] = 0;
                        }

                        var currentW = Weights(units, lastPrice, value);
                        var frozenValue = Enumerable.Range(0, n).Where(i => frozen[i]).Sum(i => units[i] * lastPrice[i]!.Value);
                        var targetSum = target.Sum();
                        var bps = transactionCostBps / 10_000d;
                        // Costs reduce the investable value; kept positions take their share of it first, and the traded targets are
                        // scaled down if they no longer fit. A few fixed-point passes suffice for bps-level costs.
                        var costs = 0d;
                        var scale = 1d;
                        for (var pass = 0; pass < 3; pass++)
                        {
                            var net = value - costs;
                            scale = targetSum > 0 ? Math.Clamp((net - frozenValue) / (net * targetSum), 0, 1) : 1;
                            costs = bps * Enumerable.Range(0, n).Where(i => !frozen[i])
                                .Sum(i => Math.Abs(scale * net * target[i] - units[i] * (lastPrice[i] ?? 0)));
                        }
                        var investable = value - costs;
                        if (targetSum > 0) scale = Math.Clamp((investable - frozenValue) / (investable * targetSum), 0, 1); // cash never negative
                        for (var i = 0; i < n; i++)
                            if (!frozen[i]) units[i] = target[i] > 0 ? scale * investable * target[i] / lastPrice[i]!.Value : 0;
                        cash = investable - Enumerable.Range(0, n).Sum(i => units[i] * (lastPrice[i] ?? 0));
                        var newW = Weights(units, lastPrice, value);

                        events.Add(new RebalanceEvent(order.Signal, date,
                            Enumerable.Range(0, n).Where(i => target[i] > 0 || frozen[i])
                                .ToDictionary(i => symbols[i], i => frozen[i] ? units[i] * lastPrice[i]!.Value / investable : scale * target[i]),
                            order.Decision.EligibleSymbols, PerformanceMetrics.Turnover(currentW, newW), costs, value, order.Decision.Succeeded, order.Decision.Message));
                    }
                }
                else
                {
                    events.Add(new RebalanceEvent(order.Signal, date, new Dictionary<string, double>(), order.Decision.EligibleSymbols, 0, 0, value,
                        false, order.Decision.Message));
                }
            }

            // 2) Decide at today's close with data dated ≤ today; execute tomorrow.
            if (signalSet.Contains(t) && t < calendar.Length - 1)
            {
                var value = Value(cash, units, lastPrice);
                var decision = decide(new SignalContext(t, date, symbols, Weights(units, lastPrice, value), value));
                if (decision.Weights is { } w)
                {
                    if (w.Length != n) throw new InvalidOperationException($"Decision has {w.Length} weights for {n} symbols.");
                    if (w.Any(x => x < -1e-12 || !double.IsFinite(x)) || w.Sum() > 1 + 1e-6)
                        throw new InvalidOperationException("Decision weights must be finite, non-negative and sum to at most 1.");
                }
                if (pending is { } superseded)
                    warnings?.Add($"The rebalance of {superseded.Signal:yyyy-MM-dd} was still waiting for closed exchanges and was replaced by the one of {date:yyyy-MM-dd}.");
                pending = (date, decision, 0);
            }

            values[t] = Value(cash, units, lastPrice);
        }

        return new SimulationResult(values, events);
    }

    private static double Value(double cash, double[] units, double?[] lastPrice) =>
        cash + Enumerable.Range(0, units.Length).Sum(i => units[i] * (lastPrice[i] ?? 0));

    private static double[] Weights(double[] units, double?[] lastPrice, double value)
    {
        return Enumerable.Range(0, units.Length).Select(i => value > 0 ? units[i] * (lastPrice[i] ?? 0) / value : 0).ToArray();
    }
}
