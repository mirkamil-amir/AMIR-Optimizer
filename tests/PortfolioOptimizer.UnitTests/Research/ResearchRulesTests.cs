using System.Text.Json;
using PortfolioOptimizer.Application.Research;

namespace PortfolioOptimizer.UnitTests.Research;

public sealed class BriefParserTests
{
    private static ResearchBrief Parse(string text) => BriefInterpreter.Parse(text, new StartResearchRequest());

    [Theory]
    [InlineData("A low risk portfolio of German stocks", "DE", "low", "MinimumVariance")]
    [InlineData("aggressive growth with S&P 500 companies", "US", "high", "MaximumSharpe")]
    [InlineData("FTSE 100 shares, I am cautious", "GB", "low", "MinimumVariance")]
    [InlineData("Nikkei stocks", "JP", "medium", "MinimumVariance")]
    public void MarketRiskAndObjective_AreRecognised(string text, string market, string risk, string objective)
    {
        var b = Parse(text);
        Assert.Equal(market, b.Market);
        Assert.Equal(risk, b.RiskPreference);
        Assert.Equal(objective, b.Objective);
        Assert.Equal(FieldOrigin.User, b.Origins["market"].Source);
        Assert.Equal(FieldOrigin.Assumption, b.Origins["objective"].Source);   // not named by the user → shown as assumption
    }

    [Fact]
    public void StatedValues_CarryTheQuote_AndAreSeparatedFromAssumptions()
    {
        var b = Parse("US stocks, no tech or oil stocks, at most 15% per position, 8 stocks, 10 years, I can't afford to lose this money, minimum variance");
        Assert.Equal(["Information Technology", "Energy"], b.ExcludeSectors.Order().Reverse().ToArray());
        Assert.Equal(0.15, b.MaxWeight, 6);
        Assert.Equal(8, b.CandidateCount);
        Assert.Equal(10, b.HorizonYears);
        Assert.Equal("low", b.LossCapacity);
        Assert.Equal("MinimumVariance", b.Objective);
        Assert.Equal(FieldOrigin.User, b.Origins["objective"].Source);
        Assert.Contains("15%", b.Origins["maxWeight"].Reason);
        Assert.Equal(FieldOrigin.Assumption, b.Origins["lookbackDays"].Source);
    }

    [Fact]
    public void SeveralMarkets_LeaveTheMarketOpen_ForTheUserToChoose()
    {
        var b = BriefRules.Complete(Parse("German and Japanese stocks"));
        Assert.Null(b.Market);
        Assert.Contains("several markets", b.Origins["market"].Reason);
        Assert.StartsWith("Which market", b.MissingInformation[0]);
    }

    [Fact]
    public void UnstatedRisk_AndAbilityToBearLosses_AreNotInvented()
    {
        var b = BriefRules.Complete(Parse("French stocks"));
        Assert.Equal("medium", b.RiskPreference);
        Assert.Equal(FieldOrigin.Assumption, b.Origins["riskPreference"].Source);
        Assert.Null(b.LossCapacity);
        Assert.Contains(b.MissingInformation, m => m.Contains("financially absorb"));
    }

    [Fact]
    public void ModelProposals_NeedAQuoteFromTheUsersText_AndAllowedValues()
    {
        var text = "I like Japanese companies and I can't afford to lose much";
        var brief = Parse(text);
        var output = JsonSerializer.SerializeToElement(new
        {
            market = "US", market_quote = "American",                   // quote not in text
            risk_preference = "extreme", risk_preference_quote = "lose",  // value not allowed
            loss_capacity = "low", loss_capacity_quote = "can't afford to lose",
            horizon_years = 30, horizon_years_quote = (string?)null,       // no quote
            excluded_sectors = new[] { "Crypto" }, excluded_sectors_quote = "Japanese",
        });
        var rejected = new List<string>();
        var merged = BriefInterpreter.Merge(brief, output, text, rejected);
        Assert.Equal("JP", merged.Market);
        Assert.Equal("medium", merged.RiskPreference);
        Assert.Equal("low", merged.LossCapacity);
        Assert.Null(merged.HorizonYears);
        Assert.Empty(merged.ExcludeSectors);
        Assert.True(rejected.Count >= 4);
    }

    [Fact]
    public void Redaction_RemovesContactAndAccountData()
    {
        var r = BriefRules.Redact("mail me at jane.doe@example.com, IBAN DE89 3704 0044 0532 0130 00, phone +49 170 1234567");
        Assert.DoesNotContain("example.com", r);
        Assert.DoesNotContain("3704", r);
        Assert.DoesNotContain("1234567", r);
        Assert.Contains("[redacted e-mail]", r);
    }
}

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
    public void NumbersAndTickersFromTheEvidence_Pass(string text) => Assert.True(V(text).Item1);

    [Theory]
    [InlineData("Estimated volatility is 9.8%.", "9.8")]
    [InlineData("MSFT would be a better choice.", "MSFT")]
    [InlineData("This is a guaranteed winner.", "guarantee")]
    [InlineData("You should buy AAPL.", "you should buy")]
    public void FabricatedNumbers_ForeignTickers_AndPromises_AreRejected(string text, string reasonPart)
    {
        var (ok, reason) = V(text);
        Assert.False(ok);
        Assert.Contains(reasonPart, reason);
    }
}

public sealed class RefinementRulesTests
{
    private static readonly HypothesisConfig Config = new("MinimumVariance", 0.25, ["AAPL", "MSFT", "JPM", "KO", "XOM", "JNJ"], [],
        new Dictionary<string, double>(), "LedoitWolf", 504, 10);

