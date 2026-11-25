using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.UnitTests.MarketData;

/// <summary>Accounting card tables (AMIR-41): reported rows as published, ratios per period with hand-computed expectations.</summary>
public sealed class FinancialStatementsTests
{
    private static readonly DateOnly Y23 = new(2023, 12, 31);
    private static readonly DateOnly Y24 = new(2024, 12, 31);
    private static readonly DateOnly Y25 = new(2025, 12, 31);

    private static FactPoint Fy(string concept, DateOnly end, double value, string? ccy = "EUR") => new(concept, "FY", end, value, ccy);
    private static FactPoint Q(string concept, DateOnly end, double value, string? ccy = "EUR") => new(concept, "Q", end, value, ccy);

    private static List<FactPoint> Company() =>
    [
        Fy("TotalRevenue", Y24, 800), Fy("TotalRevenue", Y25, 1000),
        Fy("GrossProfit", Y25, 400), Fy("EBIT", Y25, 200), Fy("NetIncome", Y25, 120), Fy("FreeCashFlow", Y25, 90),
        Fy("PretaxIncome", Y25, 160), Fy("TaxProvision", Y25, 40),
        Fy("InvestedCapital", Y24, 900), Fy("InvestedCapital", Y25, 1100),
        Fy("StockholdersEquity", Y24, 500), Fy("StockholdersEquity", Y25, 700),
        Fy("TotalDebt", Y25, 350), Fy("CurrentAssets", Y25, 300), Fy("CurrentLiabilities", Y25, 200),
        Fy("DilutedEPS", Y25, 1.25), Fy("OrdinarySharesNumber", Y25, 96, null),
        new("MarketCap", "TTM", new DateOnly(2026, 6, 1), 9e9, "USD"), new("MarketCap", "TTM", new DateOnly(2026, 11, 17), 10e9, "USD"),
        new("PeRatio", "TTM", new DateOnly(2026, 11, 17), 21.5, null),
        Q("TotalRevenue", new DateOnly(2024, 12, 31), 200), Q("TotalRevenue", new DateOnly(2025, 9, 30), 240), Q("TotalRevenue", new DateOnly(2025, 12, 31), 260),
    ];

    private static FinancialRow Row(FinancialTable t, string key) => t.Sections.SelectMany(s => s.Rows).Single(r => r.Key == key);

    [Fact]
    public void Years_AreNewestFirst_ReportedRowsAsPublished_EmptyRowsLeftOut()
    {
        var t = FinancialStatements.Table(Company(), "FY", financial: false)!;
        Assert.Equal([Y25, Y24], t.PeriodEnds);
        Assert.Equal([1000.0, 800.0], Row(t, "TotalRevenue").Values);
        Assert.Equal("EUR", Row(t, "TotalRevenue").Currency);
        Assert.Equal([1.25, null], Row(t, "DilutedEPS").Values);
        Assert.Null(Row(t, "OrdinarySharesNumber").Currency); // a share count has no currency
        Assert.DoesNotContain(t.Sections.SelectMany(s => s.Rows), r => r.Key == "CostOfRevenue"); // never reported
        Assert.Equal(["income", "balance", "cashflow", "ratios"], t.Sections.Select(s => s.Key));
    }

    [Fact]
    public void Ratios_PerYear_HandComputed()
    {
        var t = FinancialStatements.Table(Company(), "FY", financial: false)!;
        Assert.DoesNotContain(t.Sections.SelectMany(s => s.Rows), r => r.Key == "revenueGrowthQoq"); // quarters only
        Assert.Equal("Revenue growth", Row(t, "revenueGrowth").Label);
        Assert.Equal(0.25, Row(t, "revenueGrowth").Values[0]!.Value, 12); // 1000 / 800 − 1
        Assert.Null(Row(t, "revenueGrowth").Values[1]); // no 2023 revenue
        Assert.Equal(0.4, Row(t, "grossMargin").Values[0]!.Value, 12);
        Assert.Equal(0.2, Row(t, "ebitMargin").Values[0]!.Value, 12);
        Assert.Equal(0.12, Row(t, "netMargin").Values[0]!.Value, 12);
        Assert.Equal(0.09, Row(t, "fcfMargin").Values[0]!.Value, 12);
        Assert.Equal(120.0 / 600, Row(t, "roe").Values[0]!.Value, 12); // average equity (500 + 700) / 2
        Assert.Equal(200 * (1 - 0.25) / 1000.0, Row(t, "roic").Values[0]!.Value, 12); // t = 40/160, average capital 1000
        Assert.Equal(0.5, Row(t, "debtEquity").Values[0]!.Value, 12);
        Assert.Equal(FinancialRowKind.Multiple, Row(t, "debtEquity").Kind);
        Assert.Equal(1.5, Row(t, "currentRatio").Values[0]!.Value, 12);
    }

    [Fact]
    public void Ratios_AreNullWithANote_WhenNotMeaningful()
    {
        var facts = Company().Where(f => f.Concept != "StockholdersEquity").Append(Fy("StockholdersEquity", Y25, -50)).ToList();
        var t = FinancialStatements.Table(facts, "FY", financial: false)!;
        Assert.Null(Row(t, "debtEquity").Values[0]);
        Assert.Contains("negative", Row(t, "debtEquity").Notes[0]);
        var mixed = Company().Where(f => !(f.Concept == "GrossProfit")).Append(Fy("GrossProfit", Y25, 400, "USD")).ToList();
        var m = FinancialStatements.Table(mixed, "FY", financial: false)!;
        Assert.Contains("different currencies", Row(m, "grossMargin").Notes.First(n => n is not null));
    }

