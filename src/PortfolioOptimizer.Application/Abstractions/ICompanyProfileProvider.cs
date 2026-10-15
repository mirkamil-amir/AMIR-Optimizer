namespace PortfolioOptimizer.Application.Abstractions;

/// <summary>A description as supplied by the source, or the reason there is none.</summary>
/// <param name="Found">False when the source has no usable text (missing page, disambiguation page, empty extract).</param>
public sealed record ProviderCompanyProfile(
    bool Found,
    string? Description,
    string? SourceUrl,
    string? Language,
    string? Title,
    string? NotFoundReason = null);

/// <summary>
/// Company descriptions resolved from a catalogue reference (e.g. "en:Apple Inc."). Implementations throw
/// <see cref="CompanyProfileProviderException"/> for transport errors, rate limits and malformed responses.
/// </summary>
public interface ICompanyProfileProvider
{
    string Name { get; }
    /// <summary>Display name of the source, e.g. "Wikipedia".</summary>
    string SourceName { get; }
    string Licence { get; }
    bool IsConfigured { get; }
    Task<ProviderCompanyProfile> GetAsync(string reference, CancellationToken ct);
}

public sealed class CompanyProfileProviderException(string provider, string message, bool isRateLimit = false, Exception? inner = null)
    : Exception($"{provider}: {message}", inner)
{
    public bool IsRateLimit { get; } = isRateLimit;
}
