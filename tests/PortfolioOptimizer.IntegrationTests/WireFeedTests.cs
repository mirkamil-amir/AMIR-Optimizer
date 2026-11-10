using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// The five Wire feeds on one fixed set of stories for two users (Alice watches AAPL and SAP.DE, Bob watches MSFT): each
/// feed returns exactly what its contract allows, a story appears in two feeds only when it meets both, and nobody sees a
/// story linked only to a listing someone else follows.
/// </summary>
public sealed class WireFeedTests(WireFeedTests.Shared shared) : IClassFixture<WireFeedTests.Shared>
{
    public sealed class Shared : SharedApi
    {
        public HttpClient Alice { get; private set; } = null!;
        public HttpClient Bob { get; private set; } = null!;

        // Only the articles ingested below: feed requests would otherwise fetch simulated news for the watched stocks.
        protected override Task<ApiFactory> CreateFactoryAsync() =>
            ApiFactory.CreateAsync(testServices: s => s.PostConfigure<PortfolioOptimizer.Infrastructure.News.MockNewsOptions>(o => o.Enabled = false));

        protected override async Task SeedAsync(HttpClient admin)
        {
            await SyncAsync(admin, 1, "AAPL", "MSFT", "SAP.DE", "SPY");
            (Alice, _) = await Factory.CreateApprovedUserAsync(admin);
            (Bob, _) = await Factory.CreateApprovedUserAsync(admin);
            foreach (var (client, symbol) in new[] { (Alice, "AAPL"), (Alice, "SAP.DE"), (Bob, "MSFT") })
                (await client.PostJsonAsync("/api/v1/watchlist", new AddWatchlistEntryRequest { Symbol = symbol })).EnsureStatus(HttpStatusCode.Created);

            var now = DateTime.UtcNow;
            ProviderArticle A(string id, string headline, string publisher, double hoursAgo, string[] tags, bool official = false, string? region = null,
                NewsCategory? category = null) =>
                new("TestWire", id, $"https://news.example.test/{id}", headline, publisher, now.AddHours(-hoursAgo), null, null, tags,
                    IsOfficial: official, Region: region, SourceCategory: category);
            using var scope = Factory.Services.CreateScope();
            var news = scope.ServiceProvider.GetRequiredService<NewsService>();
            await news.IngestAsync(
            [
                // One AAPL results story reported three times (syndicated copies and a close rewrite).
                A("aapl-1", "Apple earnings beat estimates on iPhone demand", "Wire One", 2, ["AAPL"]),
                A("aapl-1b", "Apple earnings beat estimates on iPhone demand", "Wire Two", 1.9, ["AAPL"]),
                A("aapl-1c", "Apple earnings beat estimates on strong iPhone demand", "Wire Three", 1, ["AAPL"]),
                A("aapl-2", "Apple raises outlook for services", "Wire One", 0.5, ["AAPL"]),
                A("aapl-3", "Apple agrees to buy speech startup", "Wire One", 0.6, ["AAPL"]),
                A("aapl-4", "Apple chief executive appoints new design lead", "Wire One", 0.7, ["AAPL"]),
                A("aapl-store", "Apple opens a store in Berlin", "Wire One", 3, ["AAPL"]),
                A("msft-1", "Microsoft earnings beat estimates", "Wire One", 1, ["MSFT"]),
                A("ecb", "ECB decides on key interest rates", "European Central Bank", 4, [], true, "EU", NewsCategory.CentralBank),
                A("hicp", "Euro area annual inflation up to 4.0%", "Eurostat", 5, [], true, "EU", NewsCategory.Macroeconomic),
                A("mkt-eq", "Bank stocks lead European markets higher", "Wall Street Daily", 2, ["^STOXX", "EURUSD=X"]),
                A("mkt-fx", "Dollar slips against euro and pound", "FX Daily", 3, ["EURUSD=X", "GBPUSD=X", "DX-Y.NYB"]),
            ], CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IApplicationDbContext>().SaveChangesAsync();
            await news.GroupPendingAsync(CancellationToken.None);
        }
    }

