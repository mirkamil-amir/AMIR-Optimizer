namespace PortfolioOptimizer.Application.Dtos;

/// <summary>One report of a story: who published it, when, where (provenance; the full text stays at the link).</summary>
public sealed record WireSourceItemDto(Guid ArticleId, string Publisher, string Url, DateTime PublishedAtUtc, bool IsOfficial);

/// <summary>A listing linked to a story by its provider tag or an official identifier, with what it means to the viewer.</summary>
public sealed record WireAssetDto(string Symbol, string? Name, string AssetType, string Currency, bool OnWatchlist, bool Held);

/// <summary>Rule-based attention (High / Medium / Low) with every reason; never a prediction of a price move.</summary>
public sealed record WireAttentionDto(string Level, int Points, IReadOnlyList<string> Reasons, string Note);

/// <summary>
/// One event as reported by one or more articles. Headline, link and publisher are those of the earliest report; Sources
/// lists every report. Why says why a personal feed shows it (empty in Latest, Market-wide and Macroeconomic).
/// </summary>
public sealed record WireStoryDto(
    Guid Id,
    string Headline,
    string Url,
    string Publisher,
    DateTime FirstPublishedAtUtc,
    DateTime LastPublishedAtUtc,
    string Scope,
    string EventType,
    string EventTypeLabel,
    string? EventTypeBasis,
    string? MarketArea,
    string? Region,
    bool IsOfficial,
    bool IsSimulated,
    string? Summary,
    int ArticleCount,
    IReadOnlyList<WireSourceItemDto> Sources,
    IReadOnlyList<WireAssetDto> Assets,
    WireAttentionDto Attention,
    IReadOnlyList<string> Why,
    int RuleVersion);

/// <summary>Freshness of one group of sources: Fresh, Delayed (no success for twice its interval) or Unavailable (never succeeded).</summary>
public sealed record WireSourceStatusDto(string Group, string Status, DateTime? LastSuccessUtc, string? Problem);

/// <summary>Latest, My Watchlist and Macroeconomic: stories newest first; NextCursor fetches the next page (null at the end).</summary>
public sealed record WireFeedDto(
    string Feed,
    IReadOnlyList<WireStoryDto> Stories,
    string? NextCursor,
    DateTime AsOfUtc,
    int WindowHours,
    IReadOnlyList<WireSourceStatusDto> Sources,
    IReadOnlyList<string> Notes,
    bool Refreshing);

/// <summary>A company in Company News: its newest stories and how many it has in the window.</summary>
public sealed record WireCompanyDto(
    string Symbol,
    string? Name,
    string? Sector,
    string Currency,
    bool OnWatchlist,
    bool Held,
    bool Curated,
    int StoryCount,
    DateTime NewestAtUtc,
    IReadOnlyList<WireStoryDto> Stories);

public sealed record WireCompaniesDto(
    IReadOnlyList<WireCompanyDto> Companies,
    string? NextCursor,
    DateTime AsOfUtc,
    int WindowHours,
    IReadOnlyList<WireSourceStatusDto> Sources,
    IReadOnlyList<string> Notes,
    bool Refreshing);

/// <summary>
/// What a story concerns in one portfolio, as a fact from the current valuation: a holding's weight (market value ÷ total
/// value, cash included) or the portfolio's share in a currency (positions and cash in it ÷ total value). Weight is a
/// fraction; null when the portfolio cannot be valued (see the response's Problems). Never a predicted effect.
/// </summary>
public sealed record WireExposureDto(string Kind, string Label, double? Weight, string Basis);

public sealed record PortfolioNewsItemDto(WireStoryDto Story, IReadOnlyList<WireExposureDto> Exposure);

/// <summary>News about one portfolio's holdings and currencies, newest first, with the exposure of each story.</summary>
public sealed record PortfolioNewsDto(
    Guid PortfolioId,
    string Currency,
    IReadOnlyList<PortfolioNewsItemDto> Items,
    string? NextCursor,
    DateTime AsOfUtc,
    int WindowHours,
    IReadOnlyList<WireSourceStatusDto> Sources,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Problems,
    bool Refreshing);

/// <summary>One market area of Market-wide (Equities, Rates, Fx, Commodities, Volatility).</summary>
public sealed record WireMarketSectionDto(string Area, string Label, int StoryCount, IReadOnlyList<WireStoryDto> Stories, string? NextCursor);

public sealed record WireMarketsDto(
    IReadOnlyList<WireMarketSectionDto> Sections,
    DateTime AsOfUtc,
    int WindowHours,
    IReadOnlyList<WireSourceStatusDto> Sources,
    IReadOnlyList<string> Notes,
    bool Refreshing);
