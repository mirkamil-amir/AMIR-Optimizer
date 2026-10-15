namespace PortfolioOptimizer.Application.Dtos;

public sealed record NewsAssetDto(string Symbol, string? Name, string MatchMethod, bool OnWatchlist);

public sealed record NewsRelevanceDto(string Level, int Points, IReadOnlyList<string> Reasons, string Note);

/// <summary>
/// A normalized article: metadata, the provider's short summary (never full text) and a link to the original. The
/// image is only included when the operator enabled images (<c>News:ShowImages</c>); <see cref="ImageCredit"/> names
/// where it came from.
/// </summary>
public sealed record NewsArticleDto(
    Guid Id,
    string Headline,
    string Publisher,
    string Url,
    DateTime PublishedAtUtc,
    DateTime RetrievedAtUtc,
    string? Summary,
    string? ImageUrl,
    string? ImageCredit,
    string Category,
    string CategoryBasis,
    string Scope,
    string? Region,
    bool IsOfficialSource,
    bool IsSimulated,
    string Provider,
    IReadOnlyList<NewsAssetDto> Assets,
    NewsRelevanceDto Relevance,
    /// <summary>Other publishers that carried the same headline (syndication/corroboration).</summary>
    IReadOnlyList<string> AlsoReportedBy);

public sealed record NewsSourceStatusDto(
    string Key,
    string Provider,
    string DisplayName,
    bool IsConfigured,
    bool IsOfficial,
    bool IsSimulated,
    string Coverage,
    string Terms,
    DateTime? LastAttemptUtc,
    DateTime? LastSuccessUtc,
    int LastArticleCount,
    string? LastError);

public sealed record NewsFeedDto(
    IReadOnlyList<NewsArticleDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<NewsSourceStatusDto> Sources,
    bool ImagesEnabled,
    IReadOnlyList<string> Notes);

public sealed record EventHorizonDto(int TradingDays, DateOnly? Date, double? StockChange, double? BenchmarkChange, double? Difference, string? Limitation);

/// <summary>Event and price-movement comparison around one article (not evidence of causation).</summary>
public sealed record EventComparisonDto(
    Guid ArticleId,
    string Headline,
    string Symbol,
    string Currency,
    string? BenchmarkSymbol,
    string Basis,
    string Source,
    bool IsSimulated,
    DateTime PublishedAtUtc,
    string PublishedLocal,
    string Timing,
    DateOnly? ReferenceDate,
    double? ReferencePrice,
    IReadOnlyList<EventHorizonDto> Horizons,
    IReadOnlyList<NewsArticleDto> OverlappingArticles,
    IReadOnlyList<string> Limitations,
    string Disclaimer);