    private HttpClient Alice => shared.Alice;
    private HttpClient Bob => shared.Bob;

    private static async Task<T> Get<T>(HttpClient client, string url) => await (await client.GetAsync(url)).EnsureStatus(HttpStatusCode.OK).ReadAsync<T>();

    private static HashSet<string> Heads(IEnumerable<WireStoryDto> stories) => stories.Select(s => s.Headline).ToHashSet();

    [Fact]
    public async Task Grouping_FoldsReportsOfOneEvent_AndKeepsEverySource()
    {
        var watch = await Get<WireFeedDto>(Alice, "/api/v1/news/watchlist");
        var results = Assert.Single(watch.Stories, s => s.Headline.StartsWith("Apple earnings beat estimates"));
        Assert.Equal(3, results.ArticleCount);
        Assert.Equal(["Wire One", "Wire Two", "Wire Three"], results.Sources.Select(x => x.Publisher));
        Assert.Equal("Wire One", results.Publisher); // the earliest report leads
        Assert.Equal("Results", results.EventType);
        Assert.Equal(StoryRules.RuleVersion, results.RuleVersion);
        Assert.True(results.FirstPublishedAtUtc < results.LastPublishedAtUtc);
        Assert.Contains("does not predict", results.Attention.Note);
    }

    [Fact]
    public async Task Latest_IsMediumOrHigh_AtMostTwoPerCompany_AndHidesOthersInterests()
    {
        var latest = await Get<WireFeedDto>(Alice, "/api/v1/news/latest");
        var heads = Heads(latest.Stories);
        Assert.Contains("Euro area annual inflation up to 4.0%", heads);
        Assert.Contains("ECB decides on key interest rates", heads);
        Assert.Equal(2, latest.Stories.Count(s => s.Assets.Any(a => a.Symbol == "AAPL")));       // four material AAPL stories, two per page
        Assert.DoesNotContain("Apple opens a store in Berlin", heads);                             // Low attention
        // Market stories about two or more instruments get +1 (rules version 2): fresh ones are Medium and reach Latest.
        var broad = Assert.Single(latest.Stories, s => s.Headline == "Bank stocks lead European markets higher");
        Assert.Equal("Medium", broad.Attention.Level);
        Assert.Contains("concerns 2 market instruments at once (+1)", broad.Attention.Reasons);
        Assert.DoesNotContain(latest.Stories, s => s.Assets.Any(a => a.Symbol == "MSFT"));         // only Bob watches MSFT
        Assert.All(latest.Stories, s => Assert.Contains(s.Attention.Level, new[] { "Medium", "High" }));
        Assert.Equal(latest.Stories.OrderByDescending(s => s.LastPublishedAtUtc).Select(s => s.Id), latest.Stories.Select(s => s.Id));

        var bob = Heads((await Get<WireFeedDto>(Bob, "/api/v1/news/latest")).Stories);
        Assert.Contains("Microsoft earnings beat estimates", bob);
        Assert.DoesNotContain(bob, h => h.StartsWith("Apple"));                                    // only Alice watches AAPL
    }

    [Fact]
    public async Task Watchlist_IsOnlyWhatTheViewerWatches_PlusTheirCurrenciesRateDecisions()
    {
        var alice = await Get<WireFeedDto>(Alice, "/api/v1/news/watchlist");
        var heads = Heads(alice.Stories);
        Assert.Contains("Apple opens a store in Berlin", heads);                                   // low attention, but watched
        var ecb = Assert.Single(alice.Stories, s => s.Headline == "ECB decides on key interest rates"); // Alice watches SAP.DE (EUR)
        Assert.Contains(ecb.Why, w => w.Contains("EUR"));
        Assert.DoesNotContain("Euro area annual inflation up to 4.0%", heads);                     // a release, not a rate decision
        Assert.DoesNotContain("Bank stocks lead European markets higher", heads);                  // general market news
        Assert.DoesNotContain(alice.Stories, s => s.Assets.Any(a => a.Symbol == "MSFT"));
        var apple = alice.Stories.Where(s => s.Assets.Any(a => a.Symbol == "AAPL")).ToList();
        Assert.Equal(5, apple.Count);
        Assert.All(apple, s => Assert.Equal(("High", "AAPL is on your watchlist"), (s.Attention.Level, s.Why.Single())));

        var bob = Heads((await Get<WireFeedDto>(Bob, "/api/v1/news/watchlist")).Stories);
        Assert.Equal(["Microsoft earnings beat estimates"], bob);                                  // MSFT is USD: no ECB decision
    }

