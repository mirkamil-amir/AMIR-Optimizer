namespace PortfolioOptimizer.Application.MarketData;

/// <summary>A reported figure used by <see cref="FundamentalMetrics"/> (period type "FY" or "TTM").</summary>
public sealed record FactPoint(string Concept, string PeriodType, DateOnly PeriodEnd, double Value, string? Currency);

/// <summary>
/// A metric of the stock list: <see cref="Value"/> (an amount in <see cref="Currency"/>, or a fraction for ratios) for the
/// period ending <see cref="AsOf"/>, or null with the reason in <see cref="Limitation"/>. <see cref="SpanYears"/> is set
/// when a growth rate is annualised over more than one year (null = one year).
/// </summary>
public sealed record FundamentalMetric(double? Value, string? Currency, DateOnly? AsOf, string Basis, string? Limitation, int? SpanYears = null)
{
    public static FundamentalMetric Unavailable(string basis, string reason) => new(null, null, null, basis, reason);
}

public sealed record FundamentalMetricSet(
    FundamentalMetric MarketCap, FundamentalMetric FreeCashFlow, FundamentalMetric Roic, FundamentalMetric RevenueGrowth,
    FundamentalMetric Ebit, FundamentalMetric Ebitda, FundamentalMetric PeRatio, FundamentalMetric Revenue,
    FundamentalMetric Ebt, FundamentalMetric Ppop, FundamentalMetric Roe);

/// <summary>
/// Metrics of the stock list computed from reported figures (formula reference F-FUN-001..004). Pure: no I/O, no
/// currency conversion (amounts stay in the currency the company reports in), never a substituted value – a metric whose
/// inputs are missing or not meaningful is null with the reason.
/// </summary>
public static class FundamentalMetrics
{
    public const string FiscalYear = "FY";
    public const string Trailing = "TTM";
    /// <summary>A fiscal quarter (stored for later cards and ML; the stock list does not use quarters).</summary>
    public const string Quarter = "Q";

    /// <summary>Concepts the stock list needs; providers may deliver more (kept for later cards).</summary>
    public static class Concepts
    {
        public const string MarketCap = "MarketCap";
        public const string TotalRevenue = "TotalRevenue";
        public const string Ebit = "EBIT";
        public const string Ebitda = "EBITDA";
        public const string PeRatio = "PeRatio";
        public const string NetIncome = "NetIncome";
        public const string StockholdersEquity = "StockholdersEquity";
        public const string OperatingRevenue = "OperatingRevenue";
        public const string NonInterestExpense = "NonInterestExpense";
        public const string PretaxIncome = "PretaxIncome";
        public const string TaxProvision = "TaxProvision";
        public const string InvestedCapital = "InvestedCapital";
        public const string FreeCashFlow = "FreeCashFlow";

        /// <summary>Every concept above: the stock list loads only these (a listing stores far more).</summary>
        public static readonly string[] All =
        [
            MarketCap, TotalRevenue, Ebit, Ebitda, PeRatio, NetIncome, StockholdersEquity, OperatingRevenue,
            NonInterestExpense, PretaxIncome, TaxProvision, InvestedCapital, FreeCashFlow,
        ];
    }

    /// <summary>Two fiscal-year ends count as consecutive years when 300–430 days apart (allows shifted year ends).</summary>
    public const int MinYearGapDays = 300;
    public const int MaxYearGapDays = 430;
    /// <summary>Two fiscal-year ends count as two years apart when 665–795 days apart (the revenue-growth fallback).</summary>
    public const int MinTwoYearGapDays = 665;
    public const int MaxTwoYearGapDays = 795;

