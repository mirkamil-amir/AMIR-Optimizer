using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Entity links of stored articles (AMIR-19): articles stored before the links existed are linked by the scheduled pass from
/// their stored tags and source, links the list no longer gives are removed, and stories get the union of their reports' links.
/// </summary>
public sealed class NewsEntityLinkingTests : IAsyncLifetime
{
    private ApiFactory factory = null!;

    public async Task InitializeAsync() =>
        factory = await ApiFactory.CreateAsync(testServices: s => s.PostConfigure<PortfolioOptimizer.Infrastructure.News.MockNewsOptions>(o => o.Enabled = false));

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await factory.DeleteDatabaseAsync();
    }

    private static NewsArticle Stored(string id, string headline, string publisher, DateTime at, string? tags, bool official = false, string? region = null,
        NewsCategory? sourceCategory = null) => new()
    {
        Id = Guid.NewGuid(), Provider = "TestWire", Url = $"https://news.example.test/{id}", Fingerprint = id, HeadlineKey = id, Headline = headline,
        Publisher = publisher, PublishedAtUtc = at, RetrievedAtUtc = at, Category = sourceCategory ?? NewsCategory.General, CategoryBasis = "test",
        Scope = tags is null ? NewsScope.Macro : NewsScope.Market, IsOfficialSource = official, Region = region, ProviderTags = tags,
        SourceCategory = sourceCategory, RuleVersion = NewsTaxonomy.RuleVersion, EventType = NewsEventType.MarketNews, EntityRuleVersion = 0,
    };

    [Fact]
    public async Task StoredArticles_AreLinked_StaleLinksRemoved_AndStoriesGetTheUnion()
    {
        var now = DateTime.UtcNow;
        var oil = Stored("oil-1", "Oil rises as dollar weakens", "Wire One", now.AddHours(-3), "CL=F,DX-Y.NYB");
        var oilCopy = Stored("oil-2", "Oil rises as dollar weakens", "Wire Two", now.AddHours(-2), "BZ=F");
        var boe = Stored("boe", "Bank Rate maintained at 4%", "Bank of England", now.AddHours(-5), null, true, "GB", NewsCategory.CentralBank);
        var untagged = Stored("old", "Markets drift", "Wire One", now.AddHours(-6), null);
        oil.Entities.Add(new NewsArticleEntity { ArticleId = oil.Id, EntityKey = "GC=F", MatchMethod = "an earlier list" }); // no longer given

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            db.NewsArticles.AddRange(oil, oilCopy, boe, untagged);
            await db.SaveChangesAsync();
            var news = scope.ServiceProvider.GetRequiredService<NewsService>();
            await news.GroupPendingAsync(CancellationToken.None);   // grouped before linking: the stories have only the stale link
            Assert.Equal(4, await news.LinkEntitiesPendingAsync(CancellationToken.None));
            Assert.Equal(0, await news.LinkEntitiesPendingAsync(CancellationToken.None)); // nothing left; safe to re-run
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            async Task<List<string>> ArticleKeys(Guid id) =>
                await db.NewsArticleEntities.Where(x => x.ArticleId == id).OrderBy(x => x.EntityKey).Select(x => x.EntityKey).ToListAsync();
            Assert.Equal(["CL=F", "DXY"], await ArticleKeys(oil.Id));
            Assert.Equal(["BOE"], await ArticleKeys(boe.Id));
            Assert.Empty(await ArticleKeys(untagged.Id));
            Assert.Equal("provider tag DX-Y.NYB", await db.NewsArticleEntities.Where(x => x.ArticleId == oil.Id && x.EntityKey == "DXY").Select(x => x.MatchMethod).SingleAsync());
            Assert.False(await db.NewsArticles.AnyAsync(a => a.EntityRuleVersion < NewsEntities.RuleVersion));

            var story = await db.NewsStories.Include(s => s.Entities).SingleAsync(s => s.Articles.Any(m => m.ArticleId == oil.Id));
            Assert.Equal(2, story.ArticleCount);                                       // the syndicated copy joined
            Assert.Equal(["BZ=F", "CL=F", "DXY"], story.Entities.Select(e => e.EntityKey).Order());
        }
    }
}
