using System.Text;
using System.Text.RegularExpressions;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.News;

public enum AttentionLevel { Low, Medium, High }

public sealed record Attention(AttentionLevel Level, int Points, IReadOnlyList<string> Reasons);

/// <summary>What the grouping rule needs to know about an existing story.</summary>
public sealed record StoryCandidate(
    Guid Id, string Headline, NewsScope Scope, NewsEventType EventType, string? Region,
    DateTime FirstPublishedAtUtc, DateTime LastPublishedAtUtc, IReadOnlySet<int> AssetIds, IReadOnlySet<string> HeadlineKeys);

/// <summary>What the grouping rule needs to know about an article not yet in a story.</summary>
public sealed record StoryArticleFacts(
    string Headline, string HeadlineKey, NewsScope Scope, NewsEventType EventType, string? Region, DateTime PublishedAtUtc, IReadOnlySet<int> AssetIds);

/// <summary>
/// Story layer and attention rules (pure, deterministic, unit-tested; docs/financial-news.md › Stories and attention).
/// A change to any rule here means a new <see cref="RuleVersion"/>.
/// </summary>
public static partial class StoryRules
{
    /// <summary>2 (2026-11-08): market stories tagged with two or more market instruments get +1 (owner's decision, D-52).</summary>
    public const int RuleVersion = 2;

    /// <summary>Two reports more than this apart are never one story.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(36);

