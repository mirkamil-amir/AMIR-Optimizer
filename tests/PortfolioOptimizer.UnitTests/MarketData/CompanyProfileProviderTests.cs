using System.Text.Json;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Infrastructure.MarketData;

namespace PortfolioOptimizer.UnitTests.MarketData;

/// <summary>Wikipedia summary/langlinks parsing on synthetic payloads in the real response shapes, and catalogue references.</summary>
public sealed class CompanyProfileProviderTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Summary_StandardArticle_MapsTextUrlAndLanguage()
    {
        var p = WikipediaProfileProvider.ParseSummary(Json("""
            {"type":"standard","title":"Example Corp","lang":"en","extract":"Example Corp is a company that makes examples.   It was founded in 1900.",
             "content_urls":{"desktop":{"page":"https://en.wikipedia.org/wiki/Example_Corp"}}}
            """), "en", 1800);
        Assert.True(p.Found);
        Assert.Equal("Example Corp is a company that makes examples. It was founded in 1900.", p.Description); // whitespace normalized
        Assert.Equal("https://en.wikipedia.org/wiki/Example_Corp", p.SourceUrl);
        Assert.Equal("en", p.Language);
    }

    [Theory]
    [InlineData("""{"type":"disambiguation","title":"Mercury","extract":"Mercury may refer to many different things in many places, see below."}""", "disambiguation")]
    [InlineData("""{"type":"standard","title":"X","extract":"   "}""", "no usable summary")]
    [InlineData("""{"type":"standard","title":"X","extract":"Too short."}""", "no usable summary")]
    [InlineData("""{"type":"standard","title":"X"}""", "no usable summary")]
    public void Summary_WithoutUsableText_IsNotFound_NeverATickerOrPlaceholder(string json, string reason)
    {
        var p = WikipediaProfileProvider.ParseSummary(Json(json), "en", 1800);
        Assert.False(p.Found);
        Assert.Null(p.Description);
        Assert.Contains(reason, p.NotFoundReason);
    }

    [Fact]
    public void LongText_IsCutAfterTheLastWholeSentence()
    {
        var text = string.Join(' ', Enumerable.Range(1, 60).Select(i => $"Sentence number {i} is here."));
        var cut = WikipediaProfileProvider.Shorten(text, 200);
        Assert.True(cut.Length <= 200);
        Assert.EndsWith(".", cut);
    }

    [Fact]
    public void Langlinks_GiveTheEnglishTitle_OrNull()
    {
        Assert.Equal("Toyota", WikipediaProfileProvider.ParseEnglishTitle(Json("""
            {"query":{"pages":[{"title":"Toyota","langlinks":[{"lang":"en","title":"Toyota"}]}]}}
            """)));
        Assert.Null(WikipediaProfileProvider.ParseEnglishTitle(Json("""{"query":{"pages":[{"title":"Kleinfirma"}]}}""")));
    }

    [Theory]
    [InlineData("en:Apple Inc.", "en", "Apple Inc.")]
    [InlineData("de:Toyota", "de", "Toyota")]
    public void Reference_IsLanguageAndTitle(string reference, string lang, string title) =>
        Assert.Equal((lang, title), WikipediaProfileProvider.ParseReference(reference));

    [Theory]
    [InlineData("Apple Inc.")]
    [InlineData("e:x")]
    [InlineData("en:")]
    public void Reference_Invalid_Throws(string reference) =>
        Assert.Throws<ArgumentException>(() => WikipediaProfileProvider.ParseReference(reference));

    [Fact]
    public void UniverseCsv_WikiColumn_IsValidated()
    {
        var (rows, errors) = UniverseService.ParseMembers(
            "symbol,name,country,currency,wiki\nAAPL,Apple,US,USD,en:Apple Inc.\nMSFT,Microsoft,US,USD,Microsoft\nIBM,IBM,US,USD,");
        Assert.Equal("en:Apple Inc.", rows.Single(r => r.Symbol == "AAPL").ReferenceArticle);
        Assert.Null(rows.Single(r => r.Symbol == "MSFT").ReferenceArticle);
        Assert.Null(rows.Single(r => r.Symbol == "IBM").ReferenceArticle);
        Assert.Contains(errors, e => e.Contains("MSFT") && e.Contains("wiki reference"));
    }

    [Fact]
    public void SeedUniverses_LinkNearlyEveryCompanyToAnArticle()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Seed", "universes");
        if (!Directory.Exists(dir)) dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/PortfolioOptimizer.Infrastructure/Seed/universes"));
        var (rows, errors) = UniverseService.ParseMembers(File.ReadAllText(Path.Combine(dir, "SP500.csv")));
        Assert.Empty(errors);
        Assert.Equal("en:Apple Inc.", rows.Single(r => r.Symbol == "AAPL").ReferenceArticle);
        Assert.True(rows.Count(r => r.ReferenceArticle is not null) >= 500);
    }

    [Fact]
    public void MaxRange_StartsAtTheFirstStoredClose()
    {
        var bars = new[] { new HistoryBar(new(2020, 1, 2), 10m, 10m), new HistoryBar(new(2026, 1, 2), 30m, 30m) };
        var slice = PriceHistory.Slice(bars, PriceRange.Max);
        Assert.Equal(new DateOnly(2020, 1, 2), slice.BaseDate);
        Assert.Equal(2.0, slice.Change!.Value, 12);
    }
}
