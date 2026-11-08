using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.News;

public sealed class StatisticsFeedOptions
{
    public const string Section = "News:Statistics";

    public bool Enabled { get; set; } = true;
    public int MinRequestIntervalMs { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// Official statistics releases for the Macroeconomic feed (verified 2026-11-08): the US Bureau of Economic Analysis
/// (RSS; dates in US Eastern time, EST/EDT), the UK Office for National Statistics release calendar (RSS; only releases whose
/// title names an economic indicator are kept – the calendar also lists health and population releases) and Eurostat's
/// euro-indicators releases (Atom, UTC). Titles, dates, links and the institution's own teaser are stored; release figures
/// are not extracted from the prose. Not available: BLS (answers 403 to automated clients – never bypassed) and Japan's
/// statistics bureau (no feed found).
/// </summary>
public sealed partial class StatisticsFeedProvider(HttpClient http, IOptions<StatisticsFeedOptions> options) : INewsFeedProvider
{
    public const string Name = "OfficialStats";

    private const string PublicTerms = "Official release feed of a public statistics institution; titles, dates, links and the institution's teaser with attribution.";

    public NewsProviderDescriptor Descriptor { get; } = new(Name, "Official statistics releases", options.Value.Enabled, false,
        "US Bureau of Economic Analysis, UK Office for National Statistics (economic releases), Eurostat euro indicators.", PublicTerms);

    public IReadOnlyList<NewsFeed> Feeds { get; } =
    [
        new("bea", "U.S. Bureau of Economic Analysis", "https://apps.bea.gov/rss/rss.xml", "US", NewsCategory.Macroeconomic, true, PublicTerms),
        new("ons", "Office for National Statistics", "https://www.ons.gov.uk/releasecalendar?rss", "GB", NewsCategory.Macroeconomic, true, PublicTerms),
        new("eurostat", "Eurostat", "https://ec.europa.eu/eurostat/en/search?p_p_id=estatsearchportlet_WAR_estatsearchportlet&p_p_lifecycle=2&p_p_state=maximized&p_p_mode=view&p_p_resource_id=atom&_estatsearchportlet_WAR_estatsearchportlet_collection=CAT_PREREL",
            "EU", NewsCategory.Macroeconomic, true, PublicTerms),
    ];

    /// <summary>BEA publishes from Washington, D.C.: EST and EDT are unambiguous for this feed.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> UsEastern = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
    {
        ["EST"] = TimeSpan.FromHours(-5),
        ["EDT"] = TimeSpan.FromHours(-4),
    };

    public async Task<IReadOnlyList<ProviderArticle>> GetFeedAsync(NewsFeed feed, CancellationToken ct)
    {
        using var response = await http.GetAsync(feed.Url, ct);
        if (!response.IsSuccessStatusCode) throw new NewsProviderException(Name, $"HTTP {(int)response.StatusCode} from {feed.Publisher}.");
        var xml = await response.Content.ReadAsStringAsync(ct);
        return Parse(xml, feed);
    }

    public static IReadOnlyList<ProviderArticle> Parse(string xml, NewsFeed feed) => feed.Key switch
    {
        "bea" => ParseRss(xml, feed, UsEastern, _ => true),
        "ons" => ParseRss(xml, feed, null, IsEconomicRelease),
        "eurostat" => ParseAtom(xml, feed),
        _ => throw new NewsProviderException(Name, $"Unknown statistics feed '{feed.Key}'."),
    };

    /// <summary>ONS calendar filter: the title names an economic indicator (whole words, case-insensitive; documented list).</summary>
    public static bool IsEconomicRelease(string title) => EconomicTerms().IsMatch(title);

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(gdp|gross domestic product|inflation|consumer price|producer price|labour market|employment|unemployment|" +
                    @"vacancies|average weekly earnings|retail sales|trade|public sector finances|index of production|industrial production|" +
                    @"business investment|balance of payments|current account|services sector|construction output|house price)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EconomicTerms();

    private static XDocument Load(string xml, NewsFeed feed)
    {
        try
        {
            return XDocument.Parse(xml.TrimStart('﻿'), LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new NewsProviderException(Name, $"Malformed feed from {feed.Publisher}.", ex);
        }
    }

    /// <summary>RSS 2.0 items; items without title, link or a parseable date are skipped, as are titles the filter rejects.</summary>
    public static IReadOnlyList<ProviderArticle> ParseRss(string xml, NewsFeed feed, IReadOnlyDictionary<string, TimeSpan>? zones, Func<string, bool> keep)
    {
        var doc = Load(xml, feed);
        var result = new List<ProviderArticle>();
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            string? Child(string name) => item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            var title = Child("title") is { } t ? WebUtility.HtmlDecode(t) : null;
            var link = Child("link");
            if (title is null || link is null || !keep(title) || !OfficialFeedProvider.TryParseDate(Child("pubDate"), zones, out var published)) continue;
            result.Add(new ProviderArticle(Name, Child("guid") ?? link, link, title, feed.Publisher, published, Child("description"), null, [],
                IsOfficial: true, Region: feed.Region, SourceCategory: feed.Category));
        }
        return result;
    }

    /// <summary>Atom entries (Eurostat): title, alternate link, published (ISO 8601 with offset) and summary; incomplete entries are skipped.</summary>
    public static IReadOnlyList<ProviderArticle> ParseAtom(string xml, NewsFeed feed)
    {
        var doc = Load(xml, feed);
        var result = new List<ProviderArticle>();
        foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "entry"))
        {
            string? Child(string name) => entry.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            var title = Child("title") is { } t ? WebUtility.HtmlDecode(t) : null;
            var link = entry.Elements().Where(e => e.Name.LocalName == "link")
                .FirstOrDefault(e => (string?)e.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value;
            var published = Child("published") ?? Child("updated");
            if (title is null || string.IsNullOrWhiteSpace(link) || published is null ||
                !DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) || !HasOffset(published)) continue;
            result.Add(new ProviderArticle(Name, Child("id") ?? link, link, title, feed.Publisher, at.UtcDateTime, Child("summary"), null, [],
                IsOfficial: true, Region: feed.Region, SourceCategory: feed.Category));
        }
        return result;
    }

    /// <summary>An ISO time without a zone would be read as local server time – rejected instead.</summary>
    private static bool HasOffset(string iso) => iso.EndsWith('Z') || IsoOffset().IsMatch(iso);

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial Regex IsoOffset();
}
