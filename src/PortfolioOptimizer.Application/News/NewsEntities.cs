using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.News;

/// <summary>
/// Something other than a company a story can concern (docs/financial-news.md › Entities). Index, rates, currency,
/// commodity and volatility entities agree with the instrument kind <see cref="NewsTaxonomy.Kind"/> reads from their tags.
/// </summary>
public enum NewsEntityKind { Index, Rates, Currency, Commodity, Volatility, CentralBank, StatisticsOffice }

/// <summary>
/// One reviewed entity: a stable key, its plain name, kind, region and currency, and the provider tags that name it
/// (market instruments). Central banks and statistics offices are matched by the official source that published an item.
/// </summary>
public sealed record NewsEntityDefinition(string Key, string Name, NewsEntityKind Kind, string? Region, string? Currency, IReadOnlyList<string> Tags,
    string? Publisher = null);

/// <summary>A link an article gets to an entity, with how it was established.</summary>
public sealed record EntityMatch(string Key, string MatchMethod);

/// <summary>
/// Reviewed list of non-company entities and the rules that link articles to them (pure, deterministic, unit-tested).
/// Links come only from the provider's own tag or from the official source that published the item – never from headline
/// text. Tags not on the list stay unlinked (e.g. CL-LCO1=R, XAUUSD=X). Changing the list or a rule means a new
/// <see cref="RuleVersion"/>; stored articles of an older version are linked again by the scheduled run.
/// </summary>
public static class NewsEntities
{
    /// <summary>1 (2026-11-10, AMIR-19): first list.</summary>
    public const int RuleVersion = 1;

    private static NewsEntityDefinition Tag(string key, string name, NewsEntityKind kind, string? region, string? currency, params string[] tags) =>
        new(key, name, kind, region, currency, tags.Length == 0 ? [key] : tags);

    public static readonly IReadOnlyList<NewsEntityDefinition> All =
    [
        // Equity indexes
        Tag("^GSPC", "S&P 500", NewsEntityKind.Index, "US", "USD"),
        Tag("^IXIC", "Nasdaq Composite", NewsEntityKind.Index, "US", "USD"),
        Tag("^NDX", "Nasdaq-100", NewsEntityKind.Index, "US", "USD"),
        Tag("^DJI", "Dow Jones Industrial Average", NewsEntityKind.Index, "US", "USD"),
        Tag("^SOX", "PHLX Semiconductor Index", NewsEntityKind.Index, "US", "USD"),
        Tag("^GSPTSE", "S&P/TSX Composite", NewsEntityKind.Index, "CA", "CAD"),
        Tag("^STOXX50E", "EURO STOXX 50", NewsEntityKind.Index, "EU", "EUR"),
        Tag("^STOXX", "STOXX Europe 600", NewsEntityKind.Index, "EU", "EUR"),
        Tag("^GDAXI", "DAX", NewsEntityKind.Index, "DE", "EUR"),
        Tag("^FCHI", "CAC 40", NewsEntityKind.Index, "FR", "EUR"),
        Tag("^IBEX", "IBEX 35", NewsEntityKind.Index, "ES", "EUR"),
        Tag("^FTSE", "FTSE 100", NewsEntityKind.Index, "GB", "GBP"),
        Tag("^N225", "Nikkei 225", NewsEntityKind.Index, "JP", "JPY"),
        Tag("^HSI", "Hang Seng", NewsEntityKind.Index, "HK", "HKD"),
        // Rates (US Treasury yields)
        Tag("^IRX", "US 13-week Treasury bill yield", NewsEntityKind.Rates, "US", "USD"),
        Tag("^FVX", "US 5-year Treasury yield", NewsEntityKind.Rates, "US", "USD"),
        Tag("^TNX", "US 10-year Treasury yield", NewsEntityKind.Rates, "US", "USD"),
        Tag("^TYX", "US 30-year Treasury yield", NewsEntityKind.Rates, "US", "USD"),
        // Currencies: Yahoo's "EUR=X" quotes euros per dollar (USD/EUR), the inverse of "EURUSD=X"; both are kept apart.
        Tag("EURUSD", "EUR/USD", NewsEntityKind.Currency, null, null, "EURUSD=X"),
        Tag("USDEUR", "USD/EUR", NewsEntityKind.Currency, null, null, "EUR=X"),
        Tag("GBPUSD", "GBP/USD", NewsEntityKind.Currency, null, null, "GBPUSD=X"),
        Tag("USDGBP", "USD/GBP", NewsEntityKind.Currency, null, null, "GBP=X"),
        Tag("USDJPY", "USD/JPY", NewsEntityKind.Currency, null, null, "USDJPY=X", "JPY=X"),
        Tag("DXY", "US Dollar Index", NewsEntityKind.Currency, "US", "USD", "DX-Y.NYB"),
        // Commodities (futures)
        Tag("CL=F", "WTI crude oil", NewsEntityKind.Commodity, null, "USD"),
        Tag("BZ=F", "Brent crude oil", NewsEntityKind.Commodity, null, "USD"),
        Tag("NG=F", "Natural gas", NewsEntityKind.Commodity, null, "USD"),
        Tag("GC=F", "Gold", NewsEntityKind.Commodity, null, "USD"),
        Tag("SI=F", "Silver", NewsEntityKind.Commodity, null, "USD"),
        Tag("PL=F", "Platinum", NewsEntityKind.Commodity, null, "USD"),
        Tag("HG=F", "Copper", NewsEntityKind.Commodity, null, "USD"),
        Tag("ALI=F", "Aluminium", NewsEntityKind.Commodity, null, "USD"),
        // Volatility
        Tag("^VIX", "CBOE Volatility Index", NewsEntityKind.Volatility, "US", "USD"),
        // Central banks (their official press feeds) and statistics offices (their release feeds)
        new("FED", "Federal Reserve", NewsEntityKind.CentralBank, "US", "USD", []),
        new("ECB", "European Central Bank", NewsEntityKind.CentralBank, "EU", "EUR", []),
        new("BOE", "Bank of England", NewsEntityKind.CentralBank, "GB", "GBP", []),
        new("BOJ", "Bank of Japan", NewsEntityKind.CentralBank, "JP", "JPY", []),
        new("BEA", "U.S. Bureau of Economic Analysis", NewsEntityKind.StatisticsOffice, "US", "USD", [], "U.S. Bureau of Economic Analysis"),
        new("ONS", "Office for National Statistics", NewsEntityKind.StatisticsOffice, "GB", "GBP", [], "Office for National Statistics"),
        new("EUROSTAT", "Eurostat", NewsEntityKind.StatisticsOffice, "EU", "EUR", [], "Eurostat"),
    ];

