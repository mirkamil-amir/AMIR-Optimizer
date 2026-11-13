using System.Text.Json;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Pilot;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.UnitTests.Pilot;

public sealed class PilotMandateTests
{
    private static PilotMandate Valid => new() { Universe = ["AAPL", "MSFT", "JPM", "PG", "KO"], MaxPositionWeight = 0.25, MinHoldings = 4, MaxHoldings = 5 };

    [Fact]
    public void ValidMandate_Passes() => Valid.Normalized().Validate();

    [Theory]
    [InlineData("noUniverse")]
    [InlineData("maxBelowMin")]
    [InlineData("cashInverted")]
    [InlineData("unreachableInvested")]
    [InlineData("overfullMinimum")]
    [InlineData("allowedAndExcluded")]
    [InlineData("badLimit")]
    public void InconsistentMandate_IsRejected_NotRepaired(string kind)
    {
        var m = kind switch
        {
            "noUniverse" => Valid with { Universe = [] },
            "maxBelowMin" => Valid with { MinPositionWeight = 0.3 },
            "cashInverted" => Valid with { MinCashWeight = 0.5, MaxCashWeight = 0.1 },
            "unreachableInvested" => Valid with { MaxHoldings = 5, MaxPositionWeight = 0.1, MaxCashWeight = 0.3 },
            "overfullMinimum" => Valid with { MinHoldings = 5, MinPositionWeight = 0.25, MaxPositionWeight = 0.25 },
            "allowedAndExcluded" => Valid with { ExcludedSymbols = ["AAPL"] },
            _ => Valid with { SectorLimits = new() { ["Energy"] = 0 } },
        };
        Assert.Throws<ValidationException>(() => m.Normalized().Validate());
    }

    [Fact]
    public void Diff_ListsChangedFieldsWithBothValues()
    {
        var changes = PilotMandate.Diff(Valid, Valid with { MaxPositionWeight = 0.3, ExcludedSymbols = ["XOM"] });
        Assert.Equal(["excludedSymbols", "maxPositionWeight"], changes.Select(c => c.Field).Order());
        Assert.Contains(changes, c => c is { Field: "maxPositionWeight", Before: "0.25", After: "0.3" });
    }

    [Fact]
    public void ShortSellingAndLeverage_AreNeverAllowed() => Assert.False(Valid.ShortSellingAllowed || Valid.LeverageAllowed);
}

public sealed class PilotScheduleTests
{
    [Theory]
    [InlineData(PilotDecisionFrequency.Daily, "2026-11-12T10:00:00Z", "D:2026-11-12")]
    [InlineData(PilotDecisionFrequency.Weekly, "2026-11-12T10:00:00Z", "W:2026-W46")]
    [InlineData(PilotDecisionFrequency.Weekly, "2027-01-01T10:00:00Z", "W:2026-W53")]
    [InlineData(PilotDecisionFrequency.Monthly, "2026-11-30T23:00:00Z", "M:2026-11")]
    public void PeriodKey_IdentifiesThePeriod(PilotDecisionFrequency f, string at, string expected) =>
        Assert.Equal(expected, PilotSchedule.PeriodKey(f, DateTime.Parse(at).ToUniversalTime()));

    [Theory]
    [InlineData(PilotDecisionFrequency.Daily, "2026-11-12T10:00:00Z", "2026-11-12T22:00:00Z")] // Thursday, before the hour
    [InlineData(PilotDecisionFrequency.Daily, "2026-11-13T22:00:00Z", "2026-11-16T22:00:00Z")] // Friday at the hour → Monday
    [InlineData(PilotDecisionFrequency.Weekly, "2026-11-12T10:00:00Z", "2026-11-13T22:00:00Z")] // next Friday
    [InlineData(PilotDecisionFrequency.Monthly, "2026-11-12T10:00:00Z", "2026-11-30T22:00:00Z")] // last weekday of November
    [InlineData(PilotDecisionFrequency.Monthly, "2027-01-30T10:00:00Z", "2027-02-26T22:00:00Z")] // 29–31 Jan is a weekend → February
    public void NextDecision_IsStrictlyLater_OnAWeekday(PilotDecisionFrequency f, string after, string expected)
    {
        var next = PilotSchedule.NextDecision(f, DateTime.Parse(after).ToUniversalTime(), 22);
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), next);
        Assert.Equal(DateTimeKind.Utc, next.Kind);
    }
}