    [Fact]
    public void AFigureInAnotherCurrency_IsNotShownInTheRow()
    {
        var facts = Company().Append(Fy("NetIncome", Y24, 70, "USD")).ToList();
        var r = Row(FinancialStatements.Table(facts, "FY", financial: false)!, "NetIncome");
        Assert.Equal([120.0, null], r.Values);
        Assert.Equal("Reported in USD, not EUR.", r.Notes[1]);
    }

    [Fact]
    public void Quarters_OneGrowthRow_AgainstThePreviousQuarter_NoAnnualReturns()
    {
        var t = FinancialStatements.Table(Company(), "Q", financial: false)!;
        Assert.Equal(3, t.PeriodEnds.Count);
        var growth = Row(t, "revenueGrowthQoq");
        Assert.Equal("Revenue growth", growth.Label);
        Assert.Equal(260.0 / 240 - 1, growth.Values[0]!.Value, 12); // Q4 2025 vs Q3 2025
        Assert.Null(growth.Values[1]); // Q3 2025: Q2 2025 not stored
        Assert.Null(growth.Values[2]); // Q4 2024: Q3 2024 not stored (Q3 2025 is later, never used)
        Assert.Single(t.Sections.SelectMany(s => s.Rows), r => r.Label.Contains("growth"));
        Assert.DoesNotContain(t.Sections.SelectMany(s => s.Rows), r => r.Key is "roeQuarter" or "roicQuarter"); // no quarterly profits here
        Assert.DoesNotContain(t.Sections.SelectMany(s => s.Rows), r => r.Key is "roe" or "roic");
    }

    [Fact]
    public void Quarters_RoeAndRoic_AreTheQuarterOwnReturn_NotAnnualised()
    {
        var q3 = new DateOnly(2026, 6, 30);
        var q4 = new DateOnly(2026, 9, 30);
        List<FactPoint> facts =
        [
            Q("TotalRevenue", q3, 90), Q("TotalRevenue", q4, 100),
            Q("NetIncome", q4, 20), Q("StockholdersEquity", q3, 90), Q("StockholdersEquity", q4, 110),
            Q("EBIT", q4, 30), Q("PretaxIncome", q4, 25), Q("TaxProvision", q4, 5),
            Q("InvestedCapital", q3, 280), Q("InvestedCapital", q4, 320),
            Q("NetIncome", q3, 18), // Q3: no Q2 equity → year-end of the quarter alone
            Q("EBIT", q3, 27), // Q3: EBIT but no pre-tax income or tax → ROIC null with the reason
        ];
        var t = FinancialStatements.Table(facts, "Q", financial: false)!;
        var roe = Row(t, "roeQuarter");
        Assert.Equal("ROE", roe.Label);
        Assert.Equal(20.0 / 100, roe.Values[0]!.Value, 12); // 20 ÷ (90 + 110) / 2, not × 4
        Assert.Equal(18.0 / 90, roe.Values[1]!.Value, 12);
        var roic = Row(t, "roicQuarter");
        Assert.Equal("ROIC", roic.Label);
        Assert.Equal(30 * (1 - 0.2) / 300.0, roic.Values[0]!.Value, 12); // t = 5 / 25, average capital 300
        Assert.Contains("tax", roic.Notes[1]); // Q3 has no pre-tax income or tax
        // Banks: ROE only.
        Assert.DoesNotContain(FinancialStatements.Table(facts, "Q", financial: true)!.Sections.SelectMany(s => s.Rows), r => r.Key == "roicQuarter");
    }

    [Fact]
    public void Financials_ShowBankLines_CostIncome_AndNoRoic()
    {
        List<FactPoint> bank =
        [
            Fy("TotalRevenue", Y25, 100), Fy("NetInterestIncome", Y25, 60), Fy("OperatingRevenue", Y25, 100), Fy("NonInterestExpense", Y25, 55),
            Fy("NetIncome", Y25, 20), Fy("StockholdersEquity", Y24, 180), Fy("StockholdersEquity", Y25, 220), Fy("EBIT", Y25, 30),
            Fy("InvestedCapital", Y25, 400), Fy("PretaxIncome", Y25, 25), Fy("TaxProvision", Y25, 5), Fy("CurrentAssets", Y25, 10),
        ];
        var t = FinancialStatements.Table(bank, "FY", financial: true)!;
        Assert.Equal(0.55, Row(t, "costIncome").Values[0]!.Value, 12);
        Assert.Equal(0.1, Row(t, "roe").Values[0]!.Value, 12); // 20 / ((180 + 220) / 2)
        var keys = t.Sections.SelectMany(s => s.Rows).Select(r => r.Key).ToList();
        Assert.Contains("NetInterestIncome", keys);
        Assert.DoesNotContain("roic", keys);
        Assert.DoesNotContain("EBIT", keys);
        Assert.DoesNotContain("CurrentAssets", keys);
    }

    [Fact]
    public void Valuation_IsTheLatestDatedValue_NoPeriodTypeMeansNoTable()
    {
        var v = FinancialStatements.LatestValuation(Company());
        var cap = v.Single(x => x.Key == "MarketCap");
        Assert.Equal(10e9, cap.Value);
        Assert.Equal("USD", cap.Currency);
        Assert.Equal(new DateOnly(2026, 11, 17), cap.AsOf);
        Assert.Null(v.Single(x => x.Key == "PeRatio").Currency);
        Assert.DoesNotContain(v, x => x.Key == "PbRatio");
        Assert.Null(FinancialStatements.Table([Fy("TotalRevenue", Y23, 1)], "Q", financial: false));
    }
}
