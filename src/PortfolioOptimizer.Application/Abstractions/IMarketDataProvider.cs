namespace PortfolioOptimizer.Application.Abstractions;

/// <summary>A normalized daily bar returned by a provider. Prices are in <see cref="ProviderHistory.Currency"/>.</summary>
public sealed record ProviderBar(
    DateOnly Date,
    decimal? Open,
    decimal? High,
    decimal? Low,
    decimal Close,
    decimal? AdjustedClose,
    long? Volume);

/// <summary>Historical data plus metadata and data-quality counters from one provider request.</summary>
public sealed record ProviderHistory(
    string Symbol,
    string Currency,
    IReadOnlyList<ProviderBar> Bars,
    int RawRowCount,
    int RejectedRows,
    int DuplicateRows,
    IReadOnlyList<string> Warnings,
    string? CompanyName = null,
    string? Exchange = null,
    string? InstrumentType = null);

public sealed record MarketQuote(string Symbol, decimal Price, string Currency, DateTime AsOfUtc);

/// <summary>Static description of a provider for the UI and documentation. Never contains secrets.</summary>
public sealed record ProviderDescriptor(
    string Name,
    string DisplayName,
    bool IsSimulated,
    bool RequiresApiKey,
    bool IsConfigured,
    bool ProvidesAdjustedClose,
    string RateLimit,
    string Terms);

/// <summary>Provider-independent market data access.</summary>
public interface IMarketDataProvider
{
    ProviderDescriptor Descriptor { get; }

    Task<ProviderHistory> GetHistoricalPricesAsync(string symbol, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);

    Task<MarketQuote?> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken);
}

public interface IMarketDataProviderRegistry
{
    IReadOnlyList<ProviderDescriptor> Providers { get; }

    /// <exception cref="Common.ValidationException">Unknown or unconfigured provider.</exception>
    IMarketDataProvider Get(string name);
}

/// <summary>Raised by providers for failed requests (network errors, throttling, unknown symbols, malformed responses).</summary>
public sealed class MarketDataProviderException(string provider, string symbol, string message, bool isTransient = false, Exception? inner = null)
    : Exception($"{provider} request for {symbol} failed: {message}", inner)
{
    public string Provider { get; } = provider;
    public string Symbol { get; } = symbol;
    public bool IsTransient { get; } = isTransient;
}
