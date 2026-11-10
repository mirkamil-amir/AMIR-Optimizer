using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.UnitTests.News;

/// <summary>The reviewed entity list and its link rules (AMIR-19): provider tags and official sources only, never headline text.</summary>
public sealed class NewsEntitiesTests
{
    [Fact]
    public void Tags_LinkToTheirEntity_InListOrder_WithTheTagAsMatchMethod()
    {
        var m = NewsEntities.Match(["EURUSD=X", "^GSPC", "AAPL", "CL-LCO1=R", "^gspc"], false, null, null, "Wire One");
        Assert.Equal([("^GSPC", "provider tag ^GSPC"), ("EURUSD", "provider tag EURUSD=X")], m.Select(x => (x.Key, x.MatchMethod)));
    }

    [Fact]
    public void AliasesOfOnePair_LinkOnce_AndInversePairsStayApart()
    {
        Assert.Equal(["USDJPY"], NewsEntities.Match(["USDJPY=X", "JPY=X"], false, null, null, "").Select(x => x.Key));
        Assert.Equal(["EURUSD", "USDEUR"], NewsEntities.Match(["EUR=X", "EURUSD=X"], false, null, null, "").Select(x => x.Key));
    }

    [Theory]
    [InlineData("US", "FED")]
    [InlineData("EU", "ECB")]
    [InlineData("GB", "BOE")]
    [InlineData("JP", "BOJ")]
    public void OfficialCentralBankItems_LinkToTheBankOfTheirRegion(string region, string bank)
    {
        var m = Assert.Single(NewsEntities.Match([], true, NewsCategory.CentralBank, region, "Some publisher"));
        Assert.Equal((bank, $"official central-bank feed ({region})"), (m.Key, m.MatchMethod));
    }

    [Fact]
    public void CentralBankLinks_NeedAnOfficialSourceThatStatesTheCategory()
    {
        Assert.Empty(NewsEntities.Match([], false, NewsCategory.CentralBank, "EU", "Reuters"));       // not official
        Assert.Empty(NewsEntities.Match([], true, null, "EU", "European Central Bank"));            // category from keywords, not the source
        Assert.Empty(NewsEntities.Match([], true, NewsCategory.CentralBank, "CH", "Swiss National Bank")); // bank not on the list
    }

    [Fact]
    public void SimulatedItems_AreNeverAttributedToARealInstitution_ButKeepTagLinks()
    {
        Assert.Empty(NewsEntities.Match([], true, NewsCategory.CentralBank, "US", "Simulated Central Bank", isSimulated: true));
        Assert.Empty(NewsEntities.Match([], true, NewsCategory.Macroeconomic, "EU", "Eurostat", isSimulated: true));
        Assert.Equal(["^GSPC"], NewsEntities.Match(["^GSPC"], false, null, null, "Simulated Wire", isSimulated: true).Select(x => x.Key));
    }

    [Fact]
    public void OfficialStatisticsReleases_LinkToThePublishingOffice()
    {
        Assert.Equal(["EUROSTAT"], NewsEntities.Match([], true, NewsCategory.Macroeconomic, "EU", "Eurostat").Select(x => x.Key));
        Assert.Equal(["BEA"], NewsEntities.Match([], true, NewsCategory.Macroeconomic, "US", "U.S. Bureau of Economic Analysis").Select(x => x.Key));
        Assert.Empty(NewsEntities.Match([], false, null, "EU", "Eurostat"));                          // only the office's own feed
    }

    [Fact]
    public void CentralBanksFor_MapsCurrenciesToTheirBanks()
    {
        Assert.Equal(["FED", "ECB"], NewsEntities.CentralBanksFor(["eur", "USD", "CHF"]));
        Assert.Empty(NewsEntities.CentralBanksFor([]));
    }

    [Fact]
    public void TheList_HasUniqueKeysAndTags_AndKindsAgreeWithTheTaxonomy()
    {
        Assert.Equal(NewsEntities.All.Count, NewsEntities.All.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var tags = NewsEntities.All.SelectMany(e => e.Tags).ToList();
        Assert.Equal(tags.Count, tags.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(NewsEntities.All.Where(e => e.Key.Length > 20), e => Assert.Fail($"{e.Key} is longer than the stored key"));
        foreach (var e in NewsEntities.All)
            foreach (var tag in e.Tags)
                Assert.Equal(e.Kind switch
                {
                    NewsEntityKind.Index => InstrumentKind.Index,
                    NewsEntityKind.Rates => InstrumentKind.Rates,
                    NewsEntityKind.Currency => InstrumentKind.Fx,
                    NewsEntityKind.Commodity => InstrumentKind.Commodity,
                    NewsEntityKind.Volatility => InstrumentKind.Volatility,
                    _ => throw new InvalidOperationException($"{e.Key}: only market instruments have tags"),
                }, NewsTaxonomy.Kind(tag));
    }

    [Fact]
    public void EveryMarketFeedInstrument_IsOnTheList()
    {
        foreach (var feed in PortfolioOptimizer.Infrastructure.News.YahooNewsProvider.MarketInstruments)
            Assert.NotEmpty(NewsEntities.Match([feed.Key], false, null, null, ""));
    }
}