    [Fact]
    public async Task CompanyNews_GroupsByCompany_OwnAndCuratedOnly()
    {
        var companies = await Get<WireCompaniesDto>(Alice, "/api/v1/news/companies");
        var apple = Assert.Single(companies.Companies);                                            // MSFT is only Bob's; SAP.DE has no stories
        Assert.Equal(("AAPL", 5, true), (apple.Symbol, apple.StoryCount, apple.OnWatchlist));
        Assert.Equal(3, apple.Stories.Count);                                                      // newest three
        Assert.Equal(apple.Stories.Max(s => s.LastPublishedAtUtc), apple.NewestAtUtc);
        Assert.Equal("MSFT", Assert.Single((await Get<WireCompaniesDto>(Bob, "/api/v1/news/companies")).Companies).Symbol);
    }

    [Fact]
    public async Task MarketWide_IsMarketStoriesByArea_AndMacro_IsMacroOnly()
    {
        var markets = await Get<WireMarketsDto>(Alice, "/api/v1/news/markets");
        Assert.Equal(["Equities", "Rates", "Fx", "Commodities", "Volatility"], markets.Sections.Select(s => s.Area));
        Assert.Equal(["Bank stocks lead European markets higher"], markets.Sections.Single(s => s.Area == "Equities").Stories.Select(s => s.Headline));
        Assert.Equal(["Dollar slips against euro and pound"], markets.Sections.Single(s => s.Area == "Fx").Stories.Select(s => s.Headline));
        Assert.All(markets.Sections.SelectMany(s => s.Stories), s => Assert.Equal("Market", s.Scope));

        var macro = await Get<WireFeedDto>(Alice, "/api/v1/news/macro");
        Assert.Equal(new HashSet<string> { "ECB decides on key interest rates", "Euro area annual inflation up to 4.0%" }, Heads(macro.Stories));
        Assert.Equal("RateDecision", macro.Stories.Single(s => s.Headline.StartsWith("ECB")).EventType);
        Assert.Equal("InflationRelease", macro.Stories.Single(s => s.Headline.StartsWith("Euro")).EventType);
        var rates = await Get<WireFeedDto>(Alice, "/api/v1/news/macro?eventType=RateDecision");
        Assert.Equal(["ECB decides on key interest rates"], rates.Stories.Select(s => s.Headline));
    }

    [Fact]
    public async Task TheFeedsAreDifferentProducts_OverlapOnlyWhereBothContractsHold()
    {
        var latest = Heads((await Get<WireFeedDto>(Alice, "/api/v1/news/latest")).Stories);
        var watch = Heads((await Get<WireFeedDto>(Alice, "/api/v1/news/watchlist")).Stories);
        var company = Heads((await Get<WireCompaniesDto>(Alice, "/api/v1/news/companies")).Companies.SelectMany(c => c.Stories));
        var market = Heads((await Get<WireMarketsDto>(Alice, "/api/v1/news/markets")).Sections.SelectMany(s => s.Stories));
        var macro = Heads((await Get<WireFeedDto>(Alice, "/api/v1/news/macro")).Stories);
        Assert.NotEqual(latest, watch);
        Assert.NotEqual(watch, company);
        Assert.Empty(market.Intersect(macro));
        Assert.Empty(market.Intersect(company));
        // The ECB decision is in Latest (High), My Watchlist (EUR) and Macroeconomic – each by its own rule.
        Assert.Contains("ECB decides on key interest rates", latest.Intersect(watch).Intersect(macro));
    }

