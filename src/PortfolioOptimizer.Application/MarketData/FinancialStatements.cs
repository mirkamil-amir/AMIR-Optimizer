namespace PortfolioOptimizer.Application.MarketData;

/// <summary>How a statement row is formatted: an amount, an amount per share, a share count or a ratio (fraction).</summary>
public static class FinancialRowKind
{
    public const string Amount = "amount";
    public const string PerShare = "perShare";
    public const string Shares = "shares";
    public const string Ratio = "ratio";
    public const string Multiple = "multiple";
}

/// <summary>One row: a value per period (newest first, aligned with the table's period ends), null where not reported or not computable.</summary>
public sealed record FinancialRow(string Key, string Label, string Kind, string? Currency, IReadOnlyList<double?> Values, IReadOnlyList<string?> Notes);

public sealed record FinancialSection(string Key, string Title, IReadOnlyList<FinancialRow> Rows);

/// <summary>A statement table for one period type (fiscal years or quarters), newest period first.</summary>
public sealed record FinancialTable(string PeriodType, IReadOnlyList<DateOnly> PeriodEnds, IReadOnlyList<FinancialSection> Sections);

/// <summary>A dated trailing valuation figure as published by the source (market cap, EV, P/E, …).</summary>
public sealed record ValuationFigure(string Key, string Label, string Kind, double? Value, string? Currency, DateOnly? AsOf);

/// <summary>
/// Builds the accounting card of a stock (AMIR-41) from its current reported figures. Pure: no I/O, no currency
/// conversion (amounts stay in the company's reporting currency), never a substituted value. Reported rows are shown as
/// published; ratios are computed per period (F-FUN-009..015) and are null with a note when an input is missing or not
/// meaningful. Rows with no value in any period are left out, so a bank does not show empty industrial lines.
/// </summary>
public static class FinancialStatements
{
    private sealed record Line(string Concept, string Label, string Kind = FinancialRowKind.Amount);

    private static readonly Line[] Income =
    [
        new("TotalRevenue", "Revenue"), new("CostOfRevenue", "Cost of revenue"), new("GrossProfit", "Gross profit"),
        new("ResearchAndDevelopment", "R&D"), new("SellingGeneralAndAdministration", "SG&A"),
        new("EBITDA", "EBITDA"), new("EBIT", "EBIT"), new("InterestExpense", "Interest expense"),
        new("PretaxIncome", "Pre-tax income"), new("TaxProvision", "Tax provision"), new("NetIncome", "Net income"),
        new("DilutedEPS", "Diluted EPS", FinancialRowKind.PerShare), new("DilutedAverageShares", "Diluted shares", FinancialRowKind.Shares),
    ];

    private static readonly Line[] BankIncome =
    [
        new("TotalRevenue", "Revenue"), new("NetInterestIncome", "Net interest income"), new("NonInterestIncome", "Non-interest income"),
        new("NonInterestExpense", "Non-interest expense"), new("CreditLossesProvision", "Credit-loss provisions"),
        new("PretaxIncome", "EBT"), new("TaxProvision", "Tax provision"), new("NetIncome", "Net income"),
        new("DilutedEPS", "Diluted EPS", FinancialRowKind.PerShare), new("DilutedAverageShares", "Diluted shares", FinancialRowKind.Shares),
    ];

    private static readonly Line[] Balance =
    [
        new("TotalAssets", "Total assets"), new("CurrentAssets", "Current assets"), new("CashAndCashEquivalents", "Cash and equivalents"),
        new("TotalLiabilitiesNetMinorityInterest", "Total liabilities"), new("CurrentLiabilities", "Current liabilities"),
        new("TotalDebt", "Total debt"), new("NetDebt", "Net debt"), new("StockholdersEquity", "Shareholders' equity"),
        new("InvestedCapital", "Invested capital"), new("TangibleBookValue", "Tangible book value"),
        new("OrdinarySharesNumber", "Shares outstanding", FinancialRowKind.Shares),
    ];

    private static readonly Line[] CashFlow =
    [
        new("OperatingCashFlow", "Operating cash flow"), new("CapitalExpenditure", "Capital expenditure"), new("FreeCashFlow", "Free cash flow"),
        new("CashDividendsPaid", "Dividends paid"), new("RepurchaseOfCapitalStock", "Share buybacks"),
        new("InvestingCashFlow", "Investing cash flow"), new("FinancingCashFlow", "Financing cash flow"),
    ];

    private static readonly Line[] Valuation =
    [
        new("MarketCap", "Market cap"), new("EnterpriseValue", "Enterprise value"), new("PeRatio", "P/E", FinancialRowKind.Multiple),
        new("ForwardPeRatio", "Forward P/E", FinancialRowKind.Multiple), new("PbRatio", "P/B", FinancialRowKind.Multiple),
        new("PsRatio", "P/S", FinancialRowKind.Multiple), new("EnterprisesValueEBITDARatio", "EV/EBITDA", FinancialRowKind.Multiple),
    ];

