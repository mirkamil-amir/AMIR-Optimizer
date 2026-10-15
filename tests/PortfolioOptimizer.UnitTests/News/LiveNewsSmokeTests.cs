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