public sealed class PilotDecisionValidatorTests
{
    private static readonly Dictionary<string, PilotAssetInfo> Universe = new[]
    {
        ("AAPL", "Information Technology", "USD"), ("MSFT", "Information Technology", "USD"), ("NVDA", "Information Technology", "USD"),
        ("JPM", "Financials", "USD"), ("PG", "Consumer Staples", "USD"), ("SAP.DE", "Information Technology", "EUR"),
    }.ToDictionary(x => x.Item1, x => new PilotAssetInfo(x.Item1, x.Item1, x.Item2, x.Item3, x.Item3 == "EUR" ? "DE" : "US", true, null));

    private static readonly PilotMandate M = new()
    {
        Universe = [.. Universe.Keys], MaxPositionWeight = 0.30, MinPositionWeight = 0.05, MinHoldings = 3, MaxHoldings = 5, MinCashWeight = 0.01, MaxCashWeight = 0.20,
        MaxTurnover = 0.5,
    };

    private static PilotDecision Rebalance(double cash, params (string S, double W)[] t) => new()
    {
        Action = "REBALANCE", Strategy = new() { Approach = "hybrid" }, Confidence = "medium", Rationale = ["r"], ExpectedHoldingDays = 60, CashWeight = cash,
        Targets = t.Select(x => new PilotTarget { Symbol = x.S, Weight = x.W, Thesis = "t" }).ToList(),
    };

    private static PilotValidationResult Check(PilotDecision d, PilotMandate? m = null, Dictionary<string, double>? current = null, PilotRiskSummary? risk = null,
        PilotTradeEstimate? trades = null) =>
        PilotDecisionValidator.Validate(d, m ?? M, Universe, current ?? [], risk, trades);

    [Fact]
    public void ValidRebalance_Passes() =>
        Assert.True(Check(Rebalance(0.04, ("AAPL", 0.30), ("JPM", 0.30), ("PG", 0.30), ("MSFT", 0.06))).Passed);

    [Fact]
    public void Hold_PassesWithoutMandateChecks() =>
        Assert.True(Check(new PilotDecision { Action = "HOLD", Strategy = new() { Approach = "hold" }, Confidence = "low", Rationale = ["x"], ExpectedHoldingDays = 30 }).Passed);

    [Fact]
    public void PositionAboveTheMaximum_IsBlocked_ProposedAndAllowedReported()
    {
        var r = Check(Rebalance(0.05, ("AAPL", 0.45), ("JPM", 0.25), ("PG", 0.25)));
        Assert.False(r.Passed);
        var v = Assert.Single(r.Violations, x => x.Rule == "max_position");
        Assert.Equal(("AAPL", 0.45, 0.30), (v.Symbol, v.Proposed, v.Allowed));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("sum")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("action")]
    [InlineData("noThesis")]
    [InlineData("nan")]
    public void StructuralProblems_AreRejectedBeforeMandateChecks(string kind)
    {
        var d = kind switch
        {
            "negative" => Rebalance(0.1, ("AAPL", 0.5), ("JPM", 0.5), ("PG", -0.1)),
            "sum" => Rebalance(0.1, ("AAPL", 0.3), ("JPM", 0.3), ("PG", 0.2)),
            "duplicate" => Rebalance(0.1, ("AAPL", 0.3), ("aapl", 0.3), ("PG", 0.3)),
            "unknown" => Rebalance(0.1, ("AAPL", 0.3), ("TSLA", 0.3), ("PG", 0.3)),
            "action" => Rebalance(0.1, ("AAPL", 0.3), ("JPM", 0.3), ("PG", 0.3)) with { Action = "SELL_EVERYTHING" },
            "noThesis" => Rebalance(0.1, ("AAPL", 0.3), ("JPM", 0.3), ("PG", 0.3)) with { Targets = [new PilotTarget { Symbol = "AAPL", Weight = 0.9 }] },
            _ => Rebalance(0.1, ("AAPL", double.NaN), ("JPM", 0.3), ("PG", 0.3)),
        };
        var r = Check(d);
        Assert.False(r.Passed);
        Assert.Single(r.Checks); // only the structure check ran
    }

    [Fact]
    public void SectorCurrencyCountryHoldingsAndCash_AreEnforced()
    {
        var m = M with
        {
            SectorLimits = new() { ["Information Technology"] = 0.40 }, CurrencyLimits = new() { ["EUR"] = 0.10 }, CountryLimits = new() { ["DE"] = 0.10 },
        };
        var r = Check(Rebalance(0.05, ("AAPL", 0.25), ("SAP.DE", 0.25), ("JPM", 0.25), ("PG", 0.20)), m);
        Assert.Contains(r.Violations, v => v.Rule == "allocation" && v.Detail.Contains("Sector Information Technology"));
        Assert.Contains(r.Violations, v => v.Rule == "currency_limit");
        Assert.Contains(r.Violations, v => v.Rule == "country_limit");

        Assert.Contains(Check(Rebalance(0.4, ("AAPL", 0.3), ("JPM", 0.3))).Violations, v => v.Rule == "min_holdings");
        Assert.Contains(Check(Rebalance(0.25, ("AAPL", 0.25), ("JPM", 0.25), ("PG", 0.25))).Violations, v => v.Rule == "cash");
    }

