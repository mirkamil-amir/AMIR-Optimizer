namespace PortfolioOptimizer.Application.MarketData;

/// <summary>Chart ranges. <see cref="OneDay"/> needs intraday bars, which no configured source stores; <see cref="Max"/> is all stored history.</summary>
public enum PriceRange { OneDay, OneMonth, ThreeMonths, SixMonths, OneYear, FiveYears, Max }

public readonly record struct HistoryBar(DateOnly Date, decimal Close, decimal? AdjustedClose);

/// <summary>Result of slicing stored daily bars to a range. <see cref="Change"/> is null whenever it cannot be computed honestly.</summary>
public sealed record PriceHistorySlice(
    PriceRange Range,
    bool Supported,
    bool UsesAdjustedClose,
    DateOnly? BaseDate,
    decimal? BasePrice,
    DateOnly? EndDate,
    decimal? EndPrice,
    double? Change,
    IReadOnlyList<(DateOnly Date, decimal Price)> Points,
    DateOnly? AvailableFrom,
    string? Limitation);

/// <summary>
/// Range slicing and period change on stored daily bars (pure, no I/O).
/// Conventions: the range ends at the last stored bar (not "today", so a stale series is never stretched); the base is
/// the last observation on or before <c>end − range</c> (calendar months/years, so weekends and holidays fall back to the
/// previous session); change = end / base − 1 on one series. The series is adjusted closes (splits and dividends, i.e.
/// total return) when every bar in the slice has one, otherwise raw closes – never a mix. Missing bars are not filled.
/// </summary>
public static class PriceHistory
{
    public static readonly IReadOnlyDictionary<string, PriceRange> Codes = new Dictionary<string, PriceRange>(StringComparer.OrdinalIgnoreCase)
    {
        ["1D"] = PriceRange.OneDay, ["1M"] = PriceRange.OneMonth, ["3M"] = PriceRange.ThreeMonths,
        ["6M"] = PriceRange.SixMonths, ["1Y"] = PriceRange.OneYear, ["5Y"] = PriceRange.FiveYears, ["MAX"] = PriceRange.Max,
    };

    public static string Code(PriceRange range) => Codes.First(kv => kv.Value == range).Key;

    public static DateOnly StartFor(PriceRange range, DateOnly end) => range switch
    {
        PriceRange.OneMonth => end.AddMonths(-1),
        PriceRange.ThreeMonths => end.AddMonths(-3),
        PriceRange.SixMonths => end.AddMonths(-6),
        PriceRange.OneYear => end.AddYears(-1),
        PriceRange.FiveYears => end.AddYears(-5),
        PriceRange.Max => DateOnly.MinValue,
        _ => end,
    };

    /// <param name="bars">Daily bars of one symbol and source, any order.</param>
    public static PriceHistorySlice Slice(IEnumerable<HistoryBar> bars, PriceRange range)
    {
        var ordered = bars.Where(b => b.Close > 0).OrderBy(b => b.Date).ToList();
        if (range == PriceRange.OneDay)
            return Empty(range, ordered, "Intraday prices are not stored: the configured providers supply daily bars and a delayed last quote only. Use 1M or longer.");
        if (ordered.Count == 0)
            return Empty(range, ordered, "No daily prices are stored for this stock yet. Request its history first.");

        var end = ordered[^1].Date;
        var target = StartFor(range, end);
        var baseIndex = range == PriceRange.Max ? 0 : ordered.FindLastIndex(b => b.Date <= target);
        if (baseIndex < 0)
            return Empty(range, ordered,
                $"Stored history starts on {ordered[0].Date:yyyy-MM-dd}; {Code(range)} needs a price on or before {target:yyyy-MM-dd}. Choose a shorter range or download more history.");

        var window = ordered.Skip(baseIndex).ToList();
        var adjusted = window.All(b => b.AdjustedClose is > 0);
        var points = window.Select(b => (b.Date, adjusted ? b.AdjustedClose!.Value : b.Close)).ToList();
        var basePrice = points[0].Item2;
        var endPrice = points[^1].Item2;
        return new PriceHistorySlice(range, true, adjusted, points[0].Date, basePrice, end, endPrice,
            (double)(endPrice / basePrice) - 1, points, ordered[0].Date,
            adjusted ? null : "Raw closes (the source supplied no adjusted closes): splits and dividends are not reflected in the change.");
    }

    private static PriceHistorySlice Empty(PriceRange range, List<HistoryBar> ordered, string reason) =>
        new(range, range != PriceRange.OneDay, false, null, null, ordered.Count > 0 ? ordered[^1].Date : null, null, null, [],
            ordered.Count > 0 ? ordered[0].Date : null, reason);
}