    private static readonly CandidateDto[] Candidates =
    [
        new("AAPL", "Apple Inc.", "Information Technology", "USD", true, "", 800, null, false, 0.2, 0.1, -0.2, true),
        new("PG", "Procter & Gamble", "Consumer Staples", "USD", false, "Meets the data requirements but ranked below", 800, null, false, 0.15, 0.05, -0.1, true),
    ];

    private static HypothesisDto H() => new(Guid.NewGuid(), "x", "initial", DateTime.UtcNow, Config, "h",
        new PortfolioOptimizer.Application.Dtos.OptimizationRunDto(Guid.NewGuid(), Guid.Empty, DateTime.UtcNow, PortfolioOptimizer.Domain.ObjectiveKind.MinimumVariance,
            PortfolioOptimizer.Domain.OptimizationRunStatus.Succeeded, "", ["AAPL"], null, null, 500, "Mock", true, "", new PortfolioOptimizer.Application.Dtos.PortfolioSettingsDto(),
            0.08, 0.14, 0.3, 0, 0, 0, [], [new PortfolioOptimizer.Application.Dtos.AllocationDto("AAPL", "Apple Inc.", "Information Technology", 0.25, 0.1, 0.2, 0.3)],
            new PortfolioOptimizer.Application.Dtos.RunAnalyticsDto([], [], [], [], [], null, 0, []), ""),
        null, [], [], new InterpretationDto("deterministic", true, null, null, []), null);

    [Theory]
    [InlineData("cap each stock at 20%", "setMaxWeight")]
    [InlineData("show me a simpler alternative", "setObjective")]
    [InlineData("compare this against equal weighting", "setObjective")]
    [InlineData("exclude Apple", "excludeSymbol")]
    [InlineData("reduce the concentration in technology", "setSectorLimit")]
    [InlineData("use 3 years of history", "setLookback")]
    [InlineData("add Procter", "includeSymbol")]
    public void Requests_BecomeValidatedChanges(string text, string operation)
    {
        var r = RefinementInterpreter.Parse(text, H(), Candidates);
        Assert.Equal("change", r.Kind);
        Assert.Equal(operation, Assert.Single(r.Changes).Operation);
        Assert.False(string.IsNullOrWhiteSpace(r.Changes[0].Description));
    }

    [Theory]
    [InlineData("explain this to me as a beginner")]
    [InlineData("why is AAPL so large?")]
    [InlineData("can you guarantee returns")]
    [InlineData("please buy these for me")]
    [InlineData("use a different benchmark")]
    public void QuestionsAndOutOfScopeRequests_NeverChangeTheConfiguration(string text)
    {
        var r = RefinementInterpreter.Parse(text, H(), Candidates);
        Assert.Empty(r.Changes);
        Assert.False(string.IsNullOrWhiteSpace(r.Answer));
    }

    [Fact]
    public void InfeasibleCaps_UnknownSymbols_AndBadValues_AreRejected()
    {
        var symbols = new[] { "AAPL", "PG" };
        Assert.Null(RefinementRules.Check(new("setMaxWeight", null, null, 0.10, null, ""), Config, symbols).Change); // 6 × 10 % < 100 %
        Assert.NotNull(RefinementRules.Check(new("setMaxWeight", null, null, 0.20, null, ""), Config, symbols).Change);
        Assert.Null(RefinementRules.Check(new("excludeSymbol", "TSLA", null, null, null, ""), Config, symbols).Change);
        Assert.Null(RefinementRules.Check(new("setObjective", null, null, null, "AIPicksWinners", ""), Config, symbols).Change);
        Assert.Null(RefinementRules.Check(new("setSectorLimit", null, "Crypto", 0.3, null, ""), Config, symbols).Change);
        Assert.Null(RefinementRules.Check(new("dropTable", null, null, null, null, ""), Config, symbols).Change);
    }

    [Fact]
    public void ApplyingChanges_IsExplicit_AndKeepsTheRest()
    {
        var c = RefinementRules.Apply(Config, [new("excludeSymbol", "XOM", null, null, null, ""), new("setSectorLimit", null, "Financials", 0.3, null, "")]);
        Assert.Equal(["XOM"], c.ExcludedSymbols);
        Assert.Equal(0.3, c.SectorLimits["Financials"]);
        Assert.Equal(Config.MaxWeight, c.MaxWeight);
        Assert.Equal(Config.Universe, c.Universe);
    }

    [Fact]
    public void ConfigurationHash_IgnoresOrder_AndDetectsChanges()
    {
        var reordered = Config with { Universe = Config.Universe.Reverse().ToList() };
        Assert.Equal(ResearchAgent.Hash(Config), ResearchAgent.Hash(reordered));
        Assert.NotEqual(ResearchAgent.Hash(Config), ResearchAgent.Hash(Config with { MaxWeight = 0.2 }));
    }

    [Fact]
    public void CandidateMetrics_ByHand()
    {
        // 61 prices rising 1 % a day: zero volatility of returns, return 1.01^60 − 1, no drawdown.
        var prices = Enumerable.Range(0, 61).Select(i => 100 * Math.Pow(1.01, i)).ToList();
        var m = CandidateMetrics.FromPrices(prices)!;
        Assert.Equal(Math.Pow(1.01, 60) - 1, m.Return, 9);
        Assert.Equal(0, m.Volatility, 6);
        Assert.Equal(0, m.MaxDrawdown, 9);
        Assert.Null(CandidateMetrics.FromPrices(prices.Take(30).ToList()));
        Assert.Null(CandidateMetrics.FromPrices([.. prices.Take(60), 0]));
    }
}
