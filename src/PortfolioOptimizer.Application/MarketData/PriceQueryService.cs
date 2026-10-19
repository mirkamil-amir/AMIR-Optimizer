using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.MarketData;

public sealed record PriceSeries(string Symbol, int AssetId, IReadOnlyList<PricePoint> Points, bool UsesAdjustedClose, bool IsSimulated);

public readonly record struct DailyBar(DateOnly Date, double Price, long? Volume);

/// <summary>Daily observations of one symbol; <see cref="LastRetrievedAtUtc"/> identifies the data vintage used.</summary>
public sealed record DailySeries(string Symbol, string? Sector, IReadOnlyList<DailyBar> Bars, bool UsesAdjustedClose, bool IsSimulated, DateTime? LastRetrievedAtUtc);

public sealed record LatestPrice(int AssetId, string Symbol, decimal Price, DateOnly PriceDate, DateTime AsOfUtc, string Origin, bool IsStale, bool IsSimulated);

/// <summary>
/// Read access to persisted prices for the active data source. Analytics use adjusted closes (total return) where
/// available; trading uses raw closes / last quotes. Sources are never mixed within one series.
/// </summary>
public sealed class PriceQueryService(IApplicationDbContext db, TimeProvider clock)
{
    public async Task<ApplicationSettings> SettingsAsync(CancellationToken ct) =>
        await db.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);

    /// <summary>
    /// Ids of the instruments that have stored daily prices from <paramref name="source"/> – one grouped query. Use it instead of
    /// a per-instrument <c>EXISTS</c> subquery: PostgreSQL may plan that as one full table scan per instrument (observed: 2.4 s
    /// for 912 instruments on 55k prices, versus 9 ms for this query).
    /// </summary>
    public async Task<HashSet<int>> AssetIdsWithPricesAsync(string source, CancellationToken ct) =>
        (await db.HistoricalPrices.AsNoTracking().Where(p => p.Source == source).GroupBy(p => p.AssetId).Select(g => g.Key).ToListAsync(ct)).ToHashSet();

    /// <summary>Adjusted-close series (falls back to raw close only if the source never provides adjusted data).</summary>
    /// <remarks>One query for all symbols; with <paramref name="lastN"/> the last N observations per symbol (window function).</remarks>
    public async Task<Dictionary<string, PriceSeries>> GetSeriesAsync(
        IReadOnlyCollection<string> symbols, string source, DateOnly? from, DateOnly? to, CancellationToken ct, int? lastN = null)
    {
        var result = new Dictionary<string, PriceSeries>(StringComparer.OrdinalIgnoreCase);
        var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).Select(a => new { a.Id, a.Symbol }).ToListAsync(ct);
        if (assets.Count == 0) return result;
        var ids = assets.Select(a => a.Id).ToList();
        var lo = from ?? DateOnly.MinValue;
        var hi = to ?? DateOnly.MaxValue;
        // Last N per asset with an index lookup per asset; otherwise one range query for all assets.
        var q = lastN is { } n
            ? db.LatestPricesPerAsset(ids, source, n, lo, hi)
            : db.HistoricalPrices.AsNoTracking().Where(p => ids.Contains(p.AssetId) && p.Source == source && p.Date >= lo && p.Date <= hi);
        var rows = (await q.Select(p => new { p.AssetId, p.Date, p.Close, p.AdjustedClose, p.IsSimulated }).ToListAsync(ct))
            .ToLookup(r => r.AssetId);
        foreach (var asset in assets)
        {
            var own = rows[asset.Id].OrderBy(r => r.Date).ToList();
            var adjusted = own.Count > 0 && own.All(r => r.AdjustedClose.HasValue);
            var points = own.Select(r => new PricePoint(r.Date, (double)(adjusted ? r.AdjustedClose!.Value : r.Close))).ToList();
            result[asset.Symbol] = new PriceSeries(asset.Symbol, asset.Id, points, adjusted, own.Any(r => r.IsSimulated));
        }
        return result;
    }

    /// <summary>
    /// Daily adjusted closes with volume (for ML features). Falls back to raw closes for a symbol only when the source never
    /// provides adjusted data for it; one series never mixes adjusted and raw prices. One query for all symbols.
    /// </summary>
    public async Task<Dictionary<string, DailySeries>> GetDailyObservationsAsync(
        IReadOnlyCollection<string> symbols, string source, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new Dictionary<string, DailySeries>(StringComparer.OrdinalIgnoreCase);
        var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).Select(a => new { a.Id, a.Symbol, a.Sector }).ToListAsync(ct);
        if (assets.Count == 0) return result;
        var ids = assets.Select(a => a.Id).ToList();
        var rows = (await db.HistoricalPrices.AsNoTracking()
                .Where(p => ids.Contains(p.AssetId) && p.Source == source && p.Date >= from && p.Date <= to)
                .Select(p => new { p.AssetId, p.Date, p.Close, p.AdjustedClose, p.Volume, p.IsSimulated, p.RetrievedAtUtc })
                .ToListAsync(ct))
            .ToLookup(r => r.AssetId);
        foreach (var asset in assets)
        {
            var own = rows[asset.Id].OrderBy(r => r.Date).ToList();
            var adjusted = own.Count > 0 && own.All(r => r.AdjustedClose.HasValue);
            result[asset.Symbol] = new DailySeries(asset.Symbol, asset.Sector,
                own.Select(r => new DailyBar(r.Date, (double)(adjusted ? r.AdjustedClose!.Value : r.Close), r.Volume)).ToList(),
                adjusted, own.Any(r => r.IsSimulated), own.Count == 0 ? null : own.Max(r => r.RetrievedAtUtc));
        }
        return result;
    }

    /// <summary>Raw-close series (for valuation of the paper portfolio, matching execution prices).</summary>
    public async Task<Dictionary<int, SortedList<DateOnly, decimal>>> GetRawClosesAsync(
        IReadOnlyCollection<int> assetIds, string source, DateOnly from, CancellationToken ct)
    {
        var rows = await db.HistoricalPrices.AsNoTracking()
            .Where(p => assetIds.Contains(p.AssetId) && p.Source == source && p.Date >= from)
            .Select(p => new { p.AssetId, p.Date, p.Close })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.AssetId).ToDictionary(
            g => g.Key,
            g => new SortedList<DateOnly, decimal>(g.ToDictionary(r => r.Date, r => r.Close)));
    }

    /// <summary>
    /// Latest tradable price per asset: the more recent of the last stored quote and the last raw daily close.
    /// </summary>
    public async Task<Dictionary<int, LatestPrice>> GetLatestPricesAsync(IReadOnlyCollection<int> assetIds, string source, int staleDays, CancellationToken ct)
    {
        var result = new Dictionary<int, LatestPrice>();
        if (assetIds.Count == 0) return result;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).Select(a => new { a.Id, a.Symbol }).ToDictionaryAsync(a => a.Id, ct);
        var quotes = await db.LatestQuotes.AsNoTracking().Where(q => assetIds.Contains(q.AssetId) && q.Source == source).ToListAsync(ct);

        // The last stored close of every asset in one statement (index lookup per asset), instead of one query per asset.
        var closes = (await db.LatestPricesPerAsset(assetIds, source, 1, DateOnly.MinValue, DateOnly.MaxValue)
                .Select(p => new { p.AssetId, p.Date, p.Close, p.RetrievedAtUtc, p.IsSimulated })
                .ToListAsync(ct))
            .ToDictionary(c => c.AssetId);

        foreach (var id in assetIds)
        {
            if (!assets.TryGetValue(id, out var asset)) continue;
            var close = closes.GetValueOrDefault(id);
            var quote = quotes.FirstOrDefault(q => q.AssetId == id);

            LatestPrice? lp = null;
            if (close is not null)
                lp = new LatestPrice(id, asset.Symbol, close.Close, close.Date, close.Date.ToDateTime(new TimeOnly(21, 0), DateTimeKind.Utc), "Daily close", false, close.IsSimulated);
            if (quote is not null && (lp is null || DateOnly.FromDateTime(quote.AsOfUtc) > lp.PriceDate))
                lp = new LatestPrice(id, asset.Symbol, quote.Price, DateOnly.FromDateTime(quote.AsOfUtc), quote.AsOfUtc, "Last quote", false, quote.IsSimulated);
            if (lp is null) continue;

            var stale = BusinessDaysBetween(lp.PriceDate, today) > staleDays;
            result[id] = lp with { IsStale = stale };
        }
        return result;
    }

    public static int BusinessDaysBetween(DateOnly from, DateOnly to)
    {
        if (to <= from) return 0;
        var days = 0;
        for (var d = from.AddDays(1); d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) days++;
        return days;
    }
}
