using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Abstractions;

/// <summary>An article as parsed from a provider response, before normalization and classification.</summary>
/// <param name="RelatedSymbols">Canonical symbols the provider itself tagged the article with (e.g. Yahoo <c>relatedTickers</c>).</param>
/// <param name="SourceEventType">Event type read from the source's own structure (SEC form/item, statistics release); null → keyword rules.</param>
/// <param name="SourceEventBasis">What in the source set <paramref name="SourceEventType"/> (e.g. "8-K item 2.02").</param>
/// <param name="MatchMethod">How <paramref name="RelatedSymbols"/> were established; null → "&lt;provider&gt; ticker tag".</param>
public sealed record ProviderArticle(
    string Provider,
    string? ProviderId,
    string Url,
    string Headline,
    string Publisher,
    DateTime PublishedAtUtc,
    string? Summary,
    string? ImageUrl,
    IReadOnlyList<string> RelatedSymbols,
    bool IsOfficial = false,
    string? Region = null,
    NewsCategory? SourceCategory = null,
    bool IsSimulated = false,
    NewsEventType? SourceEventType = null,
    string? SourceEventBasis = null,
    string? MatchMethod = null);

/// <summary>A fixed feed (e.g. a central bank's press releases).</summary>
public sealed record NewsFeed(string Key, string Publisher, string Url, string Region, NewsCategory Category, bool IsOfficial, string Terms);

public sealed record NewsProviderDescriptor(string Name, string DisplayName, bool IsConfigured, bool IsSimulated, string Coverage, string Terms);

/// <summary>Company news for one listing.</summary>
public interface ICompanyNewsProvider
{
    NewsProviderDescriptor Descriptor { get; }

    /// <summary>Whether this source covers the listing at all (e.g. SEC filings exist only for US listings); unsupported symbols are never queried.</summary>
    bool Supports(string symbol) => true;

    /// <summary>Articles about <paramref name="symbol"/>; <paramref name="companyName"/> may be used as the search term.</summary>
    Task<IReadOnlyList<ProviderArticle>> GetCompanyNewsAsync(string symbol, string? companyName, CancellationToken ct);
}

/// <summary>Market-wide and macroeconomic feeds.</summary>
public interface INewsFeedProvider
{
    NewsProviderDescriptor Descriptor { get; }
    IReadOnlyList<NewsFeed> Feeds { get; }
    Task<IReadOnlyList<ProviderArticle>> GetFeedAsync(NewsFeed feed, CancellationToken ct);
}

public sealed class NewsProviderException(string provider, string message, Exception? inner = null) : Exception($"{provider}: {message}", inner)
{
    public string Provider { get; } = provider;
}
