using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.MarketData;

public sealed record PriceSeries(string Symbol, int AssetId, IReadOnlyList<PricePoint> Points, bool UsesAdjustedClose, bool IsSimulated);

public sealed record LatestPrice(int AssetId, string Symbol, decimal Price, DateOnly PriceDate, DateTime AsOfUtc, string Origin, bool IsStale, bool IsSimulated);

/// <summary>
/// Read access to persisted prices for the active data source. Analytics use adjusted closes (total return) where
/// available; trading uses raw closes / last quotes. Sources are never mixed within one series.
/// </summary>
public sealed class PriceQueryService(IApplicationDbContext db, TimeProvider clock)
{
    public async Task<ApplicationSettings> SettingsAsync(CancellationToken ct) =>
        await db.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);

    /// <summary>Adjusted-close series (falls back to raw close only if the source never provides adjusted data).</summary>
    public async Task<Dictionary<string, PriceSeries>> GetSeriesAsync(
        IReadOnlyCollection<string> symbols, string source, DateOnly? from, DateOnly? to, CancellationToken ct, int? lastN = null)
    {
        var result = new Dictionary<string, PriceSeries>(StringComparer.OrdinalIgnoreCase);
        var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).ToListAsync(ct);
        foreach (var asset in assets)
        {
            var q = db.HistoricalPrices.AsNoTracking().Where(p => p.AssetId == asset.Id && p.Source == source);
            if (from is { } f) q = q.Where(p => p.Date >= f);
            if (to is { } t) q = q.Where(p => p.Date <= t);
            if (lastN is { } n) q = q.OrderByDescending(p => p.Date).Take(n);
            var rows = await q.Select(p => new { p.Date, p.Close, p.AdjustedClose, p.IsSimulated }).ToListAsync(ct);
            rows = rows.OrderBy(r => r.Date).ToList();
            var adjusted = rows.Count > 0 && rows.All(r => r.AdjustedClose.HasValue);
            var points = rows.Select(r => new PricePoint(r.Date, (double)(adjusted ? r.AdjustedClose!.Value : r.Close))).ToList();
            result[asset.Symbol] = new PriceSeries(asset.Symbol, asset.Id, points, adjusted, rows.Any(r => r.IsSimulated));
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
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var quotes = await db.LatestQuotes.AsNoTracking().Where(q => assetIds.Contains(q.AssetId) && q.Source == source).ToListAsync(ct);

        foreach (var id in assetIds)
        {
            if (!assets.TryGetValue(id, out var asset)) continue;
            var close = await db.HistoricalPrices.AsNoTracking()
                .Where(p => p.AssetId == id && p.Source == source)
                .OrderByDescending(p => p.Date)
                .Select(p => new { p.Date, p.Close, p.RetrievedAtUtc, p.IsSimulated })
                .FirstOrDefaultAsync(ct);
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
