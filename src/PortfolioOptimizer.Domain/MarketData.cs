namespace PortfolioOptimizer.Domain;

/// <summary>An instrument in the investable universe.</summary>
public class Asset
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public string? Name { get; set; }
    public string? Exchange { get; set; }
    public string Currency { get; set; } = "USD";
    public AssetType AssetType { get; set; } = AssetType.Equity;
    public string? Sector { get; set; }
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

/// <summary>Latest intraday/last-trade quote per asset and source.</summary>
public class LatestQuote
{
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    public required string Source { get; set; }
    public decimal Price { get; set; }
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
