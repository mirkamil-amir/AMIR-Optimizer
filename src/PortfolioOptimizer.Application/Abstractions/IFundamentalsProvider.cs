namespace PortfolioOptimizer.Application.Abstractions;

/// <summary>One reported figure as supplied by the source (see <see cref="Domain.FundamentalFact"/>).</summary>
public sealed record ProviderFundamentalFact(string Concept, string PeriodType, DateOnly PeriodEnd, decimal Value, string? Currency);

/// <summary>The figures of one listing, or the reason there are none.</summary>
public sealed record ProviderFundamentals(bool Found, IReadOnlyList<ProviderFundamentalFact> Facts, string? NotFoundReason = null);

/// <summary>
/// Reported financial figures (income statement, balance sheet, cash flow, market capitalization) per listing.
/// Implementations throw <see cref="FundamentalsProviderException"/> for transport errors, rate limits and malformed responses.
/// </summary>
public interface IFundamentalsProvider
{
    string Name { get; }
    /// <summary>Display name of the source (shown to administrators only, D-30).</summary>
    string SourceName { get; }
    bool IsConfigured { get; }
    bool IsSimulated { get; }
    Task<ProviderFundamentals> GetAsync(string symbol, CancellationToken ct);
}

public sealed class FundamentalsProviderException(string provider, string message, bool isRateLimit = false, Exception? inner = null)
    : Exception($"{provider}: {message}", inner)
{
    public bool IsRateLimit { get; } = isRateLimit;
}

/// <summary>
/// Queues listings whose figures are missing or due; a background worker fetches them one after another. Listings of a
/// page someone is looking at go before those of the scheduled full refresh (background = true).
/// </summary>
public interface IFundamentalsRefreshScheduler
{
    void Schedule(IEnumerable<int> assetIds, bool background = false);
    /// <summary>True while the listing waits in the queue or is being fetched.</summary>
    bool IsQueued(int assetId);
}