    public static FundamentalMetricSet Compute(IReadOnlyList<FactPoint> facts) =>
        new(MarketCap(facts), FreeCashFlow(facts), Roic(facts), RevenueGrowth(facts),
            LatestFiscalYear(facts, Concepts.Ebit, "EBIT, latest fiscal year", "EBIT is not reported (usual for banks and insurers)."),
            LatestFiscalYear(facts, Concepts.Ebitda, "EBITDA, latest fiscal year", "EBITDA is not reported (usual for banks and insurers)."),
            PeRatio(facts),
            LatestFiscalYear(facts, Concepts.TotalRevenue, "Revenue, latest fiscal year", "The source reports no revenue."),
            LatestFiscalYear(facts, Concepts.PretaxIncome, "EBT (pre-tax income), latest fiscal year", "The source reports no pre-tax income."),
            Ppop(facts), Roe(facts));

    /// <summary>
    /// F-FUN-007: pre-provision operating profit of the latest fiscal year with both inputs: operating revenue − non-interest
    /// expense (the bank equivalent of EBITDA: profit before loan-loss provisions and taxes).
    /// </summary>
    public static FundamentalMetric Ppop(IReadOnlyList<FactPoint> facts)
    {
        const string basis = "PPOP (operating revenue − non-interest expense), latest fiscal year";
        foreach (var expense in Enumerable.Reverse(Series(facts, Concepts.NonInterestExpense, FiscalYear)))
        {
            var revenue = At(facts, Concepts.OperatingRevenue, expense.PeriodEnd);
            if (revenue is null || revenue.Currency != expense.Currency) continue;
            return new FundamentalMetric(revenue.Value - expense.Value, revenue.Currency, expense.PeriodEnd, basis, null);
        }
        return FundamentalMetric.Unavailable(basis, "Operating revenue or non-interest expense is not reported (usual for insurers and non-bank financials).");
    }

    /// <summary>
    /// F-FUN-008: return on equity of the latest fiscal year: net income ÷ average shareholders' equity at the previous and
    /// this year end (the year-end value alone when the previous one is missing); equity ≤ 0 → null.
    /// </summary>
    public static FundamentalMetric Roe(IReadOnlyList<FactPoint> facts)
    {
        const string basis = "Return on equity, latest fiscal year";
        foreach (var income in Enumerable.Reverse(Series(facts, Concepts.NetIncome, FiscalYear)))
            if (RoeAt(facts, income.PeriodEnd, basis) is { } m) return m;
        return FundamentalMetric.Unavailable(basis, "Net income or shareholders' equity is not reported.");
    }

    /// <summary>F-FUN-008 for the fiscal year ending <paramref name="end"/>; null when that year lacks net income or year-end equity in one currency.</summary>
    public static FundamentalMetric? RoeAt(IReadOnlyList<FactPoint> facts, DateOnly end, string basis = "Return on equity")
    {
        var income = At(facts, Concepts.NetIncome, end);
        var equity = Series(facts, Concepts.StockholdersEquity, FiscalYear);
        var close = equity.FirstOrDefault(e => e.PeriodEnd == end);
        if (income is null || close is null || close.Currency != income.Currency) return null;
        var start = PriorYear(equity, close);
        var average = start is not null && start.Currency == close.Currency ? (start.Value + close.Value) / 2 : close.Value;
        if (average <= 0) return FundamentalMetric.Unavailable(basis, "Shareholders' equity is zero or negative.");
        return new FundamentalMetric(income.Value / average, null, end, basis, null);
    }

    /// <summary>F-FUN-005: a reported figure of the latest fiscal year as published (revenue, EBIT, EBITDA), with its currency.</summary>
    public static FundamentalMetric LatestFiscalYear(IReadOnlyList<FactPoint> facts, string concept, string basis, string missing)
    {
        var latest = Series(facts, concept, FiscalYear).LastOrDefault();
        return latest is null ? FundamentalMetric.Unavailable(basis, missing) : new FundamentalMetric(latest.Value, latest.Currency, latest.PeriodEnd, basis, null);
    }