    /// <summary>Fiscal years or quarters; null when the stock has no reported figure of that period type.</summary>
    public static FinancialTable? Table(IReadOnlyList<FactPoint> facts, string periodType, bool financial)
    {
        var own = facts.Where(f => f.PeriodType == periodType && double.IsFinite(f.Value)).ToList();
        var ends = own.Select(f => f.PeriodEnd).Distinct().OrderByDescending(d => d).ToList();
        if (ends.Count == 0) return null;
        var byKey = own.GroupBy(f => (f.Concept, f.PeriodEnd)).ToDictionary(g => g.Key, g => g.Last());
        // The reporting currency: that of the latest revenue (else of any amount).
        var currency = own.Where(f => f.Concept == "TotalRevenue").OrderByDescending(f => f.PeriodEnd).FirstOrDefault()?.Currency
                       ?? own.Where(f => f.Currency is not null).OrderByDescending(f => f.PeriodEnd).FirstOrDefault()?.Currency;

        FinancialRow? Reported(Line l)
        {
            var currencyOfRow = l.Kind is FinancialRowKind.Amount or FinancialRowKind.PerShare ? currency : null;
            var values = new List<double?>();
            var notes = new List<string?>();
            foreach (var end in ends)
            {
                if (!byKey.TryGetValue((l.Concept, end), out var f)) { values.Add(null); notes.Add(null); continue; }
                // A figure in another currency than the row is not shown as if it were comparable.
                if (currencyOfRow is not null && f.Currency is not null && f.Currency != currencyOfRow)
                {
                    values.Add(null);
                    notes.Add($"Reported in {f.Currency}, not {currencyOfRow}.");
                    continue;
                }
                values.Add(f.Value);
                notes.Add(null);
            }
            return values.Any(v => v is not null) ? new FinancialRow(l.Concept, l.Label, l.Kind, currencyOfRow, values, notes) : null;
        }

        FinancialSection Section(string key, string title, IEnumerable<Line> lines) =>
            new(key, title, lines.Select(Reported).OfType<FinancialRow>().ToList());

        var sections = new List<FinancialSection>
        {
            Section("income", "Income statement", financial ? BankIncome : Income),
            Section("balance", "Balance sheet", financial ? Balance.Where(l => l.Concept is not ("CurrentAssets" or "CurrentLiabilities" or "InvestedCapital")) : Balance),
            Section("cashflow", "Cash flow", CashFlow),
            new("ratios", "Ratios", Ratios(facts, own, ends, periodType, financial)),
        };
        return new FinancialTable(periodType, ends, sections.Where(s => s.Rows.Count > 0).ToList());
    }

    /// <summary>Latest dated trailing valuation figures (market cap in the trading currency; multiples without currency).</summary>
    public static IReadOnlyList<ValuationFigure> LatestValuation(IReadOnlyList<FactPoint> facts) =>
        Valuation.Select(l =>
        {
            var latest = facts.Where(f => f.Concept == l.Concept && f.PeriodType == FundamentalMetrics.Trailing && double.IsFinite(f.Value))
                .OrderBy(f => f.PeriodEnd).LastOrDefault();
            return latest is null ? null : new ValuationFigure(l.Concept, l.Label, l.Kind, latest.Value,
                l.Kind == FinancialRowKind.Amount ? latest.Currency : null, latest.PeriodEnd);
        }).OfType<ValuationFigure>().ToList();

