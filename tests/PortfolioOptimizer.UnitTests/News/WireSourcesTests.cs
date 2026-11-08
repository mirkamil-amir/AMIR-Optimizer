using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.News;

namespace PortfolioOptimizer.UnitTests.News;

/// <summary>Classification rules version 2: instrument kinds of provider tags, scope, market area and event type.</summary>
public sealed class NewsTaxonomyTests
{
    [Theory]
    [InlineData("AAPL", InstrumentKind.Security)]
    [InlineData("SAP.DE", InstrumentKind.Security)]
    [InlineData("BRK-B", InstrumentKind.Security)]
    [InlineData("^GSPC", InstrumentKind.Index)]
    [InlineData("^STOXX", InstrumentKind.Index)]
    [InlineData("^TNX", InstrumentKind.Rates)]
    [InlineData("TMBMKDE-10Y", InstrumentKind.Rates)]
    [InlineData("ZN=F", InstrumentKind.Rates)]
    [InlineData("EURUSD=X", InstrumentKind.Fx)]
    [InlineData("EUR=X", InstrumentKind.Fx)]
    [InlineData("DX-Y.NYB", InstrumentKind.Fx)]
    [InlineData("GC=F", InstrumentKind.Commodity)]
    [InlineData("CL=F", InstrumentKind.Commodity)]
    [InlineData("^VIX", InstrumentKind.Volatility)]
    [InlineData("VX=F", InstrumentKind.Volatility)]
    public void Kind_FollowsYahooSymbolSyntax(string tag, InstrumentKind expected) => Assert.Equal(expected, NewsTaxonomy.Kind(tag));

    [Fact]
    public void MarketArea_NeedsOnlyMarketInstruments_MajorityWins_TiesInDocumentedOrder()
    {
        // Real tag sets seen on 2026-11-08 (Yahoo search for ^GSPC, EURUSD=X, GC=F).
        Assert.Null(NewsTaxonomy.MarketArea(["BROS", "^GSPC", "NVDA"]));                       // single-stock article tagged with the index too
        Assert.Equal(NewsMarketArea.Equities, NewsTaxonomy.MarketArea(["^STOXX", "EURUSD=X"]));  // "Bank Stocks Lead European Markets Higher": tie → equities
        Assert.Equal(NewsMarketArea.Rates, NewsTaxonomy.MarketArea(["EUR=X", "EURUSD=X", "TMBMKFR-10Y", "TMBMKDE-10Y"])); // tie FX/rates → rates
        Assert.Equal(NewsMarketArea.Fx, NewsTaxonomy.MarketArea(["DX-Y.NYB", "EUR=X", "GBP=X", "EURUSD=X", "GC=F"]));
        Assert.Equal(NewsMarketArea.Commodities, NewsTaxonomy.MarketArea(["GC=F"]));
        Assert.Equal(NewsMarketArea.Volatility, NewsTaxonomy.MarketArea(["^VIX"]));
        Assert.Null(NewsTaxonomy.MarketArea([]));
    }

    [Fact]
    public void Scope_SecurityTagsMakeCompanyNews_EvenOutsideTheCatalogue_FundsAndInstrumentsMakeMarketNews()
    {
        Assert.Equal(NewsScope.Company, NewsTaxonomy.Scope(["AAPL"], linkedEquities: 1, linkedOther: 0, NewsCategory.General));
        Assert.Equal(NewsScope.Company, NewsTaxonomy.Scope(["BROS", "^GSPC"], 0, 0, NewsCategory.MarketIndex));     // BROS not in the catalogue
        Assert.Equal(NewsScope.Market, NewsTaxonomy.Scope(["^STOXX", "EURUSD=X"], 0, 0, NewsCategory.General));
        Assert.Equal(NewsScope.Market, NewsTaxonomy.Scope(["SPY"], 0, 1, NewsCategory.General, new HashSet<string> { "SPY" })); // catalogue ETF
        Assert.Equal(NewsScope.Macro, NewsTaxonomy.Scope([], 0, 0, NewsCategory.CentralBank));                        // version 1 rule without tags
        Assert.Equal(NewsScope.Market, NewsTaxonomy.Scope([], 0, 0, NewsCategory.General));
    }