    [Fact]
    public void TurnoverRiskAndMinimumTrade_AreEnforced()
    {
        var current = new Dictionary<string, double> { ["NVDA"] = 0.5, ["MSFT"] = 0.45 };
        var turnover = Check(Rebalance(0.05, ("AAPL", 0.30), ("JPM", 0.30), ("PG", 0.30), ("MSFT", 0.05)), current: current);
        Assert.Contains(turnover.Violations, v => v.Detail.StartsWith("Turnover"));

        var risk = new PilotRiskSummary(0.31, 4, 0.3, "AAPL", new Dictionary<string, double>(), new Dictionary<string, double>(), new Dictionary<string, double>(), null, null, 500, "w");
        var vol = Check(Rebalance(0.04, ("AAPL", 0.30), ("JPM", 0.30), ("PG", 0.30), ("MSFT", 0.06)), M with { MaxVolatility = 0.20 }, risk: risk);
        Assert.Contains(vol.Violations, v => v is { Rule: "risk", Proposed: 0.31, Allowed: 0.20 });
        Assert.Contains(Check(Rebalance(0.04, ("AAPL", 0.30), ("JPM", 0.30), ("PG", 0.30), ("MSFT", 0.06)), M with { MaxVolatility = 0.20 }).Violations,
            v => v.Rule == "risk"); // no risk figure → blocked, not waved through

        var trades = new PilotTradeEstimate(1, 0, 50, 0, 1, 0.01, [new PilotTradeLine("AAPL", "Buy", 1, 50m, 0, 0.3, null)], []);
        Assert.Contains(Check(Rebalance(0.04, ("AAPL", 0.30), ("JPM", 0.30), ("PG", 0.30), ("MSFT", 0.06)), trades: trades).Violations, v => v.Rule == "min_trade");
    }

    [Fact]
    public void UnpricedSymbol_IsBlocked()
    {
        var universe = new Dictionary<string, PilotAssetInfo>(Universe) { ["PG"] = Universe["PG"] with { Priced = false, PriceProblem = "latest price is stale" } };
        var r = PilotDecisionValidator.Validate(Rebalance(0.04, ("AAPL", 0.30), ("JPM", 0.30), ("PG", 0.30), ("MSFT", 0.06)), M, universe, new Dictionary<string, double>(), null, null);
        Assert.Contains(r.Violations, v => v is { Rule: "data", Symbol: "PG" });
    }
}

public sealed class PilotMetricsAndArgsTests
{
    [Fact]
    public void TrailingMetrics_UseTheRightWindows()
    {
        var start = new DateOnly(2024, 1, 1);
        var points = Enumerable.Range(0, 300).Select(i => new PricePoint(start.AddDays(i), 100 * Math.Pow(1.001, i))).ToList();
        var m = PilotAnalytics.Compute("X", null, points);
        Assert.Equal(Math.Pow(1.001, 21) - 1, m.Return1M!.Value, 10);
        Assert.Equal(Math.Pow(1.001, 252) - 1, m.Return12M!.Value, 10);
        Assert.Equal(0, m.MaxDrawdown12M!.Value, 10);
        Assert.Equal(points[^1].Date, m.LastPriceDate);
        Assert.Null(PilotAnalytics.Compute("Y", null, points.Take(100).ToList()).Return12M); // too short → no figure, not a guess
    }

    [Fact]
    public void ForwardMoves_StartAtTheDecisionClose_AndStayBlankUntilThePeriodHasPassed()
    {
        var d0 = new DateOnly(2026, 11, 2); // Monday
        var points = new[] { 100.0, 102.0, 99.0, 110.0 }.Select((p, i) => new PricePoint(d0.AddDays(i), p)).ToList();
        var series = new Dictionary<string, PortfolioOptimizer.Application.MarketData.PriceSeries>
        {
            ["X"] = new("X", 1, points, true, false),
        };
        // Decided on day 1 (close 102): +1 day → 99, +2 days → 110; +3 days has not happened yet.
        Assert.Equal(Math.Round(99.0 / 102 - 1, 4), PilotService.Forward(series, "X", d0.AddDays(1), 1));
        Assert.Equal(Math.Round(110.0 / 102 - 1, 4), PilotService.Forward(series, "X", d0.AddDays(1), 2));
        Assert.Null(PilotService.Forward(series, "X", d0.AddDays(1), 3));
        Assert.Equal(Math.Round(110.0 / 99 - 1, 4), PilotService.Forward(series, "X", d0.AddDays(2), 1));
        // A decision after the last close has no later close yet; one before the first close has no start → blank, not 0.
        Assert.Null(PilotService.Forward(series, "X", d0.AddDays(10), 1));
        Assert.Null(PilotService.Forward(series, "X", d0.AddDays(-1), 1));
        Assert.Null(PilotService.Forward(series, "MISSING", d0, 1));
    }