    /// <summary>
    /// Ratios per period: F-FUN-009 margins (gross, EBIT, net, FCF ÷ revenue), F-FUN-010 revenue growth over the year before
    /// (fiscal years) or the previous quarter (quarters), F-FUN-014 ROE and ROIC of a quarter (not annualised), F-FUN-011 debt/equity, F-FUN-012 current ratio, F-FUN-013 cost/income (financials), and per fiscal year
    /// ROE (F-FUN-008) and ROIC (F-FUN-004, not for financials).
    /// </summary>
    private static List<FinancialRow> Ratios(IReadOnlyList<FactPoint> all, List<FactPoint> own, List<DateOnly> ends, string periodType, bool financial)
    {
        var annual = periodType == FundamentalMetrics.FiscalYear;
        FactPoint? Get(string concept, DateOnly end) =>
            own.LastOrDefault(f => f.Concept == concept && f.PeriodEnd == end);

        (double?, string?) Divide(string num, string den, DateOnly end, bool positiveDenominator = true)
        {
            var n = Get(num, end);
            var d = Get(den, end);
            if (n is null || d is null) return (null, null);
            if (n.Currency != d.Currency) return (null, "The inputs are reported in different currencies.");
            if (positiveDenominator ? d.Value <= 0 : d.Value == 0) return (null, $"Not meaningful: the denominator is {(d.Value == 0 ? "zero" : "negative")}.");
            return (n.Value / d.Value, null);
        }

        // Revenue against the revenue of a period min–max days earlier (the same period a year before, or the previous quarter).
        (double?, string?) Growth(DateOnly end, int min, int max, string earlier)
        {
            var latest = Get("TotalRevenue", end);
            if (latest is null) return (null, null);
            var prior = own.Where(f => f.Concept == "TotalRevenue" && end.DayNumber - f.PeriodEnd.DayNumber >= min && end.DayNumber - f.PeriodEnd.DayNumber <= max)
                .OrderBy(f => f.PeriodEnd).LastOrDefault();
            if (prior is null) return (null, null);
            if (prior.Currency != latest.Currency) return (null, "The two periods are reported in different currencies.");
            if (prior.Value <= 0) return (null, $"Not meaningful: revenue {earlier} is zero or negative.");
            return (latest.Value / prior.Value - 1, null);
        }

        // Profit of the quarter ÷ average capital at the previous quarter end (60–120 days earlier) and this one, or the
        // quarter-end value alone; EBIT is taxed at the quarter's effective rate, clamped to [0, 1] (0 for a pre-tax loss).
        (double?, string?) QuarterReturn(DateOnly end, string profitConcept, string capitalConcept, bool taxed, string capitalName)
        {
            var profit = Get(profitConcept, end);
            var close = Get(capitalConcept, end);
            if (profit is null || close is null) return (null, null);
            if (profit.Currency != close.Currency) return (null, "The inputs are reported in different currencies.");
            var rate = 0.0;
            if (taxed)
            {
                var pretax = Get("PretaxIncome", end);
                var tax = Get("TaxProvision", end);
                if (pretax is null || tax is null) return (null, "Pre-tax income or tax provision is not reported, so the tax rate is unknown.");
                rate = pretax.Value <= 0 ? 0 : Math.Clamp(tax.Value / pretax.Value, 0, 1);
            }
            var open = own.Where(f => f.Concept == capitalConcept && end.DayNumber - f.PeriodEnd.DayNumber >= 60 && end.DayNumber - f.PeriodEnd.DayNumber <= 120)
                .OrderBy(f => f.PeriodEnd).LastOrDefault();
            var average = open is not null && open.Currency == close.Currency ? (open.Value + close.Value) / 2 : close.Value;
            if (average <= 0) return (null, $"{capitalName} is zero or negative.");
            return (profit.Value * (1 - rate) / average, null);
        }

        FinancialRow? Row(string key, string label, Func<DateOnly, (double?, string?)> f, string kind = FinancialRowKind.Ratio)
        {
            var cells = ends.Select(f).ToList();
            // Kept when a period has a value or a reason why not (e.g. negative equity), so the reason stays visible.
            return cells.Any(c => c.Item1 is not null || c.Item2 is not null)
                ? new FinancialRow(key, label, kind, null, cells.Select(c => c.Item1).ToList(), cells.Select(c => c.Item2).ToList())
                : null;
        }

        // Fiscal years: against the year before (300–430 days earlier).
        var rows = annual
            ? new List<FinancialRow?> { Row("revenueGrowth", "Revenue growth", e => Growth(e, FundamentalMetrics.MinYearGapDays, FundamentalMetrics.MaxYearGapDays, "a year earlier")) }
            // Quarters: against the previous quarter (60–120 days before), since the source keeps only about five quarters;
            // includes seasonal swings.
            : new List<FinancialRow?> { Row("revenueGrowthQoq", "Revenue growth", e => Growth(e, 60, 120, "of the previous quarter")) };
        if (financial)
        {
            rows.Add(Row("costIncome", "Cost/income", e => Divide("NonInterestExpense", "OperatingRevenue", e)));
            rows.Add(Row("netMargin", "Net margin", e => Divide("NetIncome", "TotalRevenue", e)));
        }
        else
        {
            rows.Add(Row("grossMargin", "Gross margin", e => Divide("GrossProfit", "TotalRevenue", e)));
            rows.Add(Row("ebitMargin", "EBIT margin", e => Divide("EBIT", "TotalRevenue", e)));
            rows.Add(Row("netMargin", "Net margin", e => Divide("NetIncome", "TotalRevenue", e)));
            rows.Add(Row("fcfMargin", "FCF margin", e => Divide("FreeCashFlow", "TotalRevenue", e)));
        }
        if (annual)
        {
            rows.Add(Row("roe", "ROE", e => FundamentalMetrics.RoeAt(all, e) is { } m ? (m.Value, m.Limitation) : (null, null)));
            if (!financial)
                rows.Add(Row("roic", "ROIC", e => FundamentalMetrics.RoicAt(all, e) is { } m ? (m.Value, m.Limitation) : (null, null)));
        }
        else
        {
            // F-FUN-014: the quarter's own return, not annualised (annual figures are in the Years view).
            rows.Add(Row("roeQuarter", "ROE", e => QuarterReturn(e, "NetIncome", "StockholdersEquity", taxed: false, "Shareholders' equity")));
            if (!financial)
                rows.Add(Row("roicQuarter", "ROIC", e => QuarterReturn(e, "EBIT", "InvestedCapital", taxed: true, "Invested capital")));
        }
        rows.Add(Row("debtEquity", "Debt/equity", e => Divide("TotalDebt", "StockholdersEquity", e), FinancialRowKind.Multiple));
        if (!financial) rows.Add(Row("currentRatio", "Current ratio", e => Divide("CurrentAssets", "CurrentLiabilities", e), FinancialRowKind.Multiple));
        return rows.OfType<FinancialRow>().ToList();
    }
}
