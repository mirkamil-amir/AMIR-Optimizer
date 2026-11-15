using PortfolioOptimizer.Application.Pilot;

namespace PortfolioOptimizer.UnitTests.Pilot;

/// <summary>Model-written Matrix Pilot narratives pass only with numbers and tickers from the computed evidence and no promises.</summary>
public sealed class NarrativeValidatorTests
{
    private const string Evidence = """{"weightsPct":[{"Symbol":"AAPL","weight":25.0},{"Symbol":"KO","weight":12.5}],"estimatedVolatilityPct":14.2,"window":{"start":"2024-10-15"},"maxWeightPct":25.0}""";
    private static readonly HashSet<string> Allowed = ["AAPL", "KO"];
    private static readonly HashSet<string> Known = ["AAPL", "KO", "MSFT", "XOM"];

    private static (bool, string?) V(string text) =>
        NarrativeValidator.Validate([new InterpretationSection("found", "x", [text])], Evidence, Allowed, Known);

    [Theory]
    [InlineData("AAPL has 25.0% and KO 12.5%; estimated volatility 14.2% since 2024-10-15.")]
    [InlineData("AAPL is capped at 25% of the portfolio.")]
    [InlineData("Volatility is about 14% per year.")]
    [InlineData("Past returns do not guarantee future results.")]
    [InlineData("No portfolio is risk-free, including this one.")]
    public void NumbersAndTickersFromTheEvidence_Pass(string text) => Assert.True(V(text).Item1);

    [Theory]
    [InlineData("Estimated volatility is 9.8%.", "9.8")]
    [InlineData("MSFT would be a better choice.", "MSFT")]
    [InlineData("This is a guaranteed winner.", "guarantee")]
    [InlineData("You should buy AAPL.", "you should buy")]
    [InlineData("It is not volatile, so returns are guaranteed.", "guarantee")]
    [InlineData("This is a risk-free choice.", "risk-free")]
    [InlineData("The portfolio shows an estimatedVolatilityPct of 14.2.", "estimatedVolatilityPct")]
    public void FabricatedNumbers_ForeignTickers_AndPromises_AreRejected(string text, string reasonPart)
    {
        var (ok, reason) = V(text);
        Assert.False(ok);
        Assert.Contains(reasonPart, reason);
    }
}