    /// <summary>
    /// F-FUN-006: latest trailing price/earnings ratio as published by the source (price ÷ trailing twelve-month diluted EPS).
    /// Not meaningful with losses: a ratio ≤ 0 is reported as unavailable.
    /// </summary>
    public static FundamentalMetric PeRatio(IReadOnlyList<FactPoint> facts)
    {
        const string basis = "Price/earnings ratio, trailing twelve months";
        var latest = Series(facts, Concepts.PeRatio, Trailing).LastOrDefault();
        if (latest is null) return FundamentalMetric.Unavailable(basis, "The source reports no P/E ratio (usually because earnings are negative).");
        if (latest.Value <= 0) return FundamentalMetric.Unavailable(basis, "P/E is not meaningful: earnings are zero or negative.");
        return new FundamentalMetric(latest.Value, null, latest.PeriodEnd, basis, null);
    }

    /// <summary>F-FUN-001: latest reported market capitalization (price × shares outstanding, as published by the source).</summary>
    public static FundamentalMetric MarketCap(IReadOnlyList<FactPoint> facts)
    {
        const string basis = "Market capitalization";
        var latest = Series(facts, Concepts.MarketCap, Trailing).LastOrDefault();
        if (latest is null) return FundamentalMetric.Unavailable(basis, "The source reports no market capitalization.");
        return new FundamentalMetric(latest.Value, latest.Currency, latest.PeriodEnd, basis, null);
    }

    /// <summary>F-FUN-002: free cash flow of the latest fiscal year (operating cash flow − capital expenditure, as reported).</summary>
    public static FundamentalMetric FreeCashFlow(IReadOnlyList<FactPoint> facts)
    {
        const string basis = "Free cash flow, latest fiscal year";
        var latest = Series(facts, Concepts.FreeCashFlow, FiscalYear).LastOrDefault();
        if (latest is null) return FundamentalMetric.Unavailable(basis, "The source reports no free cash flow.");
        return new FundamentalMetric(latest.Value, latest.Currency, latest.PeriodEnd, basis, null);
    }

    /// <summary>
    /// F-FUN-003: revenue of the latest fiscal year ÷ revenue of the year before − 1. When the source skips the year before
    /// but reports the one two years earlier, the annualised growth over those two years, √(R_FY ÷ R_FY−2) − 1, marked
    /// with <see cref="FundamentalMetric.SpanYears"/> = 2.
    /// </summary>
    public static FundamentalMetric RevenueGrowth(IReadOnlyList<FactPoint> facts)
    {
        const string basis = "Revenue growth, latest fiscal year over the year before";
        var revenue = Series(facts, Concepts.TotalRevenue, FiscalYear);
        if (revenue.Count == 0) return FundamentalMetric.Unavailable(basis, "The source reports no revenue.");
        var latest = revenue[^1];
        var prior = PriorYear(revenue, latest);
        if (prior is null)
        {
            var twoBack = revenue.LastOrDefault(f => latest.PeriodEnd.DayNumber - f.PeriodEnd.DayNumber is >= MinTwoYearGapDays and <= MaxTwoYearGapDays);
            if (twoBack is null) return FundamentalMetric.Unavailable(basis, "Revenue of the previous fiscal year is not reported.");
            var twoYearBasis = $"Revenue growth per year, fiscal year ending {twoBack.PeriodEnd:yyyy-MM-dd} to {latest.PeriodEnd:yyyy-MM-dd}, annualised over 2 years (the source does not report the year between)";
            if (twoBack.Currency != latest.Currency) return FundamentalMetric.Unavailable(twoYearBasis, "The two years are reported in different currencies.");
            if (twoBack.Value <= 0) return FundamentalMetric.Unavailable(twoYearBasis, "Revenue two fiscal years earlier is zero or negative.");
            if (latest.Value < 0) return FundamentalMetric.Unavailable(twoYearBasis, "Revenue of the latest fiscal year is negative.");
            return new FundamentalMetric(Math.Sqrt(latest.Value / twoBack.Value) - 1, null, latest.PeriodEnd, twoYearBasis, null, 2);
        }
        if (prior.Currency != latest.Currency) return FundamentalMetric.Unavailable(basis, "The two years are reported in different currencies.");
        if (prior.Value <= 0) return FundamentalMetric.Unavailable(basis, "Revenue of the previous fiscal year is zero or negative.");
        return new FundamentalMetric(latest.Value / prior.Value - 1, null, latest.PeriodEnd, basis, null);
    }

