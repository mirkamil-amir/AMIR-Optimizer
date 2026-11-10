using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Classification history (AMIR-18): every stored article has one history row per rules version; articles of an older
/// version are classified again from their stored inputs, keep their earlier row, and their stories follow the lead report;
/// the history endpoint shows only stories the caller may see.
/// </summary>
public sealed class NewsClassificationHistoryTests(NewsClassificationHistoryTests.Shared shared) : IClassFixture<NewsClassificationHistoryTests.Shared>
{
    public sealed class Shared : SharedApi
    {
        public HttpClient Alice { get; private set; } = null!;
        public HttpClient Bob { get; private set; } = null!;
        public Guid OldEarnings { get; } = Guid.NewGuid();
        public Guid OldEcb { get; } = Guid.NewGuid();

        protected override Task<ApiFactory> CreateFactoryAsync() =>
            ApiFactory.CreateAsync(testServices: s => s.PostConfigure<PortfolioOptimizer.Infrastructure.News.MockNewsOptions>(o => o.Enabled = false));

        protected override async Task SeedAsync(HttpClient admin)
        {
            await SyncAsync(admin, 1, "AAPL", "MSFT");
            (Alice, _) = await Factory.CreateApprovedUserAsync(admin);
            (Bob, _) = await Factory.CreateApprovedUserAsync(admin);
            (await Alice.PostJsonAsync("/api/v1/watchlist", new AddWatchlistEntryRequest { Symbol = "AAPL" })).EnsureStatus(HttpStatusCode.Created);
            (await Bob.PostJsonAsync("/api/v1/watchlist", new AddWatchlistEntryRequest { Symbol = "MSFT" })).EnsureStatus(HttpStatusCode.Created);

            var now = DateTime.UtcNow;
            using var scope = Factory.Services.CreateScope();
            var news = scope.ServiceProvider.GetRequiredService<NewsService>();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            await news.IngestAsync(
            [
                new("TestWire", "aapl", "https://news.example.test/aapl", "Apple earnings beat estimates on iPhone demand", "Wire One", now.AddHours(-2), null, null, ["AAPL"]),
                new("TestWire", "fx", "https://news.example.test/fx", "Dollar slips against euro and pound", "FX Daily", now.AddHours(-3), null, null,
                    ["EURUSD=X", "GBPUSD=X"]),
                new("TestWire", "ecb-new", "https://news.example.test/ecb-new", "ECB decides on key interest rates", "European Central Bank", now.AddHours(-4), null, null, [],
                    IsOfficial: true, Region: "EU", SourceCategory: NewsCategory.CentralBank),
            ], CancellationToken.None);

            // Two articles as rules version 1 stored them (before 2026-11-08): no event type, no tags, history from the backfill.
            var msft = await db.Assets.SingleAsync(a => a.Symbol == "MSFT");
            NewsArticle Old(Guid id, string headline, string publisher, NewsCategory category, string basis, NewsScope scope, bool official, string? region) => new()
            {
                Id = id, Provider = "TestWire", Url = $"https://news.example.test/{id}", Fingerprint = id.ToString("N"), HeadlineKey = id.ToString("N"),
                Headline = headline, Publisher = publisher, PublishedAtUtc = now.AddDays(-20), RetrievedAtUtc = now.AddDays(-20), Category = category,
                CategoryBasis = basis, Scope = scope, IsOfficialSource = official, Region = region, RuleVersion = 1,
                SourceCategory = official ? category : null,
            };
            var earnings = Old(OldEarnings, "Microsoft quarterly results top forecasts", "Wire One", NewsCategory.Earnings, "headline mentions “quarterly results”",
                NewsScope.Company, false, "US");
            earnings.Assets.Add(new NewsArticleAsset { ArticleId = OldEarnings, AssetId = msft.Id, MatchMethod = "TestWire ticker tag" });
            var ecb = Old(OldEcb, "Monetary policy decision of the Governing Council", "European Central Bank", NewsCategory.CentralBank, "category of the official source feed",
                NewsScope.Macro, true, "EU");
            foreach (var a in new[] { earnings, ecb })
            {
                a.Classifications.Add(new NewsArticleClassification
                {
                    ArticleId = a.Id, RuleVersion = 1, Category = a.Category, CategoryBasis = a.CategoryBasis, Scope = a.Scope, ProcessedAtUtc = a.RetrievedAtUtc,
                    Trigger = ClassificationTrigger.Backfill,
                });
                db.NewsArticles.Add(a);
            }
            await db.SaveChangesAsync();
            await news.GroupPendingAsync(CancellationToken.None);
        }
    }

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = shared.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private static async Task<T> Get<T>(HttpClient client, string url) => await (await client.GetAsync(url)).EnsureStatus(HttpStatusCode.OK).ReadAsync<T>();

    private Task<Guid> StoryOf(string headline) => InScope(sp =>
        sp.GetRequiredService<IApplicationDbContext>().NewsStories.Where(s => s.Headline == headline).Select(s => s.Id).SingleAsync());

