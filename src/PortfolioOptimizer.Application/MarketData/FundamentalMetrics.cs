namespace PortfolioOptimizer.Application.MarketData;

/// <summary>A reported figure used by <see cref="FundamentalMetrics"/> (period type "FY" or "TTM").</summary>
public sealed record FactPoint(string Concept, string PeriodType, DateOnly PeriodEnd, double Value, string? Currency);

/// <summary>
/// A metric of the stock list: <see cref="Value"/> (an amount in <see cref="Currency"/>, or a fraction for ratios) for the
/// period ending <see cref="AsOf"/>, or null with the reason in <see cref="Limitation"/>.
/// </summary>
public sealed record FundamentalMetric(double? Value, string? Currency, DateOnly? AsOf, string Basis, string? Limitation)
{
    public static FundamentalMetric Unavailable(string basis, string reason) => new(null, null, null, basis, reason);
}

public sealed record FundamentalMetricSet(
    FundamentalMetric MarketCap, FundamentalMetric FreeCashFlow, FundamentalMetric Roic, FundamentalMetric RevenueGrowth,
    FundamentalMetric Ebit, FundamentalMetric Ebitda, FundamentalMetric PeRatio, FundamentalMetric Revenue);

/// <summary>
/// Metrics of the stock list computed from reported figures (formula reference F-FUN-001..004). Pure: no I/O, no
/// currency conversion (amounts stay in the currency the company reports in), never a substituted value – a metric whose
/// inputs are missing or not meaningful is null with the reason.
/// </summary>
public static class FundamentalMetrics
{
    public const string FiscalYear = "FY";
    public const string Trailing = "TTM";

    /// <summary>Concepts the stock list needs; providers may deliver more (kept for later cards).</summary>
    public static class Concepts
    {
        public const string MarketCap = "MarketCap";
        public const string TotalRevenue = "TotalRevenue";
        public const string Ebit = "EBIT";
        public const string Ebitda = "EBITDA";
        public const string PeRatio = "PeRatio";
        public const string PretaxIncome = "PretaxIncome";
        public const string TaxProvision = "TaxProvision";
        public const string InvestedCapital = "InvestedCapital";
        public const string FreeCashFlow = "FreeCashFlow";
    }

    /// <summary>Two fiscal-year ends count as consecutive years when 300–430 days apart (allows shifted year ends).</summary>
    public const int MinYearGapDays = 300;
    public const int MaxYearGapDays = 430;

    public static FundamentalMetricSet Compute(IReadOnlyList<FactPoint> facts) =>
        new(MarketCap(facts), FreeCashFlow(facts), Roic(facts), RevenueGrowth(facts),
            LatestFiscalYear(facts, Concepts.Ebit, "EBIT, latest fiscal year", "EBIT is not reported (usual for banks and insurers)."),
            LatestFiscalYear(facts, Concepts.Ebitda, "EBITDA, latest fiscal year", "EBITDA is not reported (usual for banks and insurers)."),
            PeRatio(facts),
            LatestFiscalYear(facts, Concepts.TotalRevenue, "Revenue, latest fiscal year", "The source reports no revenue."));

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

    /// <summary>F-FUN-003: revenue of the latest fiscal year ÷ revenue of the year before − 1.</summary>
    public static FundamentalMetric RevenueGrowth(IReadOnlyList<FactPoint> facts)
    {
        const string basis = "Revenue growth, latest fiscal year over the year before";
        var revenue = Series(facts, Concepts.TotalRevenue, FiscalYear);
        if (revenue.Count == 0) return FundamentalMetric.Unavailable(basis, "The source reports no revenue.");
        var latest = revenue[^1];
        var prior = PriorYear(revenue, latest);
        if (prior is null) return FundamentalMetric.Unavailable(basis, "Revenue of the previous fiscal year is not reported.");
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
        var capital = Series(facts, Concepts.InvestedCapital, FiscalYear);
        // Latest year with both EBIT and year-end invested capital in the same currency.
        foreach (var e in Enumerable.Reverse(ebit))
        {
            var end = capital.FirstOrDefault(c => c.PeriodEnd == e.PeriodEnd);
            if (end is null || end.Currency != e.Currency) continue;
            var pretax = At(facts, Concepts.PretaxIncome, e.PeriodEnd);
            var tax = At(facts, Concepts.TaxProvision, e.PeriodEnd);
            if (pretax is null || tax is null)
                return FundamentalMetric.Unavailable(basis, "Pre-tax income or tax provision is not reported, so the tax rate is unknown.");
            var rate = pretax.Value <= 0 ? 0 : Math.Clamp(tax.Value / pretax.Value, 0, 1);
            var start = PriorYear(capital, end);
            var invested = start is not null && start.Currency == end.Currency ? (start.Value + end.Value) / 2 : end.Value;
            if (invested <= 0) return FundamentalMetric.Unavailable(basis, "Invested capital is zero or negative.");
            return new FundamentalMetric(e.Value * (1 - rate) / invested, null, e.PeriodEnd, basis, null);
        }
        return FundamentalMetric.Unavailable(basis, "Invested capital is not reported for a year with EBIT.");
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
