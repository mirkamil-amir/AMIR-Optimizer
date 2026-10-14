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

/// <summary>
/// Latest price reported by a provider, in the ISO currency (minor units such as pence already converted).
/// <see cref="AsOfUtc"/> is the time of the last trade/bar the provider reported, not the retrieval time.
/// </summary>
public sealed record MarketQuote(string Symbol, decimal Price, string Currency, DateTime AsOfUtc, decimal? PreviousClose = null);

/// <summary>Result for one symbol of a (batch) quote request; exactly one of <see cref="Quote"/> or <see cref="Error"/> is set.</summary>
public sealed record QuoteFetch(string Symbol, MarketQuote? Quote, string? Error, bool IsUnsupported = false, bool IsTransient = false)
{
    public static QuoteFetch Ok(string symbol, MarketQuote quote) => new(symbol, quote, null);
    public static QuoteFetch Fail(string symbol, string error, bool transient = false) => new(symbol, null, error, IsTransient: transient);
    public static QuoteFetch Unsupported(string symbol, string reason) => new(symbol, null, reason, IsUnsupported: true);
}

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

    /// <summary>Whether the provider covers this canonical symbol (e.g. Alpha Vantage has no Tokyo listings).</summary>
    bool Supports(string symbol);

    /// <summary>Maximum symbols per <see cref="GetLatestQuotesAsync"/> request; 1 means the provider has no batch endpoint.</summary>
    int QuoteBatchSize { get; }

    /// <summary>Requests left today under a known daily quota (Alpha Vantage free tier), or null when not limited.</summary>
    int? RemainingDailyRequests { get; }

    /// <summary>
    /// Latest quotes for up to <see cref="QuoteBatchSize"/> symbols. Failures are reported per symbol and never replaced
    /// by fabricated values.
    /// </summary>
    Task<IReadOnlyList<QuoteFetch>> GetLatestQuotesAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken);
}

/// <summary>Shared fallback for providers without a batch quote endpoint.</summary>
public static class QuoteFetching
{
    public static async Task<IReadOnlyList<QuoteFetch>> OneByOneAsync(IMarketDataProvider provider, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var results = new List<QuoteFetch>(symbols.Count);
        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();
            if (!provider.Supports(symbol))
            {
                results.Add(QuoteFetch.Unsupported(symbol, $"{provider.Descriptor.DisplayName} does not cover this market."));
                continue;
            }
            try
            {
                var quote = await provider.GetLatestQuoteAsync(symbol, ct);
                results.Add(quote is null ? QuoteFetch.Fail(symbol, "The provider returned no price.") : QuoteFetch.Ok(symbol, quote));
            }
            catch (MarketDataProviderException ex)
            {
                results.Add(QuoteFetch.Fail(symbol, ex.Message, ex.IsTransient));
            }
            catch (HttpRequestException ex)
            {
                results.Add(QuoteFetch.Fail(symbol, $"Network error: {ex.Message}", transient: true));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                results.Add(QuoteFetch.Fail(symbol, "The provider did not answer in time.", transient: true));
            }
        }
        return results;
    }
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
