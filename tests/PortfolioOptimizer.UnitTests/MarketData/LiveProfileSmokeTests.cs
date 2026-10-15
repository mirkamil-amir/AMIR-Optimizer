using Microsoft.Extensions.Options;
using PortfolioOptimizer.Infrastructure.MarketData;

namespace PortfolioOptimizer.UnitTests.MarketData;

/// <summary>Opt-in (PO_LIVE_SMOKE=1): the real Wikipedia summary API answers for references from the seed catalogue.</summary>
public class LiveProfileSmokeTests
{
    [LiveSmokeFact]
    public async Task Wikipedia_ReturnsDescriptions_ForUsGermanAndJapaneseReferences()
    {
        var provider = new WikipediaProfileProvider(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, Options.Create(new WikipediaProfileOptions()));
        foreach (var (reference, expected) in new[] { ("en:Apple Inc.", "Apple"), ("en:SAP", "SAP"), ("de:Toyota", "Toyota") })
        {
            var p = await provider.GetAsync(reference, CancellationToken.None);
            Assert.True(p.Found, $"{reference}: {p.NotFoundReason}");
            Assert.Contains(expected, p.Description);
            Assert.Equal("en", p.Language); // the German reference resolves to the English article
            Assert.StartsWith("https://en.wikipedia.org/wiki/", p.SourceUrl);
        }
    }
}