    [Theory]
    [InlineData("Acme files for Chapter 11 protection", NewsEventType.Bankruptcy)]
    [InlineData("Acme agrees to buy Widget Co in $2bn deal", NewsEventType.MergerOrAcquisition)]
    [InlineData("Acme raises outlook after strong quarter", NewsEventType.Guidance)]
    [InlineData("Acme earnings beat estimates", NewsEventType.Results)]
    [InlineData("Acme boosts quarterly dividend", NewsEventType.DividendOrBuyback)]
    [InlineData("Acme CEO steps down", NewsEventType.ManagementChange)]
    [InlineData("Acme faces antitrust probe", NewsEventType.LegalOrRegulatory)]
    [InlineData("Acme opens a new store in Berlin", NewsEventType.General)]
    public void CompanyRules(string headline, NewsEventType expected) =>
        Assert.Equal(expected, NewsTaxonomy.Classify(headline, null, NewsScope.Company, isOfficial: false).EventType);

    [Theory] // official release titles as published on 2026-11-04..08 (BEA, Eurostat, ONS)
    [InlineData("GDP (Advance Estimate), 3rd Quarter 2026", NewsEventType.GrowthRelease)]
    [InlineData("Personal Income and Outlays, September 2026", NewsEventType.ActivityRelease)]
    [InlineData("U.S. International Trade in Goods and Services, September 2026", NewsEventType.TradeOrFiscalRelease)]
    [InlineData("Euro area annual inflation up to 4.0%", NewsEventType.InflationRelease)]
    [InlineData("Euro area unemployment at 6.5%", NewsEventType.LabourRelease)]
    [InlineData("Volume of retail trade down by 0.7% in the euro area and by 0.6% in the EU", NewsEventType.ActivityRelease)]
    [InlineData("Industrial producer prices up by 2.5% in the euro area and by 2.6% in the EU", NewsEventType.InflationRelease)]
    [InlineData("ECB decides on key interest rates", NewsEventType.RateDecision)]
    [InlineData("Speech by the Chair at an economic forum", NewsEventType.PolicyStatement)]
    [InlineData("Federal Reserve Board announces approval of application", NewsEventType.OtherRelease)]
    public void MacroRules_OnOfficialTitles(string headline, NewsEventType expected) =>
        Assert.Equal(expected, NewsTaxonomy.Classify(headline, null, NewsScope.Macro, isOfficial: true).EventType);

    [Fact]
    public void SourceTypeWins_MarketArticlesAreMarketNews_AndTheBasisIsStated()
    {
        var own = NewsTaxonomy.Classify("Apple Inc.: 8-K - Current report", null, NewsScope.Company, true, NewsEventType.Results, "SEC form 8-K item 2.02");
        Assert.Equal((NewsEventType.Results, "SEC form 8-K item 2.02"), (own.EventType, own.Basis));
        Assert.Equal(NewsEventType.MarketNews, NewsTaxonomy.Classify("Bank stocks lead European markets higher", null, NewsScope.Market, false).EventType);
        Assert.Contains("“gdp”", NewsTaxonomy.Classify("UK GDP rises", null, NewsScope.Macro, true).Basis);
        Assert.Equal(NewsEventType.General, NewsTaxonomy.Classify("Markets digest the week", null, NewsScope.Macro, isOfficial: false).EventType);
    }
}

public sealed class NewsScheduleTests
{
    [Fact]
    public void Merge_KeepsEachSymbolOnceUnderItsHighestPriority()
    {
        var merged = NewsSchedule.Merge([
            new("MSFT", "Microsoft", ScheduledCompanySource.Curated),
            new("AAPL", "Apple", ScheduledCompanySource.Watched),
            new("aapl", "Apple", ScheduledCompanySource.Held),
            new("SAP.DE", "SAP", ScheduledCompanySource.Curated),
        ]);
        Assert.Equal(["AAPL", "MSFT", "SAP.DE"], merged.Select(c => c.Symbol.ToUpperInvariant()));
        Assert.Equal(ScheduledCompanySource.Held, merged[0].Source);
    }

