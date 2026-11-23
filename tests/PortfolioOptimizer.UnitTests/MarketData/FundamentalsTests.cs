using System.Text.Json;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Infrastructure.MarketData;

namespace PortfolioOptimizer.UnitTests.MarketData;

/// <summary>
/// Stock-list metrics from reported figures (F-FUN-001..004) with hand-computed expectations, and parsing of the Yahoo
/// fundamentals time series on synthetic payloads in the real response shape.
/// </summary>
public sealed class FundamentalsTests
{
    private static readonly DateOnly Y24 = new(2024, 12, 31);
    private static readonly DateOnly Y25 = new(2025, 12, 31);

    private static FactPoint Fy(string concept, DateOnly end, double value, string? ccy = "EUR") => new(concept, "FY", end, value, ccy);

    private static List<FactPoint> Company() =>
    [
        new("MarketCap", "TTM", new DateOnly(2026, 6, 1), 9e9, "EUR"),
        new("MarketCap", "TTM", new DateOnly(2026, 11, 17), 10e9, "EUR"),
        Fy("TotalRevenue", Y24, 800), Fy("TotalRevenue", Y25, 1000),
        Fy("FreeCashFlow", Y24, 90), Fy("FreeCashFlow", Y25, 120),
        Fy("EBIT", Y25, 200), Fy("PretaxIncome", Y25, 180), Fy("TaxProvision", Y25, 45),
        Fy("InvestedCapital", Y24, 900), Fy("InvestedCapital", Y25, 1100),
    ];

    [Fact]
    public void MarketCap_IsTheLatestTrailingValue_WithItsDateAndCurrency()
    {
        var m = FundamentalMetrics.MarketCap(Company());
        Assert.Equal(10e9, m.Value);
        Assert.Equal("EUR", m.Currency);
        Assert.Equal(new DateOnly(2026, 11, 17), m.AsOf);
    }

    [Fact]
    public void FreeCashFlow_IsTheLatestFiscalYear()
    {
        var m = FundamentalMetrics.FreeCashFlow(Company());
        Assert.Equal(120, m.Value);
        Assert.Equal(Y25, m.AsOf);
    }

    [Fact]
    public void RevenueGrowth_IsLatestOverPreviousYearMinusOne()
    {
        var m = FundamentalMetrics.RevenueGrowth(Company());
        Assert.Equal(0.25, m.Value!.Value, 12); // 1000 / 800 − 1
        Assert.Null(m.Currency);
    }

    [Fact]
    public void RevenueGrowth_SkippedYear_FallsBackToTwoYearAnnualisedGrowth()
    {
        // Logista as reported by the source: FY2024 €12,986M, FY2025 missing, FY2026 €13,837M.
        var facts = new List<FactPoint>
        {
            Fy("TotalRevenue", new DateOnly(2023, 9, 30), 12428), Fy("TotalRevenue", new DateOnly(2024, 9, 30), 12986),
            Fy("TotalRevenue", new DateOnly(2026, 9, 30), 13837),
        };
        var m = FundamentalMetrics.RevenueGrowth(facts);
        Assert.Equal(Math.Sqrt(13837.0 / 12986) - 1, m.Value!.Value, 12); // ≈ 3.2 % per year
        Assert.Equal(2, m.SpanYears);
        Assert.Contains("annualised over 2 years", m.Basis);
        Assert.Contains("2024-09-30", m.Basis);
        // The one-year rate is used whenever the year before exists.
        Assert.Null(FundamentalMetrics.RevenueGrowth(Company()).SpanYears);
        // Fallback base zero or in another currency: unavailable with the reason.
        Assert.Null(FundamentalMetrics.RevenueGrowth([Fy("TotalRevenue", new DateOnly(2024, 9, 30), 0), Fy("TotalRevenue", new DateOnly(2026, 9, 30), 5)]).Value);
        Assert.Null(FundamentalMetrics.RevenueGrowth([Fy("TotalRevenue", new DateOnly(2024, 9, 30), 5, "TRY"), Fy("TotalRevenue", new DateOnly(2026, 9, 30), 5)]).Value);
    }

    [Fact]
    public void Roic_UsesEffectiveTaxRate_AndAverageInvestedCapital()
    {
        // t = 45 / 180 = 0.25; NOPAT = 200 × 0.75 = 150; average IC = (900 + 1100) / 2 = 1000 → 15 %.
        var m = FundamentalMetrics.Roic(Company());
        Assert.Equal(0.15, m.Value!.Value, 12);
        Assert.Equal(Y25, m.AsOf);
    }

    [Fact]
    public void Roic_WithoutPreviousYear_UsesYearEndCapital()
    {
        var facts = Company().Where(f => !(f.Concept == "InvestedCapital" && f.PeriodEnd == Y24)).ToList();
        Assert.Equal(150.0 / 1100, FundamentalMetrics.Roic(facts).Value!.Value, 12);
    }