    private static readonly Dictionary<string, NewsEntityDefinition> Keys = All.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, NewsEntityDefinition> Tags =
        All.SelectMany(e => e.Tags.Select(t => (Tag: t, Entity: e))).ToDictionary(x => x.Tag, x => x.Entity, StringComparer.OrdinalIgnoreCase);

    public static NewsEntityDefinition? Find(string key) => Keys.GetValueOrDefault(key);

    public static IEnumerable<NewsEntityDefinition> CentralBanks => All.Where(e => e.Kind == NewsEntityKind.CentralBank);

    /// <summary>Keys of the central banks that issue the given currencies (USD → FED, EUR → ECB, GBP → BOE, JPY → BOJ).</summary>
    public static List<string> CentralBanksFor(IEnumerable<string> currencies)
    {
        var set = currencies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CentralBanks.Where(b => b.Currency is { } c && set.Contains(c)).Select(b => b.Key).ToList();
    }

    /// <summary>
    /// Entities of one article: each provider tag on the list (match method "provider tag ^GSPC"); for an official item whose
    /// source states the central-bank category, the central bank of its region; for an official statistics item, the
    /// office that published it. A simulated item is never attributed to a real institution: it gets tag links only.
    /// Deterministic order (list order), no duplicates.
    /// </summary>
    public static IReadOnlyList<EntityMatch> Match(IReadOnlyCollection<string> tags, bool isOfficial, NewsCategory? sourceCategory, string? region,
        string publisher, bool isSimulated = false)
    {
        isOfficial &= !isSimulated;
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, e) in tags.Select(t => t.Trim()).Where(Tags.ContainsKey).Select(t => (t, Tags[t])))
            found.TryAdd(e.Key, $"provider tag {tag}");
        if (isOfficial && sourceCategory == NewsCategory.CentralBank && region is not null &&
            CentralBanks.FirstOrDefault(b => string.Equals(b.Region, region, StringComparison.OrdinalIgnoreCase)) is { } bank)
            found.TryAdd(bank.Key, $"official central-bank feed ({region})");
        if (isOfficial && All.FirstOrDefault(e => e.Kind == NewsEntityKind.StatisticsOffice && string.Equals(e.Publisher, publisher, StringComparison.OrdinalIgnoreCase))
                is { } office)
            found.TryAdd(office.Key, "official statistics release feed");
        return All.Where(e => found.ContainsKey(e.Key)).Select(e => new EntityMatch(e.Key, found[e.Key])).ToList();
    }

    /// <summary>Plain label of a kind (UI and docs use the same words).</summary>
    public static string KindLabel(NewsEntityKind kind) => kind switch
    {
        NewsEntityKind.Index => "index",
        NewsEntityKind.Rates => "rates",
        NewsEntityKind.Currency => "currency",
        NewsEntityKind.Commodity => "commodity",
        NewsEntityKind.Volatility => "volatility",
        NewsEntityKind.CentralBank => "central bank",
        NewsEntityKind.StatisticsOffice => "statistics office",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
