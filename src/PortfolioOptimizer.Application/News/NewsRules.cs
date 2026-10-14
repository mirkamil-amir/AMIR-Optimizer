using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.News;

/// <summary>URL canonicalization and duplicate keys (pure).</summary>
public static partial class NewsNormalizer
{
    private static readonly string[] TrackingPrefixes = ["utm_", "guccounter", "guce_", "ncid", "cmpid", "src", "soc_"];

    /// <summary>Lower-case scheme/host, no fragment, no tracking parameters, no trailing slash; http and https are the same story.</summary>
    public static string CanonicalUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return url.Trim();
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !TrackingPrefixes.Any(t => p.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var path = uri.AbsolutePath.Replace("//", "/").TrimEnd('/');
        return $"{uri.Host.ToLowerInvariant()}{path}{(query.Count > 0 ? "?" + string.Join('&', query) : "")}";
    }

    public static string Fingerprint(string url) => Hash(CanonicalUrl(url));

    /// <summary>Lower-case words of the headline (punctuation removed): syndicated copies of a story share it.</summary>
    public static string HeadlineKey(string headline) => Hash(string.Join(' ', Words().Matches(headline.ToLowerInvariant()).Select(m => m.Value)));

    public static string Clean(string text) => Whitespace().Replace(System.Net.WebUtility.HtmlDecode(Tags().Replace(text, " ")), " ").Trim();

    /// <summary>Provider summaries are shortened to a teaser; the full article stays at the publisher.</summary>
    public static string? Teaser(string? summary, int max = 320)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var s = Clean(summary);
        if (s.Length <= max) return s;
        var cut = s.LastIndexOf(' ', max);
        return s[..(cut > max / 2 ? cut : max)] + "…";
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex Words();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}

public sealed record Classification(NewsCategory Category, string Basis);

/// <summary>
/// Rule-based event classification on the headline and summary (pure, deterministic, unit-tested). The first matching
/// rule wins, in the order below; the basis names the matched term. It is a keyword heuristic, not a trained model, and
/// can misclassify (e.g. "results" in a non-earnings context). An official feed's own category is kept.
/// </summary>
public static partial class NewsClassifier
{
    // Terms match whole words (case-insensitive); a trailing * matches a word prefix (commodit* → commodity, commodities).
    private static readonly (NewsCategory Category, string[] Terms)[] Rules =
    [
        (NewsCategory.CentralBank, ["interest rate decision", "rate decision", "monetary policy", "rate hike", "rate cut", "federal reserve", "fomc", "ecb", "bank of england", "bank of japan", "central bank"]),
        (NewsCategory.Earnings, ["earnings", "quarterly results", "full-year results", "annual results", "profit warning", "guidance", "revenue", "eps", "first quarter", "second quarter", "third quarter", "fourth quarter", "q1", "q2", "q3", "q4"]),
        (NewsCategory.CorporateTransaction, ["acquisition", "acquire*", "merger", "takeover", "buyout", "agrees to buy", "spin-off", "spinoff", "ipo", "divest*", "stake in"]),
        (NewsCategory.RegulatoryLegal, ["antitrust", "regulator*", "lawsuit", "sued", "settlement", "fined", "probe", "investigation", "court", "sec", "ftc", "european commission", "recall"]),
        (NewsCategory.Macroeconomic, ["inflation", "cpi", "consumer prices", "gdp", "unemployment", "jobless", "payrolls", "jobs report", "pmi", "retail sales", "recession", "economic growth"]),
        (NewsCategory.Geopolitical, ["sanction*", "tariff*", "war", "conflict", "election", "geopolitic*", "trade deal", "export ban"]),
        (NewsCategory.CommoditiesEnergy, ["oil", "crude", "opec", "natural gas", "gold price", "commodit*", "copper", "energy prices"]),
        (NewsCategory.CurrenciesBonds, ["bond yield*", "treasury yield*", "treasuries", "bund", "gilt*", "dollar", "euro", "yen", "currenc*", "forex"]),
        (NewsCategory.MarketIndex, ["s&p 500", "dow jones", "nasdaq", "dax", "ftse", "cac 40", "nikkei", "stock market", "wall street"]),
    ];

