using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.News;

public sealed record NewsQuery(
    string? Category = null,
    string? Scope = null,
    string? Region = null,
    string? Symbol = null,
    bool WatchlistOnly = false,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Publisher = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 30);

/// <summary>
/// Financial news: fetches from the configured providers on demand (at most once per source per refresh window, shared
/// by all users, one fetch per source at a time), normalizes, classifies with documented rules, links articles to
/// listings only through provider tags, stores them once, and serves filtered feeds. Syndicated copies of a story
/// (same headline) are shown once with the other publishers listed. A provider failure is recorded and reported; it never
/// produces substitute articles.
/// </summary>
public sealed class NewsService(
    IApplicationDbContext db,
    IEnumerable<ICompanyNewsProvider> companyProviders,
    IEnumerable<INewsFeedProvider> feedProviders,
    ICurrentUser currentUser,
    PriceQueryService prices,
    MarketDataService marketData,
    IOptions<NewsOptions> options,
    INewsRefreshScheduler refresher,
    TimeProvider clock,
    ILogger<NewsService> logger)
{
    public const string RelevanceNote =
        "Rule-based indication of how material and well-supported the item is. It does not predict whether or how much any price will move.";

    public const string EventDisclaimer =
        "Event and price-movement comparison: shows how prices moved after publication. It is not evidence that the article caused the move; " +
        "other news, market-wide movements, expectations and liquidity also affect prices. Not investment advice.";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private NewsOptions Options => options.Value;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ queries

    public async Task<NewsFeedDto> GetFeedAsync(NewsQuery query, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        if (query.Page < 1) throw new ValidationException("page", "Page must be ≥ 1.");
        if (query.PageSize is < 1 or > 100) throw new ValidationException("pageSize", "Page size must be between 1 and 100.");
        NewsCategory? category = null;
        if (!string.IsNullOrWhiteSpace(query.Category))
            category = Enum.TryParse<NewsCategory>(query.Category, true, out var c) ? c : throw new ValidationException("category", $"Unknown category '{query.Category}'.");
        NewsScope? scope = null;
        if (!string.IsNullOrWhiteSpace(query.Scope))
            scope = Enum.TryParse<NewsScope>(query.Scope, true, out var s) ? s : throw new ValidationException("scope", $"Unknown scope '{query.Scope}'.");

        var watchlist = await db.WatchlistEntries.AsNoTracking().Where(e => e.OwnerId == owner).OrderBy(e => e.AddedAtUtc)
            .Select(e => new { e.AssetId, e.Asset!.Symbol, e.Asset.Name }).ToListAsync(ct);
        Asset? symbolAsset = null;
        if (!string.IsNullOrWhiteSpace(query.Symbol))
        {
            var sym = Asset.NormalizeSymbol(query.Symbol);
            symbolAsset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Symbol == sym, ct) ?? throw new NotFoundException("Stock", sym);
        }

        var refreshing = false;
        if (Options.Enabled)
        {
            var companies = symbolAsset is not null
                ? [(symbolAsset.Symbol, symbolAsset.Name)]
                : watchlist.Select(w => (w.Symbol, w.Name)).ToList();
            refreshing = await RefreshOrScheduleAsync(companies, stalestOnly: symbolAsset is null, ct);
        }

        var q = db.NewsArticles.AsNoTracking().AsQueryable();
        if (category is { } cat) q = q.Where(a => a.Category == cat);
        if (scope is { } sc) q = q.Where(a => a.Scope == sc);
        if (!string.IsNullOrWhiteSpace(query.Region))
        {
            var region = query.Region.Trim().ToUpperInvariant();
            q = q.Where(a => a.Region == region || a.Assets.Any(x => x.Asset!.Country == region));
        }
        if (symbolAsset is not null) q = q.Where(a => a.Assets.Any(x => x.AssetId == symbolAsset.Id));
        if (query.WatchlistOnly)
        {
            var ids = watchlist.Select(w => w.AssetId).ToList();
            q = q.Where(a => a.Assets.Any(x => ids.Contains(x.AssetId)));
        }
        if (query.From is { } from) { var f = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); q = q.Where(a => a.PublishedAtUtc >= f); }
        if (query.To is { } to) { var t = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); q = q.Where(a => a.PublishedAtUtc < t); }
        if (!string.IsNullOrWhiteSpace(query.Publisher))
        {
            var p = query.Publisher.Trim().ToLower();
            q = q.Where(a => a.Publisher.ToLower() == p);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            q = q.Where(a => a.Headline.ToLower().Contains(term) ||
                             a.Assets.Any(x => x.Asset!.Symbol.ToLower() == term || (x.Asset.Name != null && x.Asset.Name.ToLower().Contains(term))));
        }

        // One row per story: syndicated copies share a headline key.
        var total = await q.Select(a => a.HeadlineKey).Distinct().CountAsync(ct);
        var keys = await q.GroupBy(a => a.HeadlineKey)
            .Select(g => new { Key = g.Key, Latest = g.Max(a => a.PublishedAtUtc) })
            .OrderByDescending(x => x.Latest).ThenBy(x => x.Key)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(ct);
        var keyList = keys.Select(k => k.Key).ToList();
        var articles = await db.NewsArticles.AsNoTracking().Include(a => a.Assets).ThenInclude(x => x.Asset)
            .Where(a => keyList.Contains(a.HeadlineKey)).ToListAsync(ct);
        var watched = watchlist.Select(w => w.AssetId).ToHashSet();
        var items = keys.Select(k => ToDto(articles.Where(a => a.HeadlineKey == k.Key).ToList(), watched)).ToList();

        return new NewsFeedDto(items, query.Page, query.PageSize, total, await SourcesAsync(ct), Options.ShowImages, Notes(), refreshing);
    }

    public async Task<IReadOnlyList<NewsSourceStatusDto>> SourcesAsync(CancellationToken ct)
    {
        var states = await db.NewsFetchStates.AsNoTracking().ToListAsync(ct);
        var result = new List<NewsSourceStatusDto>();
        foreach (var p in feedProviders)
            foreach (var feed in p.Feeds)
            {
                var st = states.FirstOrDefault(s => s.Key == FeedKey(p, feed));
                result.Add(new NewsSourceStatusDto(FeedKey(p, feed), p.Descriptor.Name, $"{feed.Publisher} ({p.Descriptor.DisplayName})", p.Descriptor.IsConfigured && Options.Enabled,
                    feed.IsOfficial, p.Descriptor.IsSimulated, $"{feed.Category} · {feed.Region}", feed.Terms,
                    st?.LastAttemptUtc, st?.LastSuccessUtc, st?.LastArticleCount ?? 0, st?.LastError));
            }
        foreach (var p in companyProviders)
        {
            var own = states.Where(s => s.Provider == p.Descriptor.Name && s.Key.StartsWith(p.Descriptor.Name + ":company:")).ToList();
            var last = own.MaxBy(s => s.LastAttemptUtc);
            var failing = own.Where(s => s.LastError is not null && s.LastAttemptUtc > s.LastSuccessUtc.GetValueOrDefault()).ToList();
            result.Add(new NewsSourceStatusDto($"{p.Descriptor.Name}:company", p.Descriptor.Name, p.Descriptor.DisplayName, p.Descriptor.IsConfigured && Options.Enabled,
                false, p.Descriptor.IsSimulated, p.Descriptor.Coverage, p.Descriptor.Terms, last?.LastAttemptUtc,
                own.Max(s => s.LastSuccessUtc), own.Sum(s => s.LastArticleCount),
                failing.Count == 0 ? null : $"{failing.Count} company quer{(failing.Count == 1 ? "y" : "ies")} failed recently, e.g. {failing[0].LastError}"));
        }
        return result;
    }

    // ------------------------------------------------------------------ event comparison

    public async Task<EventComparisonDto> GetEventComparisonAsync(Guid articleId, string symbol, CancellationToken ct)
    {
        currentUser.RequireUserId();
        var article = await db.NewsArticles.AsNoTracking().Include(a => a.Assets).ThenInclude(x => x.Asset)
            .SingleOrDefaultAsync(a => a.Id == articleId, ct) ?? throw new NotFoundException("Article", articleId);
        var sym = Asset.NormalizeSymbol(symbol);
        var link = article.Assets.FirstOrDefault(x => x.Asset!.Symbol == sym)
                   ?? throw new ValidationException("symbol", $"The provider did not tag this article with {sym}; choose one of its linked stocks.");
        var asset = link.Asset!;
        var venue = Markets.ForSymbol(asset.Symbol);
        var benchmark = await BenchmarkForAsync(asset, ct);
        var settings = await prices.SettingsAsync(ct);
        var day = DateOnly.FromDateTime(article.PublishedAtUtc);
        var symbols = benchmark is null ? new[] { asset.Symbol } : [asset.Symbol, benchmark];
        var series = await prices.GetDailyObservationsAsync(symbols, settings.ActiveProvider, day.AddDays(-30), day.AddDays(60), ct);
        var sb = series.GetValueOrDefault(asset.Symbol);
        var stockBars = sb is null ? [] : sb.Bars.Select(b => new DailyClose(b.Date, b.Price)).ToList();
        var benchBars = benchmark is not null && series.GetValueOrDefault(benchmark) is { } bb ? bb.Bars.Select(b => new DailyClose(b.Date, b.Price)).ToList() : [];

        var result = EventPriceComparison.Compute(article.PublishedAtUtc, venue, stockBars, benchBars);
        var limitations = result.Limitations.ToList();
        if (stockBars.Count == 0)
        {
            var queued = await marketData.EnsureHistoryAsync(benchmark is null ? [asset.Symbol] : [asset.Symbol, benchmark], day.AddYears(-1), 20, ct);
            limitations.Insert(0, queued.Count > 0
                ? "No stored prices around the publication date; a history download was requested – try again shortly."
                : "No stored prices around the publication date.");
        }
        if (benchmark is null) limitations.Add($"No benchmark in {asset.Currency} is configured for this market, so no market comparison is shown.");
        else if (benchBars.Count == 0) limitations.Add($"No stored prices for the benchmark {benchmark} around the publication date.");
        var usesAdjusted = sb?.UsesAdjustedClose ?? false;

        var watched = await WatchedAssetIdsAsync(ct);
        var from = article.PublishedAtUtc.AddDays(-3);
        var to = article.PublishedAtUtc.AddDays(3);
        var overlapping = await db.NewsArticles.AsNoTracking().Include(a => a.Assets).ThenInclude(x => x.Asset)
            .Where(a => a.Id != article.Id && a.HeadlineKey != article.HeadlineKey && a.PublishedAtUtc >= from && a.PublishedAtUtc <= to &&
                        a.Assets.Any(x => x.AssetId == asset.Id))
            .OrderBy(a => a.PublishedAtUtc).Take(10).ToListAsync(ct);
        if (overlapping.Count > 0)
            limitations.Add($"{overlapping.Count} other stored article(s) about {asset.Symbol} were published within three days; their effects cannot be separated.");

        return new EventComparisonDto(article.Id, article.Headline, asset.Symbol, asset.Currency, benchmark,
            usesAdjusted ? "Adjusted close (splits and dividends)" : "Close (raw)", settings.ActiveProvider, sb?.IsSimulated ?? false,
            article.PublishedAtUtc, result.PublishedLocal, result.Timing, result.ReferenceDate, result.ReferencePrice,
            result.Horizons.Select(h => new EventHorizonDto(h.TradingDays, h.Date, h.StockChange, h.BenchmarkChange, h.Difference, h.Limitation)).ToList(),
            overlapping.Select(a => ToDto([a], watched)).ToList(), limitations, EventDisclaimer);
    }

    /// <summary>Benchmark of the stock's current index in the stock's currency, else the market's default benchmark.</summary>
    private async Task<string?> BenchmarkForAsync(Asset asset, CancellationToken ct)
    {
        var fromIndex = await db.IndexMemberships.AsNoTracking().Where(m => m.AssetId == asset.Id)
            .Join(db.MarketIndices, m => m.IndexCode, i => i.Code, (m, i) => i)
            .Where(i => !i.IsHistorical && i.BenchmarkSymbol != null && i.Currency == asset.Currency)
            .OrderBy(i => i.Code).Select(i => i.BenchmarkSymbol).FirstOrDefaultAsync(ct);
        if (fromIndex is not null) return fromIndex;
        return await db.MarketIndices.AsNoTracking()
            .Where(i => i.Country == Markets.ForSymbol(asset.Symbol).Country && !i.IsHistorical && i.BenchmarkSymbol != null && i.Currency == asset.Currency)
            .OrderBy(i => i.Code).Select(i => i.BenchmarkSymbol).FirstOrDefaultAsync(ct);
    }

    // ------------------------------------------------------------------ fetching and ingestion

    private static string FeedKey(INewsFeedProvider p, NewsFeed feed) => $"{p.Descriptor.Name}:{feed.Key}";

    /// <summary>
    /// Sources never fetched before are fetched now (there is nothing stored to show yet); sources that are merely due are
    /// refreshed in the background while the stored articles are served. Returns whether a background refresh is pending.
    /// All fetch states are read in one query.
    /// </summary>
    private async Task<bool> RefreshOrScheduleAsync(IReadOnlyList<(string Symbol, string? Name)> companies, bool stalestOnly, CancellationToken ct)
    {
        var states = (await db.NewsFetchStates.AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.Key);
        var feedKeys = feedProviders.Where(p => p.Descriptor.IsConfigured).SelectMany(p => p.Feeds.Select(f => FeedKey(p, f))).ToList();
        var configuredCompanyProviders = companyProviders.Where(p => p.Descriptor.IsConfigured).ToList();
        IEnumerable<ICompanyNewsProvider> For(string symbol) => configuredCompanyProviders.Where(p => p.Supports(symbol));
        DateTime LastAttempt(string symbol) => For(symbol).Select(p => states.GetValueOrDefault(CompanyKey(p, symbol))?.LastAttemptUtc ?? DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue).Min();
        // A large watchlist refreshes only its stalest few per request, so it never causes request bursts.
        var chosen = stalestOnly ? companies.OrderBy(c => LastAttempt(c.Symbol)).Take(Options.WatchlistRefreshLimit).ToList() : companies.ToList();

        if (feedKeys.Any(k => !states.ContainsKey(k))) await RefreshFeedsAsync(ct);
        foreach (var c in chosen.Where(c => For(c.Symbol).Any(p => !states.ContainsKey(CompanyKey(p, c.Symbol)))))
            await RefreshCompanyAsync(c.Symbol, c.Name, Options.RefreshMinutes, ct);

        var feedsDue = feedKeys.Any(k => states.TryGetValue(k, out var s) && IsDue(s));
        var companiesDue = chosen.Where(c => For(c.Symbol).Any(p => states.TryGetValue(CompanyKey(p, c.Symbol), out var s) && IsDue(s))).ToList();
        if (feedsDue || companiesDue.Count > 0) refresher.Schedule(new NewsRefreshRequest(feedsDue, companiesDue));
        return refresher.IsBusy;
    }

    /// <summary>
    /// What a Wire feed request does before reading: sources never fetched are fetched now, due feeds and the viewer's
    /// stalest watched companies are refreshed in the background. Returns whether a background refresh is pending.
    /// </summary>
    public async Task<bool> RefreshForViewerAsync(IReadOnlyList<(string Symbol, string? Name)> watched, CancellationToken ct) =>
        Options.Enabled && await RefreshOrScheduleAsync(watched, stalestOnly: true, ct);

    /// <summary>Refreshes the requested sources that are (still) due; called by the background refresher.</summary>
    public async Task RefreshDueAsync(NewsRefreshRequest request, CancellationToken ct)
    {
        if (!Options.Enabled) return;
        if (request.Feeds) await RefreshFeedsAsync(ct);
        foreach (var (symbol, name) in request.Companies) await RefreshCompanyAsync(symbol, name, Options.RefreshMinutes, ct);
    }

    /// <summary>
    /// Scheduled run (background, no user context): every due feed, then the due companies of the scheduled set – held in
    /// any portfolio, on any watchlist, curated – in that priority, stalest first, at most <c>Schedule.CompaniesPerRun</c>.
    /// Fetching is shared; what a user sees stays scoped by the feed queries.
    /// </summary>
    public async Task<NewsScheduleRun> RefreshScheduledAsync(CancellationToken ct)
    {
        if (!Options.Enabled) return new NewsScheduleRun(0, 0);
        await RefreshFeedsAsync(ct);

        var held = await db.Holdings.AsNoTracking().Where(h => h.Quantity > 0 && h.Asset!.IsEnabled)
            .Select(h => new { h.Asset!.Symbol, h.Asset.Name }).Distinct().ToListAsync(ct);
        var watched = await db.WatchlistEntries.AsNoTracking().Where(e => e.Asset!.IsEnabled)
            .Select(e => new { e.Asset!.Symbol, e.Asset.Name }).Distinct().ToListAsync(ct);
        var curatedSymbols = Options.Schedule.CuratedSymbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
        var curated = await db.Assets.AsNoTracking().Where(a => a.IsEnabled && curatedSymbols.Contains(a.Symbol))
            .Select(a => new { a.Symbol, a.Name }).ToListAsync(ct);
        var merged = NewsSchedule.Merge(
            held.Select(x => new ScheduledCompany(x.Symbol, x.Name, ScheduledCompanySource.Held))
                .Concat(watched.Select(x => new ScheduledCompany(x.Symbol, x.Name, ScheduledCompanySource.Watched)))
                .Concat(curated.Select(x => new ScheduledCompany(x.Symbol, x.Name, ScheduledCompanySource.Curated))));

        var states = (await db.NewsFetchStates.AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.Key);
        var providers = companyProviders.Where(p => p.Descriptor.IsConfigured).ToList();
        var interval = Options.Schedule.CompanyRefreshMinutes;
        var due = NewsSchedule.SelectDue(merged,
            symbol => providers.Where(p => p.Supports(symbol)).Any(p => IsDue(states.GetValueOrDefault(CompanyKey(p, symbol)), interval)),
            symbol => providers.Where(p => p.Supports(symbol)).Select(p => states.GetValueOrDefault(CompanyKey(p, symbol))?.LastAttemptUtc).Min(),
            Options.Schedule.CompaniesPerRun);
        foreach (var c in due) await RefreshCompanyAsync(c.Symbol, c.Name, interval, ct);
        // Articles stored before the story layer existed, or by a run that stopped early, are grouped here in batches.
        for (var batch = 0; batch < 20 && await GroupPendingAsync(ct) > 0; batch++) { }
        // Articles classified by an older rules version are classified again from their stored inputs, a few batches per run.
        var reclassified = 0;
        for (var batch = 0; batch < 10; batch++)
        {
            var n = await ReclassifyPendingAsync(ct);
            reclassified += n;
            if (n == 0) break;
        }
        // Articles linked by an older entity list (or before entity links existed) are linked again, a few batches per run.
        var linked = 0;
        for (var batch = 0; batch < 10; batch++)
        {
            var n = await LinkEntitiesPendingAsync(ct);
            linked += n;
            if (n == 0) break;
        }
        return new NewsScheduleRun(merged.Count, due.Count, reclassified, linked);
    }

    private const int EntityLinkBatch = 500;

    /// <summary>
    /// Links stored articles of an older <see cref="NewsEntities.RuleVersion"/> (0 = before entity links) again, at most
    /// <see cref="EntityLinkBatch"/> per call, from their stored provider tags (none before 2026-11-08), official flag, the
    /// category the source stated, region and publisher. Links that the current list no longer gives are removed; the
    /// stories containing these articles get the union of their reports' links again. Returns how many articles were
    /// linked; safe to re-run.
    /// </summary>
    public async Task<int> LinkEntitiesPendingAsync(CancellationToken ct)
    {
        var pending = await db.NewsArticles.Include(a => a.Entities)
            .Where(a => a.EntityRuleVersion < NewsEntities.RuleVersion)
            .OrderBy(a => a.PublishedAtUtc).ThenBy(a => a.Id).Take(EntityLinkBatch).ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var keysByArticle = new Dictionary<Guid, List<string>>();
        foreach (var a in pending)
        {
            var matches = NewsEntities.Match(NewsTaxonomy.SplitTags(a.ProviderTags), a.IsOfficialSource, a.SourceCategory, a.Region, a.Publisher, a.IsSimulated);
            foreach (var stale in a.Entities.Where(x => matches.All(m => !string.Equals(m.Key, x.EntityKey, StringComparison.OrdinalIgnoreCase))).ToList())
                a.Entities.Remove(stale);
            foreach (var m in matches.Where(m => a.Entities.All(x => !string.Equals(x.EntityKey, m.Key, StringComparison.OrdinalIgnoreCase))))
                a.Entities.Add(new NewsArticleEntity { ArticleId = a.Id, EntityKey = m.Key, MatchMethod = Truncate(m.MatchMethod, 60) });
            a.EntityRuleVersion = NewsEntities.RuleVersion;
            keysByArticle[a.Id] = matches.Select(m => m.Key).ToList();
        }

        // Stories hold the union of their reports' links: recompute it for every story containing one of these articles.
        var ids = pending.Select(a => a.Id).ToList();
        var stories = await db.NewsStories.Include(s => s.Entities)
            .Where(s => db.NewsStoryArticles.Any(m => m.StoryId == s.Id && ids.Contains(m.ArticleId))).ToListAsync(ct);
        var storyIds = stories.Select(s => s.Id).ToList();
        var members = await db.NewsStoryArticles.AsNoTracking().Where(m => storyIds.Contains(m.StoryId)).Select(m => new { m.StoryId, m.ArticleId }).ToListAsync(ct);
        var others = members.Select(m => m.ArticleId).Where(id => !keysByArticle.ContainsKey(id)).Distinct().ToList();
        foreach (var row in await db.NewsArticleEntities.AsNoTracking().Where(x => others.Contains(x.ArticleId)).Select(x => new { x.ArticleId, x.EntityKey }).ToListAsync(ct))
            (keysByArticle.TryGetValue(row.ArticleId, out var list) ? list : keysByArticle[row.ArticleId] = []).Add(row.EntityKey);
        foreach (var story in stories)
        {
            var keys = members.Where(m => m.StoryId == story.Id).SelectMany(m => keysByArticle.GetValueOrDefault(m.ArticleId) ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in story.Entities.Where(x => !keys.Contains(x.EntityKey)).ToList()) story.Entities.Remove(stale);
            foreach (var key in keys.Where(k => story.Entities.All(x => !string.Equals(x.EntityKey, k, StringComparison.OrdinalIgnoreCase))))
                story.Entities.Add(new NewsStoryEntity { StoryId = story.Id, EntityKey = key });
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
        {
            db.ChangeTracker.Clear(); // another instance linked the same articles first; nothing of this batch was written
            logger.LogInformation("News entity linking: another instance linked the same articles first; retried on the next run.");
            return 0;
        }
        return pending.Count;
    }

    private const int ReclassificationBatch = 500;

    /// <summary>Why a reclassified article's scope may be less precise than one classified when it was stored.</summary>
    public const string TagsNotStoredNote =
        "Provider tags were not stored before 2026-11-08; scope and market area follow from the linked listings and the category only.";

    /// <summary>
    /// When provider tags began to be stored (migration NewsProviderTags, 2026-11-08 21:50 UTC). An article retrieved later
    /// without tags simply had none (official feeds deliver none), so only earlier ones get <see cref="TagsNotStoredNote"/>.
    /// </summary>
    public static readonly DateTime ProviderTagsStoredSinceUtc = new(2026, 11, 8, 21, 50, 15, DateTimeKind.Utc);

    /// <summary>A history row with the article's current classification.</summary>
    private static NewsArticleClassification HistoryRow(NewsArticle a, ClassificationTrigger trigger, string? note) => new()
    {
        ArticleId = a.Id, RuleVersion = a.RuleVersion, Category = a.Category, CategoryBasis = a.CategoryBasis, Scope = a.Scope,
        EventType = a.EventType, EventTypeBasis = a.EventTypeBasis, MarketArea = a.MarketArea,
        ProcessedAtUtc = a.ProcessedAtUtc ?? a.RetrievedAtUtc, Trigger = trigger, Note = note,
    };

    /// <summary>
    /// Classifies stored articles of an older rules version again under <see cref="NewsTaxonomy.RuleVersion"/>, at most
    /// <see cref="ReclassificationBatch"/> per call, from what was stored: headline, summary, provider tags (none before
    /// 2026-11-08 – noted on the row), linked listings, official flag and what the source stated. Each gets a new history
    /// row; earlier rows stay. Stories containing a reclassified report take their lead report's classification and
    /// attention points again; their reports are never regrouped. Returns how many were reclassified; safe to re-run.
    /// </summary>
    public async Task<int> ReclassifyPendingAsync(CancellationToken ct)
    {
        var pending = await db.NewsArticles.Include(a => a.Assets).ThenInclude(x => x.Asset)
            .Where(a => a.RuleVersion < NewsTaxonomy.RuleVersion)
            .OrderBy(a => a.PublishedAtUtc).ThenBy(a => a.Id).Take(ReclassificationBatch).ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var tags = pending.SelectMany(a => NewsTaxonomy.SplitTags(a.ProviderTags)).Select(Asset.NormalizeSymbol).Distinct().ToList();
        var nonEquityTags = (await db.Assets.AsNoTracking().Where(x => tags.Contains(x.Symbol) && x.AssetType != AssetType.Equity)
            .Select(x => x.Symbol).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = Now;
        foreach (var a in pending)
        {
            var own = NewsTaxonomy.SplitTags(a.ProviderTags);
            var equities = a.Assets.Count(x => x.Asset!.AssetType == AssetType.Equity);
            var fundTags = own.Where(t => nonEquityTags.Contains(Asset.NormalizeSymbol(t))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var c = NewsTaxonomy.ClassifyArticle(new ArticleClassificationInput(a.Headline, a.Summary, own, equities, a.Assets.Count - equities, fundTags,
                a.IsOfficialSource, a.SourceCategory, a.SourceEventType, a.SourceEventTypeBasis, a.Region));
            a.Category = c.Category;
            a.CategoryBasis = Truncate(c.CategoryBasis, 200);
            a.Scope = c.Scope;
            a.EventType = c.EventType;
            a.EventTypeBasis = Truncate(c.EventTypeBasis, 200);
            a.MarketArea = c.MarketArea;
            a.RuleVersion = NewsTaxonomy.RuleVersion;
            a.ProcessedAtUtc = now;
            db.NewsArticleClassifications.Add(HistoryRow(a, ClassificationTrigger.Reclassification,
                a.ProviderTags is null && a.RetrievedAtUtc < ProviderTagsStoredSinceUtc ? TagsNotStoredNote : null));
        }

        // Stories copy their lead report's classification for indexed feed queries; refresh those that contain one of these reports.
        var ids = pending.Select(a => a.Id).ToList();
        var stories = await db.NewsStories.Where(s => db.NewsStoryArticles.Any(m => m.StoryId == s.Id && ids.Contains(m.ArticleId))).ToListAsync(ct);
        var leadIds = stories.Select(s => s.LeadArticleId).ToList();
        var leads = pending.Where(a => leadIds.Contains(a.Id)).ToDictionary(a => a.Id);
        foreach (var missing in leadIds.Where(id => !leads.ContainsKey(id)).ToList() is { Count: > 0 } rest
                     ? await db.NewsArticles.Where(a => rest.Contains(a.Id)).ToListAsync(ct) : [])
            leads[missing.Id] = missing;
        foreach (var (story, lead) in stories.Where(s => leads.ContainsKey(s.LeadArticleId)).Select(s => (s, leads[s.LeadArticleId])))
        {
            story.Scope = lead.Scope;
            story.Category = lead.Category;
            story.EventType = lead.EventType ?? NewsEventType.General;
            story.EventTypeBasis = lead.EventTypeBasis;
            story.MarketArea = lead.MarketArea;
            story.BasePoints = StoryRules.BasePoints(story.EventType, story.Category, story.HasOfficialSource, story.PublisherCount, story.MarketInstrumentCount);
            story.UpdatedAtUtc = now;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
        {
            db.ChangeTracker.Clear(); // another instance reclassified the same articles first; nothing of this batch was written
            logger.LogInformation("News reclassification: another instance reclassified the same articles first; retried on the next run.");
            return 0;
        }
        return pending.Count;
    }

    private static readonly SemaphoreSlim GroupingGate = new(1, 1);
    private const int GroupingBatch = 1000;

    /// <summary>
    /// Puts stored articles that belong to no story into stories (<see cref="StoryRules"/>), oldest first, at most
    /// <see cref="GroupingBatch"/> per call; returns how many were grouped. One grouping at a time in this process; a
    /// concurrent instance that grouped the same article first wins (unique article index) and this batch is retried later.
    /// </summary>
    public async Task<int> GroupPendingAsync(CancellationToken ct)
    {
        await GroupingGate.WaitAsync(ct);
        try
        {
            var pending = await db.NewsArticles.Include(a => a.Assets).Include(a => a.Entities)
                .Where(a => !db.NewsStoryArticles.Any(s => s.ArticleId == a.Id))
                .OrderBy(a => a.PublishedAtUtc).ThenBy(a => a.Id).Take(GroupingBatch).ToListAsync(ct);
            if (pending.Count == 0) return 0;

            var from = pending[0].PublishedAtUtc - StoryRules.Window;
            var to = pending[^1].PublishedAtUtc + StoryRules.Window;
            var stories = await db.NewsStories.Include(s => s.Assets).Include(s => s.Entities)
                .Where(s => s.LastPublishedAtUtc >= from && s.FirstPublishedAtUtc <= to).ToListAsync(ct);
            var storyIds = stories.Select(s => s.Id).ToList();
            var members = await db.NewsStoryArticles.AsNoTracking().Where(m => storyIds.Contains(m.StoryId))
                .Select(m => new { m.StoryId, m.Article!.HeadlineKey, m.Article.Publisher }).ToListAsync(ct);
            var keys = stories.ToDictionary(s => s.Id, s => members.Where(m => m.StoryId == s.Id).Select(m => m.HeadlineKey).ToHashSet(StringComparer.Ordinal));
            var publishers = stories.ToDictionary(s => s.Id, s => members.Where(m => m.StoryId == s.Id).Select(m => m.Publisher).ToHashSet(StringComparer.OrdinalIgnoreCase));
            StoryCandidate Candidate(NewsStory s) => new(s.Id, s.Headline, s.Scope, s.EventType, s.Region, s.FirstPublishedAtUtc, s.LastPublishedAtUtc,
                s.Assets.Select(x => x.AssetId).ToHashSet(), keys[s.Id]);

            var now = Now;
            foreach (var a in pending)
            {
                var type = a.EventType ?? NewsEventType.General;
                var facts = new StoryArticleFacts(a.Headline, a.HeadlineKey, a.Scope, type, a.Region, a.PublishedAtUtc, a.Assets.Select(x => x.AssetId).ToHashSet());
                var chosen = StoryRules.Choose(stories.Select(Candidate), facts);
                var story = chosen is null ? null : stories.Single(s => s.Id == chosen.Id);
                if (story is null)
                {
                    story = new NewsStory
                    {
                        Id = Guid.NewGuid(), LeadArticleId = a.Id, Headline = a.Headline, Scope = a.Scope, Category = a.Category, EventType = type,
                        EventTypeBasis = a.EventTypeBasis, MarketArea = a.MarketArea, Region = a.Region,
                        FirstPublishedAtUtc = a.PublishedAtUtc, LastPublishedAtUtc = a.PublishedAtUtc, RuleVersion = StoryRules.RuleVersion, CreatedAtUtc = now,
                    };
                    db.NewsStories.Add(story);
                    stories.Add(story);
                    keys[story.Id] = new HashSet<string>(StringComparer.Ordinal);
                    publishers[story.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                else if (a.PublishedAtUtc < story.FirstPublishedAtUtc)
                {
                    // An earlier report becomes the lead: the story shows the first publication.
                    story.LeadArticleId = a.Id;
                    story.Headline = a.Headline;
                }
                story.Articles.Add(new NewsStoryArticle { StoryId = story.Id, ArticleId = a.Id, JoinedAtUtc = now });
                foreach (var assetId in facts.AssetIds.Where(id => story.Assets.All(x => x.AssetId != id)))
                    story.Assets.Add(new NewsStoryAsset { StoryId = story.Id, AssetId = assetId });
                foreach (var key in a.Entities.Select(x => x.EntityKey).Where(k => story.Entities.All(x => x.EntityKey != k)))
                    story.Entities.Add(new NewsStoryEntity { StoryId = story.Id, EntityKey = key });
                keys[story.Id].Add(a.HeadlineKey);
                publishers[story.Id].Add(a.Publisher);
                story.FirstPublishedAtUtc = story.ArticleCount == 0 ? a.PublishedAtUtc : Min(story.FirstPublishedAtUtc, a.PublishedAtUtc);
                story.LastPublishedAtUtc = story.ArticleCount == 0 ? a.PublishedAtUtc : Max(story.LastPublishedAtUtc, a.PublishedAtUtc);
                story.ArticleCount++;
                story.PublisherCount = publishers[story.Id].Count;
                story.HasOfficialSource |= a.IsOfficialSource;
                story.IsSimulated |= a.IsSimulated;
                var instruments = (a.ProviderTags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Count(t => NewsTaxonomy.Kind(t) != InstrumentKind.Security);
                story.MarketInstrumentCount = Math.Max(story.MarketInstrumentCount, instruments);
                story.BasePoints = StoryRules.BasePoints(story.EventType, story.Category, story.HasOfficialSource, story.PublisherCount, story.MarketInstrumentCount);
                story.RuleVersion = StoryRules.RuleVersion;
                story.UpdatedAtUtc = now;
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
            {
                db.ChangeTracker.Clear(); // nothing of this batch was written; the next run regroups what is still pending
                logger.LogInformation("News grouping: another instance grouped the same articles first; retried on the next run.");
                return 0;
            }
            return pending.Count;
        }
        finally
        {
            GroupingGate.Release();
        }
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static string CompanyKey(ICompanyNewsProvider p, string symbol) => $"{p.Descriptor.Name}:company:{symbol}";

    private async Task RefreshFeedsAsync(CancellationToken ct)
    {
        foreach (var p in feedProviders.Where(p => p.Descriptor.IsConfigured))
            foreach (var feed in p.Feeds)
                await RefreshKeyAsync(FeedKey(p, feed), p.Descriptor.Name, t => p.GetFeedAsync(feed, t), Options.RefreshMinutes, ct);
    }

    private async Task RefreshCompanyAsync(string symbol, string? name, int intervalMinutes, CancellationToken ct)
    {
        foreach (var p in companyProviders.Where(p => p.Descriptor.IsConfigured && p.Supports(symbol)))
            await RefreshKeyAsync(CompanyKey(p, symbol), p.Descriptor.Name, t => p.GetCompanyNewsAsync(symbol, name, t), intervalMinutes, ct);
    }

    private async Task RefreshKeyAsync(string key, string provider, Func<CancellationToken, Task<IReadOnlyList<ProviderArticle>>> fetch,
        int intervalMinutes, CancellationToken ct)
    {
        if (!IsDue(await db.NewsFetchStates.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct), intervalMinutes)) return;
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return; // another request is fetching this source right now; serve stored articles
        try
        {
            var state = await db.NewsFetchStates.SingleOrDefaultAsync(s => s.Key == key, ct);
            if (!IsDue(state, intervalMinutes)) return;
            if (state is null)
            {
                state = new NewsFetchState { Key = key, Provider = provider };
                db.NewsFetchStates.Add(state);
            }
            state.LastAttemptUtc = Now;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(FetchTimeout);
                var articles = await fetch(timeout.Token);
                state.LastArticleCount = await IngestAsync(articles, ct);
                state.LastSuccessUtc = Now;
                state.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("News fetch {Key} failed: {Message}", key, ex.Message);
                state.LastError = ex is OperationCanceledException ? "Timed out." : Truncate(ex.Message, 300);
            }
            try
            {
                await db.SaveChangesAsync(ct);
                if (state.LastArticleCount > 0) await GroupPendingAsync(ct); // new articles join or start stories right away
            }
            catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
            {
                // Another instance stored the same state/article concurrently; the next request reads its result.
                db.ChangeTracker.Clear();
                logger.LogInformation("News fetch {Key}: concurrent update ignored.", key);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsDue(NewsFetchState? state) => IsDue(state, Options.RefreshMinutes);

    private bool IsDue(NewsFetchState? state, int intervalMinutes)
    {
        if (state?.LastAttemptUtc is not { } last) return true;
        var failed = state.LastError is not null;
        return Now - last >= TimeSpan.FromMinutes(failed ? Options.FailureBackoffMinutes : intervalMinutes);
    }

    /// <summary>Normalizes and stores new articles; returns how many of the delivered articles were valid.</summary>
    public async Task<int> IngestAsync(IReadOnlyList<ProviderArticle> delivered, CancellationToken ct)
    {
        var now = Now;
        var oldest = now.AddDays(-Options.RetentionDays);
        var valid = delivered
            .Where(a => !string.IsNullOrWhiteSpace(a.Headline) && Uri.TryCreate(a.Url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            .Where(a => a.PublishedAtUtc <= now.AddMinutes(10) && a.PublishedAtUtc >= oldest) // no future or expired timestamps
            .GroupBy(a => NewsNormalizer.Fingerprint(a.Url)).Select(g => (Fingerprint: g.Key, Article: g.First()))
            .ToList();
        if (valid.Count == 0) return 0;

        var fingerprints = valid.Select(v => v.Fingerprint).ToList();
        var existing = await db.NewsArticles.Include(a => a.Assets).Where(a => fingerprints.Contains(a.Fingerprint)).ToDictionaryAsync(a => a.Fingerprint, ct);
        var symbols = valid.SelectMany(v => v.Article.RelatedSymbols).Select(Asset.NormalizeSymbol).Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);

        foreach (var (fingerprint, a) in valid)
        {
            var linked = a.RelatedSymbols.Select(Asset.NormalizeSymbol).Distinct().Where(assets.ContainsKey).Select(s => assets[s]).ToList();
            var matchMethod = Truncate(a.MatchMethod ?? $"{a.Provider} ticker tag", 60);
            if (existing.TryGetValue(fingerprint, out var stored))
            {
                foreach (var asset in linked.Where(x => stored.Assets.All(l => l.AssetId != x.Id)))
                    stored.Assets.Add(new NewsArticleAsset { ArticleId = stored.Id, AssetId = asset.Id, MatchMethod = matchMethod });
                continue;
            }
            var headline = NewsNormalizer.Clean(a.Headline);
            var summary = NewsNormalizer.Teaser(a.Summary);
            var equities = linked.Count(x => x.AssetType == AssetType.Equity);
            var fundTags = a.RelatedSymbols.Where(t => assets.TryGetValue(Asset.NormalizeSymbol(t), out var x) && x.AssetType != AssetType.Equity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var c = NewsTaxonomy.ClassifyArticle(new ArticleClassificationInput(headline, summary, a.RelatedSymbols, equities, linked.Count - equities, fundTags,
                a.IsOfficial, a.SourceCategory, a.SourceEventType, a.SourceEventBasis, a.Region));
            var article = new NewsArticle
            {
                Id = Guid.NewGuid(),
                Provider = a.Provider,
                ProviderArticleId = a.ProviderId is { Length: > 0 } id ? Truncate(id, 200) : null,
                Url = Truncate(a.Url, 2000),
                Fingerprint = fingerprint,
                HeadlineKey = NewsNormalizer.HeadlineKey(headline),
                Headline = Truncate(headline, 500),
                Publisher = Truncate(string.IsNullOrWhiteSpace(a.Publisher) ? "Unknown publisher" : NewsNormalizer.Clean(a.Publisher), 200),
                PublishedAtUtc = DateTime.SpecifyKind(a.PublishedAtUtc, DateTimeKind.Utc),
                RetrievedAtUtc = now,
                Summary = summary,
                ImageUrl = a.ImageUrl is { } img && img.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? Truncate(img, 2000) : null,
                Category = c.Category,
                CategoryBasis = Truncate(c.CategoryBasis, 200),
                Scope = c.Scope,
                Region = a.Region ?? linked.Select(x => x.Country).FirstOrDefault(c => c is not null),
                IsOfficialSource = a.IsOfficial,
                IsSimulated = a.IsSimulated,
                EventType = c.EventType,
                EventTypeBasis = Truncate(c.EventTypeBasis, 200),
                MarketArea = c.MarketArea,
                RuleVersion = NewsTaxonomy.RuleVersion,
                ProcessedAtUtc = now,
                ProviderTags = a.RelatedSymbols.Count == 0 ? null : Truncate(string.Join(',', a.RelatedSymbols.Distinct()), 500),
                SourceCategory = a.SourceCategory,
                SourceEventType = a.SourceEventType,
                SourceEventTypeBasis = a.SourceEventBasis is null ? null : Truncate(a.SourceEventBasis, 200),
            };
            article.Classifications.Add(HistoryRow(article, ClassificationTrigger.Ingest, null));
            article.Entities.AddRange(NewsEntities.Match(a.RelatedSymbols, a.IsOfficial, a.SourceCategory, article.Region, article.Publisher, a.IsSimulated)
                .Select(m => new NewsArticleEntity { ArticleId = article.Id, EntityKey = m.Key, MatchMethod = Truncate(m.MatchMethod, 60) }));
            article.EntityRuleVersion = NewsEntities.RuleVersion;
            article.Assets.AddRange(linked.Select(x => new NewsArticleAsset { ArticleId = article.Id, AssetId = x.Id, MatchMethod = matchMethod }));
            db.NewsArticles.Add(article);
            existing[fingerprint] = article;
        }
        await db.NewsArticles.Where(a => a.PublishedAtUtc < oldest).ExecuteDeleteAsync(ct);
        // A story whose every report is past the retention goes with them (its memberships went with the articles).
        await db.NewsStories.Where(s => s.LastPublishedAtUtc < oldest).ExecuteDeleteAsync(ct);
        return valid.Count;
    }

    // ------------------------------------------------------------------ mapping

    private async Task<HashSet<int>> WatchedAssetIdsAsync(CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        return (await db.WatchlistEntries.AsNoTracking().Where(e => e.OwnerId == owner).Select(e => e.AssetId).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>One story: the earliest-published copy represents it; the other copies' publishers corroborate it.</summary>
    private NewsArticleDto ToDto(IReadOnlyList<NewsArticle> copies, HashSet<int> watched)
    {
        var primary = copies.OrderBy(a => a.PublishedAtUtc).ThenBy(a => a.Id).First();
        var assets = copies.SelectMany(a => a.Assets).GroupBy(x => x.AssetId).Select(g => g.First())
            .Select(x => new NewsAssetDto(x.Asset!.Symbol, x.Asset.Name, x.MatchMethod, watched.Contains(x.AssetId)))
            .OrderByDescending(x => x.OnWatchlist).ThenBy(x => x.Symbol).ToList();
        var publishers = copies.Select(a => a.Publisher).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var relevance = NewsRelevance.Assess(primary.Category, assets.Count, copies.Any(a => a.IsOfficialSource), primary.PublishedAtUtc, publishers.Count, Now);
        var showImage = Options.ShowImages && primary.ImageUrl is not null;
        return new NewsArticleDto(primary.Id, primary.Headline, primary.Publisher, primary.Url, primary.PublishedAtUtc, primary.RetrievedAtUtc,
            primary.Summary, showImage ? primary.ImageUrl : null, showImage ? $"Image supplied with the article by {primary.Provider} ({primary.Publisher})" : null,
            primary.Category.ToString(), primary.CategoryBasis, primary.Scope.ToString(), primary.Region, primary.IsOfficialSource, primary.IsSimulated,
            primary.Provider, assets,
            new NewsRelevanceDto(relevance.Level.ToString(), relevance.Points, relevance.Reasons, RelevanceNote),
            publishers.Where(p => !string.Equals(p, primary.Publisher, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private List<string> Notes()
    {
        var notes = new List<string>
        {
            "Headlines, publishers, times and short summaries come from the listed providers; the full article is only at the original link.",
            "Articles are linked to stocks only when the provider tagged them with that listing; untagged articles about a company may be missing.",
            "Categories come from keyword rules (the basis is shown on each article) and can be wrong.",
        };
        if (!Options.ShowImages) notes.Add("Article images are not shown: display rights for third-party images have not been confirmed for this deployment.");
        if (!Options.Enabled) notes.Add("News retrieval is disabled by configuration (News:Enabled=false); only stored articles are shown.");
        if (!companyProviders.Any(p => p.Descriptor.IsConfigured) && !feedProviders.Any(p => p.Descriptor.IsConfigured))
            notes.Add("No news source is configured.");
        return notes;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
