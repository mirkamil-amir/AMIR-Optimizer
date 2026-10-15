using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.News;

public sealed class YahooNewsOptions
{
    public const string Section = "News:Yahoo";

    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://query1.finance.yahoo.com";
    public int MaxArticles { get; set; } = 20;
    public int MinRequestIntervalMs { get; set; } = 1000;
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class OfficialFeedOptions
{
    public const string Section = "News:OfficialFeeds";

    public bool Enabled { get; set; } = true;
    public int MinRequestIntervalMs { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class MockNewsOptions
{
    public const string Section = "News:Mock";

    /// <summary>Deterministic SIMULATED articles for tests and the e2e stack only. Never enable on a real deployment.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Company news from Yahoo Finance's search endpoint (<c>/v1/finance/search</c>, unofficial, personal non-commercial use).
/// The query is the ticker for US listings and the company name elsewhere (the endpoint does not resolve suffixed
/// tickers such as 7203.T); results are returned with the provider's <c>relatedTickers</c>, which are the only basis for
/// linking an article to a listing.
/// </summary>
public sealed class YahooNewsProvider(HttpClient http, IOptions<YahooNewsOptions> options) : ICompanyNewsProvider
{
    public const string Name = "YahooNews";

    public NewsProviderDescriptor Descriptor { get; } = new(Name, "Yahoo Finance news search (unofficial)", options.Value.Enabled, false,
        "Company news for US and international listings as tagged by Yahoo; coverage outside the US is sparse.",
        "Unofficial endpoint; personal, non-commercial use only per Yahoo terms. Headlines and links only; summaries are not provided by this endpoint. " +
        "Thumbnail display rights belong to the publishers and were not verified.");

    public async Task<IReadOnlyList<ProviderArticle>> GetCompanyNewsAsync(string symbol, string? companyName, CancellationToken ct)
    {
        var venue = Markets.ForSymbol(symbol);
        var term = venue.Suffix.Length == 0 || string.IsNullOrWhiteSpace(companyName) ? SymbolMapper.ForYahoo(symbol) : companyName;
        var url = $"/v1/finance/search?q={Uri.EscapeDataString(term)}&quotesCount=0&newsCount={options.Value.MaxArticles}&enableFuzzyQuery=false";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) throw new NewsProviderException(Name, $"HTTP {(int)response.StatusCode} for {symbol}.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new NewsProviderException(Name, "Malformed JSON response.", ex);
        }
    }

    /// <summary>Parses a search response; items without title, link or publish time are skipped (never completed with guesses).</summary>
    public static IReadOnlyList<ProviderArticle> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("news", out var news) || news.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ProviderArticle>();
        foreach (var n in news.EnumerateArray())
        {
            var title = Str(n, "title");
            var link = Str(n, "link");
            if (title is null || link is null || !n.TryGetProperty("providerPublishTime", out var t) || !t.TryGetInt64(out var unix)) continue;
            var related = n.TryGetProperty("relatedTickers", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            result.Add(new ProviderArticle(Name, Str(n, "uuid"), link, title, Str(n, "publisher") ?? "Unknown publisher",
                DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, Summary: null, Thumbnail(n), related));
        }
        return result;
    }

    /// <summary>The original-resolution thumbnail supplied with the article, if any.</summary>
    private static string? Thumbnail(JsonElement n)
    {
        if (!n.TryGetProperty("thumbnail", out var th) || !th.TryGetProperty("resolutions", out var res) || res.ValueKind != JsonValueKind.Array) return null;
        var all = res.EnumerateArray().ToList();
        var pick = all.FirstOrDefault(x => Str(x, "tag") == "original");
        if (pick.ValueKind == JsonValueKind.Undefined && all.Count > 0) pick = all[0];
        return pick.ValueKind == JsonValueKind.Undefined ? null : Str(pick, "url");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
}

/// <summary>
/// Official press-release RSS feeds of central banks (public institutions publishing their own announcements). Titles,
/// dates and links are shown; descriptions only when the feed includes them. Feed list per docs/financial-news.md.
/// </summary>
public sealed class OfficialFeedProvider(HttpClient http, IOptions<OfficialFeedOptions> options) : INewsFeedProvider
{
    public const string Name = "OfficialRss";

    private const string PublicTerms = "Official RSS feed of a public institution; headlines and links are shown with attribution and link to the original.";

    public NewsProviderDescriptor Descriptor { get; } = new(Name, "Official central-bank RSS feeds", options.Value.Enabled, false,
        "Federal Reserve, ECB, Bank of England and Bank of Japan press releases.", PublicTerms);

    public IReadOnlyList<NewsFeed> Feeds { get; } =
    [
        new("fed", "Federal Reserve Board", "https://www.federalreserve.gov/feeds/press_all.xml", "US", NewsCategory.CentralBank, true, PublicTerms),
        new("ecb", "European Central Bank", "https://www.ecb.europa.eu/rss/press.html", "EU", NewsCategory.CentralBank, true, PublicTerms),
        new("boe", "Bank of England", "https://www.bankofengland.co.uk/rss/news", "GB", NewsCategory.CentralBank, true, PublicTerms),
        new("boj", "Bank of Japan", "https://www.boj.or.jp/en/rss/whatsnew.xml", "JP", NewsCategory.CentralBank, true, PublicTerms),
    ];

    public async Task<IReadOnlyList<ProviderArticle>> GetFeedAsync(NewsFeed feed, CancellationToken ct)
    {
        using var response = await http.GetAsync(feed.Url, ct);
        if (!response.IsSuccessStatusCode) throw new NewsProviderException(Name, $"HTTP {(int)response.StatusCode} from {feed.Publisher}.");
        var xml = await response.Content.ReadAsStringAsync(ct);
        return ParseRss(xml, feed);
    }

    /// <summary>Parses RSS 2.0 items; items without a title, link or parseable date are skipped.</summary>
    public static IReadOnlyList<ProviderArticle> ParseRss(string xml, NewsFeed feed)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml.TrimStart('﻿'), LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new NewsProviderException(Name, $"Malformed feed from {feed.Publisher}.", ex);
        }
        var result = new List<ProviderArticle>();
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            string? Child(string name) => item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            var title = Child("title");
            var link = Child("link");
            if (title is null || link is null || !TryParseDate(Child("pubDate"), out var published)) continue;
            var description = Child("description");
            // Some feeds repeat the title as the description; that adds nothing.
            if (description is not null && string.Equals(WebUtility.HtmlDecode(description).Trim(), WebUtility.HtmlDecode(title).Trim(), StringComparison.OrdinalIgnoreCase))
                description = null;
            result.Add(new ProviderArticle(Name, Child("guid"), link.Replace("europa.eu//", "europa.eu/"), WebUtility.HtmlDecode(title), feed.Publisher, published,
                description, null, [], IsOfficial: feed.IsOfficial, Region: feed.Region, SourceCategory: feed.Category));
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex Rfc822 = new(
        @"^(?:[A-Za-z]{3},\s*)?(?<d>\d{1,2})\s+(?<m>[A-Za-z]{3})\s+(?<y>\d{4})\s+(?<h>\d{1,2}):(?<min>\d{2})(?::(?<s>\d{2}))?\s*(?<z>[A-Za-z]+|[+-]\d{4})?$");

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>RFC 822 dates as used by RSS ("Tue, 13 Oct 2026 18:00:00 GMT", "Wed, 14 Oct 2026 02:00:00 +0200"); unknown zone names fail.</summary>
    public static bool TryParseDate(string? value, out DateTime utc)
    {
        utc = default;
        var m = value is null ? null : Rfc822.Match(value.Trim());
        if (m is null || !m.Success) return false;
        var month = Array.FindIndex(Months, x => string.Equals(x, m.Groups["m"].Value, StringComparison.OrdinalIgnoreCase)) + 1;
        if (month == 0) return false;
        var zone = m.Groups["z"].Success ? m.Groups["z"].Value : "GMT";
        TimeSpan offset;
        if (zone is "GMT" or "UT" or "UTC" or "Z") offset = TimeSpan.Zero;
        else if (zone.Length == 5 && (zone[0] == '+' || zone[0] == '-'))
        {
            offset = new TimeSpan(int.Parse(zone[1..3], CultureInfo.InvariantCulture), int.Parse(zone[3..5], CultureInfo.InvariantCulture), 0);
            if (zone[0] == '-') offset = -offset;
        }
        else return false; // ambiguous zone abbreviations (EST, CET, ...) are rejected rather than guessed
        try
        {
            var local = new DateTime(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), month, int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture),
                m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0);
            utc = new DateTimeOffset(local, offset).UtcDateTime;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

/// <summary>Deterministic SIMULATED company and macro news for automated tests and the e2e stack (headlines say so).</summary>
public sealed class MockNewsProvider(IOptions<MockNewsOptions> options, TimeProvider clock) : ICompanyNewsProvider, INewsFeedProvider
{
    public const string Name = "MockNews";

    public NewsProviderDescriptor Descriptor { get; } = new(Name, "Simulated news (tests only)", options.Value.Enabled, true,
        "Synthetic articles for automated tests.", "Synthetic data – not real news.");

    public IReadOnlyList<NewsFeed> Feeds { get; } =
        [new("macro", "Simulated Central Bank", "https://example.invalid/mock-feed", "US", NewsCategory.CentralBank, true, "Synthetic data – not real news.")];

    public Task<IReadOnlyList<ProviderArticle>> GetCompanyNewsAsync(string symbol, string? companyName, CancellationToken ct)
    {
        var day = clock.GetUtcNow().UtcDateTime.Date;
        var name = companyName ?? symbol;
        var slug = Uri.EscapeDataString(symbol.ToLowerInvariant());
        IReadOnlyList<ProviderArticle> items =
        [
            new(Name, $"{symbol}-1", $"https://example.invalid/news/{slug}/results", $"SIMULATED: {name} reports quarterly earnings", "Simulated Wire",
                day.AddDays(-12).AddHours(21), "Synthetic article for automated tests.", null, [symbol], IsSimulated: true),
            new(Name, $"{symbol}-2", $"https://example.invalid/news/{slug}/results-syndicated", $"SIMULATED: {name} reports quarterly earnings", "Simulated Daily",
                day.AddDays(-12).AddHours(22), null, null, [symbol], IsSimulated: true),
            new(Name, $"{symbol}-3", $"https://example.invalid/news/{slug}/product", $"SIMULATED: {name} presents a new product line", "Simulated Wire",
                day.AddDays(-40).AddHours(14), null, null, [symbol], IsSimulated: true),
        ];
        return Task.FromResult(items);
    }

    public Task<IReadOnlyList<ProviderArticle>> GetFeedAsync(NewsFeed feed, CancellationToken ct)
    {
        var day = clock.GetUtcNow().UtcDateTime.Date;
        IReadOnlyList<ProviderArticle> items =
        [
            new(Name, "macro-1", "https://example.invalid/news/macro/rates", "SIMULATED: Central bank leaves interest rates unchanged", feed.Publisher,
                day.AddDays(-2).AddHours(18), null, null, [], IsOfficial: true, Region: feed.Region, SourceCategory: feed.Category, IsSimulated: true),
        ];
        return Task.FromResult(items);
    }
}