    [Fact]
    public async Task Paging_ContinuesWithoutRepeats_AndRejectsBadInput()
    {
        var first = await Get<WireFeedDto>(Alice, "/api/v1/news/watchlist?pageSize=2");
        Assert.Equal(2, first.Stories.Count);
        Assert.NotNull(first.NextCursor);
        var second = await Get<WireFeedDto>(Alice, $"/api/v1/news/watchlist?pageSize=2&cursor={first.NextCursor}");
        Assert.Empty(first.Stories.Select(s => s.Id).Intersect(second.Stories.Select(s => s.Id)));
        Assert.True(first.Stories[^1].LastPublishedAtUtc >= second.Stories[0].LastPublishedAtUtc);

        // Latest pages through every Medium/High story once (the per-company cap applies per page).
        var seen = new List<Guid>();
        string? cursor = null;
        for (var i = 0; i < 10; i++)
        {
            var page = await Get<WireFeedDto>(Alice, $"/api/v1/news/latest?pageSize=2{(cursor is null ? "" : $"&cursor={cursor}")}");
            seen.AddRange(page.Stories.Select(s => s.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }
        Assert.Null(cursor);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(8, seen.Count); // four material AAPL stories, the ECB decision, the inflation release, two broad market stories

        foreach (var bad in new[] { "/api/v1/news/latest?cursor=garbage", "/api/v1/news/latest?attention=Low", "/api/v1/news/latest?hours=0",
                     "/api/v1/news/markets?area=Bonds", "/api/v1/news/markets?cursor=x", "/api/v1/news/macro?eventType=Nope", "/api/v1/news/companies?pageSize=500" })
            (await Alice.GetAsync(bad)).EnsureStatus(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PortfolioNews_IsItsHoldingsAndCurrencies_WithWeightsFromTheValuation()
    {
        // A USD portfolio holding AAPL: the five AAPL stories, each with AAPL's weight; no ECB decision (no EUR exposure).
        var usd = await (await Alice.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
            { Name = "News USD", BaseCurrency = "USD", InitialInvestment = 10_000m, Universe = ["AAPL", "MSFT"] })).ReadAsync<PortfolioDto>();
        await Book(usd.Id, "AAPL");
        var news = await Get<PortfolioNewsDto>(Alice, $"/api/v1/portfolios/{usd.Id}/news");
        Assert.Equal(5, news.Items.Count);
        Assert.All(news.Items, i =>
        {
            Assert.Contains(i.Story.Assets, a => a.Symbol == "AAPL" && a.Held);
            var holding = Assert.Single(i.Exposure);
            Assert.Equal(("Holding", "AAPL"), (holding.Kind, holding.Label));
            Assert.InRange(holding.Weight!.Value, 0.0001, 0.9999);          // part of a portfolio that still holds cash
            Assert.Contains("÷ total value incl. cash", holding.Basis);
            Assert.StartsWith("AAPL is ", Assert.Single(i.Story.Why));
            Assert.Equal("High", i.Story.Attention.Level);
        });
        Assert.DoesNotContain(news.Items, i => i.Story.Headline.StartsWith("ECB"));
        Assert.DoesNotContain(news.Items, i => i.Story.Assets.Any(a => a.Symbol == "MSFT")); // in the universe, not held

        // A EUR portfolio holding SAP.DE: no SAP stories, but the ECB rate decision with the EUR share.
        var eur = await (await Alice.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
            { Name = "News EUR", BaseCurrency = "EUR", InitialInvestment = 10_000m, Universe = ["SAP.DE"] })).ReadAsync<PortfolioDto>();
        await Book(eur.Id, "SAP.DE");
        var euro = Assert.Single((await Get<PortfolioNewsDto>(Alice, $"/api/v1/portfolios/{eur.Id}/news")).Items);
        Assert.Equal("ECB decides on key interest rates", euro.Story.Headline);
        var share = Assert.Single(euro.Exposure);
        Assert.Equal(("Currency", "EUR", 1.0), (share.Kind, share.Label, Math.Round(share.Weight!.Value, 6))); // SAP.DE and cash are all EUR

        // Bob cannot read Alice's portfolio news; nobody signed out can.
        Assert.Equal(HttpStatusCode.NotFound, (await Bob.GetAsync($"/api/v1/portfolios/{usd.Id}/news")).StatusCode);
        using var anonymous = shared.Factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/portfolios/{usd.Id}/news")).StatusCode);

        async Task Book(Guid portfolioId, string symbol)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/portfolios/{portfolioId}/journal")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                    new RecordTransactionRequest { Type = PortfolioTransactionType.Buy, Symbol = symbol, Quantity = 5, Price = 100m }, ApiFactory.Json),
                    System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            (await Alice.SendAsync(request)).EnsureStatus(HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task Feeds_RequireAnApprovedAccount_AndReportSourceFreshness()
    {
        using var anonymous = shared.Factory.CreateSession();
        foreach (var feed in new[] { "latest", "watchlist", "companies", "markets", "macro" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/news/{feed}")).StatusCode);
        var latest = await Get<WireFeedDto>(Alice, "/api/v1/news/latest");
        Assert.All(latest.Sources, s => Assert.Contains(s.Status, new[] { "Fresh", "Delayed", "Unavailable" }));
        Assert.DoesNotContain(latest.Sources, s => s.Group.Contains("Yahoo") || s.Group.Contains("Mock")); // no provider names (D-30)
    }

    [Fact]
    public async Task Stories_CarryTheirEntities_FromProviderTagsAndOfficialSources()
    {
        var markets = await Get<WireMarketsDto>(Alice, "/api/v1/news/markets");
        var fx = markets.Sections.SelectMany(s => s.Stories).Single(s => s.Headline == "Dollar slips against euro and pound");
        Assert.Equal(["EURUSD", "GBPUSD", "DXY"], fx.Entities.Select(e => e.Key));
        Assert.All(fx.Entities, e => Assert.Equal(("Currency", "currency"), (e.Kind, e.KindLabel)));
        var equities = markets.Sections.SelectMany(s => s.Stories).Single(s => s.Headline == "Bank stocks lead European markets higher");
        Assert.Equal([("STOXX Europe 600", "Index"), ("EUR/USD", "Currency")], equities.Entities.Select(e => (e.Name, e.Kind)));

        var macro = await Get<WireFeedDto>(Alice, "/api/v1/news/macro");
        Assert.Equal(("ECB", "central bank"), macro.Stories.Single(s => s.Headline.StartsWith("ECB")).Entities.Select(e => (e.Key, e.KindLabel)).Single());
        Assert.Equal("EUROSTAT", macro.Stories.Single(s => s.Headline.StartsWith("Euro")).Entities.Single().Key);
        // Company stories without such tags have none.
        Assert.All((await Get<WireFeedDto>(Alice, "/api/v1/news/watchlist")).Stories.Where(s => s.Assets.Any()), s => Assert.Empty(s.Entities));
    }

    [Fact]
    public async Task EntityFilter_ShowsOnlyStoriesLinkedToIt_AndRejectsUnknownKeys()
    {
        var eur = await Get<WireMarketsDto>(Alice, "/api/v1/news/markets?entity=EURUSD");
        Assert.Equal(new HashSet<string> { "Bank stocks lead European markets higher", "Dollar slips against euro and pound" },
            Heads(eur.Sections.SelectMany(s => s.Stories)));
        var dxy = await Get<WireMarketsDto>(Alice, "/api/v1/news/markets?entity=dxy");                // keys are case-insensitive
        Assert.Equal(["Dollar slips against euro and pound"], dxy.Sections.SelectMany(s => s.Stories).Select(s => s.Headline));
        var ecb = await Get<WireFeedDto>(Alice, "/api/v1/news/macro?entity=ECB");
        Assert.Equal(["ECB decides on key interest rates"], ecb.Stories.Select(s => s.Headline));
        Assert.Equal(HttpStatusCode.BadRequest, (await Alice.GetAsync("/api/v1/news/markets?entity=NOPE")).StatusCode);
    }
}