    [Fact]
    public async Task Ingest_WritesOneHistoryRow_AndKeepsWhatTheSourceStated()
    {
        var (article, rows) = await InScope(async sp =>
        {
            var db = sp.GetRequiredService<IApplicationDbContext>();
            var a = await db.NewsArticles.AsNoTracking().SingleAsync(x => x.Headline == "ECB decides on key interest rates");
            return (a, await db.NewsArticleClassifications.AsNoTracking().Where(c => c.ArticleId == a.Id).ToListAsync());
        });
        Assert.Equal(NewsCategory.CentralBank, article.SourceCategory);
        var row = Assert.Single(rows);
        Assert.Equal((NewsTaxonomy.RuleVersion, ClassificationTrigger.Ingest), (row.RuleVersion, row.Trigger));
        Assert.Equal((article.Category, article.CategoryBasis, article.Scope, article.EventType, article.EventTypeBasis, article.MarketArea),
            (row.Category, row.CategoryBasis, row.Scope, row.EventType, row.EventTypeBasis, row.MarketArea));
        Assert.Equal(article.ProcessedAtUtc, row.ProcessedAtUtc);
        Assert.Null(row.Note);
    }

    [Fact]
    public async Task Reclassification_UpgradesOldArticles_KeepsTheirHistory_AndUpdatesTheirStories()
    {
        await InScope(async sp =>
        {
            var news = sp.GetRequiredService<NewsService>();
            while (await news.ReclassifyPendingAsync(CancellationToken.None) > 0) { }
            Assert.Equal(0, await news.ReclassifyPendingAsync(CancellationToken.None)); // nothing left; safe to re-run
            return 0;
        });

        await InScope(async sp =>
        {
            var db = sp.GetRequiredService<IApplicationDbContext>();
            Assert.False(await db.NewsArticles.AnyAsync(a => a.RuleVersion < NewsTaxonomy.RuleVersion));

            var earnings = await db.NewsArticles.AsNoTracking().SingleAsync(a => a.Id == shared.OldEarnings);
            Assert.Equal((NewsScope.Company, NewsEventType.Results), (earnings.Scope, earnings.EventType));
            var history = await db.NewsArticleClassifications.AsNoTracking().Where(c => c.ArticleId == shared.OldEarnings).OrderBy(c => c.RuleVersion).ToListAsync();
            Assert.Equal([(1, ClassificationTrigger.Backfill), (NewsTaxonomy.RuleVersion, ClassificationTrigger.Reclassification)],
                history.Select(h => (h.RuleVersion, h.Trigger)));
            Assert.Null(history[0].EventType);                                    // version 1 had no event types; the row is unchanged
            Assert.Equal(NewsEventType.Results, history[1].EventType);
            Assert.Equal(NewsService.TagsNotStoredNote, history[1].Note);         // stored before tags were kept

            // The official source's own category is used again, so the central-bank item stays macro.
            var ecb = await db.NewsArticles.AsNoTracking().SingleAsync(a => a.Id == shared.OldEcb);
            Assert.Equal((NewsCategory.CentralBank, "category of the official source feed", NewsScope.Macro), (ecb.Category, ecb.CategoryBasis, ecb.Scope));
            Assert.Equal(NewsEventType.RateDecision, ecb.EventType);              // "monetary policy decision" → rate decision

            // Stories take their lead report's new classification; their reports are not regrouped.
            var story = await db.NewsStories.AsNoTracking().SingleAsync(s => s.LeadArticleId == shared.OldEarnings);
            Assert.Equal((NewsEventType.Results, earnings.EventTypeBasis, 1), (story.EventType, story.EventTypeBasis, story.ArticleCount));
            Assert.Equal(StoryRules.BasePoints(story.EventType, story.Category, story.HasOfficialSource, story.PublisherCount, story.MarketInstrumentCount),
                story.BasePoints);
            Assert.Equal(NewsEventType.RateDecision, (await db.NewsStories.AsNoTracking().SingleAsync(s => s.LeadArticleId == shared.OldEcb)).EventType);
            return 0;
        });
    }

    [Fact]
    public async Task HistoryEndpoint_ListsEveryReportsVersions_ForStoriesTheCallerMaySee()
    {
        var apple = await StoryOf("Apple earnings beat estimates on iPhone demand");
        var history = await Get<WireClassificationHistoryDto>(shared.Alice, $"/api/v1/news/stories/{apple}/classifications");
        Assert.Equal((apple, NewsTaxonomy.RuleVersion), (history.StoryId, history.CurrentRuleVersion));
        var report = Assert.Single(history.Reports);
        var current = Assert.Single(report.Classifications);
        Assert.Equal(("Results", "results", "Company", "Ingest", true), (current.EventType, current.EventTypeLabel, current.Scope, current.Trigger, current.IsCurrent));
        Assert.Equal("earnings", current.CategoryLabel);
        Assert.NotEmpty(history.Notes);

        // Market stories are visible to everyone.
        var fx = await StoryOf("Dollar slips against euro and pound");
        var fxHistory = await Get<WireClassificationHistoryDto>(shared.Bob, $"/api/v1/news/stories/{fx}/classifications");
        Assert.Equal("Fx", Assert.Single(Assert.Single(fxHistory.Reports).Classifications).MarketArea);
    }

    [Fact]
    public async Task HistoryEndpoint_HidesOthersInterests_UnknownStories_AndAnonymousCallers()
    {
        var apple = await StoryOf("Apple earnings beat estimates on iPhone demand");
        Assert.Equal(HttpStatusCode.NotFound, (await shared.Bob.GetAsync($"/api/v1/news/stories/{apple}/classifications")).StatusCode); // only Alice watches AAPL
        Assert.Equal(HttpStatusCode.NotFound, (await shared.Alice.GetAsync($"/api/v1/news/stories/{Guid.NewGuid()}/classifications")).StatusCode);
        using var anonymous = shared.Factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/news/stories/{apple}/classifications")).StatusCode);
    }
}
