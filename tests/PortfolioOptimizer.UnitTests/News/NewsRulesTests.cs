using System.Text.Json;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.News;

namespace PortfolioOptimizer.UnitTests.News;

public sealed class NewsNormalizerTests
{
    [Fact]
    public void CanonicalUrl_IgnoresSchemeCaseTrackingAndTrailingSlash()
    {
        var a = NewsNormalizer.Fingerprint("https://Example.com/markets/story-1/?utm_source=x&id=7#top");
        var b = NewsNormalizer.Fingerprint("http://example.com/markets/story-1?id=7");
        Assert.Equal(a, b);
        Assert.NotEqual(a, NewsNormalizer.Fingerprint("https://example.com/markets/story-1?id=8"));
    }

    [Fact]
    public void HeadlineKey_MatchesSyndicatedCopies_ButNotDifferentStories()
    {
        Assert.Equal(NewsNormalizer.HeadlineKey("Siemens Beats Estimates; Shares Rise!"), NewsNormalizer.HeadlineKey("siemens beats estimates shares rise"));
        Assert.NotEqual(NewsNormalizer.HeadlineKey("Siemens beats estimates"), NewsNormalizer.HeadlineKey("Siemens misses estimates"));
    }

    [Fact]
    public void Teaser_StripsMarkup_AndShortensAtAWordBoundary()
    {
        var text = "<p>" + string.Join(' ', Enumerable.Repeat("word", 200)) + "</p>";
        var teaser = NewsNormalizer.Teaser(text, 50)!;
        Assert.DoesNotContain("<p>", teaser);
        Assert.EndsWith("…", teaser);
        Assert.True(teaser.Length <= 51);
        Assert.Null(NewsNormalizer.Teaser("   "));
    }
}

public sealed class NewsClassifierTests
{
    [Theory]
    [InlineData("Apple reports record quarterly results", NewsCategory.Earnings, "quarterly results")]
    [InlineData("Siemens agrees to buy software maker", NewsCategory.CorporateTransaction, "agrees to buy")]
    [InlineData("EU regulators open antitrust probe into chipmaker", NewsCategory.RegulatoryLegal, "antitrust")]
    [InlineData("Euro zone inflation slows to 2.1%", NewsCategory.Macroeconomic, "inflation")]
    [InlineData("Oil climbs as OPEC extends cuts", NewsCategory.CommoditiesEnergy, "oil")]
    [InlineData("DAX closes at record high", NewsCategory.MarketIndex, "dax")]
    [InlineData("Federal Reserve holds rates steady", NewsCategory.CentralBank, "federal reserve")]
    public void Headlines_AreClassified_WithTheMatchedTermAsBasis(string headline, NewsCategory expected, string term)
    {
        var c = NewsClassifier.Classify(headline, null);
        Assert.Equal(expected, c.Category);
        Assert.Contains(term, c.Basis);
        Assert.StartsWith("headline", c.Basis);
    }

    [Theory]
    [InlineData("Market turmoil hits tech shares")]      // "oil" inside "turmoil"
    [InlineData("Second-hand car dealer opens new store")] // "sec" inside "second"
    [InlineData("Europe's startups attract investors")]  // "euro" inside "europe"
    public void Terms_MatchWholeWordsOnly(string headline) =>
        Assert.Equal(NewsCategory.General, NewsClassifier.Classify(headline, null).Category);

    [Fact]
    public void OfficialFeedCategory_IsKept_AndSummaryIsUsedWhenTheHeadlineIsNeutral()
    {
        Assert.Equal(NewsCategory.CentralBank, NewsClassifier.Classify("Minutes of the Board meeting", null, NewsCategory.CentralBank).Category);
        var c = NewsClassifier.Classify("Company update", "The group raised its full-year guidance.");
        Assert.Equal(NewsCategory.Earnings, c.Category);
        Assert.StartsWith("summary", c.Basis);
    }

    [Fact]
    public void Scope_IsCompanyWhenLinked_MacroForEconomyTypes_ElseMarket()
    {
        Assert.Equal(NewsScope.Company, NewsClassifier.Scope(NewsCategory.Macroeconomic, 1));
        Assert.Equal(NewsScope.Macro, NewsClassifier.Scope(NewsCategory.CentralBank, 0));
        Assert.Equal(NewsScope.Market, NewsClassifier.Scope(NewsCategory.MarketIndex, 0));
    }
}

