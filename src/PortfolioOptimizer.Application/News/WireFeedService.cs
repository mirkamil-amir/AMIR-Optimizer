using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.News;

/// <summary>Shared filters of the Wire feeds. Hours = time window back from now (each feed has its own default).</summary>
public sealed record WireQuery(
    int? Hours = null,
    string? Region = null,
    string? Attention = null,
    string? Search = null,
    string? Cursor = null,
    int? PageSize = null,
    string? Area = null,
    string? EventType = null);

/// <summary>
/// The five Wire feeds, each its own query over the story layer with written rules (docs/financial-news.md › Wire feeds):
/// Latest (everything Medium or High attention, newest first, at most two stories per company per page), My Watchlist
/// (stories linked to the viewer's watched listings, plus rate decisions of the central bank of a watched listing's
/// currency), Company News (grouped by company: curated, held and watched listings), Market-wide (market-level stories by
/// area) and Macroeconomic (central-bank and statistics releases). Personal data – watchlist, holdings – is read for the
/// authenticated viewer only and never cached; stories linked only to listings other users follow are hidden
/// (<see cref="WireFeedOptions.HideOthersInterests"/>).
/// </summary>
public sealed class WireFeedService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IEnumerable<ICompanyNewsProvider> companyProviders,
    IEnumerable<INewsFeedProvider> feedProviders,
    IOptions<NewsOptions> options,
    NewsService news,
    PortfolioService portfolios,
    PortfolioValuationService valuations,
    TimeProvider clock)
{
    public const string AttentionNote =
        "Rule-based label of how material and well-supported a story is (event type, official source, number of publishers, recency). It does not predict whether or how much any price will move.";

    private const int MaxHours = 24 * 90;
    private NewsOptions Options => options.Value;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private sealed record Viewer(HashSet<int> Watched, HashSet<int> Held, HashSet<int> Curated, HashSet<int> Benchmarks, Dictionary<int, string> WatchedCurrency,
        bool Refreshing)
    {
        public List<int> Visible { get; } = [.. Watched.Union(Held).Union(Curated).Union(Benchmarks)];
    }

    // ------------------------------------------------------------------ feeds

    /// <summary>Latest: every visible story with Medium or High attention, newest first, at most N per company per page.</summary>
    public async Task<WireFeedDto> LatestAsync(WireQuery query, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        var (since, hours) = Window(query, 48);
        var size = PageSize(query, 20);
        var minimum = ParseAttention(query.Attention) ?? AttentionLevel.Medium;
        if (minimum == AttentionLevel.Low) throw new ValidationException("attention", "Latest shows Medium and High attention only.");
        var q = Filter(Visible(viewer), query with { Attention = minimum.ToString() }, since);

        // Scan newest first in batches; skip a story once its company already has the per-page maximum on this page.
        var cap = Math.Max(1, Options.Feeds.LatestPerCompanyPerPage);
        var page = new List<NewsStory>();
        var perCompany = new Dictionary<int, int>();
        var equities = new Dictionary<Guid, List<int>>();
        var position = Decode(query.Cursor); // continues after the last story the previous page scanned
        NewsStory? lastScanned = null;
        var more = true; // whether stories after lastScanned may exist
        var batchSize = size * 3;
        for (var round = 0; round < 5 && page.Count < size && more; round++)
        {
            var batch = await After(q, lastScanned is null ? position : (lastScanned.LastPublishedAtUtc, lastScanned.Id)).Take(batchSize).ToListAsync(ct);
            var batchIds = batch.Select(s => s.Id).ToList();
            foreach (var row in await db.NewsStoryAssets.AsNoTracking().Where(x => batchIds.Contains(x.StoryId) && x.Asset!.AssetType == AssetType.Equity)
                         .Select(x => new { x.StoryId, x.AssetId }).ToListAsync(ct))
                (equities.TryGetValue(row.StoryId, out var list) ? list : equities[row.StoryId] = []).Add(row.AssetId);
            var scannedAll = true;
            foreach (var s in batch)
            {
                lastScanned = s;
                var companies = equities.GetValueOrDefault(s.Id) ?? [];
                if (companies.Any(c => perCompany.GetValueOrDefault(c) >= cap)) continue;
                foreach (var c in companies) perCompany[c] = perCompany.GetValueOrDefault(c) + 1;
                page.Add(s);
                if (page.Count == size) { scannedAll = s == batch[^1]; break; }
            }
            more = batch.Count == batchSize || !scannedAll;
        }
        var next = more ? Cursor(lastScanned) : null;
        var stories = await ToDtosAsync(page, viewer, null, ct);
        return new WireFeedDto("Latest", stories, next, Now, hours, await SourcesAsync(ct),
        [
            "Latest shows Medium and High attention stories of every kind, newest first; at most " + cap + " stories per company per page – Company News shows them all.",
            .. CommonNotes(),
        ], viewer.Refreshing);
    }

    /// <summary>My Watchlist: stories linked to a watched listing, and rate decisions of the central bank of a watched listing's currency.</summary>
    public async Task<WireFeedDto> WatchlistAsync(WireQuery query, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        var (since, hours) = Window(query, 24 * 7);
        var size = PageSize(query, 20);
        var watched = viewer.Watched.ToList();
        var regions = StoryRules.CentralBankCurrency.Where(kv => viewer.WatchedCurrency.Values.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();
        var q = db.NewsStories.AsNoTracking().Where(s =>
            s.Assets.Any(x => watched.Contains(x.AssetId)) ||
            (s.EventType == NewsEventType.RateDecision && s.HasOfficialSource && s.Region != null && regions.Contains(s.Region)));
        q = Filter(q, query, since);
        var rows = await After(q, Decode(query.Cursor)).Take(size + 1).ToListAsync(ct);
        var page = rows.Take(size).ToList();
        var stories = await ToDtosAsync(page, viewer, WatchlistReasons(viewer), ct);
        var notes = new List<string>
        {
            "My Watchlist shows stories linked to a listing you watch (by the provider's tag or an official filing) and rate decisions of the central bank of a currency your watched listings trade in. General market news is not included.",
        };
        if (viewer.Watched.Count == 0) notes.Insert(0, "Your watchlist is empty: add listings in Radar to see their news here.");
        return new WireFeedDto("Watchlist", stories, rows.Count > size ? Cursor(page[^1]) : null, Now, hours, await SourcesAsync(ct), [.. notes, .. CommonNotes()], viewer.Refreshing);
    }

    /// <summary>Company News: companies with stories in the window, newest first, each with its latest stories.</summary>
    public async Task<WireCompaniesDto> CompaniesAsync(WireQuery query, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        var (since, hours) = Window(query, 24 * 7);
        var size = PageSize(query, 10);
        var offset = DecodeOffset(query.Cursor);
        var allowed = viewer.Curated.Union(viewer.Watched).Union(viewer.Held).ToList();
        var restrict = Options.Feeds.HideOthersInterests;

        var links = from x in db.NewsStoryAssets.AsNoTracking()
                    join s in db.NewsStories.AsNoTracking() on x.StoryId equals s.Id
                    where s.Scope == NewsScope.Company && s.LastPublishedAtUtc >= since && x.Asset!.AssetType == AssetType.Equity
                    select new { x.AssetId, Story = s, x.Asset };
        if (restrict) links = links.Where(l => allowed.Contains(l.AssetId));
        if (!string.IsNullOrWhiteSpace(query.Region))
        {
            var region = query.Region.Trim().ToUpperInvariant();
            links = links.Where(l => l.Asset!.Country == region);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            links = links.Where(l => l.Asset!.Symbol.ToLower() == term || (l.Asset.Name != null && l.Asset.Name.ToLower().Contains(term)) || l.Story.Headline.ToLower().Contains(term));
        }
        if (ParseEventType(query.EventType) is { } type) links = links.Where(l => l.Story.EventType == type);

        var groups = await links.GroupBy(l => l.AssetId)
            .Select(g => new { AssetId = g.Key, Newest = g.Max(l => l.Story.LastPublishedAtUtc), Count = g.Count() })
            .OrderByDescending(g => g.Newest).ThenBy(g => g.AssetId)
            .Skip(offset).Take(size + 1).ToListAsync(ct);
        var pageGroups = groups.Take(size).ToList();
        var assetIds = pageGroups.Select(g => g.AssetId).ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var perCompany = Math.Max(1, Options.Feeds.StoriesPerCompany);
        var storyRows = await links.Where(l => assetIds.Contains(l.AssetId))
            .Select(l => new { l.AssetId, l.Story }).ToListAsync(ct);
        var chosen = storyRows.GroupBy(r => r.AssetId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Story).OrderByDescending(s => s.LastPublishedAtUtc).ThenByDescending(s => s.Id).Take(perCompany).ToList());
        var dtos = (await ToDtosAsync(chosen.Values.SelectMany(v => v).DistinctBy(s => s.Id).ToList(), viewer, null, ct)).ToDictionary(d => d.Id);

        var companies = pageGroups.Select(g =>
        {
            var a = assets[g.AssetId];
            return new WireCompanyDto(a.Symbol, a.Name, a.Sector, a.Currency, viewer.Watched.Contains(a.Id), viewer.Held.Contains(a.Id), viewer.Curated.Contains(a.Id),
                g.Count, g.Newest, chosen.GetValueOrDefault(g.AssetId, []).Select(s => dtos[s.Id]).ToList());
        }).ToList();
        var notes = new List<string>
        {
            restrict
                ? "Company News lists companies from the curated list and the companies you hold or watch, each with its newest stories; companies only other users follow are not shown."
                : "Company News lists every company with stories in the window, each with its newest stories.",
        };
        if (restrict && allowed.Count == 0) notes.Insert(0, "No companies to show yet: the curated list is empty and you hold or watch no listings.");
        return new WireCompaniesDto(companies, groups.Count > size ? EncodeOffset(offset + size) : null, Now, hours, await SourcesAsync(ct), [.. notes, .. CommonNotes()], viewer.Refreshing);
    }

    private static readonly NewsMarketArea[] Areas = [NewsMarketArea.Equities, NewsMarketArea.Rates, NewsMarketArea.Fx, NewsMarketArea.Commodities, NewsMarketArea.Volatility];

    /// <summary>Market-wide: market-level stories by area; with <c>area</c> one area, paged.</summary>
    public async Task<WireMarketsDto> MarketsAsync(WireQuery query, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        var (since, hours) = Window(query, 72);
        NewsMarketArea? single = null;
        if (!string.IsNullOrWhiteSpace(query.Area))
            single = Enum.TryParse<NewsMarketArea>(query.Area, true, out var a) && Enum.IsDefined(a) ? a : throw new ValidationException("area", $"Unknown market area '{query.Area}'.");
        if (single is null && query.Cursor is not null) throw new ValidationException("cursor", "Paging needs an area.");
        var size = PageSize(query, single is null ? 5 : 20);
        var sections = new List<WireMarketSectionDto>();
        foreach (var area in single is { } one ? [one] : Areas)
        {
            var q = Filter(Visible(viewer).Where(s => s.Scope == NewsScope.Market && s.MarketArea == area), query, since);
            var count = await q.CountAsync(ct);
            var rows = await After(q, Decode(query.Cursor)).Take(size + 1).ToListAsync(ct);
            var page = rows.Take(size).ToList();
            sections.Add(new WireMarketSectionDto(area.ToString(), AreaLabel(area), count, await ToDtosAsync(page, viewer, null, ct),
                rows.Count > size ? Cursor(page[^1]) : null));
        }
        return new WireMarketsDto(sections, Now, hours, await SourcesAsync(ct),
        [
            "Market-wide shows stories tagged only with market instruments – indexes, yields, currencies, commodities, volatility – grouped by area. Single-company stories are in Company News even when an index is tagged too.",
            .. CommonNotes(),
        ], viewer.Refreshing);
    }

    /// <summary>Macroeconomic: central-bank and statistics releases and macro news, newest first.</summary>
    public async Task<WireFeedDto> MacroAsync(WireQuery query, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        var (since, hours) = Window(query, 24 * 30);
        var size = PageSize(query, 20);
        var q = Filter(Visible(viewer).Where(s => s.Scope == NewsScope.Macro), query, since);
        var rows = await After(q, Decode(query.Cursor)).Take(size + 1).ToListAsync(ct);
        var page = rows.Take(size).ToList();
        return new WireFeedDto("Macroeconomic", await ToDtosAsync(page, viewer, null, ct), rows.Count > size ? Cursor(page[^1]) : null, Now, hours, await SourcesAsync(ct),
        [
            "Macroeconomic shows central-bank announcements, official statistics releases and other economy-wide news. Release figures are not extracted; open the release for the numbers.",
            .. CommonNotes(),
        ], viewer.Refreshing);
    }

    // ------------------------------------------------------------------ portfolio news (Overview)

    /// <summary>
    /// News about one portfolio (owner-scoped; another user's portfolio → 404): stories linked to its holdings and official
    /// rate decisions for the currencies it holds, newest first. Each story states the holding's weight or the currency's
    /// share from the current valuation – facts, never predicted effects. Stories linked to a holding are High attention.
    /// </summary>
    public async Task<PortfolioNewsDto> PortfolioAsync(Guid portfolioId, WireQuery query, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var (since, hours) = Window(query, 24 * 7);
        var size = PageSize(query, 10);
        var valuation = await valuations.ValueAsync(portfolio, null, ct);
        var positions = valuation.Positions.Values.Where(p => p.Quantity > 0).ToList();
        var total = valuation.TotalValue;
        double? Share(decimal value) => total is > 0 ? (double)(value / total.Value) : null;

        var heldIds = positions.Select(p => p.AssetId).ToList();
        var holdings = positions.ToDictionary(p => p.Symbol, p => new WireExposureDto("Holding", p.Symbol, p.Value is { } v ? Share(v) : null,
            p.Price is { } price ? $"market value at the {price.PriceDate:yyyy-MM-dd} close ÷ total value incl. cash" : "no stored price"),
            StringComparer.OrdinalIgnoreCase);
        var currencyValues = positions.GroupBy(p => p.Currency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Value ?? 0m), StringComparer.OrdinalIgnoreCase);
        currencyValues[valuation.AccountCurrency] = currencyValues.GetValueOrDefault(valuation.AccountCurrency) + valuation.Cash;
        var currencies = currencyValues.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key,
            kv => new WireExposureDto("Currency", kv.Key, Share(kv.Value), $"positions and cash in {kv.Key} ÷ total value"), StringComparer.OrdinalIgnoreCase);
        var regions = StoryRules.CentralBankCurrency.Where(kv => currencies.ContainsKey(kv.Value)).Select(kv => kv.Key).ToList();

        // Like the feeds: never-fetched sources now, the held companies' due news in the background.
        var names = await db.Assets.AsNoTracking().Where(a => heldIds.Contains(a.Id)).Select(a => new { a.Symbol, a.Name }).ToListAsync(ct);
        var refreshing = await news.RefreshForViewerAsync(names.Select(n => (n.Symbol, n.Name)).ToList(), ct);
        var viewer = await ViewerAsync(ct);

        var q = db.NewsStories.AsNoTracking().Where(s =>
            s.Assets.Any(x => heldIds.Contains(x.AssetId)) ||
            (s.EventType == NewsEventType.RateDecision && s.HasOfficialSource && s.Region != null && regions.Contains(s.Region)));
        q = Filter(q, query, since);
        var rows = await After(q, Decode(query.Cursor)).Take(size + 1).ToListAsync(ct);
        var page = rows.Take(size).ToList();

        static string Percent(double? w) => w is { } x ? x.ToString("0.0%", System.Globalization.CultureInfo.InvariantCulture) : "not available";
        List<WireExposureDto> Exposure(NewsStory s, IReadOnlyList<WireAssetDto> assets)
        {
            var list = assets.Select(a => holdings.GetValueOrDefault(a.Symbol)).OfType<WireExposureDto>().ToList();
            if (list.Count == 0 && s.EventType == NewsEventType.RateDecision && s.Region is { } r && StoryRules.CentralBankCurrency.TryGetValue(r, out var c) &&
                currencies.TryGetValue(c, out var share))
                list.Add(share);
            return list;
        }
        var exposures = new Dictionary<Guid, List<WireExposureDto>>();
        var stories = await ToDtosAsync(page, viewer, (s, assets) =>
        {
            var e = exposures[s.Id] = Exposure(s, assets);
            var why = e.Select(x => x.Kind == "Holding"
                ? $"{x.Label} is {Percent(x.Weight)} of this portfolio ({x.Basis})"
                : $"rate decision for the {x.Label}; {Percent(x.Weight)} of this portfolio is in {x.Label}").ToList();
            Attention? own = e.Any(x => x.Kind == "Holding") ? new Attention(AttentionLevel.High, 0, ["linked to a holding of this portfolio"]) : null;
            return (own, why);
        }, ct);

        var notes = new List<string>
        {
            "Stories linked to this portfolio's holdings, and official rate decisions for the currencies it holds. Weights and currency shares are facts from the current valuation, not predicted effects; Compare opens the price movement around a story, which is not evidence that the news caused it.",
        };
        if (positions.Count == 0) notes.Insert(0, "This portfolio holds no positions yet: news appears here once it does.");
        return new PortfolioNewsDto(portfolio.Id, valuation.Currency, stories.Select(s => new PortfolioNewsItemDto(s, exposures.GetValueOrDefault(s.Id, []))).ToList(),
            rows.Count > size ? Cursor(page[^1]) : null, Now, hours, await SourcesAsync(ct), [.. notes, .. CommonNotes()],
            [.. valuation.Problems, .. valuation.Warnings], refreshing || viewer.Refreshing);
    }

    // ------------------------------------------------------------------ query building

    private async Task<Viewer> ViewerAsync(CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var watched = await db.WatchlistEntries.AsNoTracking().Where(e => e.OwnerId == owner).Select(e => new { e.AssetId, e.Asset!.Currency, e.Asset.Symbol, e.Asset.Name }).ToListAsync(ct);
        var held = await db.Holdings.AsNoTracking().Where(h => h.Quantity > 0 && db.Portfolios.Any(p => p.Id == h.PortfolioId && p.OwnerId == owner))
            .Select(h => h.AssetId).Distinct().ToListAsync(ct);
        var curatedSymbols = Options.Schedule.CuratedSymbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
        var curated = await db.Assets.AsNoTracking().Where(a => curatedSymbols.Contains(a.Symbol)).Select(a => a.Id).ToListAsync(ct);
        var benchmarks = await db.Assets.AsNoTracking().Where(a => a.AssetType != AssetType.Equity && db.MarketIndices.Any(i => i.BenchmarkSymbol == a.Symbol))
            .Select(a => a.Id).ToListAsync(ct);
        // Like the article feed: never-fetched sources now, due feeds and the stalest watched companies in the background.
        var refreshing = await news.RefreshForViewerAsync(watched.Select(w => (w.Symbol, w.Name)).ToList(), ct);
        return new Viewer([.. watched.Select(w => w.AssetId)], [.. held], [.. curated], [.. benchmarks],
            watched.GroupBy(w => w.AssetId).ToDictionary(g => g.Key, g => g.First().Currency), refreshing);
    }

    /// <summary>Stories the viewer may see: no listing, or a listing that is curated, an index or benchmark, or the viewer's own.</summary>
    private IQueryable<NewsStory> Visible(Viewer viewer)
    {
        var q = db.NewsStories.AsNoTracking();
        if (!Options.Feeds.HideOthersInterests) return q;
        var ids = viewer.Visible;
        return q.Where(s => !s.Assets.Any() || s.Assets.Any(x => ids.Contains(x.AssetId) || x.Asset!.AssetType == AssetType.Index));
    }

    private IQueryable<NewsStory> Filter(IQueryable<NewsStory> q, WireQuery query, DateTime since)
    {
        q = q.Where(s => s.LastPublishedAtUtc >= since);
        if (!string.IsNullOrWhiteSpace(query.Region))
        {
            var region = query.Region.Trim().ToUpperInvariant();
            if (region.Length > 8) throw new ValidationException("region", "Unknown region.");
            q = q.Where(s => s.Region == region || s.Assets.Any(x => x.Asset!.Country == region));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            if (term.Length > 100) throw new ValidationException("search", "Search text is too long.");
            q = q.Where(s => s.Headline.ToLower().Contains(term) ||
                             s.Assets.Any(x => x.Asset!.Symbol.ToLower() == term || (x.Asset.Name != null && x.Asset.Name.ToLower().Contains(term))));
        }
        if (ParseAttention(query.Attention) is { } level && level != AttentionLevel.Low)
        {
            var (recent, older) = StoryRules.MinBasePoints(level);
            var dayAgo = Now.AddHours(-24);
            q = q.Where(s => s.BasePoints >= older || (s.BasePoints >= recent && s.LastPublishedAtUtc >= dayAgo));
        }
        if (ParseEventType(query.EventType) is { } type) q = q.Where(s => s.EventType == type);
        return q;
    }

    /// <summary>Newest first; after the cursor's position when given (keyset on time, then id).</summary>
    private static IQueryable<NewsStory> After(IQueryable<NewsStory> q, (DateTime At, Guid Id)? cursor)
    {
        if (cursor is { } c) q = q.Where(s => s.LastPublishedAtUtc < c.At || (s.LastPublishedAtUtc == c.At && s.Id.CompareTo(c.Id) < 0));
        return Ordered(q);
    }

    private static IQueryable<NewsStory> Ordered(IQueryable<NewsStory> q) => q.OrderByDescending(s => s.LastPublishedAtUtc).ThenByDescending(s => s.Id);

    private (DateTime Since, int Hours) Window(WireQuery query, int defaultHours)
    {
        var hours = query.Hours ?? defaultHours;
        if (hours is < 1 or > MaxHours) throw new ValidationException("hours", $"The time window must be between 1 and {MaxHours} hours.");
        return (Now.AddHours(-hours), hours);
    }

    private static int PageSize(WireQuery query, int fallback)
    {
        var size = query.PageSize ?? fallback;
        if (size is < 1 or > 50) throw new ValidationException("pageSize", "Page size must be between 1 and 50.");
        return size;
    }

    private static AttentionLevel? ParseAttention(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : Enum.TryParse<AttentionLevel>(value, true, out var l) && Enum.IsDefined(l) ? l
        : throw new ValidationException("attention", $"Unknown attention level '{value}' (High, Medium or Low).");

    private static NewsEventType? ParseEventType(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : Enum.TryParse<NewsEventType>(value, true, out var t) && Enum.IsDefined(t) ? t
        : throw new ValidationException("eventType", $"Unknown event type '{value}'.");

    private static (DateTime, Guid)? Decode(string? cursor)
    {
        if (cursor is null) return null;
        return StoryRules.TryDecodeCursor(cursor, out var at, out var id) ? (at, id) : throw new ValidationException("cursor", "The page cursor is not valid.");
    }

    private static string? Cursor(NewsStory? s) => s is null ? null : StoryRules.EncodeCursor(s.LastPublishedAtUtc, s.Id);

    private static int DecodeOffset(string? cursor)
    {
        if (cursor is null) return 0;
        return cursor.StartsWith('o') && int.TryParse(cursor.AsSpan(1), out var o) && o is >= 0 and <= 10_000
            ? o : throw new ValidationException("cursor", "The page cursor is not valid.");
    }

    private static string EncodeOffset(int offset) => $"o{offset}";

    // ------------------------------------------------------------------ mapping

    private static Func<NewsStory, IReadOnlyList<WireAssetDto>, (Attention?, List<string>)> WatchlistReasons(Viewer viewer) => (s, assets) =>
    {
        var why = assets.Where(a => a.OnWatchlist).Select(a => $"{a.Symbol} is on your watchlist").ToList();
        if (why.Count == 0 && s.EventType == NewsEventType.RateDecision && s.Region is { } r && StoryRules.CentralBankCurrency.TryGetValue(r, out var currency))
        {
            var n = viewer.WatchedCurrency.Values.Count(c => string.Equals(c, currency, StringComparison.OrdinalIgnoreCase));
            why.Add($"rate decision for the {currency}; {n} of your watched listings trade in {currency}");
        }
        Attention? personal = assets.Any(a => a.OnWatchlist)
            ? new Attention(AttentionLevel.High, 0, ["linked to a listing on your watchlist"])
            : null;
        return (personal, why);
    };

    private async Task<IReadOnlyList<WireStoryDto>> ToDtosAsync(IReadOnlyList<NewsStory> stories, Viewer viewer,
        Func<NewsStory, IReadOnlyList<WireAssetDto>, (Attention?, List<string>)>? personal, CancellationToken ct)
    {
        if (stories.Count == 0) return [];
        var ids = stories.Select(s => s.Id).ToList();
        var members = await db.NewsStoryArticles.AsNoTracking().Where(m => ids.Contains(m.StoryId))
            .Select(m => new { m.StoryId, m.ArticleId, m.Article!.Publisher, m.Article.Url, m.Article.PublishedAtUtc, m.Article.IsOfficialSource, m.Article.Summary })
            .ToListAsync(ct);
        var links = await db.NewsStoryAssets.AsNoTracking().Where(x => ids.Contains(x.StoryId))
            .Select(x => new { x.StoryId, x.AssetId, x.Asset!.Symbol, x.Asset.Name, x.Asset.AssetType, x.Asset.Currency })
            .ToListAsync(ct);
        var now = Now;
        return stories.Select(s =>
        {
            var reports = members.Where(m => m.StoryId == s.Id).OrderBy(m => m.PublishedAtUtc).ThenBy(m => m.ArticleId).ToList();
            var lead = reports.FirstOrDefault(m => m.ArticleId == s.LeadArticleId) ?? reports.FirstOrDefault();
            var assets = links.Where(l => l.StoryId == s.Id)
                .Select(l => new WireAssetDto(l.Symbol, l.Name, l.AssetType.ToString(), l.Currency, viewer.Watched.Contains(l.AssetId), viewer.Held.Contains(l.AssetId)))
                .OrderByDescending(a => a.OnWatchlist || a.Held).ThenBy(a => a.Symbol, StringComparer.Ordinal).ToList();
            var general = StoryRules.Assess(s.EventType, s.Category, s.HasOfficialSource, s.PublisherCount, s.LastPublishedAtUtc, now, s.MarketInstrumentCount);
            var (own, why) = personal?.Invoke(s, assets) ?? (null, []);
            var attention = own is null ? general : new Attention(AttentionLevel.High, general.Points, [.. own.Reasons, .. general.Reasons]);
            return new WireStoryDto(s.Id, s.Headline, lead?.Url ?? "", lead?.Publisher ?? "", s.FirstPublishedAtUtc, s.LastPublishedAtUtc,
                s.Scope.ToString(), s.EventType.ToString(), NewsTaxonomy.Label(s.EventType), s.EventTypeBasis, s.MarketArea?.ToString(), s.Region,
                s.HasOfficialSource, s.IsSimulated, lead?.Summary, s.ArticleCount,
                reports.Take(20).Select(m => new WireSourceItemDto(m.ArticleId, m.Publisher, m.Url, m.PublishedAtUtc, m.IsOfficialSource)).ToList(),
                assets, new WireAttentionDto(attention.Level.ToString(), attention.Points, attention.Reasons, AttentionNote), why, s.RuleVersion);
        }).ToList();
    }

    private static string AreaLabel(NewsMarketArea area) => area switch
    {
        NewsMarketArea.Fx => "Currencies",
        _ => area.ToString(),
    };

    private static IEnumerable<string> CommonNotes() =>
    [
        "A story groups reports of the same event (same subject and event type within 36 hours, mostly the same headline words); every report stays listed with its publisher and time.",
        "Headlines, publishers and times come from the sources; the full article is only at the original link.",
    ];

    // ------------------------------------------------------------------ source freshness

    /// <summary>Freshness per group of sources (no provider names – administrators see those on the source table).</summary>
    private async Task<IReadOnlyList<WireSourceStatusDto>> SourcesAsync(CancellationToken ct)
    {
        if (!Options.Enabled) return [new WireSourceStatusDto("All sources", "Unavailable", null, "News retrieval is disabled by configuration.")];
        var states = await db.NewsFetchStates.AsNoTracking().ToListAsync(ct);
        var configured = companyProviders.Where(p => p.Descriptor.IsConfigured).Select(p => p.Descriptor.Name)
            .Concat(feedProviders.Where(p => p.Descriptor.IsConfigured && p.Feeds.Count > 0).Select(p => p.Descriptor.Name)).ToHashSet();
        var groups = new (string Group, Func<NewsFetchState, bool> Member, string Provider, int Minutes)[]
        {
            ("Central banks", s => s.Provider == "OfficialRss", "OfficialRss", Options.RefreshMinutes),
            ("Statistics offices", s => s.Provider == "OfficialStats", "OfficialStats", Options.RefreshMinutes),
            ("Company filings", s => s.Provider == "SecEdgar", "SecEdgar", Options.Schedule.CompanyRefreshMinutes),
            ("Company news", s => s.Provider == "YahooNews" && s.Key.Contains(":company:"), "YahooNews", Options.Schedule.CompanyRefreshMinutes),
            ("Market news", s => s.Provider == "YahooNews" && !s.Key.Contains(":company:"), "YahooNews", Options.RefreshMinutes),
            ("Simulated news", s => s.Provider == "MockNews", "MockNews", Options.RefreshMinutes),
        };
        var now = Now;
        var result = new List<WireSourceStatusDto>();
        foreach (var (group, member, provider, minutes) in groups)
        {
            if (!configured.Contains(provider)) continue;
            var own = states.Where(member).ToList();
            var lastSuccess = own.Max(s => s.LastSuccessUtc);
            var failing = own.Count(s => s.LastError is not null && s.LastAttemptUtc > (s.LastSuccessUtc ?? DateTime.MinValue));
            var problem = failing > 0 ? $"{failing} of {own.Count} sources failed at their last attempt; stored stories are shown." : null;
            var status = lastSuccess is null ? "Unavailable"
                : now - lastSuccess.Value > TimeSpan.FromMinutes(2.0 * Math.Max(1, minutes)) ? "Delayed"
                : "Fresh";
            result.Add(new WireSourceStatusDto(group, status, lastSuccess, lastSuccess is null ? problem ?? "Not fetched yet." : problem));
        }
        return result;
    }
}
