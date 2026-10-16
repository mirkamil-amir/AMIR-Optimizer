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

        if (Options.Enabled)
        {
            await RefreshFeedsAsync(ct);
            if (symbolAsset is not null) await RefreshCompanyAsync(symbolAsset.Symbol, symbolAsset.Name, ct);
            else await RefreshWatchlistAsync(watchlist.Select(w => (w.Symbol, w.Name)).ToList(), ct);
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

        return new NewsFeedDto(items, query.Page, query.PageSize, total, await SourcesAsync(ct), Options.ShowImages, Notes());
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
        var stockBars = series.TryGetValue(asset.Symbol, out var sb) ? sb.Bars.Select(b => new DailyClose(b.Date, b.Price)).ToList() : [];
        var benchBars = benchmark is not null && series.TryGetValue(benchmark, out var bb) ? bb.Bars.Select(b => new DailyClose(b.Date, b.Price)).ToList() : [];

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

    private async Task RefreshFeedsAsync(CancellationToken ct)
    {
        foreach (var p in feedProviders.Where(p => p.Descriptor.IsConfigured))
            foreach (var feed in p.Feeds)
                await RefreshKeyAsync(FeedKey(p, feed), p.Descriptor.Name, t => p.GetFeedAsync(feed, t), ct);
    }

    private async Task RefreshCompanyAsync(string symbol, string? name, CancellationToken ct)
    {
        foreach (var p in companyProviders.Where(p => p.Descriptor.IsConfigured))
            await RefreshKeyAsync($"{p.Descriptor.Name}:company:{symbol}", p.Descriptor.Name, t => p.GetCompanyNewsAsync(symbol, name, t), ct);
    }

    /// <summary>Refreshes the stalest few watched companies per request so large watchlists never cause request bursts.</summary>
    private async Task RefreshWatchlistAsync(IReadOnlyList<(string Symbol, string? Name)> watchlist, CancellationToken ct)
    {
        if (watchlist.Count == 0) return;
        var keys = watchlist.Select(w => w.Symbol).ToList();
        var states = await db.NewsFetchStates.AsNoTracking().Where(s => s.Key.Contains(":company:")).ToListAsync(ct);
        DateTime LastAttempt(string symbol) => states.Where(s => s.Key.EndsWith(":company:" + symbol)).Select(s => s.LastAttemptUtc ?? DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue).Min();
        foreach (var w in watchlist.OrderBy(w => LastAttempt(w.Symbol)).Take(Options.WatchlistRefreshLimit))
            await RefreshCompanyAsync(w.Symbol, w.Name, ct);
    }

    private async Task RefreshKeyAsync(string key, string provider, Func<CancellationToken, Task<IReadOnlyList<ProviderArticle>>> fetch, CancellationToken ct)
    {
        if (!IsDue(await db.NewsFetchStates.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct))) return;
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return; // another request is fetching this source right now; serve stored articles
        try
        {
            var state = await db.NewsFetchStates.SingleOrDefaultAsync(s => s.Key == key, ct);
            if (!IsDue(state)) return;
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
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
            {
                // Another instance stored the same state/article concurrently; the next request reads its result.
                logger.LogInformation("News fetch {Key}: concurrent update ignored.", key);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsDue(NewsFetchState? state)
    {
        if (state?.LastAttemptUtc is not { } last) return true;
        var failed = state.LastError is not null;
        return Now - last >= TimeSpan.FromMinutes(failed ? Options.FailureBackoffMinutes : Options.RefreshMinutes);
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
            if (existing.TryGetValue(fingerprint, out var stored))
            {
                foreach (var asset in linked.Where(x => stored.Assets.All(l => l.AssetId != x.Id)))
                    stored.Assets.Add(new NewsArticleAsset { ArticleId = stored.Id, AssetId = asset.Id, MatchMethod = $"{a.Provider} ticker tag" });
                continue;
            }
            var headline = NewsNormalizer.Clean(a.Headline);
            var summary = NewsNormalizer.Teaser(a.Summary);
            var classification = NewsClassifier.Classify(headline, summary, a.SourceCategory);
            var companies = linked.Count(x => x.AssetType == AssetType.Equity);
            var scope = companies == 0 && linked.Count > 0 ? NewsScope.Market : NewsClassifier.Scope(classification.Category, companies);
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
                Category = classification.Category,
                CategoryBasis = Truncate(classification.Basis, 200),
                Scope = scope,
                Region = a.Region ?? linked.Select(x => x.Country).FirstOrDefault(c => c is not null),
                IsOfficialSource = a.IsOfficial,
                IsSimulated = a.IsSimulated,
            };
            article.Assets.AddRange(linked.Select(x => new NewsArticleAsset { ArticleId = article.Id, AssetId = x.Id, MatchMethod = $"{a.Provider} ticker tag" }));
            db.NewsArticles.Add(article);
            existing[fingerprint] = article;
        }
        await db.NewsArticles.Where(a => a.PublishedAtUtc < oldest).ExecuteDeleteAsync(ct);
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