    [Fact]
    public void Hypothetical_IsTheWeightedMoveOfFixedWeights_CashEarnsNothing_AndBlankUntilEveryHoldingHasData()
    {
        var d0 = new DateOnly(2026, 11, 2);
        PortfolioOptimizer.Application.MarketData.PriceSeries S(string sym, params double[] p) => new(sym, 1, p.Select((x, i) => new PricePoint(d0.AddDays(i), x)).ToList(), true, false);
        var series = new Dictionary<string, PortfolioOptimizer.Application.MarketData.PriceSeries>
        {
            ["A"] = S("A", 100, 110, 121), ["B"] = S("B", 50, 45, 40), ["C"] = S("C", 10),
        };
        // 60 % A (+10 %), 30 % B (−10 %), 10 % cash: 0.6 · 0.10 + 0.3 · (−0.10) = +3 %.
        Assert.Equal(0.03, PilotService.Hypothetical(series, [("A", 0.6), ("B", 0.3)], d0, 1));
        Assert.Null(PilotService.Hypothetical(series, [("A", 0.5), ("C", 0.5)], d0, 1)); // C has no later price yet
        Assert.Null(PilotService.Hypothetical(series, [], d0, 1));
    }

    [Fact]
    public void LiveForward_IsTheChangeOfTheWealthIndex_NotADifferenceOfReturns()
    {
        var d0 = new DateOnly(2026, 11, 2);
        var na = new PortfolioOptimizer.Application.Dtos.MetricDto(null, null);
        var series = new[] { 0.0, 0.10, -0.01 }.Select((r, i) => new PortfolioOptimizer.Application.Dtos.PerformancePointDto(d0.AddDays(i), 100 * (1 + r), r, null, 0)).ToList();
        var perf = new PortfolioOptimizer.Application.Dtos.PerformanceDto(Guid.Empty, "SPY", d0, d0.AddDays(2), series, na, na, na, na, na, na, na, [], "", "Mock", true, []);
        // From +10 % to −1 %: (0.99 / 1.10) − 1 = −10 %, not −11 percentage points.
        Assert.Equal(Math.Round(0.99 / 1.10 - 1, 4), PilotService.LiveForward(perf, d0.AddDays(1), 1));
        Assert.Null(PilotService.LiveForward(perf, d0.AddDays(1), 2));
        Assert.Null(PilotService.LiveForward(null, d0, 1));
    }

    [Fact]
    public void Arguments_AreReadStrictly()
    {
        var args = JsonDocument.Parse("""{"symbols":["aapl","MSFT","aapl"],"limit":7,"weights":{"AAPL":0.6,"MSFT":0.5}}""").RootElement;
        Assert.Equal(["AAPL", "MSFT"], PilotArgs.Symbols(args, "symbols", 1, 5));
        Assert.Equal(7, PilotArgs.Int(args, "limit", 1, 1, 10));
        Assert.Throws<PilotToolException>(() => PilotArgs.Int(args, "limit", 1, 1, 5));
        Assert.Throws<PilotToolException>(() => PilotArgs.Weights(args, "weights", 10)); // sums to more than 100 %
        Assert.Throws<PilotToolException>(() => PilotArgs.Choice(args, "sortBy", null, "a", "b"));
        Assert.Throws<PilotToolException>(() => PilotArgs.Symbols(args, "symbols", 3, 5));
    }

    [Fact]
    public void BackgroundActor_CannotBeSetInsideARequest_OrSwitchedToAnotherUser()
    {
        var actor = new BackgroundActor();
        Assert.Throws<InvalidOperationException>(() => actor.ActAs(Guid.NewGuid(), "x", Guid.NewGuid()));
        var owner = Guid.NewGuid();
        actor.ActAs(owner, "Matrix Pilot", null);
        Assert.Throws<InvalidOperationException>(() => actor.ActAs(Guid.NewGuid(), "x", null));
        Assert.Equal(owner, actor.UserId);
    }
}