    [Fact]
    public void Roic_PreTaxLoss_AssumesNoTax_AndTaxRateIsClamped()
    {
        var loss = Company().Where(f => f.Concept != "PretaxIncome").Append(Fy("PretaxIncome", Y25, -10)).ToList();
        Assert.Equal(200.0 / 1000, FundamentalMetrics.Roic(loss).Value!.Value, 12);
        var overTaxed = Company().Where(f => f.Concept != "TaxProvision").Append(Fy("TaxProvision", Y25, 500)).ToList();
        Assert.Equal(0.0, FundamentalMetrics.Roic(overTaxed).Value!.Value, 12); // t clamped to 1
    }

    [Fact]
    public void Roic_Bank_WithoutEbit_IsUnavailableWithReason()
    {
        var bank = Company().Where(f => f.Concept != "EBIT").ToList();
        var m = FundamentalMetrics.Roic(bank);
        Assert.Null(m.Value);
        Assert.Contains("banks", m.Limitation);
    }

    [Fact]
    public void Roic_NonPositiveCapital_OrMissingTax_IsUnavailable()
    {
        var negative = Company().Where(f => f.Concept != "InvestedCapital").Append(Fy("InvestedCapital", Y25, -5)).ToList();
        Assert.Null(FundamentalMetrics.Roic(negative).Value);
        var noTax = Company().Where(f => f.Concept != "TaxProvision").ToList();
        Assert.Contains("tax", FundamentalMetrics.Roic(noTax).Limitation);
    }

    [Fact]
    public void RevenueGrowth_NeedsConsecutiveYears_InOneCurrency_WithPositiveBase()
    {
        Assert.Null(FundamentalMetrics.RevenueGrowth([Fy("TotalRevenue", Y25, 1000)]).Value);
        // Three years apart: neither the year before nor the two-year fallback.
        Assert.Null(FundamentalMetrics.RevenueGrowth([Fy("TotalRevenue", new DateOnly(2022, 12, 31), 800), Fy("TotalRevenue", Y25, 1000)]).Value);
        Assert.Null(FundamentalMetrics.RevenueGrowth([Fy("TotalRevenue", Y24, 800, "USD"), Fy("TotalRevenue", Y25, 1000)]).Value);
        Assert.Null(FundamentalMetrics.RevenueGrowth([Fy("TotalRevenue", Y24, 0), Fy("TotalRevenue", Y25, 1000)]).Value);
        // A shifted year end (e.g. 52/53-week years) still counts.
        Assert.Equal(0.1, FundamentalMetrics.RevenueGrowth([Fy("TotalRevenue", new DateOnly(2024, 9, 28), 100), Fy("TotalRevenue", new DateOnly(2025, 9, 27), 110)]).Value!.Value, 12);
    }

    [Fact]
    public void ReportedFigures_AreTheLatestFiscalYear_AsPublished()
    {
        var facts = Company().Concat([Fy("EBITDA", Y24, 230), Fy("EBITDA", Y25, 260)]).ToList();
        var m = FundamentalMetrics.Compute(facts);
        Assert.Equal(1000, m.Revenue.Value);
        Assert.Equal(200, m.Ebit.Value);
        Assert.Equal(260, m.Ebitda.Value);
        Assert.Equal("EUR", m.Ebitda.Currency);
        Assert.Equal(Y25, m.Ebitda.AsOf);
        Assert.Contains("banks", m.Ebitda.Limitation ?? FundamentalMetrics.Compute(Company()).Ebitda.Limitation);
    }

    [Fact]
    public void PeRatio_IsTheLatestTrailingValue_AndNotShownForLosses()
    {
        List<FactPoint> pe(double v) => [new("PeRatio", "TTM", new DateOnly(2026, 5, 1), 30, null), new("PeRatio", "TTM", new DateOnly(2026, 11, 17), v, null)];
        var m = FundamentalMetrics.PeRatio(pe(39.2));
        Assert.Equal(39.2, m.Value);
        Assert.Null(m.Currency);
        Assert.Equal(new DateOnly(2026, 11, 17), m.AsOf);
        Assert.Null(FundamentalMetrics.PeRatio(pe(-4)).Value);
        Assert.Contains("negative", FundamentalMetrics.PeRatio([]).Limitation);
    }

    private static List<FactPoint> Bank() =>
    [
        Fy("PretaxIncome", Y25, 9.7), Fy("NetIncome", Y25, 6.9),
        Fy("StockholdersEquity", Y24, 74.0), Fy("StockholdersEquity", Y25, 78.0),
        Fy("OperatingRevenue", Y25, 32.1), Fy("NonInterestExpense", Y25, 20.3),
    ];

    [Fact]
    public void Bank_Ebt_IsPreTaxIncome_AndPpop_IsOperatingRevenueMinusNonInterestExpense()
    {
        var m = FundamentalMetrics.Compute(Bank());
        Assert.Equal(9.7, m.Ebt.Value);
        Assert.Equal(32.1 - 20.3, m.Ppop.Value!.Value, 12);
        Assert.Equal("EUR", m.Ppop.Currency);
        Assert.Equal(Y25, m.Ppop.AsOf);
        var insurer = Bank().Where(f => f.Concept != "NonInterestExpense").ToList();
        Assert.Contains("insurers", FundamentalMetrics.Ppop(insurer).Limitation);
    }

