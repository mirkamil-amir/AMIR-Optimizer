using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>News feed with the simulated provider: storage, dedup, provider-tag linking, filters, watchlist and event comparison.</summary>
public sealed class NewsTests(NewsTests.Shared shared) : IClassFixture<NewsTests.Shared>, IAsyncLifetime
{
    public sealed class Shared : SharedApi
    {
        protected override Task SeedAsync(HttpClient admin) => SyncAsync(admin, 1, "AAPL", "MSFT", "SPY");
    }

    private readonly ApiFactory _factory = shared.Factory;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;

    public async Task InitializeAsync()
    {
        _admin = await _factory.CreateAdminSessionAsync();
        (_user, _) = await _factory.CreateApprovedUserAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CompanyNews_IsStoredOnce_SyndicatedCopiesAreMerged_AndLinkedByProviderTag()
    {
        var feed = await (await _user.GetAsync("/api/v1/news?symbol=AAPL")).ReadAsync<NewsFeedDto>();
        Assert.Equal(2, feed.TotalCount); // three simulated items, two of them the same story
        var results = Assert.Single(feed.Items, i => i.Headline.Contains("quarterly earnings"));
        Assert.Equal(["Simulated Daily"], results.AlsoReportedBy);
        Assert.Equal("Earnings", results.Category);
        Assert.Contains("earnings", results.CategoryBasis);
        Assert.Equal("Company", results.Scope);
        Assert.True(results.IsSimulated);
        var asset = Assert.Single(results.Assets);
        Assert.Equal("AAPL", asset.Symbol);
        Assert.Contains("ticker tag", asset.MatchMethod);
        Assert.Contains(results.Relevance.Reasons, r => r.StartsWith("reported by 2 different publishers"));
        Assert.Contains("does not predict", results.Relevance.Note);
        Assert.Null(results.ImageUrl); // images disabled by default

        // A second request inside the refresh window serves stored articles without duplicating them.
        var again = await (await _user.GetAsync("/api/v1/news?symbol=AAPL")).ReadAsync<NewsFeedDto>();
        Assert.Equal(2, again.TotalCount);
    }

    [Fact]
    public async Task Feed_FiltersByCategoryScopeSearchAndWatchlist()
    {
        await _user.GetAsync("/api/v1/news?symbol=MSFT");
        var macro = await (await _user.GetAsync("/api/v1/news?scope=Macro")).ReadAsync<NewsFeedDto>();
        var rates = Assert.Single(macro.Items);
        Assert.True(rates.IsOfficialSource);
        Assert.Equal("CentralBank", rates.Category);
        Assert.Contains("official source", rates.CategoryBasis);

        var earnings = await (await _user.GetAsync("/api/v1/news?category=Earnings")).ReadAsync<NewsFeedDto>();
        Assert.All(earnings.Items, i => Assert.Equal("Earnings", i.Category));
        Assert.Contains(earnings.Items, i => i.Assets.Any(a => a.Symbol == "MSFT"));

        var search = await (await _user.GetAsync("/api/v1/news?search=product%20line")).ReadAsync<NewsFeedDto>();
        Assert.All(search.Items, i => Assert.Contains("product line", i.Headline));

        // Watchlist filter: empty list → nothing; after adding AAPL → only AAPL stories, flagged as watched.
        Assert.Equal(0, (await (await _user.GetAsync("/api/v1/news?watchlist=true")).ReadAsync<NewsFeedDto>()).TotalCount);
        (await _user.PostJsonAsync("/api/v1/watchlist", new AddWatchlistEntryRequest { Symbol = "AAPL" })).EnsureStatus(HttpStatusCode.Created);
        var watched = await (await _user.GetAsync("/api/v1/news?watchlist=true")).ReadAsync<NewsFeedDto>();
        Assert.NotEmpty(watched.Items);
        Assert.All(watched.Items, i => Assert.Contains(i.Assets, a => a.Symbol == "AAPL" && a.OnWatchlist));

        (await _user.GetAsync("/api/v1/news?category=Nonsense")).EnsureStatus(HttpStatusCode.BadRequest);
        (await _user.GetAsync("/api/v1/news?symbol=NOPE123")).EnsureStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EventComparison_UsesTheCloseBeforePublication_AndTheBenchmarkOnTheSameDates()
    {
        var feed = await (await _user.GetAsync("/api/v1/news?symbol=AAPL&category=Earnings")).ReadAsync<NewsFeedDto>();
        var article = Assert.Single(feed.Items);
        var cmp = await (await _user.GetAsync($"/api/v1/news/{article.Id}/price-movement?symbol=AAPL")).ReadAsync<EventComparisonDto>();
        Assert.Equal("AAPL", cmp.Symbol);
        Assert.Equal("SPY", cmp.BenchmarkSymbol);
        Assert.True(cmp.IsSimulated);
        Assert.NotNull(cmp.ReferenceDate);
        Assert.True(cmp.ReferenceDate!.Value <= DateOnly.FromDateTime(cmp.PublishedAtUtc));
        var h1 = Assert.Single(cmp.Horizons, h => h.TradingDays == 1);
        Assert.NotNull(h1.StockChange);
        Assert.NotNull(h1.BenchmarkChange);
        Assert.Equal(h1.StockChange!.Value - h1.BenchmarkChange!.Value, h1.Difference!.Value, 12);
        Assert.Contains("not evidence", cmp.Disclaimer);
        Assert.Contains(cmp.Limitations, l => l.Contains("Daily closes only"));

        // Only stocks the provider tagged can be compared; unknown articles are 404.
        (await _user.GetAsync($"/api/v1/news/{article.Id}/price-movement?symbol=MSFT")).EnsureStatus(HttpStatusCode.BadRequest);
        (await _user.GetAsync($"/api/v1/news/{Guid.NewGuid()}/price-movement?symbol=AAPL")).EnsureStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task News_RequiresAnApprovedAccount()
    {
        using var anonymous = _factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/news")).StatusCode);
    }

    [Fact]
    public async Task WatchlistFeed_FetchesTheViewersWatchedCompanies_LikeThePageViewFeed()
    {
        var (viewer, _) = await _factory.CreateApprovedUserAsync(_admin);
        using (viewer)
        {
            (await viewer.PostJsonAsync("/api/v1/watchlist", new AddWatchlistEntryRequest { Symbol = "MSFT" })).EnsureStatus(HttpStatusCode.Created);
            // The simulated articles are 12 and 40 days old: outside the default 7-day window, so ask for 90 days.
            var feed = await (await viewer.GetAsync("/api/v1/news/watchlist?hours=2160")).EnsureStatus(HttpStatusCode.OK).ReadAsync<WireFeedDto>();
            // Never-fetched sources are fetched in the request, already-fetched ones in the background: either way MSFT has stories.
            var msft = feed.Stories.Where(s => s.Assets.Any(a => a.Symbol == "MSFT")).ToList();
            Assert.NotEmpty(msft);
            Assert.All(msft, s => Assert.True(s.IsSimulated));
            Assert.Contains(msft, s => s.ArticleCount == 2); // the simulated earnings story is reported twice
            Assert.All(msft, s => Assert.Contains("MSFT is on your watchlist", s.Why));
            // Everything else is a rate decision of the US central bank (MSFT trades in USD) – here the simulated feed's.
            Assert.All(feed.Stories.Except(msft), s => Assert.Equal(("RateDecision", "US"), (s.EventType, s.Region)));
        }
    }

    [Fact]
    public async Task ScheduledRun_FetchesAnyUsersWatchedListings_AndClassifiesWithRuleVersion2()
    {
        // Another user's watchlist puts SPY into the scheduled set; nobody opens a news page for it.
        var (other, _) = await _factory.CreateApprovedUserAsync(_admin);
        using (other)
            (await other.PostJsonAsync("/api/v1/watchlist", new AddWatchlistEntryRequest { Symbol = "SPY" })).EnsureStatus(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<NewsService>().RefreshScheduledAsync(CancellationToken.None);
        Assert.True(run.Companies >= 1);

        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        Assert.True(await db.NewsFetchStates.AnyAsync(s => s.Key == "MockNews:company:SPY" && s.LastSuccessUtc != null));
        Assert.True(await db.NewsFetchStates.AnyAsync(s => s.Key == "MockNews:macro")); // feeds are part of every run

        var spy = await db.NewsArticles.AsNoTracking().Where(a => a.Assets.Any(x => x.Asset!.Symbol == "SPY")).ToListAsync();
        Assert.NotEmpty(spy);
        Assert.All(spy, a =>
        {
            Assert.Equal(NewsTaxonomy.RuleVersion, a.RuleVersion);
            Assert.Equal(NewsScope.Market, a.Scope);                 // tagged only with a catalogue ETF → market-level
            Assert.Equal(NewsEventType.MarketNews, a.EventType);
            Assert.Equal(NewsMarketArea.Equities, a.MarketArea);
            Assert.True(a.ProcessedAtUtc >= a.RetrievedAtUtc.AddSeconds(-1));
            Assert.True(a.PublishedAtUtc < a.RetrievedAtUtc);       // publication time is the provider's, never the fetch time
        });

        // A second run inside the company refresh interval fetches nothing again.
        var before = (await db.NewsFetchStates.AsNoTracking().SingleAsync(s => s.Key == "MockNews:company:SPY")).LastAttemptUtc;
        await scope.ServiceProvider.GetRequiredService<NewsService>().RefreshScheduledAsync(CancellationToken.None);
        Assert.Equal(before, (await db.NewsFetchStates.AsNoTracking().SingleAsync(s => s.Key == "MockNews:company:SPY")).LastAttemptUtc);
    }
}

/// <summary>A failing provider is reported per source; the feed still answers with stored articles (no substitutes).</summary>
public sealed class NewsProviderFailureTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    private sealed class FailingProvider : ICompanyNewsProvider
    {
        public NewsProviderDescriptor Descriptor { get; } = new("Failing", "Failing test provider", true, false, "none", "none");
        public Task<IReadOnlyList<ProviderArticle>> GetCompanyNewsAsync(string symbol, string? companyName, CancellationToken ct) =>
            throw new NewsProviderException("Failing", "HTTP 503 for " + symbol);
    }

    public async Task InitializeAsync() =>
        _factory = await ApiFactory.CreateAsync(testServices: s => s.AddSingleton<ICompanyNewsProvider, FailingProvider>());

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    [Fact]
    public async Task ProviderError_IsRecorded_AndTheOtherSourcesStillServeArticles()
    {
        using var admin = await _factory.CreateAdminSessionAsync();
        var feed = await (await admin.GetAsync("/api/v1/news?symbol=AAPL")).ReadAsync<NewsFeedDto>();
        Assert.NotEmpty(feed.Items); // the simulated provider still delivered
        Assert.DoesNotContain(feed.Items, i => i.Provider == "Failing");
        var failing = Assert.Single(feed.Sources, s => s.Provider == "Failing");
        Assert.Contains("HTTP 503", failing.LastError);
        Assert.Null(failing.LastSuccessUtc);
    }
}