public sealed class NewsRelevanceTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CompanyEarnings_FreshAndCorroborated_IsHigh_WithEveryReason()
    {
        var r = NewsRelevance.Assess(NewsCategory.Earnings, 1, false, Now.AddHours(-3), 2, Now);
        Assert.Equal(6, r.Points); // 2 company + 2 material + 1 recent + 1 corroborated
        Assert.Equal(RelevanceLevel.High, r.Level);
        Assert.Equal(4, r.Reasons.Count);
    }

    [Fact]
    public void OldUncategorizedSingleSource_IsLow()
    {
        var r = NewsRelevance.Assess(NewsCategory.General, 0, false, Now.AddDays(-5), 1, Now);
        Assert.Equal(0, r.Points);
        Assert.Equal(RelevanceLevel.Low, r.Level);
        Assert.Contains("no relevance criterion met", r.Reasons);
    }

    [Fact]
    public void OfficialCentralBankRelease_IsMediumWhenOld_HighNever_WithoutCompanyLink()
    {
        var r = NewsRelevance.Assess(NewsCategory.CentralBank, 0, true, Now.AddDays(-2), 1, Now);
        Assert.Equal(3, r.Points);
        Assert.Equal(RelevanceLevel.Medium, r.Level);
    }
}

public sealed class NewsProviderParsingTests
{
    // Synthetic payloads in the providers' real response shapes (verified against live responses on 2026-10-15).
    private const string YahooJson = """
        {"count":3,"quotes":[],"news":[
          {"uuid":"u1","title":"Siemens wins rail contract","publisher":"Example Wire","link":"https://example.com/a1","providerPublishTime":1792015200,
           "type":"STORY","thumbnail":{"resolutions":[{"url":"https://img.example.com/a1.jpg","width":1280,"height":720,"tag":"original"},
           {"url":"https://img.example.com/a1-140.jpg","width":140,"height":140,"tag":"140x140"}]},"relatedTickers":["SIE.DE","ALO.PA"]},
          {"uuid":"u2","title":"No link","publisher":"Example Wire","providerPublishTime":1792015200},
          {"uuid":"u3","title":"Without time","publisher":"Example Wire","link":"https://example.com/a3"}
        ]}
        """;

