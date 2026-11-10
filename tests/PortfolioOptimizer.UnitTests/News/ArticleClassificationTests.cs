using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.UnitTests.News;

/// <summary>
/// The whole-article classification used both when an article is stored and when it is classified again under a newer
/// rules version (classification history, AMIR-18): the same inputs give the same result as the separate rules.
/// </summary>
public sealed class ArticleClassificationTests
{
    private static readonly HashSet<string> NoFunds = new(StringComparer.OrdinalIgnoreCase);

    private static ArticleClassification Classify(string headline, string[] tags, int equities = 0, int other = 0, bool official = false,
        NewsCategory? sourceCategory = null, NewsEventType? sourceType = null, string? sourceBasis = null, IReadOnlySet<string>? funds = null) =>
        NewsTaxonomy.ClassifyArticle(new ArticleClassificationInput(headline, null, tags, equities, other, funds ?? NoFunds, official,
            sourceCategory, sourceType, sourceBasis));

    [Fact]
    public void CompanyArticle_GetsCompanyScope_AndKeywordEventType()
    {
        var c = Classify("Apple earnings beat estimates on iPhone demand", ["AAPL"], equities: 1);
        Assert.Equal((NewsCategory.Earnings, NewsScope.Company, NewsEventType.Results, (NewsMarketArea?)null), (c.Category, c.Scope, c.EventType, c.MarketArea));
        Assert.Equal("headline mentions “earnings”", c.EventTypeBasis);
    }

    [Fact]
    public void MarketArticle_GetsMarketScope_AndTheAreaOfItsTags()
    {
        var c = Classify("Dollar slips against euro and pound", ["EURUSD=X", "GBPUSD=X", "DX-Y.NYB"]);
        Assert.Equal((NewsScope.Market, NewsEventType.MarketNews, (NewsMarketArea?)NewsMarketArea.Fx), (c.Scope, c.EventType, c.MarketArea));
    }

    [Fact]
    public void WithoutTags_AnIndexListingAloneMeansMarket_InEquities()
    {
        // Articles stored before 2026-11-08 have no tags: only the linked listings and the category decide.
        var c = Classify("FTSE 100 closes at a record", [], other: 1);
        Assert.Equal((NewsScope.Market, (NewsMarketArea?)NewsMarketArea.Equities), (c.Scope, c.MarketArea));
    }

    [Fact]
    public void WithoutTagsOrListings_TheCategoryDecidesTheScope()
    {
        var c = Classify("Euro area annual inflation up to 4.0%", [], official: true, sourceCategory: NewsCategory.Macroeconomic);
        Assert.Equal((NewsCategory.Macroeconomic, "category of the official source feed"), (c.Category, c.CategoryBasis));
        Assert.Equal((NewsScope.Macro, NewsEventType.InflationRelease), (c.Scope, c.EventType));
    }

    [Fact]
    public void TheSourcesOwnEventType_Wins()
    {
        var c = Classify("Apple Inc.: 8-K - Current report (items 2.02), filed 2026-10-29", ["AAPL"], equities: 1, official: true,
            sourceCategory: NewsCategory.Earnings, sourceType: NewsEventType.Results, sourceBasis: "8-K item 2.02");
        Assert.Equal((NewsEventType.Results, "8-K item 2.02"), (c.EventType, c.EventTypeBasis));
    }

    [Fact]
    public void AnEtfListingTag_IsNotACompany()
    {
        var c = Classify("Investors pour money into S&P 500 funds", ["SPY"], other: 1, funds: new HashSet<string>(["SPY"], StringComparer.OrdinalIgnoreCase));
        Assert.Equal(NewsScope.Market, c.Scope);
    }

    [Theory]
    [InlineData("Apple earnings beat estimates", new[] { "AAPL" }, 1, 0)]
    [InlineData("Bank stocks lead European markets higher", new[] { "^STOXX", "EURUSD=X" }, 0, 0)]
    [InlineData("ECB decides on key interest rates", new string[0], 0, 0)]
    [InlineData("Siemens to acquire software firm", new[] { "SIE.DE", "^GDAXI" }, 1, 1)]
    public void AgreesWithTheSeparateRules(string headline, string[] tags, int equities, int other)
    {
        var c = Classify(headline, tags, equities, other);
        var category = NewsClassifier.Classify(headline, null);
        var scope = NewsTaxonomy.Scope(tags, equities, other, category.Category, NoFunds);
        var type = NewsTaxonomy.Classify(headline, null, scope, false);
        Assert.Equal((category.Category, category.Basis, scope, type.EventType, type.Basis), (c.Category, c.CategoryBasis, c.Scope, c.EventType, c.EventTypeBasis));
        Assert.Equal(scope == NewsScope.Market ? NewsTaxonomy.MarketArea(tags) ?? NewsMarketArea.Equities : null, c.MarketArea);
    }

    [Fact]
    public void SplitTags_ReadsTheStoredList()
    {
        Assert.Empty(NewsTaxonomy.SplitTags(null));
        Assert.Equal(["^STOXX", "EURUSD=X"], NewsTaxonomy.SplitTags("^STOXX, EURUSD=X,"));
    }
}