    /// <summary>Minimum share of headline words two reports must have in common (Jaccard index of their word sets).</summary>
    public const double MinOverlap = 0.5;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "after", "into", "over", "its", "his", "her", "their", "this", "that", "are", "was", "were",
        "has", "have", "had", "will", "says", "said", "amid", "about", "than", "but", "not", "you", "your", "what", "why", "how", "who",
    };

    /// <summary>Lower-case words of at least three letters or digits, without common stop words.</summary>
    public static IReadOnlySet<string> Words(string headline) =>
        WordPattern().Matches(headline.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 3 && !StopWords.Contains(w)).ToHashSet(StringComparer.Ordinal);

    public static double Overlap(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>
    /// An article joins a story when it was published within <see cref="Window"/> of the story's reports and either has the
    /// same headline words as one of them (a syndicated copy), or has the same scope and event type, concerns the same thing
    /// (a shared listing; for reports without listings, the same region) and shares at least half of its headline words.
    /// </summary>
    public static bool Belongs(StoryCandidate story, StoryArticleFacts article)
    {
        if (article.PublishedAtUtc < story.FirstPublishedAtUtc - Window || article.PublishedAtUtc > story.LastPublishedAtUtc + Window) return false;
        if (story.HeadlineKeys.Contains(article.HeadlineKey)) return true;
        if (story.Scope != article.Scope || story.EventType != article.EventType) return false;
        var sameSubject = story.AssetIds.Count > 0 || article.AssetIds.Count > 0
            ? story.AssetIds.Overlaps(article.AssetIds)
            : string.Equals(story.Region, article.Region, StringComparison.OrdinalIgnoreCase);
        return sameSubject && Overlap(Words(story.Headline), Words(article.Headline)) >= MinOverlap;
    }

    /// <summary>The best story for an article: highest headline overlap, then the most recent; null → a new story.</summary>
    public static StoryCandidate? Choose(IEnumerable<StoryCandidate> candidates, StoryArticleFacts article)
    {
        var words = Words(article.Headline);
        return candidates.Where(c => Belongs(c, article))
            .OrderByDescending(c => c.HeadlineKeys.Contains(article.HeadlineKey) ? 2 : Overlap(Words(c.Headline), words))
            .ThenByDescending(c => c.LastPublishedAtUtc).ThenBy(c => c.Id)
            .FirstOrDefault();
    }

    private static readonly HashSet<NewsEventType> MaterialTypes =
    [
        NewsEventType.Results, NewsEventType.Guidance, NewsEventType.MergerOrAcquisition, NewsEventType.Bankruptcy, NewsEventType.ManagementChange,
        NewsEventType.RateDecision, NewsEventType.InflationRelease, NewsEventType.LabourRelease, NewsEventType.GrowthRelease,
    ];

    private static readonly HashSet<NewsCategory> MaterialCategories =
        [NewsCategory.Earnings, NewsCategory.CorporateTransaction, NewsCategory.RegulatoryLegal, NewsCategory.CentralBank];

    /// <summary>Material: a material event type, or – for articles classified before event types existed – a material category.</summary>
    public static bool IsMaterial(NewsEventType type, NewsCategory category) =>
        MaterialTypes.Contains(type) || (type == NewsEventType.General && MaterialCategories.Contains(category));

    /// <summary>A market story about several instruments at once (e.g. an index and a currency) is about the market as a whole.</summary>
    public static bool IsBroadMarket(NewsEventType type, int marketInstruments) => type == NewsEventType.MarketNews && marketInstruments >= 2;

    /// <summary>
    /// Points that do not depend on time: material event +2, official source +1, two or more publishers +1, market story
    /// tagged with two or more market instruments +1.
    /// </summary>
    public static int BasePoints(NewsEventType type, NewsCategory category, bool official, int publishers, int marketInstruments = 0) =>
        (IsMaterial(type, category) ? 2 : 0) + (official ? 1 : 0) + (publishers >= 2 ? 1 : 0) + (IsBroadMarket(type, marketInstruments) ? 1 : 0);

    /// <summary>
    /// General attention: base points plus 1 when the latest report is at most 24 hours old. High ≥ 4, Medium 2–3,
    /// Low ≤ 1. A label of how material and well-supported a story is – never a prediction of a price move.
    /// </summary>
    public static Attention Assess(NewsEventType type, NewsCategory category, bool official, int publishers, DateTime lastPublishedAtUtc, DateTime nowUtc,
        int marketInstruments = 0)
    {
        var reasons = new List<string>();
        var points = 0;
        if (IsMaterial(type, category))
        {
            points += 2;
            reasons.Add($"{(type != NewsEventType.General ? NewsTaxonomy.Label(type) : NewsRelevance.Label(category))} is a material event type (+2)");
        }
        if (official) { points += 1; reasons.Add("published by the issuing institution or company (+1)"); }
        if (publishers >= 2) { points += 1; reasons.Add($"reported by {publishers} publishers (+1)"); }
        if (IsBroadMarket(type, marketInstruments)) { points += 1; reasons.Add($"concerns {marketInstruments} market instruments at once (+1)"); }
        if (nowUtc - lastPublishedAtUtc <= TimeSpan.FromHours(24)) { points += 1; reasons.Add("latest report within 24 hours (+1)"); }
        if (reasons.Count == 0) reasons.Add("no attention criterion met");
        return new Attention(Level(points), points, reasons);
    }

    public static AttentionLevel Level(int points) => points >= 4 ? AttentionLevel.High : points >= 2 ? AttentionLevel.Medium : AttentionLevel.Low;

    /// <summary>Minimum base points for a level, with and without the recency point (used to filter in SQL).</summary>
    public static (int Recent, int Older) MinBasePoints(AttentionLevel level) => level switch
    {
        AttentionLevel.High => (3, 4),
        AttentionLevel.Medium => (1, 2),
        _ => (0, 0),
    };

    /// <summary>
    /// Currency whose central bank publishes from a region (the four central-bank feeds). My Watchlist shows a rate decision
    /// of that bank when a watched listing trades in its currency.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CentralBankCurrency = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "USD",
        ["EU"] = "EUR",
        ["GB"] = "GBP",
        ["JP"] = "JPY",
    };

    /// <summary>Opaque keyset cursor: the time and id of the last story of a page.</summary>
    public static string EncodeCursor(DateTime atUtc, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{atUtc.Ticks}:{id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecodeCursor(string? cursor, out DateTime atUtc, out Guid id)
    {
        atUtc = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 100) return false;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || ticks < 0 || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "N", out id))
                return false;
            atUtc = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