    [Fact]
    public void SelectDue_TakesOnlyDue_HeldFirst_StalestFirst_WithinTheBudget()
    {
        var merged = NewsSchedule.Merge([
            new("A", null, ScheduledCompanySource.Curated), new("B", null, ScheduledCompanySource.Watched),
            new("C", null, ScheduledCompanySource.Watched), new("D", null, ScheduledCompanySource.Held), new("E", null, ScheduledCompanySource.Held),
        ]);
        var last = new Dictionary<string, DateTime?> { ["B"] = new DateTime(2026, 11, 8, 9, 0, 0), ["C"] = null, ["D"] = new DateTime(2026, 11, 8, 8, 0, 0) };
        var due = NewsSchedule.SelectDue(merged, s => s != "E", s => last.GetValueOrDefault(s), budget: 3);
        Assert.Equal(["D", "C", "B"], due.Select(c => c.Symbol)); // E not due; held before watched; never-fetched C before B; A over budget
        Assert.Empty(NewsSchedule.SelectDue(merged, _ => true, _ => null, budget: 0));
    }
}

/// <summary>Parsers of the new sources, on payloads in the shape the live sources returned on 2026-11-08.</summary>
public sealed class WireSourceParsingTests
{
    private static NewsFeed Feed(string key) => new StatisticsFeedProvider(new HttpClient(), Options.Create(new StatisticsFeedOptions())).Feeds.Single(f => f.Key == key);

    private const string Bea = """
        <?xml version="1.0" encoding="us-ascii"?>
        <rss version="2.0"><channel><title>U.S. Bureau of Economic Analysis</title>
          <item><dbId>101</dbId><title>GDP (Advance Estimate), 3rd Quarter 2026</title>
            <link>https://www.bea.gov/news/2026/gdp-advance-estimate-3rd-quarter-2026</link>
            <guid>https://www.bea.gov/news/2026/gdp-advance-estimate-3rd-quarter-2026</guid>
            <description>&lt;p&gt;Real gross domestic product (GDP) increased at an annual rate of 3.4 percent.&lt;/p&gt;</description>
            <vintage>adv</vintage><NextReleaseDate>November 25, 2026</NextReleaseDate><unitsOfMeasure>BLN$</unitsOfMeasure>
            <pubDate>Thu, 29 Oct 2026 08:30:00 EDT</pubDate></item>
          <item><dbId>102</dbId><title>U.S. International Trade in Goods and Services, September 2026</title>
            <link>https://www.bea.gov/news/2026/us-international-trade-goods-and-services-september-2026</link>
            <guid>https://www.bea.gov/news/2026/us-international-trade-goods-and-services-september-2026</guid>
            <description>The trade deficit decreased.</description><pubDate>Wed, 04 Nov 2026 08:30:00 EST</pubDate></item>
          <item><title>No date</title><link>https://www.bea.gov/news/x</link></item>
        </channel></rss>
        """;

    [Fact]
    public void Bea_EasternTimeIsConvertedToUtc_ForThisFeedOnly()
    {
        var items = StatisticsFeedProvider.Parse(Bea, Feed("bea"));
        Assert.Equal(2, items.Count);
        Assert.Equal(new DateTime(2026, 10, 29, 12, 30, 0, DateTimeKind.Utc), items[0].PublishedAtUtc); // EDT = UTC−4
        Assert.Equal(new DateTime(2026, 11, 4, 13, 30, 0, DateTimeKind.Utc), items[1].PublishedAtUtc);  // EST = UTC−5
        Assert.All(items, i => Assert.True(i.IsOfficial && i.Region == "US" && i.SourceCategory == NewsCategory.Macroeconomic));
        Assert.False(OfficialFeedProvider.TryParseDate("Thu, 29 Oct 2026 08:30:00 EDT", out _)); // central-bank feeds still reject it
    }

