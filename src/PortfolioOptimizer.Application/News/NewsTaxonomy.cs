using System.Text.RegularExpressions;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.News;

/// <summary>Kind of instrument a provider tag names, read from Yahoo's symbol syntax (documented in docs/financial-news.md › Taxonomy).</summary>
public enum InstrumentKind { Security, Index, Rates, Fx, Commodity, Volatility }

public sealed record EventClassification(NewsEventType EventType, string Basis);

/// <summary>
/// Everything the classification rules read about one article: text, the provider's tags, the catalogue listings linked
/// to it (equities vs. index/ETF listings, and which tags name an index or ETF listing) and what the source itself stated.
/// Ingest and reclassification build it from the same facts, so both classify alike.
/// </summary>
public sealed record ArticleClassificationInput(
    string Headline,
    string? Summary,
    IReadOnlyCollection<string> Tags,
    int LinkedEquities,
    int LinkedOther,
    IReadOnlySet<string> FundTags,
    bool IsOfficial,
    NewsCategory? SourceCategory = null,
    NewsEventType? SourceEventType = null,
    string? SourceEventBasis = null,
    string? Region = null);

/// <summary>An article's classification under <see cref="NewsTaxonomy.RuleVersion"/>.</summary>
public sealed record ArticleClassification(
    NewsCategory Category, string CategoryBasis, NewsScope Scope, NewsEventType EventType, string EventTypeBasis, NewsMarketArea? MarketArea);

/// <summary>
/// Classification rules version 3 (pure, deterministic, unit-tested): instrument kinds of provider tags, the scope and market
/// area that follow from them, and the event type. Official sources set the event type from their own structure (SEC form
/// and item; since version 3 the decision-release titles of the four central banks); other
/// articles are classified by keyword rules on the headline, then the summary (first match wins), which is a heuristic.
/// Changing any rule here means a new <see cref="RuleVersion"/>, so stored classifications stay attributable.
/// </summary>
public static class NewsTaxonomy
{
    /// <summary>
    /// 2 (2026-11-08): instrument kinds, scope, market area and event types. 3 (2026-11-10, AMIR-20): rate decisions from the
    /// central banks' own decision-release titles (none of them was matched by the keyword rules).
    /// </summary>
    public const int RuleVersion = 3;

