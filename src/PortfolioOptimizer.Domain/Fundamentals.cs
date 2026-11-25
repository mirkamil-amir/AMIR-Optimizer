namespace PortfolioOptimizer.Domain;

/// <summary>Outcome of the last attempt to obtain a listing's reported financial figures.</summary>
public enum FundamentalsFetchStatus
{
    /// <summary>Figures were retrieved from the source.</summary>
    Available,
    /// <summary>The source has no figures for this listing.</summary>
    NotFound,
    /// <summary>The source could not be reached or answered with an error/rate limit.</summary>
    Error,
}

/// <summary>
/// One reported figure of a listing as supplied by the source (never computed or estimated): a concept such as
/// "TotalRevenue" for a fiscal year (<c>FY</c>), a fiscal quarter (<c>Q</c>) or a trailing value (<c>TTM</c>, e.g. the
/// market capitalization on a date). Shared reference data like prices; derived metrics (ROIC, growth) are computed from
/// these when read.
/// <para>
/// History is kept (AMIR-41): a refresh never deletes figures. Periods the source no longer returns stay, and a value the
/// source changes (a restatement) is closed with <see cref="SupersededAtUtc"/> and followed by a new row, so
/// <see cref="FirstSeenAtUtc"/> records when each value became known here. Current figures have no
/// <see cref="SupersededAtUtc"/>; at most one per (listing, source, concept, period type, period end).
/// </para>
/// </summary>
public class FundamentalFact
{
    public long Id { get; set; }
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    /// <summary>Provider name (e.g. "Yahoo", "MockFundamentals"); figures of different sources are never mixed.</summary>
    public required string Source { get; set; }
    /// <summary>Concept name without period prefix, e.g. "TotalRevenue", "EBIT", "MarketCap".</summary>
    public required string Concept { get; set; }
    /// <summary>"FY" (fiscal year) or "TTM" (trailing value on <see cref="PeriodEnd"/>).</summary>
    public required string PeriodType { get; set; }
    /// <summary>Fiscal-year end, or the as-of date of a trailing value.</summary>
    public DateOnly PeriodEnd { get; set; }
    /// <summary>Value in ISO major units of <see cref="Currency"/> (ratios such as a tax rate have no currency).</summary>
    public decimal Value { get; set; }
    public string? Currency { get; set; }
    /// <summary>When the source last reported this value (updated by every refresh that returns it unchanged).</summary>
    public DateTime RetrievedAtUtc { get; set; }
    /// <summary>When this value was first retrieved here (point in time for later analyses; not the publication date).</summary>
    public DateTime FirstSeenAtUtc { get; set; }
    /// <summary>When the source reported a different value for the same period (null = current value).</summary>
    public DateTime? SupersededAtUtc { get; set; }
}

/// <summary>Last fetch attempt of a listing's figures (one row per listing); drives refresh timing and error reporting.</summary>
public class FundamentalsFetchState
{
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    public required string Source { get; set; }
    public FundamentalsFetchStatus Status { get; set; }
    public DateTime AttemptedAtUtc { get; set; }
    /// <summary>When the stored figures were retrieved (set once a fetch succeeded).</summary>
    public DateTime? RetrievedAtUtc { get; set; }
    public string? LastError { get; set; }
}
