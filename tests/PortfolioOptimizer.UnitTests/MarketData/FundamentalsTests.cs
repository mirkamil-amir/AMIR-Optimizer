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

/// <summary>History of reported figures (AMIR-41): a refresh confirms, supersedes or adds rows and never removes one.</summary>
public sealed class FundamentalsHistoryTests
{
    private static readonly DateTime Before = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 25, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Fy21 = new(2021, 12, 31);
    private static readonly DateOnly Fy25 = new(2025, 12, 31);

    private static Domain.FundamentalFact Stored(string concept, DateOnly end, decimal value, string? ccy = "USD", string period = "FY") => new()
    {
        AssetId = 7, Source = "Yahoo", Concept = concept, PeriodType = period, PeriodEnd = end, Value = value, Currency = ccy,
        RetrievedAtUtc = Before, FirstSeenAtUtc = Before,
    };

    [Fact]
    public void UnchangedValue_IsConfirmed_NotDuplicated()
    {
        var row = Stored("TotalRevenue", Fy25, 1000m);
        var r = FundamentalsHistory.Merge(7, "Yahoo", [row], [new("TotalRevenue", "FY", Fy25, 1000m, "USD")], Now);
        Assert.Empty(r.Added);
        Assert.Equal(1, r.Confirmed);
        Assert.Equal(Now, row.RetrievedAtUtc);
        Assert.Equal(Before, row.FirstSeenAtUtc);
        Assert.Null(row.SupersededAtUtc);
    }

    [Fact]
    public void ChangedValueOrCurrency_SupersedesTheOldRow_AndAddsTheNewOne()
    {
        var revenue = Stored("TotalRevenue", Fy25, 1000m);
        var eps = Stored("DilutedEPS", Fy25, 2.5m);
        var r = FundamentalsHistory.Merge(7, "Yahoo", [revenue, eps],
            [new("TotalRevenue", "FY", Fy25, 1010m, "USD"), new("DilutedEPS", "FY", Fy25, 2.5m, "EUR")], Now);
        Assert.Equal(2, r.Superseded);
        Assert.Equal(Now, revenue.SupersededAtUtc);
        Assert.Equal(Before, revenue.RetrievedAtUtc); // the old value keeps its own dates
        var added = r.Added.Single(f => f.Concept == "TotalRevenue");
        Assert.Equal(1010m, added.Value);
        Assert.Equal(Now, added.FirstSeenAtUtc);
        Assert.Null(added.SupersededAtUtc);
        Assert.Equal("EUR", r.Added.Single(f => f.Concept == "DilutedEPS").Currency);
    }

    [Fact]
    public void PeriodsTheSourceNoLongerReturns_AreLeftAsTheyAre()
    {
        var old = Stored("TotalRevenue", Fy21, 700m);
        var r = FundamentalsHistory.Merge(7, "Yahoo", [old], [new("TotalRevenue", "FY", Fy25, 1000m, "USD")], Now);
        Assert.Single(r.Added);
        Assert.Equal(0, r.Confirmed + r.Superseded);
        Assert.Null(old.SupersededAtUtc);
        Assert.Equal(Before, old.RetrievedAtUtc);
    }

    [Fact]
    public void ValuesAreComparedAtStoredPrecision_SoRoundingIsNoRestatement()
    {
        var eps = Stored("DilutedEPS", Fy25, 6.08123457m); // stored with 8 decimals
        var r = FundamentalsHistory.Merge(7, "Yahoo", [eps], [new("DilutedEPS", "FY", Fy25, 6.081234567891m, "USD")], Now);
        Assert.Equal(1, r.Confirmed);
        Assert.Empty(r.Added);
    }

    [Fact]
    public void PeriodTypesAreSeparate_AndARepeatedPeriodCountsOnce()
    {
        var fy = Stored("TotalRevenue", Fy25, 1000m);
        var r = FundamentalsHistory.Merge(7, "Yahoo", [fy],
            [new("TotalRevenue", "Q", Fy25, 290m, "USD"), new("TotalRevenue", "Q", Fy25, 291m, "USD"), new("TotalRevenue", "TTM", Fy25, 1000m, "USD")], Now);
        Assert.Equal(2, r.Added.Count);
        Assert.Equal(291m, r.Added.Single(f => f.PeriodType == "Q").Value); // the source's last value of the period
        Assert.Null(fy.SupersededAtUtc);
    }

    [Fact]
    public void SupersededRows_AreNotMatchedAgain()
    {
        var closed = Stored("TotalRevenue", Fy25, 900m);
        closed.SupersededAtUtc = Before;
        var current = Stored("TotalRevenue", Fy25, 1000m);
        var r = FundamentalsHistory.Merge(7, "Yahoo", [closed, current], [new("TotalRevenue", "FY", Fy25, 1000m, "USD")], Now);
        Assert.Equal(1, r.Confirmed);
        Assert.Equal(Before, closed.RetrievedAtUtc);
    }
}

/// <summary>Request layout and parsing of the Yahoo fundamentals time series for every concept (AMIR-41).</summary>
public sealed class YahooFundamentalsRequestTests
{
    [Fact]
    public void FourRequestsPerListing_EachStatementWithYearsQuartersAndTrailing()
    {
        var requests = YahooFundamentalsProvider.RequestTypes();
        Assert.Equal(4, requests.Count);
        var income = requests[0].Split(',');
        Assert.Equal(YahooFundamentalsConcepts.IncomeStatement.Length * 3, income.Length);
        Assert.Contains("annualTotalRevenue", income);
        Assert.Contains("quarterlyTotalRevenue", income);
        Assert.Contains("trailingTotalRevenue", income);
        Assert.Contains("annualNonInterestExpense", income);
        Assert.Contains("trailingMarketCap", requests[3].Split(','));
        // Every concept the stock list computes from is requested.
        var all = requests.SelectMany(r => r.Split(',')).ToHashSet();
        Assert.All(FundamentalMetrics.Concepts.All, c => Assert.True(all.Contains("annual" + c) || all.Contains("trailing" + c), c));
        // No concept twice within a statement (a duplicate would be sent twice and stored once).
        Assert.All(YahooFundamentalsConcepts.Statements, s => Assert.Equal(s.Concepts.Length, s.Concepts.Distinct().Count()));
    }

    [Fact]
    public void Parse_MapsQuarterlyToQ()
    {
        var root = JsonDocument.Parse("""
            {"timeseries":{"result":[
              {"meta":{"symbol":["AAPL"],"type":["quarterlyTotalRevenue"]},"timestamp":[1],
               "quarterlyTotalRevenue":[{"asOfDate":"2026-09-30","periodType":"3M","currencyCode":"USD","reportedValue":{"raw":102466000000.0}}]},
              {"meta":{"symbol":["AAPL"],"type":["trailingDilutedEPS"]},"timestamp":[2],
               "trailingDilutedEPS":[{"asOfDate":"2026-09-30","periodType":"TTM","currencyCode":"USD","reportedValue":{"raw":7.46}}]}
            ],"error":null}}
            """).RootElement;
        var p = YahooFundamentalsProvider.Parse(root);
        Assert.Contains(new ProviderFundamentalFact("TotalRevenue", "Q", new DateOnly(2026, 9, 30), 102466000000m, "USD"), p.Facts);
        Assert.Contains(new ProviderFundamentalFact("DilutedEPS", "TTM", new DateOnly(2026, 9, 30), 7.46m, "USD"), p.Facts);
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