    /// <summary>
    /// F-FUN-004: return on invested capital of the latest fiscal year with EBIT:
    /// NOPAT = EBIT × (1 − t), t = tax provision ÷ pre-tax income clamped to [0, 1] (t = 0 when pre-tax income ≤ 0);
    /// ROIC = NOPAT ÷ average invested capital (debt + equity) of that year's end and the previous year's end, or the
    /// year-end value alone when the previous one is not reported.
    /// </summary>
    public static FundamentalMetric Roic(IReadOnlyList<FactPoint> facts)
    {
        const string basis = "Return on invested capital, latest fiscal year";
        var ebit = Series(facts, Concepts.Ebit, FiscalYear);
        if (ebit.Count == 0)
            return FundamentalMetric.Unavailable(basis, "EBIT is not reported (usual for banks and insurers, where ROIC is not meaningful).");
        // Latest year with both EBIT and year-end invested capital in the same currency.
        foreach (var e in Enumerable.Reverse(ebit))
            if (RoicAt(facts, e.PeriodEnd, basis) is { } m) return m;
        return FundamentalMetric.Unavailable(basis, "Invested capital is not reported for a year with EBIT.");
    }

    /// <summary>F-FUN-004 for the fiscal year ending <paramref name="end"/>; null when that year lacks EBIT or year-end invested capital in one currency.</summary>
    public static FundamentalMetric? RoicAt(IReadOnlyList<FactPoint> facts, DateOnly end, string basis = "Return on invested capital")
    {
        var e = At(facts, Concepts.Ebit, end);
        var capital = Series(facts, Concepts.InvestedCapital, FiscalYear);
        var close = capital.FirstOrDefault(c => c.PeriodEnd == end);
        if (e is null || close is null || close.Currency != e.Currency) return null;
        var pretax = At(facts, Concepts.PretaxIncome, end);
        var tax = At(facts, Concepts.TaxProvision, end);
        if (pretax is null || tax is null)
            return FundamentalMetric.Unavailable(basis, "Pre-tax income or tax provision is not reported, so the tax rate is unknown.");
        var rate = pretax.Value <= 0 ? 0 : Math.Clamp(tax.Value / pretax.Value, 0, 1);
        var start = PriorYear(capital, close);
        var invested = start is not null && start.Currency == close.Currency ? (start.Value + close.Value) / 2 : close.Value;
        if (invested <= 0) return FundamentalMetric.Unavailable(basis, "Invested capital is zero or negative.");
        return new FundamentalMetric(e.Value * (1 - rate) / invested, null, end, basis, null);
    }

    private static List<FactPoint> Series(IReadOnlyList<FactPoint> facts, string concept, string periodType) =>
        facts.Where(f => f.Concept == concept && f.PeriodType == periodType && double.IsFinite(f.Value))
            .GroupBy(f => f.PeriodEnd).Select(g => g.Last()).OrderBy(f => f.PeriodEnd).ToList();

    private static FactPoint? At(IReadOnlyList<FactPoint> facts, string concept, DateOnly end) =>
        Series(facts, concept, FiscalYear).FirstOrDefault(f => f.PeriodEnd == end);

    private static FactPoint? PriorYear(List<FactPoint> series, FactPoint latest) =>
        series.LastOrDefault(f => f.PeriodEnd < latest.PeriodEnd
            && latest.PeriodEnd.DayNumber - f.PeriodEnd.DayNumber is >= MinYearGapDays and <= MaxYearGapDays);
}
