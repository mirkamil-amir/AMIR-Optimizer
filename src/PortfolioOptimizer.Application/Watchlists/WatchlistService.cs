using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Watchlists;

/// <summary>
/// The caller's watchlist: stocks they follow without holding them. Entries are owner-scoped (another user's entry is
/// 404); quotes, prices and news stay shared and are never copied per user. Adding an entry may queue a download of
/// the stock's daily history (the same on-demand download as the stock browser); it never touches portfolios or runs.
/// </summary>
public sealed class WatchlistService(
    IApplicationDbContext db,
    StockDiscoveryService stocks,
    MarketDataService marketData,
    PriceQueryService prices,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const int MaxEntries = 100; // equals the page-refresh limit, so one bulk refresh covers the whole list
    private const int SparklinePoints = 60;

    public const string Note =
        "A watchlist follows stocks; it is not a portfolio and implies no purchase. Prices are delayed quotes or daily closes from the " +
        "configured provider (never real-time); each row shows its source, timestamp and freshness.";

    public async Task<WatchlistDto> GetAsync(string? range, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var r = ParseRange(range ?? "1M");
        var entries = await db.WatchlistEntries.AsNoTracking().Where(e => e.OwnerId == owner).OrderBy(e => e.AddedAtUtc).ToListAsync(ct);
        return new WatchlistDto(await BuildAsync(entries, r, ct), PriceHistory.Code(r), MaxEntries, Note);
    }

    public async Task<WatchlistAddResultDto> AddAsync(AddWatchlistEntryRequest request, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var symbol = Asset.NormalizeSymbol(request.Symbol);
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Symbol == symbol, ct) ?? throw new NotFoundException("Stock", symbol);
        if (await db.WatchlistEntries.AnyAsync(e => e.OwnerId == owner && e.AssetId == asset.Id, ct))
            throw new ConflictException($"{asset.Symbol} is already on your watchlist.");
        if (await db.WatchlistEntries.CountAsync(e => e.OwnerId == owner, ct) >= MaxEntries)
            throw new ConflictException($"A watchlist holds at most {MaxEntries} stocks. Remove one first.");

        var entry = new WatchlistEntry { Id = Guid.NewGuid(), OwnerId = owner, AssetId = asset.Id, AddedAtUtc = clock.GetUtcNow().UtcDateTime };
        db.WatchlistEntries.Add(entry);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
        {
            throw new ConflictException($"{asset.Symbol} is already on your watchlist."); // concurrent double add
        }

        // Charts need daily history; download up to five years when (almost) nothing is stored yet.
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        IReadOnlyList<string> queued = asset.Symbol.StartsWith('^') ? [] : await marketData.EnsureHistoryAsync([asset.Symbol], today.AddYears(-5), 20, ct);
        var item = (await BuildAsync([entry], PriceRange.OneMonth, ct))[0];
        return new WatchlistAddResultDto(item, queued.Count > 0);
    }

    public async Task RemoveAsync(string symbol, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var s = Asset.NormalizeSymbol(symbol);
        var entry = await db.WatchlistEntries.SingleOrDefaultAsync(e => e.OwnerId == owner && e.Asset!.Symbol == s, ct)
                    ?? throw new NotFoundException("Watchlist entry", s);
        db.WatchlistEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Symbols on the caller's watchlist (used for refreshes and the news filter).</summary>
    public async Task<IReadOnlyList<string>> SymbolsAsync(CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        return await db.WatchlistEntries.AsNoTracking().Where(e => e.OwnerId == owner).OrderBy(e => e.AddedAtUtc)
            .Select(e => e.Asset!.Symbol).ToListAsync(ct);
    }

    /// <summary>Refreshes the quotes of all watched stocks (cached quotes within the cache window are reused).</summary>
    public async Task<QuoteRefreshResultDto> RefreshAsync(CancellationToken ct)
    {
        var symbols = await SymbolsAsync(ct);
        return symbols.Count == 0 ? new QuoteRefreshResultDto([], []) : await stocks.RefreshQuotesAsync(symbols, force: false, ct);
    }

    private async Task<List<WatchlistItemDto>> BuildAsync(IReadOnlyList<WatchlistEntry> entries, PriceRange range, CancellationToken ct)
    {
        var ids = entries.Select(e => e.AssetId).ToList();
        var items = (await stocks.GetItemsAsync(ids, ct)).ToDictionary(i => i.Id);
        var source = (await prices.SettingsAsync(ct)).ActiveProvider;
        var bars = await stocks.LoadBarsAsync(ids, source, range, ct);
        var result = new List<WatchlistItemDto>();
        foreach (var e in entries)
        {
            if (!items.TryGetValue(e.AssetId, out var item)) continue;
            bars.TryGetValue(e.AssetId, out var own);
            var slice = PriceHistory.Slice(own?.Bars ?? [], range);
            result.Add(new WatchlistItemDto(e.Id, e.AddedAtUtc, item, new PeriodChangeDto(
                PriceHistory.Code(range), slice.Change, slice.BaseDate, slice.EndDate, slice.UsesAdjustedClose ? "Adjusted close" : "Close",
                slice.Limitation, Downsample(slice.Points))));
        }
        return result;
    }

    private static PriceRange ParseRange(string range) =>
        PriceHistory.Codes.TryGetValue(range, out var r) && r != PriceRange.OneDay
            ? r
            : throw new ValidationException("range", $"Range must be one of {string.Join(", ", StockDiscoveryService.SupportedRanges)}.");

    /// <summary>Evenly spaced observations for a sparkline; always keeps the first and last real observation.</summary>
    public static IReadOnlyList<PricePointDto> Downsample(IReadOnlyList<(DateOnly Date, decimal Price)> points, int max = SparklinePoints)
    {
        if (points.Count <= max) return points.Select(p => new PricePointDto(p.Date, p.Price)).ToList();
        var step = (points.Count - 1) / (double)(max - 1);
        return Enumerable.Range(0, max).Select(i => points[(int)Math.Round(i * step)]).Select(p => new PricePointDto(p.Date, p.Price)).ToList();
    }
}
