namespace PortfolioOptimizer.Domain;

/// <summary>Event type assigned by the rule-based classifier (see docs/financial-news.md). <see cref="General"/> when no rule matched.</summary>
public enum NewsCategory
{
    General,
    Earnings,
    CorporateTransaction,
    RegulatoryLegal,
    CentralBank,
    Macroeconomic,
    Geopolitical,
    CommoditiesEnergy,
    CurrenciesBonds,
    MarketIndex,
}

/// <summary>Breadth of an article: tied to listed companies, to a whole market, or to the economy.</summary>
public enum NewsScope { Company, Market, Macro }

/// <summary>
/// Finer event type (classification rules version 2, docs/financial-news.md › Taxonomy). Official sources set it from their
/// own structure (SEC form and 8-K item, statistics release title); other articles from headline keywords (a heuristic).
/// </summary>
public enum NewsEventType
{
    General,
    // Company
    Results,
    PeriodicReport,
    Guidance,
    DividendOrBuyback,
    MergerOrAcquisition,
    MaterialAgreement,
    ManagementChange,
    CapitalRaise,
    LegalOrRegulatory,
    Bankruptcy,
    OtherFiling,
    // Macro
    RateDecision,
    PolicyStatement,
    InflationRelease,
    LabourRelease,
    GrowthRelease,
    ActivityRelease,
    TradeOrFiscalRelease,
    OtherRelease,
    // Market
    MarketNews,
}

/// <summary>Part of the financial system a market-level article concerns, from the kind of instruments it is tagged with.</summary>
public enum NewsMarketArea { Equities, Rates, Fx, Commodities, Volatility }

/// <summary>
/// A news item as delivered by a provider, normalized and shared by all users (never copied per user). Only metadata and
/// the provider's short summary are stored – never full article text. <see cref="Fingerprint"/> (canonical URL hash) is
/// unique; <see cref="HeadlineKey"/> groups syndicated copies of the same story.
/// </summary>
public class NewsArticle
{
    public Guid Id { get; set; }
    public required string Provider { get; set; }
    public string? ProviderArticleId { get; set; }
    public required string Url { get; set; }
    public required string Fingerprint { get; set; }
    public required string HeadlineKey { get; set; }
    public required string Headline { get; set; }
    public required string Publisher { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
    /// <summary>Short summary exactly as supplied by the provider (truncated), or null. Never generated.</summary>
    public string? Summary { get; set; }
    /// <summary>Thumbnail URL supplied by the provider for this article (displayed only when images are enabled).</summary>
    public string? ImageUrl { get; set; }
    public NewsCategory Category { get; set; }
    /// <summary>Why the category was assigned (e.g. "headline mentions 'acquisition'").</summary>
    public required string CategoryBasis { get; set; }
    public NewsScope Scope { get; set; }
    /// <summary>ISO country of the market/economy the item concerns, when known (central-bank feeds, listing venue).</summary>
    public string? Region { get; set; }
    /// <summary>Published by the issuing institution itself (central bank, statistics office).</summary>
    public bool IsOfficialSource { get; set; }
    public bool IsSimulated { get; set; }
    /// <summary>Event type from classification rules version ≥ 2; null for articles classified before (version 1).</summary>
    public NewsEventType? EventType { get; set; }
    /// <summary>Why the event type was assigned (source structure or matched term).</summary>
    public string? EventTypeBasis { get; set; }
    /// <summary>Market area for market-level articles (all tags are index, rate, currency, commodity or volatility instruments).</summary>
    public NewsMarketArea? MarketArea { get; set; }
    /// <summary>Version of the classification rules that set Category, Scope, EventType and MarketArea (1 = before 2026-11).</summary>
    public int RuleVersion { get; set; } = 1;
    /// <summary>When Matrix normalized and classified the article (≥ retrieval; publication time is never replaced).</summary>
    public DateTime? ProcessedAtUtc { get; set; }
    public List<NewsArticleAsset> Assets { get; set; } = [];
}

/// <summary>
/// One event as reported by one or more articles (story layer, docs/financial-news.md › Stories). Built by the versioned
/// grouping rule from stored articles; keeps every contributing article (<see cref="Articles"/>) and when it joined, so
/// the story as known at any past time can be reconstructed. Facts of the lead (earliest) article are copied for fast,
/// indexed feed queries; nothing here is generated text.
/// </summary>
public class NewsStory
{
    public Guid Id { get; set; }
    public Guid LeadArticleId { get; set; }
    public required string Headline { get; set; }
    public NewsScope Scope { get; set; }
    public NewsCategory Category { get; set; }
    public NewsEventType EventType { get; set; }
    public string? EventTypeBasis { get; set; }
    public NewsMarketArea? MarketArea { get; set; }
    public string? Region { get; set; }
    public DateTime FirstPublishedAtUtc { get; set; }
    public DateTime LastPublishedAtUtc { get; set; }
    public int ArticleCount { get; set; }
    public int PublisherCount { get; set; }
    public bool HasOfficialSource { get; set; }
    public bool IsSimulated { get; set; }
    /// <summary>Time-independent attention points (material event, official source, several publishers); recency is added at query time.</summary>
    public int BasePoints { get; set; }
    /// <summary>Version of the grouping and attention rules that built this story.</summary>
    public int RuleVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<NewsStoryArticle> Articles { get; set; } = [];
    public List<NewsStoryAsset> Assets { get; set; } = [];
}

/// <summary>An article's membership in a story (each article belongs to exactly one story).</summary>
public class NewsStoryArticle
{
    public Guid StoryId { get; set; }
    public Guid ArticleId { get; set; }
    public NewsArticle? Article { get; set; }
    public DateTime JoinedAtUtc { get; set; }
}

/// <summary>Catalogue listing linked to a story (union of its articles' links), for indexed watchlist and company queries.</summary>
public class NewsStoryAsset
{
    public Guid StoryId { get; set; }
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
}

/// <summary>Link between an article and a catalogue listing, with how it was established (provider tag, never a fuzzy name match).</summary>
public class NewsArticleAsset
{
    public Guid ArticleId { get; set; }
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    public required string MatchMethod { get; set; }
}

/// <summary>Last fetch of one news source or query (shared across users; bounds outbound requests and reports failures).</summary>
public class NewsFetchState
{
    public required string Key { get; set; }
    public required string Provider { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public int LastArticleCount { get; set; }
    public string? LastError { get; set; }
}
