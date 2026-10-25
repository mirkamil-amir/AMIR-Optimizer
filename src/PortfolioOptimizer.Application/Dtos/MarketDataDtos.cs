using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Dtos;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record AssetDto(
    int Id,
    string Symbol,
    string? Name,
    string? Exchange,
    string Currency,
    AssetType AssetType,
    string? Sector,
    bool IsEnabled);

public sealed record CreateAssetRequest
{
    /// <example>AAPL</example>
    [Required, RegularExpression(@"^[A-Za-z0-9.\-^=]{1,15}$", ErrorMessage = "Symbol must be 1-15 characters: letters, digits, '.', '-', '^' or '='.")]
    public string Symbol { get; init; } = "";

    /// <example>Apple Inc.</example>
    [StringLength(200)]
    public string? Name { get; init; }

    [StringLength(50)]
    public string? Exchange { get; init; }

    /// <summary>Trading currency (ISO 4217). Optional: derived from the exchange suffix (SAP.DE → EUR, 7203.T → JPY, no suffix → USD).</summary>
    /// <example>USD</example>
    [RegularExpression("^[A-Z]{3}$", ErrorMessage = "Currency must be an ISO 4217 code such as USD.")]
    public string? Currency { get; init; }

    public AssetType AssetType { get; init; } = AssetType.Equity;

    /// <example>Information Technology</example>
    [StringLength(100)]
    public string? Sector { get; init; }
}

public sealed record UpdateAssetRequest
{
    [StringLength(200)] public string? Name { get; init; }
    [StringLength(100)] public string? Sector { get; init; }
    public bool? IsEnabled { get; init; }
}

public sealed record PriceBarDto(
    DateOnly Date,
    decimal? Open,
    decimal? High,
    decimal? Low,
    decimal Close,
    decimal? AdjustedClose,
    long? Volume,
    string Source,
    bool IsSimulated,
    DateTime RetrievedAtUtc);

public sealed record SyncRequest
{
    /// <summary>Symbols to refresh. Empty = all enabled assets.</summary>
    public List<string> Symbols { get; init; } = [];

    /// <summary>First date to download. Defaults to 5 years ago.</summary>
    public DateOnly? StartDate { get; init; }

    /// <summary>Last date to download. Defaults to today.</summary>
    public DateOnly? EndDate { get; init; }

    /// <summary>Provider name (Yahoo, AlphaVantage, Mock). Defaults to the active provider.</summary>
    public string? Provider { get; init; }

    /// <summary>Re-download the whole range even if cached.</summary>
    public bool ForceFullRefresh { get; init; }
}

public sealed record SyncJobDto(
    Guid Id,
    JobStatus Status,
    string Source,
    IReadOnlyList<string> Symbols,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTime RequestedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    int AssetsTotal,
    int AssetsCompleted,
    int AssetsFailed,
    string? Message,
    IReadOnlyList<SyncLogDto> Logs);

public sealed record SyncLogDto(
    string Symbol,
    bool Success,
    int RowsReceived,
    int RowsInserted,
    int RowsUpdated,
    int RowsRejected,
    int DuplicatesSkipped,
    string? Message,
    DateTime CompletedAtUtc);

public enum DataFreshness { Fresh, Stale, Missing }

public sealed record AssetCoverageDto(
    int AssetId,
    string Symbol,
    string? Name,
    bool IsEnabled,
    string Source,
    bool IsSimulated,
    int Observations,
    DateOnly? FirstDate,
    DateOnly? LastDate,
    bool HasAdjustedClose,
    DateTime? LastRetrievedAtUtc,
    DataFreshness Freshness,
    decimal? LatestPrice,
    DateTime? LatestPriceAsOfUtc,
    string? LastSyncMessage,
    bool? LastSyncSuccess);

/// <summary>
/// Compact data status for the navigation (every page): freshness counts of the tracked instruments, the latest retrieval
/// and whether a download is running. The full per-instrument list is <see cref="MarketDataStatusDto"/>.
/// </summary>
public sealed record MarketDataSummaryDto(
    string ActiveProvider,
    bool ActiveProviderIsSimulated,
    int Fresh,
    int Stale,
    int Missing,
    DateTime? LastRetrievedAtUtc,
    bool SyncRunning,
    int StalePriceDays,
    DateTime AsOfUtc);