    /// <summary>
    /// The title each central bank gives its rate-decision release, as published in its official press feed (checked against
    /// the stored feed items, 2026-07 to 2026-11). Whole-title patterns, applied only to official items whose source states
    /// the central-bank category and whose region is that bank's: the same words elsewhere decide nothing.
    /// </summary>
    private static readonly (string Region, string Bank, Regex Title)[] DecisionTitles =
    [
        ("US", "Federal Reserve", new Regex(@"^Federal Reserve issues FOMC statement$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("EU", "ECB", new Regex(@"^Monetary policy decisions$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("GB", "Bank of England", new Regex(@"^Bank Rate (maintained|increased|reduced|cut|raised|lowered) (at|to) \d+(\.\d+)?%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("JP", "Bank of Japan", new Regex(@"^Statement on Monetary Policy$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    /// <summary>
    /// Rate decision when an official central-bank item carries its bank's decision-release title (see
    /// <see cref="DecisionTitles"/>); null otherwise. The basis names the bank and the title rule.
    /// </summary>
    public static EventClassification? CentralBankDecision(string headline, bool isOfficial, NewsCategory? sourceCategory, string? region)
    {
        if (!isOfficial || sourceCategory != NewsCategory.CentralBank || region is null) return null;
        foreach (var (r, bank, title) in DecisionTitles)
            if (string.Equals(r, region, StringComparison.OrdinalIgnoreCase) && title.IsMatch(headline.Trim()))
                return new EventClassification(NewsEventType.RateDecision, $"{bank} decision-release title");
        return null;
    }

    private static readonly HashSet<string> RateIndexes = new(StringComparer.OrdinalIgnoreCase) { "^TNX", "^TYX", "^FVX", "^IRX" };
    private static readonly HashSet<string> VolatilityIndexes = new(StringComparer.OrdinalIgnoreCase) { "^VIX", "^VXN", "^VXD", "^V2TX", "^VSTOXX" };
    private static readonly HashSet<string> RateFutures = new(StringComparer.OrdinalIgnoreCase) { "ZN=F", "ZB=F", "ZF=F", "ZT=F", "UB=F" };

    /// <summary>
    /// ^… index (^TNX, ^TYX, ^FVX, ^IRX = Treasury yields → rates; ^VIX, ^VXN, ^VXD, ^V2TX, ^VSTOXX → volatility);
    /// …=X and DX-Y.NYB currency; …=F futures (Treasury futures → rates, VX=F → volatility, others commodities);
    /// TMBMK… bond benchmarks → rates; anything else is a security (company, ETF or fund).
    /// </summary>
    public static InstrumentKind Kind(string tag)
    {
        var t = tag.Trim();
        if (VolatilityIndexes.Contains(t) || t.Equals("VX=F", StringComparison.OrdinalIgnoreCase)) return InstrumentKind.Volatility;
        if (RateIndexes.Contains(t) || RateFutures.Contains(t) || t.StartsWith("TMBMK", StringComparison.OrdinalIgnoreCase)) return InstrumentKind.Rates;
        if (t.StartsWith('^')) return InstrumentKind.Index;
        if (t.EndsWith("=X", StringComparison.OrdinalIgnoreCase) || t.Equals("DX-Y.NYB", StringComparison.OrdinalIgnoreCase)) return InstrumentKind.Fx;
        if (t.EndsWith("=F", StringComparison.OrdinalIgnoreCase)) return InstrumentKind.Commodity;
        return InstrumentKind.Security;
    }

    /// <summary>
    /// Market area when every tag is a market instrument (no security): the kind named most often; ties go to the first of
    /// equities (indexes), rates, currencies, commodities, volatility. Null when there is no tag or any tag is a security.
    /// </summary>
    public static NewsMarketArea? MarketArea(IReadOnlyCollection<string> tags)
    {
        if (tags.Count == 0) return null;
        var kinds = tags.Select(Kind).ToList();
        if (kinds.Contains(InstrumentKind.Security)) return null;
        NewsMarketArea Area(InstrumentKind k) => k switch
        {
            InstrumentKind.Rates => NewsMarketArea.Rates,
            InstrumentKind.Fx => NewsMarketArea.Fx,
            InstrumentKind.Commodity => NewsMarketArea.Commodities,
            InstrumentKind.Volatility => NewsMarketArea.Volatility,
            _ => NewsMarketArea.Equities,
        };
        return kinds.GroupBy(Area).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
    }

    /// <summary>
    /// Company when a catalogue equity is linked or any tag names a security (even one outside the catalogue) other than a
    /// catalogue ETF or index (<paramref name="fundTags"/>); Market when all tags are market instruments or only index/ETF
    /// listings are linked; otherwise by category (version 1 rule).
    /// </summary>
    public static NewsScope Scope(IReadOnlyCollection<string> tags, int linkedEquities, int linkedOther, NewsCategory category,
        IReadOnlySet<string>? fundTags = null)
    {
        if (linkedEquities > 0) return NewsScope.Company;
        if (tags.Any(t => Kind(t) == InstrumentKind.Security && fundTags?.Contains(t) != true)) return NewsScope.Company;
        if (tags.Count > 0 || linkedOther > 0) return NewsScope.Market;
        return NewsClassifier.Scope(category, 0);
    }

    /// <summary>
    /// The whole classification of one article: category (version 1 keyword rules or the source's own), scope, event type
    /// and – for market articles – market area (equities when no tag decides it). Pure; used when an article is stored and
    /// when stored articles are classified again under a newer rules version.
    /// </summary>
    public static ArticleClassification ClassifyArticle(ArticleClassificationInput a)
    {
        var category = NewsClassifier.Classify(a.Headline, a.Summary, a.SourceCategory);
        var scope = Scope(a.Tags, a.LinkedEquities, a.LinkedOther, category.Category, a.FundTags);
        var eventType = a.SourceEventType is null && CentralBankDecision(a.Headline, a.IsOfficial, a.SourceCategory, a.Region) is { } decision
            ? decision
            : Classify(a.Headline, a.Summary, scope, a.IsOfficial, a.SourceEventType, a.SourceEventBasis);
        var area = scope == NewsScope.Market ? MarketArea(a.Tags) ?? NewsMarketArea.Equities : (NewsMarketArea?)null;
        return new ArticleClassification(category.Category, category.Basis, scope, eventType.EventType, eventType.Basis, area);
    }

    /// <summary>Provider tags as stored on an article (comma-separated); empty when none were delivered or stored.</summary>
    public static IReadOnlyList<string> SplitTags(string? stored) =>
        (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Terms match whole words (case-insensitive); a trailing * matches a word prefix.
    private static readonly (NewsEventType Type, string[] Terms)[] CompanyRules =
    [
        (NewsEventType.Bankruptcy, ["bankruptcy", "chapter 11", "insolven*", "administration proceedings"]),
        (NewsEventType.MergerOrAcquisition, ["acquisition", "acquire*", "merger", "takeover", "buyout", "agrees to buy", "spin-off", "spinoff"]),
        (NewsEventType.Guidance, ["guidance", "profit warning", "raises outlook", "cuts outlook", "lowers outlook", "raises forecast", "cuts forecast"]),
        (NewsEventType.Results, ["earnings", "quarterly results", "full-year results", "annual results", "beats estimates", "misses estimates", "eps"]),
        (NewsEventType.DividendOrBuyback, ["dividend*", "buyback*", "share repurchase*"]),
        (NewsEventType.ManagementChange, ["ceo", "chief executive", "cfo", "chief financial officer", "steps down", "appoints", "resign*"]),
        (NewsEventType.CapitalRaise, ["share offering", "stock offering", "capital increase", "rights issue", "convertible", "bond issue", "raises capital"]),
        (NewsEventType.LegalOrRegulatory, ["antitrust", "lawsuit", "sued", "settlement", "fined", "probe", "investigation", "regulator*", "recall"]),
    ];

    private static readonly (NewsEventType Type, string[] Terms)[] MacroRules =
    [
        (NewsEventType.RateDecision, ["rate decision", "interest rate decision", "monetary policy decision", "raises interest rates", "cuts interest rates",
            "holds interest rates", "leaves interest rates", "rate cut", "rate hike", "key interest rates"]),
        (NewsEventType.InflationRelease, ["inflation", "consumer price*", "cpi", "hicp", "producer price*", "ppi", "pce price*"]),
        (NewsEventType.LabourRelease, ["unemployment", "employment", "payrolls", "jobless", "labour market", "labor market", "wages", "salary", "salaries"]),
        (NewsEventType.GrowthRelease, ["gdp", "gross domestic product", "economic growth"]),
        (NewsEventType.ActivityRelease, ["retail trade", "retail sales", "industrial production", "industrial producer", "pmi", "personal income", "consumer spending",
            "housing starts", "construction"]),
        (NewsEventType.TradeOrFiscalRelease, ["international trade", "trade balance", "trade deficit", "trade surplus", "current account", "public sector finances",
            "government debt", "government deficit", "budget", "tariff*", "international transactions"]),
        (NewsEventType.PolicyStatement, ["speech", "remarks", "testimony", "minutes", "monetary policy statement", "press conference", "statement"]),
    ];

    private static readonly (NewsEventType Type, string Term, Regex Pattern)[] CompanyCompiled = Compile(CompanyRules);
    private static readonly (NewsEventType Type, string Term, Regex Pattern)[] MacroCompiled = Compile(MacroRules);

    private static (NewsEventType, string, Regex)[] Compile((NewsEventType Type, string[] Terms)[] rules) =>
        rules.SelectMany(r => r.Terms.Select(t => (r.Type, t.TrimEnd('*'),
            new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(t.TrimEnd('*'))}{(t.EndsWith('*') ? "" : @"(?![\p{L}\p{N}])")}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))).ToArray();

    /// <summary>
    /// Event type: the source's own type when it has one; else company rules for company articles, macro rules for macro
    /// articles, "market news" for market articles. Unmatched: official macro items → other release, everything else general.
    /// </summary>
    public static EventClassification Classify(string headline, string? summary, NewsScope scope, bool isOfficial,
        NewsEventType? sourceEventType = null, string? sourceBasis = null)
    {
        if (sourceEventType is { } own) return new EventClassification(own, sourceBasis ?? "event type stated by the source");
        if (scope == NewsScope.Market) return new EventClassification(NewsEventType.MarketNews, "tagged only with market instruments or index listings");
        var rules = scope == NewsScope.Company ? CompanyCompiled : MacroCompiled;
        foreach (var (field, text) in new[] { ("headline", headline), ("summary", summary ?? "") })
            foreach (var (type, term, pattern) in rules)
                if (pattern.IsMatch(text))
                    return new EventClassification(type, $"{field} mentions “{term}”");
        return scope == NewsScope.Macro && isOfficial
            ? new EventClassification(NewsEventType.OtherRelease, "official release; no event-type rule matched")
            : new EventClassification(NewsEventType.General, "no event-type rule matched");
    }

    /// <summary>Plain label for an event type (UI and docs use the same words).</summary>
    public static string Label(NewsEventType t) => t switch
    {
        NewsEventType.PeriodicReport => "periodic report",
        NewsEventType.DividendOrBuyback => "dividend or buyback",
        NewsEventType.MergerOrAcquisition => "merger or acquisition",
        NewsEventType.MaterialAgreement => "material agreement",
        NewsEventType.ManagementChange => "management change",
        NewsEventType.CapitalRaise => "capital raise",
        NewsEventType.LegalOrRegulatory => "legal or regulatory",
        NewsEventType.OtherFiling => "other filing",
        NewsEventType.RateDecision => "rate decision",
        NewsEventType.PolicyStatement => "policy statement",
        NewsEventType.InflationRelease => "inflation release",
        NewsEventType.LabourRelease => "labour release",
        NewsEventType.GrowthRelease => "growth release",
        NewsEventType.ActivityRelease => "activity release",
        NewsEventType.TradeOrFiscalRelease => "trade or fiscal release",
        NewsEventType.OtherRelease => "other release",
        NewsEventType.MarketNews => "market news",
        _ => t.ToString().ToLowerInvariant(),
    };
}