    [Fact]
    public void Roe_IsNetIncomeOverAverageEquity()
    {
        Assert.Equal(6.9 / 76.0, FundamentalMetrics.Roe(Bank()).Value!.Value, 12); // (74 + 78) / 2 = 76
        var oneYear = Bank().Where(f => !(f.Concept == "StockholdersEquity" && f.PeriodEnd == Y24)).ToList();
        Assert.Equal(6.9 / 78.0, FundamentalMetrics.Roe(oneYear).Value!.Value, 12);
        var negative = Bank().Where(f => f.Concept != "StockholdersEquity").Append(Fy("StockholdersEquity", Y25, -3)).ToList();
        Assert.Null(FundamentalMetrics.Roe(negative).Value);
        Assert.Null(FundamentalMetrics.Roe(Bank().Where(f => f.Concept != "NetIncome").ToList()).Value);
    }

    [Fact]
    public void NoFigures_EveryMetricIsUnavailable_NeverZero()
    {
        var m = FundamentalMetrics.Compute([]);
        Assert.All(new[] { m.MarketCap, m.FreeCashFlow, m.Roic, m.RevenueGrowth, m.Ebit, m.Ebitda, m.PeRatio, m.Revenue, m.Ebt, m.Ppop, m.Roe }, x =>
        {
            Assert.Null(x.Value);
            Assert.False(string.IsNullOrEmpty(x.Limitation));
        });
    }

    [Fact]
    public void Yahoo_Parse_MapsAnnualAndTrailing_SkipsNullPadding_ConvertsPence()
    {
        var root = JsonDocument.Parse("""
            {"timeseries":{"result":[
              {"meta":{"symbol":["ABC.L"],"type":["annualTotalRevenue"]},"timestamp":[1,2],
               "annualTotalRevenue":[null,{"dataId":20100,"asOfDate":"2025-12-31","periodType":"12M","currencyCode":"GEL","reportedValue":{"raw":4344000000.0,"fmt":"4.34B"}}]},
              {"meta":{"symbol":["ABC.L"],"type":["trailingMarketCap"]},"timestamp":[3],
               "trailingMarketCap":[{"asOfDate":"2026-11-17","periodType":"TTM","currencyCode":"GBp","reportedValue":{"raw":250000.0}}]},
              {"meta":{"symbol":["ABC.L"],"type":["annualEBIT"]}}
            ],"error":null}}
            """).RootElement;
        var p = YahooFundamentalsProvider.Parse(root);
        Assert.True(p.Found);
        Assert.Equal(2, p.Facts.Count);
        Assert.Contains(new ProviderFundamentalFact("TotalRevenue", "FY", Y25, 4344000000m, "GEL"), p.Facts);
        Assert.Contains(new ProviderFundamentalFact("MarketCap", "TTM", new DateOnly(2026, 11, 17), 2500m, "GBP"), p.Facts);
    }

    [Fact]
    public void Yahoo_Parse_EmptyResult_IsNotFound_AndErrorThrows()
    {
        Assert.False(YahooFundamentalsProvider.Parse(JsonDocument.Parse("""{"timeseries":{"result":[],"error":null}}""").RootElement).Found);
        Assert.Throws<FundamentalsProviderException>(() => YahooFundamentalsProvider.Parse(
            JsonDocument.Parse("""{"timeseries":{"result":null,"error":{"code":"Bad Request","description":"Invalid type"}}}""").RootElement));
        Assert.Throws<FundamentalsProviderException>(() => YahooFundamentalsProvider.Parse(JsonDocument.Parse("""{"chart":{}}""").RootElement));
    }
}

/// <summary>Queue order of the fundamentals worker: page requests before the scheduled full refresh, each listing once.</summary>
public sealed class FundamentalsQueueTests
{
    private static FundamentalsRefreshWorker Worker() =>
        new(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<FundamentalsRefreshWorker>.Instance);

    private static List<int> Drain(FundamentalsRefreshWorker w)
    {
        var order = new List<int>();
        while (w.TryTakeNext(out var id)) order.Add(id);
        return order;
    }

    [Fact]
    public void PageRequests_GoBeforeTheFullRefresh_AndArePromotedOutOfIt()
    {
        var w = Worker();
        w.Schedule([1, 2, 3, 4], background: true);
        w.Schedule([9, 3]); // 3 already waits in the background queue: moved ahead, not fetched twice
        w.Schedule([2], background: true); // already queued: ignored
        Assert.True(w.IsQueued(3));
        Assert.Equal([9, 3, 1, 2, 4], Drain(w));
    }

    [Fact]
    public void AListingBeingFetched_IsNotQueuedAgain()
    {
        var w = Worker();
        w.Schedule([5], background: true);
        Assert.True(w.TryTakeNext(out var id));
        Assert.Equal(5, id);
        w.Schedule([5]); // a page view while it is being fetched
        Assert.False(w.TryTakeNext(out _));
    }
}