public sealed record MarketDataStatusDto(
    string ActiveProvider,
    bool ActiveProviderIsSimulated,
    IReadOnlyList<ProviderStatusDto> Providers,
    IReadOnlyList<AssetCoverageDto> Coverage,
    IReadOnlyList<SyncJobDto> RecentJobs,
    int StalePriceDays,
    DateTime GeneratedAtUtc);

public sealed record ProviderStatusDto(
    string Name,
    string DisplayName,
    bool IsSimulated,
    bool RequiresApiKey,
    bool IsConfigured,
    bool ProvidesAdjustedClose,
    string RateLimit,
    string Terms);

public sealed record SettingsDto(
    string ActiveProvider,
    string DefaultCurrency,
    double DefaultRiskFreeRate,
    string DefaultBenchmark,
    int StalePriceDays,
    IReadOnlyList<ProviderStatusDto> Providers,
    bool ScheduledRefreshEnabled,
    double ScheduledRefreshIntervalHours);

public sealed record UpdateSettingsRequest
{
    [Required] public string ActiveProvider { get; init; } = "";
    [Required, RegularExpression("^[A-Z]{3}$")] public string DefaultCurrency { get; init; } = "USD";
    [Range(-0.05, 0.25)] public double DefaultRiskFreeRate { get; init; }
    [Required, StringLength(15)] public string DefaultBenchmark { get; init; } = "SPY";
    [Range(1, 60)] public int StalePriceDays { get; init; } = 5;
}

/// <summary>The data an analysis is about to use: stocks (and benchmark) over [From, To], converted into Currency.</summary>
public sealed record DataCheckRequest
{
    public List<string> Symbols { get; init; } = [];
    public string? BenchmarkSymbol { get; init; }
    public DateOnly From { get; init; }
    /// <summary>Defaults to today.</summary>
    public DateOnly? To { get; init; }
    /// <summary>Analysis currency; null = resolved like the analysis does (portfolio base currency or the selection's).</summary>
    public string? Currency { get; init; }
}

/// <summary>
/// One stock's stored prices against the requested period. <see cref="NeedsOlder"/>: history starts later than requested;
/// <see cref="NeedsRecent"/>: the latest price is older than the stale threshold; <see cref="OlderUnavailable"/>: older
/// prices were already requested from the data source and it has none (e.g. the listing is younger), so downloading again
/// would not help.
/// </summary>
public sealed record MissingPriceDto(string Symbol, DateOnly? StoredFrom, DateOnly? StoredTo, int StoredPrices, bool NeedsOlder, bool NeedsRecent,
    bool OlderUnavailable, bool Downloadable, string Explanation);

/// <summary>Exchange rates of one currency not stored for the period (stored range and what is missing).</summary>
public sealed record MissingRateDto(string Currency, DateOnly? StoredFrom, DateOnly? StoredTo, DateOnly MissingFrom, DateOnly MissingTo, bool Downloadable,
    string Explanation);

/// <summary>
/// What is missing for an analysis, before it runs. <see cref="Complete"/>: nothing is missing. <see cref="Downloading"/>:
/// price downloads are queued/running or exchange rates are being downloaded – check again shortly.
/// </summary>
public sealed record DataCheckDto(string Currency, DateOnly From, DateOnly To, bool Complete, bool Downloadable, bool Downloading,
    IReadOnlyList<MissingPriceDto> Prices, IReadOnlyList<MissingRateDto> Rates, IReadOnlyList<string> Notes);

/// <summary>Downloads started for a data check: price jobs (poll /market-data/jobs/{id}) and currencies whose rates load in the background.</summary>
public sealed record DataDownloadDto(IReadOnlyList<Guid> SyncJobIds, IReadOnlyList<string> PriceSymbols, IReadOnlyList<string> Currencies);

/// <summary>Stored daily prices of one symbol (active source).</summary>
public sealed record HistoryCoverageDto(string Symbol, int Observations);

public sealed record HistoryCoverageRequest
{
    [Required, MinLength(1)] public List<string> Symbols { get; init; } = [];
}