    [Fact]
    public void Yahoo_ParsesTagsAndOriginalThumbnail_AndSkipsIncompleteItems()
    {
        using var doc = JsonDocument.Parse(YahooJson);
        var items = YahooNewsProvider.Parse(doc.RootElement);
        var a = Assert.Single(items);
        Assert.Equal(["SIE.DE", "ALO.PA"], a.RelatedSymbols);
        Assert.Equal("https://img.example.com/a1.jpg", a.ImageUrl);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792015200).UtcDateTime, a.PublishedAtUtc);
        Assert.Null(a.Summary); // the endpoint provides none; nothing is generated
    }

    [Fact]
    public void Yahoo_EmptyOrUnexpectedPayload_GivesNoArticles()
    {
        using var doc = JsonDocument.Parse("""{"quotes":[]}""");
        Assert.Empty(YahooNewsProvider.Parse(doc.RootElement));
    }

    private static readonly NewsFeed Feed = new("fed", "Federal Reserve Board", "https://example.com/feed", "US", NewsCategory.CentralBank, true, "terms");

    [Fact]
    public void Rss_ParsesItems_WithTimeZones_AndDropsDescriptionsThatRepeatTheTitle()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8" ?>
            <rss version="2.0"><channel><title>Feed</title>
              <item><title>Rate decision</title><link><![CDATA[https://example.com/r1]]></link><description><![CDATA[Rate decision]]></description>
                <pubDate>Tue, 13 Oct 2026 18:00:00 GMT</pubDate></item>
              <item><title>Speech &amp; outlook</title><link>https://example.com/r2</link><description>Remarks on the outlook.</description>
                <pubDate>Wed, 14 Oct 2026 02:00:00 +0200</pubDate></item>
              <item><title>Undated</title><link>https://example.com/r3</link></item>
              <item><title>Ambiguous zone</title><link>https://example.com/r4</link><pubDate>Wed, 14 Oct 2026 02:00:00 CET</pubDate></item>
            </channel></rss>
            """;
        var items = OfficialFeedProvider.ParseRss(xml, Feed);
        Assert.Equal(2, items.Count);
        Assert.Equal(new DateTime(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc), items[0].PublishedAtUtc);
        Assert.Null(items[0].Summary);
        Assert.Equal("Speech & outlook", items[1].Headline);
        Assert.Equal(new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc), items[1].PublishedAtUtc);
        Assert.All(items, i => Assert.True(i.IsOfficial));
        Assert.All(items, i => Assert.Equal(NewsCategory.CentralBank, i.SourceCategory));
    }

    [Fact]
    public void Rss_Malformed_ThrowsAProviderError()
    {
        Assert.Throws<NewsProviderException>(() => OfficialFeedProvider.ParseRss("<rss><channel><item>", Feed));
    }
}

/// <summary>Event comparison on a hand-built New York series (EST, before the 2026-03-08 DST switch).</summary>
public sealed class EventPriceComparisonTests
{
    private static readonly MarketVenue Us = Markets.UnitedStates;

    // Mon 2 Mar … Mon 9 Mar 2026 (no 7/8 Mar weekend bars).
    private static readonly DailyClose[] Stock =
    [
        new(new(2026, 3, 2), 100), new(new(2026, 3, 3), 102), new(new(2026, 3, 4), 101), new(new(2026, 3, 5), 105),
        new(new(2026, 3, 6), 104), new(new(2026, 3, 9), 110),
    ];

    private static readonly DailyClose[] Bench =
    [
        new(new(2026, 3, 2), 50), new(new(2026, 3, 3), 50.5), new(new(2026, 3, 4), 50), new(new(2026, 3, 5), 51),
        new(new(2026, 3, 6), 51), new(new(2026, 3, 9), 52),
    ];

    [Fact]
    public void DuringTheSession_ReferenceIsThePreviousClose()
    {
        // 15:00 UTC = 10:00 New York on Tue 3 Mar: the Tuesday close already contains the reaction, Monday's does not.
        var r = EventPriceComparison.Compute(new DateTime(2026, 3, 3, 15, 0, 0, DateTimeKind.Utc), Us, Stock, Bench, [1, 5]);
        Assert.Equal(new DateOnly(2026, 3, 2), r.ReferenceDate);
        Assert.Equal("during the trading session", r.Timing);
        var h1 = r.Horizons[0];
        Assert.Equal(new DateOnly(2026, 3, 3), h1.Date);
        Assert.Equal(0.02, h1.StockChange!.Value, 12);
        Assert.Equal(0.01, h1.BenchmarkChange!.Value, 12);
        Assert.Equal(0.01, h1.Difference!.Value, 12);
        var h5 = r.Horizons[1];
        Assert.Equal(new DateOnly(2026, 3, 9), h5.Date);
        Assert.Equal(0.10, h5.StockChange!.Value, 12);
        Assert.Contains(r.Limitations, l => l.Contains("during trading hours"));
    }

    [Fact]
    public void AfterTheClose_ReferenceIsThatDaysClose()
    {
        // 22:00 UTC = 17:00 New York, after the 16:00 close.
        var r = EventPriceComparison.Compute(new DateTime(2026, 3, 3, 22, 0, 0, DateTimeKind.Utc), Us, Stock, Bench, [1]);
        Assert.Equal(new DateOnly(2026, 3, 3), r.ReferenceDate);
        Assert.Equal("after the session closed", r.Timing);
        Assert.Equal(101.0 / 102 - 1, r.Horizons[0].StockChange!.Value, 12);
    }

    [Fact]
    public void OnAWeekend_ReferenceIsFridaysClose_AndTooShortHistoryIsReported()
    {
        var r = EventPriceComparison.Compute(new DateTime(2026, 3, 7, 12, 0, 0, DateTimeKind.Utc), Us, Stock, Bench, [1, 5]);
        Assert.Equal(new DateOnly(2026, 3, 6), r.ReferenceDate);
        Assert.StartsWith("outside trading days", r.Timing);
        Assert.Equal(new DateOnly(2026, 3, 9), r.Horizons[0].Date);
        Assert.Null(r.Horizons[1].StockChange);
        Assert.Contains("Fewer than 5", r.Horizons[1].Limitation);
    }

    [Fact]
    public void MissingBenchmarkDate_GivesNoDifference_InsteadOfASubstitute()
    {
        var bench = Bench.Where(b => b.Date != new DateOnly(2026, 3, 3)).ToArray();
        var r = EventPriceComparison.Compute(new DateTime(2026, 3, 3, 15, 0, 0, DateTimeKind.Utc), Us, Stock, bench, [1]);
        Assert.NotNull(r.Horizons[0].StockChange);
        Assert.Null(r.Horizons[0].BenchmarkChange);
        Assert.Null(r.Horizons[0].Difference);
        Assert.Contains("no stored close on 2026-03-03", r.Horizons[0].Limitation);
    }

    [Fact]
    public void BeforeAnyStoredClose_ThereIsNoReference()
    {
        var r = EventPriceComparison.Compute(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), Us, Stock, Bench);
        Assert.Null(r.ReferenceDate);
        Assert.All(r.Horizons, h => Assert.Null(h.StockChange));
    }

    [Fact]
    public void TokyoClose_Moved_From1500To1530_InNovember2024() =>
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(15, 0)), Markets.Session(Markets.ByCountry("JP"), new DateOnly(2024, 11, 1)));
}