    private static readonly (NewsCategory Category, string Term, Regex Pattern)[] Compiled =
        Rules.SelectMany(r => r.Terms.Select(t => (r.Category, t.TrimEnd('*'),
            new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(t.TrimEnd('*'))}{(t.EndsWith('*') ? "" : @"(?![\p{L}\p{N}])")}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))).ToArray();

    public static Classification Classify(string headline, string? summary, NewsCategory? sourceCategory = null)
    {
        if (sourceCategory is { } c) return new Classification(c, "category of the official source feed");
        foreach (var (field, text) in new[] { ("headline", headline), ("summary", summary ?? "") })
            foreach (var (category, term, pattern) in Compiled)
                if (pattern.IsMatch(text))
                    return new Classification(category, $"{field} mentions “{term}”");
        return new Classification(NewsCategory.General, "no classification rule matched");
    }

    public static NewsScope Scope(NewsCategory category, int linkedAssets) =>
        linkedAssets > 0 ? NewsScope.Company
        : category is NewsCategory.CentralBank or NewsCategory.Macroeconomic or NewsCategory.Geopolitical or NewsCategory.CurrenciesBonds or NewsCategory.CommoditiesEnergy
            ? NewsScope.Macro
            : NewsScope.Market;
}

public enum RelevanceLevel { Low, Medium, High }

public sealed record RelevanceAssessment(RelevanceLevel Level, int Points, IReadOnlyList<string> Reasons);

/// <summary>
/// "Potential market relevance": a transparent points score of how material and well-supported an item is. It says
/// nothing about the direction or size of any price move and has not been validated against returns.
/// Points: direct link to a listed company +2; material event type (earnings, corporate transaction, regulatory/legal,
/// central bank) +2, other market-moving types (macro, geopolitical, commodities, currencies/bonds, index) +1; official
/// issuer +1; published within 24 h +1; reported by at least two different publishers +1. High ≥ 5, Medium 3–4, Low ≤ 2.
/// </summary>
public static class NewsRelevance
{
    public static RelevanceAssessment Assess(NewsCategory category, int linkedAssets, bool isOfficial, DateTime publishedAtUtc, int publishers, DateTime nowUtc)
    {
        var points = 0;
        var reasons = new List<string>();
        if (linkedAssets > 0) { points += 2; reasons.Add("tagged by the provider with a specific listed company (+2)"); }
        if (category is NewsCategory.Earnings or NewsCategory.CorporateTransaction or NewsCategory.RegulatoryLegal or NewsCategory.CentralBank)
        { points += 2; reasons.Add($"event type {Label(category)} is typically material (+2)"); }
        else if (category != NewsCategory.General) { points += 1; reasons.Add($"event type {Label(category)} can affect markets (+1)"); }
        if (isOfficial) { points += 1; reasons.Add("published by the issuing institution (+1)"); }
        if (nowUtc - publishedAtUtc <= TimeSpan.FromHours(24)) { points += 1; reasons.Add("published within the last 24 hours (+1)"); }
        if (publishers >= 2) { points += 1; reasons.Add($"reported by {publishers} different publishers (+1)"); }
        if (reasons.Count == 0) reasons.Add("no relevance criterion met");
        var level = points >= 5 ? RelevanceLevel.High : points >= 3 ? RelevanceLevel.Medium : RelevanceLevel.Low;
        return new RelevanceAssessment(level, points, reasons);
    }

    public static string Label(NewsCategory c) => c switch
    {
        NewsCategory.CorporateTransaction => "corporate transaction",
        NewsCategory.RegulatoryLegal => "regulatory/legal",
        NewsCategory.CentralBank => "central bank",
        NewsCategory.CommoditiesEnergy => "commodities/energy",
        NewsCategory.CurrenciesBonds => "currencies/bonds",
        NewsCategory.MarketIndex => "market/index",
        _ => c.ToString().ToLowerInvariant(),
    };
}