    [Fact]
    public void Ons_KeepsOnlyEconomicReleases()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?><rss version="2.0"><channel><title>ONS Release Calendar RSS Feed.</title>
              <item><title>GDP monthly estimate, UK: September 2026</title><link>https://www.ons.gov.uk/releases/gdpmonthlyestimateukseptember2026</link>
                <description>Gross domestic product (GDP) measures the value of goods and services.</description><guid>https://www.ons.gov.uk/releases/gdp</guid>
                <pubDate>Fri, 06 Nov 2026 07:00:00 +0000</pubDate></item>
              <item><title>Experiences of NHS healthcare services in England: 5 November 2026</title><link>https://www.ons.gov.uk/releases/nhs</link>
                <guid>https://www.ons.gov.uk/releases/nhs</guid><pubDate>Thu, 05 Nov 2026 09:30:00 +0000</pubDate></item>
            </channel></rss>
            """;
        var item = Assert.Single(StatisticsFeedProvider.Parse(xml, Feed("ons")));
        Assert.StartsWith("GDP monthly estimate", item.Headline);
        Assert.Equal("GB", item.Region);
    }

    [Fact]
    public void Eurostat_AtomEntriesParse_EntriesWithoutZoneOrLinkAreSkipped()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Eurostat - Custom RSS Feed</title>
              <entry><title>Euro area annual inflation up to 4.0%</title>
                <link rel="alternate" href="https://ec.europa.eu/eurostat/product?code=2-30102026-AP" />
                <author><name>Eurostat</name></author><id>https://ec.europa.eu/eurostat/product?code=2-30102026-AP</id>
                <updated>2026-10-30T10:00:00Z</updated><published>2026-10-30T10:00:00Z</published>
                <summary type="html">Euro area annual inflation is expected to be 4.0% in October 2026.</summary></entry>
              <entry><title>No zone</title><link rel="alternate" href="https://ec.europa.eu/eurostat/product?code=x" /><published>2026-10-30T10:00:00</published></entry>
              <entry><title>No link</title><published>2026-10-30T10:00:00Z</published></entry>
            </feed>
            """;
        var item = Assert.Single(StatisticsFeedProvider.Parse(xml, Feed("eurostat")));
        Assert.Equal(new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc), item.PublishedAtUtc);
        Assert.Equal("https://ec.europa.eu/eurostat/product?code=2-30102026-AP", item.Url);
        Assert.Equal("EU", item.Region);
    }

    // EDGAR company filing list (browse-edgar output=atom). Shape per EDGAR's documented feed; not yet checked live (needs the SEC contact).
    private const string Edgar = """
        <?xml version="1.0" encoding="ISO-8859-1" ?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <company-info><cik>0000320193</cik><conformed-name>Apple Inc.</conformed-name></company-info>
          <title>Apple Inc.  (0000320193)</title><updated>2026-11-08T12:00:00-05:00</updated>
          <entry>
            <category label="form type" scheme="https://www.sec.gov/" term="8-K" />
            <content type="text/xml"><filing-date>2026-10-29</filing-date><filing-type>8-K</filing-type><form-name>Current report</form-name>
              <items-desc>items 2.02 and 9.01</items-desc></content>
            <id>urn:tag:sec.gov,2008:accession-number=0000320193-26-000101</id>
            <link href="https://www.sec.gov/Archives/edgar/data/320193/000032019326000101/0000320193-26-000101-index.htm" rel="alternate" type="text/html" />
            <title>8-K  - Current report</title><updated>2026-10-29T16:30:41-04:00</updated>
          </entry>
          <entry>
            <category label="form type" scheme="https://www.sec.gov/" term="4" />
            <id>urn:tag:sec.gov,2008:accession-number=0000320193-26-000102</id>
            <link href="https://www.sec.gov/Archives/edgar/data/320193/x-index.htm" rel="alternate" type="text/html" />
            <title>4  - Statement of changes in beneficial ownership</title><updated>2026-10-30T18:00:00-04:00</updated>
          </entry>
          <entry>
            <category label="form type" scheme="https://www.sec.gov/" term="10-K" />
            <id>urn:tag:sec.gov,2008:accession-number=0000320193-26-000103</id>
            <link href="https://www.sec.gov/Archives/edgar/data/320193/y-index.htm" rel="alternate" type="text/html" />
            <title>10-K  - Annual report</title><updated>2026-10-31T06:01:00</updated>
          </entry>
        </feed>
        """;

    [Fact]
    public void Edgar_KeepsEventFilings_WithAcceptanceTimeInUtc_TypeFromItem_LinkedByFilingNotName()
    {
        var item = Assert.Single(SecEdgarProvider.Parse(Edgar, "AAPL")); // form 4 is not kept; the 10-K has no zone and is skipped
        Assert.Equal(new DateTime(2026, 10, 29, 20, 30, 41, DateTimeKind.Utc), item.PublishedAtUtc);
        Assert.Equal(NewsEventType.Results, item.SourceEventType);
        Assert.Equal("SEC form 8-K item 2.02", item.SourceEventBasis);
        Assert.Contains("Apple Inc.: 8-K", item.Headline);
        Assert.EndsWith("(items 2.02, 9.01), filed 2026-10-29", item.Headline);
        Assert.Equal(["AAPL"], item.RelatedSymbols);
        Assert.Equal(SecEdgarProvider.Match, item.MatchMethod);
        Assert.True(item.IsOfficial);
        Assert.Empty(SecEdgarProvider.Parse("<html><body>No matching Ticker Symbol.</body></html>", "ZZZZ"));
    }

    [Theory]
    [InlineData("10-Q", null, NewsEventType.PeriodicReport)]
    [InlineData("20-F", null, NewsEventType.PeriodicReport)]
    [InlineData("6-K", null, NewsEventType.OtherFiling)]
    [InlineData("8-K", "items 1.03 and 2.02", NewsEventType.Bankruptcy)]
    [InlineData("8-K", "item 2.01", NewsEventType.MergerOrAcquisition)]
    [InlineData("8-K", "items 5.02 and 9.01", NewsEventType.ManagementChange)]
    [InlineData("8-K", "item 1.01", NewsEventType.MaterialAgreement)]
    [InlineData("8-K/A", "items 3.02", NewsEventType.CapitalRaise)]
    [InlineData("8-K", "items 7.01 and 9.01", NewsEventType.OtherFiling)]
    [InlineData("8-K", null, NewsEventType.OtherFiling)]
    public void Edgar_EventTypeFromFormAndItem(string form, string? items, NewsEventType expected) =>
        Assert.Equal(expected, SecEdgarProvider.EventType(form, items).Type);

    [Theory]
    [InlineData("AAPL", true)]
    [InlineData("BRK-B", true)]
    [InlineData("SAP.DE", false)]
    [InlineData("7203.T", false)]
    [InlineData("^GSPC", false)]
    public void Edgar_SupportsUsListingsOnly(string symbol, bool expected) =>
        Assert.Equal(expected, new SecEdgarProvider(new HttpClient(), Options.Create(new SecEdgarOptions())).Supports(symbol));

    [Fact]
    public void Edgar_IsOffWithoutTheOperatorsContact()
    {
        Assert.False(new SecEdgarProvider(new HttpClient(), Options.Create(new SecEdgarOptions())).Descriptor.IsConfigured);
        Assert.True(new SecEdgarProvider(new HttpClient(), Options.Create(new SecEdgarOptions { Contact = "Operator ops@example.test" })).Descriptor.IsConfigured);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task YahooMarketFeed_QueriesTheInstrument_AndKeepsItsRegionOnlyWhenTagged()
    {
        const string body = """
            {"news":[
              {"uuid":"1","title":"Bank Stocks Lead European Markets Higher","publisher":"The Wall Street Journal","link":"https://www.wsj.com/a",
               "providerPublishTime":1793801655,"relatedTickers":["^STOXX50E","EURUSD=X"]},
              {"uuid":"2","title":"Some fund idea","publisher":"Motley Fool","link":"https://www.fool.com/b","providerPublishTime":1793801000,"relatedTickers":["DIVO"]}
            ]}
            """;
        var handler = new StubHandler(body);
        var yahoo = new YahooNewsProvider(new HttpClient(handler) { BaseAddress = new Uri("https://query1.finance.yahoo.com") }, Options.Create(new YahooNewsOptions()));
        var feed = yahoo.Feeds.Single(f => f.Key == "^STOXX50E");
        var items = await yahoo.GetFeedAsync(feed, CancellationToken.None);
        Assert.Contains("q=%5ESTOXX50E", handler.Requests.Single().Query);
        Assert.Equal("EU", items[0].Region);
        Assert.Null(items[1].Region);
        Assert.All(items, i => Assert.Null(i.SourceCategory)); // the query's category is never forced on articles
        Assert.Empty(new YahooNewsProvider(new HttpClient(), Options.Create(new YahooNewsOptions { MarketFeeds = false })).Feeds);
    }
}
