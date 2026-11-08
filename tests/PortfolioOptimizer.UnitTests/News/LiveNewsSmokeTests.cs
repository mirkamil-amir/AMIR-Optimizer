using Microsoft.Extensions.Options;
using PortfolioOptimizer.Infrastructure.News;
using PortfolioOptimizer.UnitTests.MarketData;

namespace PortfolioOptimizer.UnitTests.News;

/// <summary>Opt-in (PO_LIVE_SMOKE=1) checks that the real news sources still answer in the parsed format.</summary>
public class LiveNewsSmokeTests
{
    private static HttpClient Client(string? baseUrl = null)
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (baseUrl is not null) c.BaseAddress = new Uri(baseUrl);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; PortfolioOptimizer/1.0; personal research)");
        return c;
    }

    [LiveSmokeFact]
    public async Task Yahoo_CompanyNews_ForAUsTicker_IsTaggedAndDated()
    {
        var yahoo = new YahooNewsProvider(Client("https://query1.finance.yahoo.com"), Options.Create(new YahooNewsOptions()));
        var items = await yahoo.GetCompanyNewsAsync("AAPL", "Apple Inc.", CancellationToken.None);
        Assert.NotEmpty(items);
        Assert.Contains(items, i => i.RelatedSymbols.Contains("AAPL"));
        Assert.All(items, i => Assert.InRange(i.PublishedAtUtc, DateTime.UtcNow.AddYears(-1), DateTime.UtcNow.AddMinutes(10)));
    }

    [LiveSmokeFact]
    public async Task StatisticsFeeds_AllParse_WithUtcTimesInThePast()
    {
        var c = Client();
        c.DefaultRequestHeaders.UserAgent.Clear();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PortfolioOptimizer/1.0 (educational research; RSS reader)");
        var provider = new StatisticsFeedProvider(c, Options.Create(new StatisticsFeedOptions()));
        foreach (var feed in provider.Feeds)
        {
            var items = await provider.GetFeedAsync(feed, CancellationToken.None);
            Assert.True(items.Count > 0, $"{feed.Publisher}: no items parsed");
            // BEA lists the latest release of every series, discontinued ones back to 2013 (ingestion drops them by retention);
            // what must hold is no future time and at least one recent release.
            Assert.All(items, i => Assert.True(i.PublishedAtUtc <= DateTime.UtcNow.AddMinutes(10), $"{feed.Publisher}: future time {i.PublishedAtUtc:O}"));
            Assert.Contains(items, i => i.PublishedAtUtc >= DateTime.UtcNow.AddDays(-60));
        }
    }

    [LiveSmokeFact]
    public async Task Yahoo_MarketFeed_ReturnsItemsTaggedWithTheInstrument()
    {
        var yahoo = new YahooNewsProvider(Client("https://query1.finance.yahoo.com"), Options.Create(new YahooNewsOptions()));
        var items = await yahoo.GetFeedAsync(yahoo.Feeds.Single(f => f.Key == "EURUSD=X"), CancellationToken.None);
        Assert.NotEmpty(items);
        Assert.Contains(items, i => i.RelatedSymbols.Contains("EURUSD=X"));
    }

    /// <summary>Live smoke test that also needs the operator's contact for SEC requests; skipped (not passed) without it.</summary>
    public sealed class LiveSecFactAttribute : FactAttribute
    {
        public LiveSecFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PO_LIVE_SMOKE") != "1") Skip = "Live provider smoke test; set PO_LIVE_SMOKE=1 to run.";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PO_SEC_CONTACT")))
                Skip = "Set PO_SEC_CONTACT to the operator's name and e-mail to run the SEC EDGAR check (SEC requires it).";
        }
    }

    [LiveSecFact]
    public async Task SecEdgar_FilingList_ParsesWithZonedAcceptanceTimes()
    {
        var contact = Environment.GetEnvironmentVariable("PO_SEC_CONTACT")!;
        var c = new HttpClient { BaseAddress = new Uri("https://www.sec.gov"), Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.TryParseAdd($"Matrix-educational-research {contact}");
        var edgar = new SecEdgarProvider(c, Options.Create(new SecEdgarOptions { Contact = contact }));
        var items = await edgar.GetCompanyNewsAsync("AAPL", "Apple Inc.", CancellationToken.None);
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.InRange(i.PublishedAtUtc, DateTime.UtcNow.AddYears(-5), DateTime.UtcNow.AddMinutes(10)));
    }

    [LiveSmokeFact]
    public async Task OfficialFeeds_AllParse()
    {
        var provider = new OfficialFeedProvider(Client(), Options.Create(new OfficialFeedOptions()));
        foreach (var feed in provider.Feeds)
        {
            var items = await provider.GetFeedAsync(feed, CancellationToken.None);
            Assert.True(items.Count > 0, $"{feed.Publisher}: no items parsed");
            Assert.All(items, i => Assert.StartsWith("http", i.Url));
        }
    }
}
