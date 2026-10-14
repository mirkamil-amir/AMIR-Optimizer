namespace PortfolioOptimizer.Domain;

/// <summary>
/// An instrument in the shared catalogue. <see cref="IsEnabled"/> means "tracked": included in whole-universe and
/// scheduled price refreshes. Catalogue-only instruments can still be browsed and selected; their prices are downloaded
/// on demand (after which they become tracked).
/// </summary>
public class Asset
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public string? Name { get; set; }
    public string? Exchange { get; set; }
    public string Currency { get; set; } = "USD";
    public AssetType AssetType { get; set; } = AssetType.Equity;
    public string? Sector { get; set; }
    public string? Industry { get; set; }
    /// <summary>ISO 3166 country of the listing venue (US, DE, FR, NL, GB, JP).</summary>
    public string? Country { get; set; }
    /// <summary>ISO 10383 market identifier code of the listing venue (XNYS, XETR, XLON, ...).</summary>
    public string? Mic { get; set; }
    /// <summary>ISIN, only when supplied by the catalogue source (never derived).</summary>
    public string? Isin { get; set; }
    /// <summary>Index memberships (S&amp;P 500, DAX 40, ...).</summary>
    public List<IndexMembership> Memberships { get; set; } = [];
    /// <summary>Short company description, only when supplied by a licensed source (never generated).</summary>
    public string? Description { get; set; }
    /// <summary>Where name/sector/industry came from (e.g. catalogue file and revision, provider name).</summary>
    public string? MetadataSource { get; set; }
    public DateTime? MetadataUpdatedAtUtc { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    public static string NormalizeSymbol(string symbol) => symbol.Trim().ToUpperInvariant();
}

/// <summary>
/// One daily bar from one source. <see cref="Close"/> is the raw (unadjusted) close used for trading;
/// <see cref="AdjustedClose"/> is split- and dividend-adjusted (total return) when the source provides it.
/// Unique per (asset, date, source).
/// </summary>
public class HistoricalPrice
{
    public long Id { get; set; }
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    public DateOnly Date { get; set; }
    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal Close { get; set; }
    public decimal? AdjustedClose { get; set; }
    public long? Volume { get; set; }
    public required string Source { get; set; }
    public bool IsSimulated { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
}

/// <summary>
/// A stock index whose constituents form a browsable universe. Membership comes from a versioned source file
/// (see docs/market-data.md); <see cref="IsHistorical"/> marks fixed past compositions such as the DAX 30 of 2021.
/// </summary>
public class MarketIndex
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Country { get; set; }
    public required string Currency { get; set; }
    /// <summary>Symbol used as the default benchmark for this universe (an index level or an ETF).</summary>
    public string? BenchmarkSymbol { get; set; }
    public bool IsHistorical { get; set; }
    public string? Description { get; set; }
    public required string Source { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceRevision { get; set; }
    public string? Licence { get; set; }
    /// <summary>Date the membership list was last checked against its source.</summary>
    public DateOnly? VerifiedOn { get; set; }
    /// <summary>SHA-256 of the imported constituent file; a changed file is re-imported on start.</summary>
    public string? ContentHash { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<IndexMembership> Members { get; set; } = [];
}

/// <summary>An asset belonging to an index universe. Replaced as a whole when the index file is re-imported.</summary>
public class IndexMembership
{
    public required string IndexCode { get; set; }
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
}

/// <summary>
/// One batch refresh of latest quotes (triggered by a sign-in, the scheduler, a page or an administrator). Recorded so
/// the UI can show progress and administrators can see market-data health; never contains prices itself.
/// </summary>
public class QuoteRefreshRun
{
    public Guid Id { get; set; }
    public required string Trigger { get; set; }
    public Guid? RequestedBy { get; set; }
    public required string Source { get; set; }
    public JobStatus Status { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int SymbolsRequested { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Failed { get; set; }
    public string? Message { get; set; }
}

/// <summary>Latest intraday/last-trade quote per asset and source.</summary>
public class LatestQuote
{
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    public required string Source { get; set; }
    public decimal Price { get; set; }
    /// <summary>Previous session's close as reported with the quote (lets the UI show a change without stored history).</summary>
    public decimal? PreviousClose { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime AsOfUtc { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
    public bool IsSimulated { get; set; }
}

public class MarketDataSyncJob
{
    public Guid Id { get; set; }
    public JobStatus Status { get; set; }
    public required string Source { get; set; }
    public string Symbols { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool ForceFullRefresh { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int AssetsTotal { get; set; }
    public int AssetsCompleted { get; set; }
    public int AssetsFailed { get; set; }
    public string? Message { get; set; }
    public List<DataSyncLog> Logs { get; set; } = [];
}

/// <summary>Per-asset outcome of a data download, including data-quality counters.</summary>
public class DataSyncLog
{
    public long Id { get; set; }
    public Guid? JobId { get; set; }
    public int AssetId { get; set; }
    public required string Symbol { get; set; }
    public required string Source { get; set; }
    public bool Success { get; set; }
    public int RowsReceived { get; set; }
    public int RowsInserted { get; set; }
    public int RowsUpdated { get; set; }
    public int RowsRejected { get; set; }
    public int DuplicatesSkipped { get; set; }
    public string? Message { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

/// <summary>Single-row global configuration (local single-user application).</summary>
public class ApplicationSettings
{
    public int Id { get; set; } = 1;
    public required string ActiveProvider { get; set; }
    public string DefaultCurrency { get; set; } = "USD";
    public double DefaultRiskFreeRate { get; set; } = 0.04;
    public string DefaultBenchmark { get; set; } = "SPY";
    public int StalePriceDays { get; set; } = 5;
    public DateTime UpdatedAtUtc { get; set; }
}
