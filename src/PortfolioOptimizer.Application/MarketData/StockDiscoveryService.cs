using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>Filters for the stock browser. All are optional and combine with AND.</summary>
public sealed record StockQuery(
    string? Country = null,
    string? Index = null,
    string? Sector = null,
    string? Search = null,
    bool? Tracked = null,
    bool? WithPrices = null,
    string? Sort = null,
    int Page = 1,
    int PageSize = 50,
    IReadOnlyCollection<string>? Symbols = null);

/// <summary>
/// Read-only stock browser over the shared catalogue: markets → indices → sectors → stocks, search, metadata and the latest
/// price with its provenance (delayed provider quote vs. session close vs. stored daily close, provider, timestamps,
/// cached/stale flags). Prices come only from the active data source; a missing price is reported as missing, never as 0.
/// </summary>
public sealed class StockDiscoveryService(
    IApplicationDbContext db,
    PriceQueryService prices,
    IMarketDataProviderRegistry providers,
    UniverseService universes,
    QuoteRefreshService quoteRefresh,
    IOptions<QuoteRefreshOptions> quoteOptions,
    TimeProvider clock)
{
    public const string UnclassifiedSector = "Unclassified";

    /// <summary>A provider quote whose last trade is at most this old is shown as a (delayed) intraday quote.</summary>
    private static readonly TimeSpan DelayedQuoteWindow = TimeSpan.FromMinutes(30);

    public static readonly IReadOnlyList<string> SortKeys = ["symbol", "name", "sector", "change", "change-desc"];

    public async Task<IReadOnlyList<MarketDto>> GetMarketsAsync(CancellationToken ct) =>
        await universes.GetMarketsAsync((await prices.SettingsAsync(ct)).ActiveProvider, ct);

    public async Task<IReadOnlyList<SectorDto>> GetSectorsAsync(string? country, string? index, CancellationToken ct)
    {
        var settings = await prices.SettingsAsync(ct);
        var withPrices = await prices.AssetIdsWithPricesAsync(settings.ActiveProvider, ct);
        var q = Filter(db.Assets.AsNoTracking().Where(a => a.AssetType != AssetType.Index), new StockQuery(country, index));
        var rows = await q.Select(a => new { a.Id, a.Sector }).ToListAsync(ct);
        return rows.GroupBy(a => a.Sector ?? UnclassifiedSector)
            .Select(g => new SectorDto(g.Key, g.Count(), g.Count(a => withPrices.Contains(a.Id))))
            .OrderBy(s => s.Name == UnclassifiedSector ? 1 : 0).ThenBy(s => s.Name)
            .ToList();
    }

    public async Task<PagedResult<StockListItemDto>> SearchAsync(StockQuery query, CancellationToken ct)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > 200) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 200).");
        var sort = (query.Sort ?? "symbol").ToLowerInvariant();
        if (!SortKeys.Contains(sort)) throw new ValidationException("sort", $"Sort must be one of: {string.Join(", ", SortKeys)}.");
        var settings = await prices.SettingsAsync(ct);
        var source = settings.ActiveProvider;

        var q = Filter(db.Assets.AsNoTracking(), query);
        if (query.Symbols is not { Count: > 0 }) q = q.Where(a => a.AssetType != AssetType.Index);
        if (query.WithPrices is { } w)
        {
            var withPrices = (await prices.AssetIdsWithPricesAsync(source, ct)).ToList();
            q = w ? q.Where(a => withPrices.Contains(a.Id)) : q.Where(a => !withPrices.Contains(a.Id));
        }

        var total = await q.CountAsync(ct);
        var exact = query.Search?.Trim().ToUpperInvariant() ?? "";
        IQueryable<Asset> ordered = sort switch
        {
            "name" => q.OrderBy(a => a.Name).ThenBy(a => a.Symbol),
            "sector" => q.OrderBy(a => a.Sector).ThenBy(a => a.Symbol),
            // Daily change from the stored quote; instruments without a quote sort last.
            "change" or "change-desc" => OrderByChange(q, source, sort == "change-desc"),
            // Exact symbol matches first when searching, then alphabetical.
            _ => q.OrderBy(a => a.Symbol == exact || a.Symbol.StartsWith(exact + ".") ? 0 : 1).ThenBy(a => a.Symbol),
        };
        var assets = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        var items = await BuildItemsAsync(assets, settings, ct);
        return new PagedResult<StockListItemDto>(items, query.Page, query.PageSize, total);
    }

    public async Task<StockDetailDto> GetAsync(string symbol, CancellationToken ct)
    {
        var settings = await prices.SettingsAsync(ct);
        var asset = await FindAsync(symbol, ct);
        var item = (await BuildItemsAsync([asset], settings, ct))[0];
        var hasAdjusted = item.Observations > 0 && !await db.HistoricalPrices.AnyAsync(
            p => p.AssetId == asset.Id && p.Source == settings.ActiveProvider && p.AdjustedClose == null, ct);
        return new StockDetailDto(item, asset.MetadataSource, asset.MetadataUpdatedAtUtc, hasAdjusted, settings.ActiveProvider, asset.Isin, asset.Mic);
    }

    public async Task<StockQuoteDto> GetQuoteAsync(string symbol, CancellationToken ct)
    {
        var settings = await prices.SettingsAsync(ct);
        var asset = await FindAsync(symbol, ct);
        return (await QuotesAsync([asset], settings, ct))[asset.Id];
    }

    /// <summary>Refreshes quotes for the given symbols through the backend provider and returns the updated rows.</summary>
    public async Task<QuoteRefreshResultDto> RefreshQuotesAsync(IReadOnlyCollection<string> symbols, bool force, CancellationToken ct)
    {
        var outcomes = await quoteRefresh.RefreshSymbolsAsync(symbols, force, ct);
        var list = outcomes.Select(o => o.Symbol).ToList();
        var settings = await prices.SettingsAsync(ct);
        var assets = await db.Assets.AsNoTracking().Where(a => list.Contains(a.Symbol)).OrderBy(a => a.Symbol).ToListAsync(ct);
        var items = await BuildItemsAsync(assets, settings, ct);
        return new QuoteRefreshResultDto(outcomes.Select(o => new QuoteRefreshItemDto(o.Symbol, o.Outcome.ToString(), o.Message)).ToList(), items);
    }

    /// <summary>List rows (metadata, coverage, latest quote) for known asset ids, in the given order.</summary>
    public async Task<IReadOnlyList<StockListItemDto>> GetItemsAsync(IReadOnlyList<int> assetIds, CancellationToken ct)
    {
        if (assetIds.Count == 0) return [];
        var settings = await prices.SettingsAsync(ct);
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return await BuildItemsAsync(assetIds.Where(assets.ContainsKey).Select(id => assets[id]).ToList(), settings, ct);
    }

    /// <summary>Stored daily prices of a stock for a chart range (1D, 1M, 3M, 6M, 1Y, 5Y) from the active source.</summary>
    public async Task<PriceHistoryDto> GetHistoryAsync(string symbol, string range, CancellationToken ct)
    {
        if (!PriceHistory.Codes.TryGetValue(range ?? "", out var r))
            throw new ValidationException("range", $"Range must be one of {string.Join(", ", PriceHistory.Codes.Keys)}.");
        var settings = await prices.SettingsAsync(ct);
        var asset = await FindAsync(symbol, ct);
        var source = settings.ActiveProvider;
        var bars = await LoadBarsAsync([asset.Id], source, r, ct);
        bars.TryGetValue(asset.Id, out var own);
        var slice = PriceHistory.Slice(own?.Bars ?? [], r);
        return new PriceHistoryDto(asset.Symbol, asset.Name, asset.Currency, Markets.ForSymbol(asset.Symbol).Exchange, PriceHistory.Code(r), slice.Supported,
            slice.UsesAdjustedClose ? "Adjusted close" : "Close", slice.Points.Select(p => new PricePointDto(p.Date, p.Price)).ToList(),
            slice.BaseDate, slice.BasePrice, slice.EndDate, slice.EndPrice, slice.Change, slice.AvailableFrom, slice.Limitation, source,
            own?.IsSimulated ?? false, own?.LastRetrievedAtUtc, SupportedRanges);
    }

    public const int MaxTrendSymbols = 200;

    /// <summary>
    /// Change and sparkline over <paramref name="range"/> (default 1M) for up to 200 stocks – one page of the stock list – from
    /// stored daily prices of the active source, in two batched queries. Same slice and basis as the watchlist; stocks
    /// without stored history get a null change with the reason. Unknown symbols are skipped.
    /// </summary>
    public async Task<IReadOnlyList<StockTrendDto>> GetTrendsAsync(IReadOnlyList<string> symbols, string? range, CancellationToken ct)
    {
        if (!PriceHistory.Codes.TryGetValue(range ?? "1M", out var r) || r == PriceRange.OneDay)
            throw new ValidationException("range", $"Range must be one of {string.Join(", ", SupportedRanges)}.");
        var list = symbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
        if (list.Count > MaxTrendSymbols) throw new ValidationException("symbols", $"At most {MaxTrendSymbols} symbols per request.");
        if (list.Count == 0) return [];
        var ids = await db.Assets.AsNoTracking().Where(a => list.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, a => a.Id, ct);
        var source = (await prices.SettingsAsync(ct)).ActiveProvider;
        var bars = await LoadBarsAsync(ids.Values.ToList(), source, r, ct);
        return list.Where(ids.ContainsKey).Select(symbol =>
        {
            bars.TryGetValue(ids[symbol], out var own);
            var slice = PriceHistory.Slice(own?.Bars ?? [], r);
            return new StockTrendDto(symbol, new PeriodChangeDto(PriceHistory.Code(r), slice.Change, slice.BaseDate, slice.EndDate,
                slice.UsesAdjustedClose ? "Adjusted close" : "Close", slice.Limitation, PriceHistory.Downsample(slice.Points)));
        }).ToList();
    }

    /// <summary>Ranges a client may request; 1D is accepted but always reported as unsupported (no intraday bars are stored).</summary>
    public static readonly IReadOnlyList<string> SupportedRanges = ["1M", "3M", "6M", "1Y", "5Y", "MAX"];

    public sealed record StoredBars(List<HistoryBar> Bars, bool IsSimulated, DateTime? LastRetrievedAtUtc);

    /// <summary>
    /// Daily bars per asset, enough for <paramref name="range"/>: from a little before the range start relative to each
    /// asset's own last bar (so the base observation on or before the start is included).
    /// </summary>
    public async Task<Dictionary<int, StoredBars>> LoadBarsAsync(IReadOnlyCollection<int> assetIds, string source, PriceRange range, CancellationToken ct)
    {
        if (range == PriceRange.OneDay || assetIds.Count == 0) return [];
        var lastDates = await db.HistoricalPrices.AsNoTracking().Where(p => assetIds.Contains(p.AssetId) && p.Source == source)
            .GroupBy(p => p.AssetId).Select(g => new { AssetId = g.Key, Last = g.Max(p => p.Date) }).ToListAsync(ct);
        if (lastDates.Count == 0) return [];
        // Ten extra days cover weekends and holiday runs before the range start.
        var from = range == PriceRange.Max ? DateOnly.MinValue : PriceHistory.StartFor(range, lastDates.Min(x => x.Last)).AddDays(-10);
        var rows = await db.HistoricalPrices.AsNoTracking()
            .Where(p => assetIds.Contains(p.AssetId) && p.Source == source && p.Date >= from)
            .Select(p => new { p.AssetId, p.Date, p.Close, p.AdjustedClose, p.IsSimulated, p.RetrievedAtUtc })
            .ToListAsync(ct);
        return rows.GroupBy(x => x.AssetId).ToDictionary(g => g.Key, g => new StoredBars(
            g.Select(x => new HistoryBar(x.Date, x.Close, x.AdjustedClose)).ToList(), g.Any(x => x.IsSimulated), g.Max(x => x.RetrievedAtUtc)));
    }

    private IQueryable<Asset> Filter(IQueryable<Asset> q, StockQuery query)
    {
        if (query.Symbols is { Count: > 0 })
        {
            var list = query.Symbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
            q = q.Where(a => list.Contains(a.Symbol));
        }
        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            // One market or several (a region: "DE,FR,IT"). A market contains its listings and the members of its indices
            // (e.g. ArcelorMittal, the Amsterdam-listed CAC 40 member).
            var countries = query.Country.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.ToUpperInvariant()).Distinct().ToList();
            var codes = db.MarketIndices.Where(i => countries.Contains(i.Country)).Select(i => i.Code);
            q = q.Where(a => countries.Contains(a.Country) || a.Memberships.Any(m => codes.Contains(m.IndexCode)));
        }
        if (!string.IsNullOrWhiteSpace(query.Index))
        {
            var index = query.Index.Trim().ToUpperInvariant();
            q = q.Where(a => a.Memberships.Any(m => m.IndexCode == index));
        }
        if (!string.IsNullOrWhiteSpace(query.Sector))
            q = query.Sector == UnclassifiedSector ? q.Where(a => a.Sector == null) : q.Where(a => a.Sector == query.Sector);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(a => a.Symbol.ToLower().Contains(term) || (a.Name != null && a.Name.ToLower().Contains(term)) || (a.Isin != null && a.Isin.ToLower() == term));
        }
        if (query.Tracked is { } t) q = q.Where(a => a.IsEnabled == t);
        return q;
    }

    private IQueryable<Asset> OrderByChange(IQueryable<Asset> q, string source, bool descending)
    {
        var withChange = q.Select(a => new
        {
            Asset = a,
            Change = db.LatestQuotes.Where(x => x.AssetId == a.Id && x.Source == source && x.PreviousClose > 0)
                .Select(x => (decimal?)(x.Price / x.PreviousClose!.Value - 1)).FirstOrDefault(),
        });
        var ordered = descending
            ? withChange.OrderBy(x => x.Change == null).ThenByDescending(x => x.Change).ThenBy(x => x.Asset.Symbol)
            : withChange.OrderBy(x => x.Change == null).ThenBy(x => x.Change).ThenBy(x => x.Asset.Symbol);
        return ordered.Select(x => x.Asset);
    }

    private async Task<Asset> FindAsync(string symbol, CancellationToken ct)
    {
        var s = Asset.NormalizeSymbol(symbol);
        return await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Symbol == s, ct) ?? throw new NotFoundException("Stock", s);
    }

    /// <summary>Largest selection whose stored history can be checked in one request.</summary>
    public const int MaxCoverageSymbols = 3000;

    /// <summary>
    /// Stored daily prices per symbol (active source) for any selection – one grouped query, so a selection of thousands
    /// of stocks still learns which ones lack history. Unknown symbols are reported with 0.
    /// </summary>
    public async Task<IReadOnlyList<HistoryCoverageDto>> GetHistoryCoverageAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var list = symbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
        if (list.Count > MaxCoverageSymbols) throw new ValidationException("symbols", $"At most {MaxCoverageSymbols} symbols per request.");
        var source = (await prices.SettingsAsync(ct)).ActiveProvider;
        var ids = await db.Assets.AsNoTracking().Where(a => list.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, a => a.Id, ct);
        var idList = ids.Values.ToList();
        var counts = await db.HistoricalPrices.AsNoTracking()
            .Where(p => idList.Contains(p.AssetId) && p.Source == source)
            .GroupBy(p => p.AssetId)
            .Select(g => new { AssetId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AssetId, x => x.Count, ct);
        return list.Select(s => new HistoryCoverageDto(s, ids.TryGetValue(s, out var id) ? counts.GetValueOrDefault(id) : 0)).ToList();
    }

    private async Task<List<StockListItemDto>> BuildItemsAsync(IReadOnlyList<Asset> assets, ApplicationSettings settings, CancellationToken ct)
    {
        var ids = assets.Select(a => a.Id).ToList();
        var source = settings.ActiveProvider;
        var coverage = await db.HistoricalPrices.AsNoTracking()
            .Where(p => ids.Contains(p.AssetId) && p.Source == source)
            .GroupBy(p => p.AssetId)
            .Select(g => new { AssetId = g.Key, Count = g.Count(), First = g.Min(p => p.Date), Last = g.Max(p => p.Date) })
            .ToDictionaryAsync(x => x.AssetId, ct);
        var memberships = (await db.IndexMemberships.AsNoTracking().Where(m => ids.Contains(m.AssetId)).ToListAsync(ct))
            .GroupBy(m => m.AssetId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(m => m.IndexCode).OrderBy(c => c).ToList());
        var quotes = await QuotesAsync(assets, settings, ct);
        return assets.Select(a =>
        {
            coverage.TryGetValue(a.Id, out var c);
            return new StockListItemDto(a.Id, a.Symbol, a.Name, a.Sector, a.Industry, a.Exchange, a.Country,
                memberships.TryGetValue(a.Id, out var m) ? m : [], a.Currency, a.AssetType, a.IsEnabled,
                c?.Count ?? 0, c?.First, c?.Last, quotes[a.Id]);
        }).ToList();
    }

    /// <summary>
    /// Latest price per asset from the active source: the newer of the stored provider quote and the last stored daily
    /// close. A quote whose last trade is recent is a (delayed) intraday quote; an older one is the last session's price.
    /// </summary>
    private async Task<Dictionary<int, StockQuoteDto>> QuotesAsync(IReadOnlyList<Asset> assets, ApplicationSettings settings, CancellationToken ct)
    {
        var ids = assets.Select(a => a.Id).ToList();
        var source = settings.ActiveProvider;
        var providerName = providers.Providers.FirstOrDefault(p => p.Name == source)?.DisplayName ?? source;
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var freshFor = TimeSpan.FromMinutes(quoteOptions.Value.FreshForMinutes);

        // The last two bars of each asset lie within a few weeks of that asset's own last date (holidays included).
        var lastDates = db.HistoricalPrices.Where(p => ids.Contains(p.AssetId) && p.Source == source)
            .GroupBy(p => p.AssetId).Select(g => new { AssetId = g.Key, Last = g.Max(p => p.Date) });
        var recent = await (from p in db.HistoricalPrices.AsNoTracking()
                            join l in lastDates on p.AssetId equals l.AssetId
                            where p.Source == source && p.Date >= l.Last.AddDays(-20)
                            select new { p.AssetId, p.Date, p.Close, p.RetrievedAtUtc, p.IsSimulated })
            .ToListAsync(ct);
        var bars = recent.GroupBy(r => r.AssetId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Date).Take(2).ToList());
        var stored = await db.LatestQuotes.AsNoTracking().Where(q => ids.Contains(q.AssetId) && q.Source == source).ToDictionaryAsync(q => q.AssetId, ct);

        var result = new Dictionary<int, StockQuoteDto>();
        foreach (var asset in assets)
        {
            var venue = Markets.ForSymbol(asset.Symbol);
            bars.TryGetValue(asset.Id, out var b);
            stored.TryGetValue(asset.Id, out var quote);
            var last = b is { Count: > 0 } ? b[0] : null;
            var prev = b is { Count: > 1 } ? b[1] : null;
            var quoteDate = quote is null ? (DateOnly?)null : Markets.LocalDate(venue, quote.AsOfUtc);

            if (quote is not null && (last is null || quoteDate >= last.Date))
            {
                var priceDate = quoteDate!.Value;
                var intraday = now - quote.AsOfUtc <= DelayedQuoteWindow;
                // Previous close: from the provider with the quote, else the last stored bar before the quote's session.
                var reference = quote.PreviousClose ?? (last is not null && last.Date < priceDate ? last.Close : prev?.Close);
                var referenceDate = quote.PreviousClose is not null ? null : last is not null && last.Date < priceDate ? last.Date : prev?.Date;
                var change = reference is null ? (decimal?)null : quote.Price - reference;
                var cached = now - quote.RetrievedAtUtc > freshFor;
                var kindText = quote.IsSimulated ? "SIMULATED price" : intraday ? "Delayed quote" : "Last price of the session";
                result[asset.Id] = new StockQuoteDto(asset.Symbol, quote.Price, asset.Currency, intraday ? QuoteKind.DelayedQuote : QuoteKind.LastClose,
                    priceDate, quote.AsOfUtc, reference, referenceDate, change, Percent(change, reference), source, quote.RetrievedAtUtc, quote.IsSimulated,
                    PriceQueryService.BusinessDaysBetween(priceDate, today) > settings.StalePriceDays, cached,
                    $"{kindText}: last trade {quote.AsOfUtc:yyyy-MM-dd HH:mm} UTC ({venue.Exchange}), retrieved {quote.RetrievedAtUtc:yyyy-MM-dd HH:mm} UTC" +
                    $"{(cached ? " (cached)" : "")}. Provider prices are delayed or end-of-day, never real-time.");
            }
            else if (last is not null)
            {
                var change = prev is null ? (decimal?)null : last.Close - prev.Close;
                result[asset.Id] = new StockQuoteDto(asset.Symbol, last.Close, asset.Currency, QuoteKind.LastClose, last.Date, null,
                    prev?.Close, prev?.Date, change, Percent(change, prev?.Close), source, last.RetrievedAtUtc, last.IsSimulated,
                    PriceQueryService.BusinessDaysBetween(last.Date, today) > settings.StalePriceDays, true,
                    $"{(last.IsSimulated ? "SIMULATED daily close" : "Last stored daily close")} of {last.Date:yyyy-MM-dd}, retrieved {last.RetrievedAtUtc:yyyy-MM-dd HH:mm} UTC.");
            }
            else
            {
                result[asset.Id] = new StockQuoteDto(asset.Symbol, null, asset.Currency, QuoteKind.None, null, null, null, null, null, null,
                    source, null, false, false, false, "No price available yet. Use refresh to request one.");
            }
        }
        return result;
    }

    private static double? Percent(decimal? change, decimal? basePrice) =>
        change is { } c && basePrice is { } b && b > 0 ? (double)(c / b) : null;
}
