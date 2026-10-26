using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Dtos;

public sealed record SectorDto(string Name, int StockCount, int WithPriceData);

/// <summary>An index universe (constituent list) with its provenance and data coverage for the active source.</summary>
public sealed record MarketIndexDto(
    string Code,
    string Name,
    string Country,
    string Currency,
    string? BenchmarkSymbol,
    bool IsHistorical,
    string? Description,
    int Members,
    int MembersWithPriceHistory,
    int MembersWithQuotes,
    string Source,
    string? SourceUrl,
    string? SourceRevision,
    string? Licence,
    DateOnly? VerifiedOn);

/// <summary>A market (country) and its index universes.</summary>
/// <param name="Instruments">Stocks and ETFs of the market: its listings plus members of its indices listed elsewhere – the
/// rows the stock chooser shows for it (indices themselves excluded). Unlike summing index members, a stock in two indices,
/// in a historical index only, or in none (e.g. SPY) counts once.</param>
public sealed record MarketDto(string Country, string Name, IReadOnlyList<MarketIndexDto> Indices, int Instruments = 0);

/// <summary>A background quote refresh cycle (started by a sign-in, the scheduler or an administrator).</summary>
public sealed record QuoteRefreshRunDto(
    Guid Id,
    string Trigger,
    string Source,
    JobStatus Status,
    DateTime RequestedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    int SymbolsRequested,
    int Updated,
    int Unchanged,
    int Failed,
    string? Message,
    bool Reused);

public sealed record RefreshQuotesRequest
{
    /// <summary>Canonical symbols to refresh (the stocks visible on a page). At most 100.</summary>
    [Required, MinLength(1), MaxLength(100)] public List<string> Symbols { get; init; } = [];
    /// <summary>Ignore the cache window (a short cooldown still applies).</summary>
    public bool Force { get; init; }
}

public sealed record QuoteRefreshItemDto(string Symbol, string Outcome, string? Message);

/// <summary>Per-symbol outcomes plus the refreshed rows for the stock browser.</summary>
public sealed record QuoteRefreshResultDto(IReadOnlyList<QuoteRefreshItemDto> Items, IReadOnlyList<StockListItemDto> Stocks);

public sealed record UniverseImportResultDto(string Index, int Members, int Added, int Removed, int NewInstruments, int UpdatedInstruments, IReadOnlyList<string> Errors);

public sealed record ImportUniverseRequest
{
    /// <summary>CSV with header symbol,name,country,exchange,mic,currency,sector,industry,isin (canonical symbols, e.g. SAP.DE).</summary>
    [Required, StringLength(2_000_000, MinimumLength = 10)] public string Csv { get; init; } = "";
    /// <summary>Required for a new index; existing indices keep their metadata when omitted.</summary>
    [StringLength(100)] public string? Name { get; init; }
    [StringLength(2, MinimumLength = 2)] public string? Country { get; init; }
    [StringLength(3, MinimumLength = 3)] public string? Currency { get; init; }
    [StringLength(20)] public string? BenchmarkSymbol { get; init; }
    public bool? IsHistorical { get; init; }
    [StringLength(1000)] public string? Description { get; init; }
    /// <summary>Where the membership list comes from, e.g. "STOXX DAX composition file 2026-10-01".</summary>
    [Required, StringLength(300, MinimumLength = 3)] public string Source { get; init; } = "";
    [StringLength(500)] public string? SourceUrl { get; init; }
    [StringLength(50)] public string? SourceRevision { get; init; }
    [StringLength(100)] public string? Licence { get; init; }
    public DateOnly? VerifiedOn { get; init; }
}

/// <summary>How a displayed price was obtained. "Live" is never claimed: supported providers are delayed or end-of-day.</summary>
public enum QuoteKind
{
    /// <summary>No price stored for the active data source yet.</summary>
    None,
    /// <summary>Last intraday/last-trade quote reported by the provider; typically delayed and stored when last synchronized.</summary>
    DelayedQuote,
    /// <summary>Price of the last completed (or no longer trading) session: a stored daily close or a provider's last price of that day.</summary>
    LastClose,
}

/// <summary>Latest available price with full provenance.</summary>
public sealed record StockQuoteDto(
    string Symbol,
    decimal? Price,
    string Currency,
    QuoteKind Kind,
    DateOnly? PriceDate,
    DateTime? AsOfUtc,
    decimal? PreviousClose,
    DateOnly? PreviousCloseDate,
    decimal? Change,
    double? ChangePercent,
    string? Provider,
    DateTime? RetrievedAtUtc,
    bool IsSimulated,
    bool IsStale,
    /// <summary>The price was retrieved longer ago than the cache window (it may be refreshed).</summary>
    bool IsCached,
    string Description);

public sealed record StockListItemDto(
    int Id,
    string Symbol,
    string? Name,
    string? Sector,
    string? Industry,
    string? Exchange,
    string? Country,
    IReadOnlyList<string> Indices,
    string Currency,
    AssetType AssetType,
    bool IsTracked,
    int Observations,
    DateOnly? FirstDate,
    DateOnly? LastDate,
    StockQuoteDto Quote);

/// <summary>Stock metadata; the company description has its own endpoint (<c>/stocks/{symbol}/profile</c>).</summary>
public sealed record StockDetailDto(
    StockListItemDto Stock,
    string? MetadataSource,
    DateTime? MetadataUpdatedAtUtc,
    bool HasAdjustedCloses,
    string DataSource,
    string? Isin,
    string? Mic);

public sealed record ImportCatalogueRequest
{
    /// <summary>CSV with header: symbol,name,sector,industry,exchange,currency,type (lines starting with # are comments).</summary>
    [Required, StringLength(2_000_000, MinimumLength = 10)] public string Csv { get; init; } = "";

    /// <summary>Provenance recorded on each instrument, e.g. "Vendor X universe file, 2026-10-01".</summary>
    [Required, StringLength(300, MinimumLength = 3)] public string Source { get; init; } = "";

    /// <summary>Mark imported instruments as tracked (included in scheduled price refreshes). Default: catalogue only.</summary>
    public bool Track { get; init; }

    /// <summary>Overwrite name/sector/industry of existing instruments. Default: only fill missing values.</summary>
    public bool OverwriteMetadata { get; init; }
}

public sealed record CatalogueImportResultDto(int Rows, int Inserted, int Updated, int Unchanged, IReadOnlyList<string> Errors);
