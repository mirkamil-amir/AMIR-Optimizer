using System.ComponentModel.DataAnnotations;

namespace PortfolioOptimizer.Application.Dtos;

public sealed record PricePointDto(DateOnly Date, decimal Price);

/// <summary>
/// Stored daily prices of one stock for a chart range, with the period change and its basis. <see cref="Change"/> is
/// null (with <see cref="Limitation"/>) when the stored history does not cover the range; nothing is extrapolated.
/// </summary>
public sealed record PriceHistoryDto(
    string Symbol,
    string? Name,
    string Currency,
    string Exchange,
    string Range,
    bool Supported,
    /// <summary>"Adjusted close" (splits and dividends, total return) or "Close" (raw).</summary>
    string Basis,
    IReadOnlyList<PricePointDto> Points,
    DateOnly? BaseDate,
    decimal? BasePrice,
    DateOnly? EndDate,
    decimal? EndPrice,
    double? Change,
    DateOnly? AvailableFrom,
    string? Limitation,
    string Source,
    bool IsSimulated,
    DateTime? LastRetrievedAtUtc,
    IReadOnlyList<string> SupportedRanges);

/// <summary>Change over the selected range plus a downsampled sparkline (same basis as the stock's history chart).</summary>
public sealed record PeriodChangeDto(
    string Range,
    double? Change,
    DateOnly? BaseDate,
    DateOnly? EndDate,
    string Basis,
    string? Limitation,
    IReadOnlyList<PricePointDto> Sparkline);

public sealed record WatchlistItemDto(Guid Id, DateTime AddedAtUtc, StockListItemDto Stock, PeriodChangeDto Period);

public sealed record WatchlistDto(IReadOnlyList<WatchlistItemDto> Items, string Range, int MaxEntries, string Note);

/// <summary>The caller's complete watchlist in the new order (every watched symbol exactly once).</summary>
public sealed record ReorderWatchlistRequest
{
    [Required, MinLength(1), MaxLength(100)] public List<string> Symbols { get; init; } = [];
}

public sealed record AddWatchlistEntryRequest
{
    /// <summary>Canonical catalogue symbol including the venue suffix (e.g. SAP.DE, 7203.T, AAPL).</summary>
    [Required, StringLength(20, MinimumLength = 1)] public string Symbol { get; init; } = "";
}

/// <summary>The added entry; <see cref="HistoryQueued"/> tells the UI that a price-history download was started.</summary>
public sealed record WatchlistAddResultDto(WatchlistItemDto Item, bool HistoryQueued);

/// <summary>A stock's change and sparkline over a range for the stock list (same calculation as the watchlist).</summary>
public sealed record StockTrendDto(string Symbol, PeriodChangeDto Period);
