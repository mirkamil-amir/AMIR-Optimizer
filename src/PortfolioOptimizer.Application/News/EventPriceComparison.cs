using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.Application.News;

public readonly record struct DailyClose(DateOnly Date, double Price);

public sealed record EventHorizon(
    int TradingDays,
    DateOnly? Date,
    double? StockChange,
    double? BenchmarkChange,
    /// <summary>Stock change minus benchmark change over the same dates (simple market adjustment, not a model-based abnormal return).</summary>
    double? Difference,
    string? Limitation);

public sealed record EventComparisonResult(
    DateTime PublishedAtUtc,
    string PublishedLocal,
    string Timing,
    DateOnly? ReferenceDate,
    double? ReferencePrice,
    double? BenchmarkReferencePrice,
    IReadOnlyList<EventHorizon> Horizons,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Event and price-movement comparison on daily closes (pure). The reference observation is the last close whose
/// session ended before the publication instant (exchange time zone, regular session hours), so it cannot contain any
/// reaction. Horizons are counted in stored trading days of the stock after that reference; the benchmark is read on
/// exactly the same dates (a date the benchmark lacks gives no comparison rather than a substitute). This shows what
/// happened after publication; it cannot show that the article caused it.
/// </summary>
public static class EventPriceComparison
{
    public static readonly int[] DefaultHorizons = [1, 5, 20];

    public static EventComparisonResult Compute(DateTime publishedAtUtc, MarketVenue venue, IReadOnlyList<DailyClose> stock, IReadOnlyList<DailyClose> benchmark,
        IReadOnlyList<int>? horizons = null)
    {
        horizons ??= DefaultHorizons;
        var limitations = new List<string>
        {
            "Daily closes only: the move within the publication day cannot be isolated, and other news, market-wide moves or expectations may explain any change.",
        };
        var published = DateTime.SpecifyKind(publishedAtUtc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(published, Markets.TimeZone(venue));
        var localDate = DateOnly.FromDateTime(local);
        var (open, close) = Markets.Session(venue, localDate);
        var bars = stock.Where(b => b.Price > 0).OrderBy(b => b.Date).ToList();
        var tradedThatDay = bars.Any(b => b.Date == localDate);
        var time = TimeOnly.FromDateTime(local);
        var timing = !tradedThatDay ? "outside trading days (weekend, holiday or no stored price for that date)"
            : time < open ? "before the session opened"
            : time >= close ? "after the session closed"
            : "during the trading session";
        if (timing == "during the trading session")
            limitations.Add("Published during trading hours: the first close after publication also contains trading from before publication on that day.");

        var refIndex = bars.FindLastIndex(b => Markets.ToUtc(venue, b.Date, Markets.Session(venue, b.Date).Close) <= published);
        var benchmarkByDate = benchmark.Where(b => b.Price > 0).GroupBy(b => b.Date).ToDictionary(g => g.Key, g => g.Last().Price);
        if (refIndex < 0)
        {
            limitations.Add("No stored close before the publication time, so there is no pre-publication reference price.");
            return new EventComparisonResult(published, $"{local:yyyy-MM-dd HH:mm} {venue.Exchange} time", timing, null, null, null,
                horizons.Select(h => new EventHorizon(h, null, null, null, null, "No reference price.")).ToList(), limitations);
        }

        var reference = bars[refIndex];
        double? benchRef = benchmarkByDate.TryGetValue(reference.Date, out var br) ? br : null;
        if (benchRef is null) limitations.Add($"The benchmark has no stored close on the reference date {reference.Date:yyyy-MM-dd}; benchmark comparisons are omitted.");

        var results = new List<EventHorizon>();
        foreach (var h in horizons)
        {
            var i = refIndex + h;
            if (i >= bars.Count)
            {
                results.Add(new EventHorizon(h, null, null, null, null, $"Fewer than {h} stored trading days after the reference date."));
                continue;
            }
            var bar = bars[i];
            var stockChange = bar.Price / reference.Price - 1;
            double? benchChange = benchRef is { } b0 && benchmarkByDate.TryGetValue(bar.Date, out var b1) ? b1 / b0 - 1 : null;
            var note = benchRef is not null && benchChange is null ? $"The benchmark has no stored close on {bar.Date:yyyy-MM-dd}." : null;
            results.Add(new EventHorizon(h, bar.Date, stockChange, benchChange, benchChange is { } bc ? stockChange - bc : null, note));
        }
        return new EventComparisonResult(published, $"{local:yyyy-MM-dd HH:mm} {venue.Exchange} time", timing, reference.Date, reference.Price, benchRef, results, limitations);
    }
}
