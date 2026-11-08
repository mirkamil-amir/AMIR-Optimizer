using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.News;

public sealed class SecEdgarOptions
{
    public const string Section = "News:SecEdgar";

    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Contact the SEC requires in every request's User-Agent (a name and an e-mail address of the operator). Empty = the
    /// source is off. Set by the owner (NEWS_SEC_CONTACT); never derived from a user account.
    /// </summary>
    public string Contact { get; set; } = "";
    public string BaseUrl { get; set; } = "https://www.sec.gov";
    /// <summary>SEC fair access: at most 10 requests per second; 250 ms stays well below.</summary>
    public int MinRequestIntervalMs { get; set; } = 250;
    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// Company filings from SEC EDGAR (official, free) for US listings: the company's filing list as Atom
/// (<c>/cgi-bin/browse-edgar?action=getcompany&amp;CIK=&lt;ticker&gt;&amp;output=atom</c>). Only event and periodic reports are kept
/// (8-K, 10-Q, 10-K, 20-F, 40-F, 6-K and their amendments). The event type comes from the form and, for 8-K, from its item
/// numbers; the filing is linked to the listing it was requested for (an official identifier, not a name match). The
/// headline is the company name, the form title as EDGAR states it, the 8-K item numbers and the filing date; nothing is
/// paraphrased.
/// </summary>
public sealed partial class SecEdgarProvider(HttpClient http, IOptions<SecEdgarOptions> options) : ICompanyNewsProvider
{
    public const string Name = "SecEdgar";
    public const string Publisher = "U.S. SEC (EDGAR)";
    public const string Match = "SEC filing of the listed company";

    public NewsProviderDescriptor Descriptor { get; } = new(Name, "SEC EDGAR company filings",
        options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.Contact), false,
        "Event and periodic reports of US-listed companies (8-K, 10-Q, 10-K, 20-F, 40-F, 6-K).",
        "Public filings of the U.S. Securities and Exchange Commission; requests identify the operator as SEC fair-access rules require.");

    private static readonly HashSet<string> Forms = new(StringComparer.OrdinalIgnoreCase)
        { "8-K", "8-K/A", "10-Q", "10-Q/A", "10-K", "10-K/A", "20-F", "20-F/A", "40-F", "40-F/A", "6-K", "6-K/A" };

    /// <summary>US listings only: no exchange suffix and not an index.</summary>
    public bool Supports(string symbol) => !symbol.StartsWith('^') && !symbol.Contains('.') && Markets.ForSymbol(symbol).Country == "US";

    public async Task<IReadOnlyList<ProviderArticle>> GetCompanyNewsAsync(string symbol, string? companyName, CancellationToken ct)
    {
        var ticker = SymbolMapper.ForYahoo(symbol).Replace('-', '.');
        var url = $"/cgi-bin/browse-edgar?action=getcompany&CIK={Uri.EscapeDataString(ticker)}&type=&dateb=&owner=include&count=40&output=atom";
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return []; // no EDGAR filer for this ticker
        if (!response.IsSuccessStatusCode) throw new NewsProviderException(Name, $"HTTP {(int)response.StatusCode} for {symbol}.");
        var xml = await response.Content.ReadAsStringAsync(ct);
        return Parse(xml, symbol);
    }

    /// <summary>
    /// Parses the company Atom feed. Entries need a form, a link and an <c>updated</c> time with an explicit offset (EDGAR
    /// acceptance time); anything else is skipped rather than completed.
    /// </summary>
    public static IReadOnlyList<ProviderArticle> Parse(string xml, string symbol)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml.TrimStart('﻿'), LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new NewsProviderException(Name, $"Malformed filing list for {symbol}.", ex);
        }
        if (doc.Root?.Name.LocalName != "feed") return []; // EDGAR answers an unknown ticker with an HTML page
        var company = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "conformed-name")?.Value.Trim();
        var result = new List<ProviderArticle>();
        foreach (var entry in doc.Root.Elements().Where(e => e.Name.LocalName == "entry"))
        {
            string? Find(string name) => entry.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            var form = (string?)entry.Elements().FirstOrDefault(e => e.Name.LocalName == "category")?.Attribute("term") ?? Find("filing-type");
            var link = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "link")?.Attribute("href")?.Value;
            var updated = Find("updated");
            var title = Find("title");
            if (form is null || !Forms.Contains(form) || string.IsNullOrWhiteSpace(link) || title is null || updated is null || !IsoOffset().IsMatch(updated) ||
                !DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.None, out var accepted)) continue;
            var items = Find("items-desc");
            var (type, basis) = EventType(form, items);
            // Every 8-K has the same EDGAR title, and stories group by headline words: the item numbers and the filing date
            // (both from the filing itself) keep separate filings apart.
            var codes = items is null ? "" : string.Join(", ", ItemCode().Matches(items).Select(m => m.Value).Distinct());
            var headline = $"{company ?? symbol}: {title}{(codes.Length > 0 ? $" (items {codes})" : "")}, filed {accepted:yyyy-MM-dd}";
            result.Add(new ProviderArticle(Name, Find("id") ?? link, link, headline, Publisher, accepted.UtcDateTime, items, null, [symbol],
                IsOfficial: true, Region: "US", SourceCategory: Category(type), SourceEventType: type, SourceEventBasis: basis, MatchMethod: Match));
        }
        return result;
    }

    /// <summary>
    /// Periodic reports (10-Q, 10-K, 20-F, 40-F) → periodic report; 6-K → other filing; 8-K by its most specific item:
    /// 1.03 bankruptcy, 2.01 completed acquisition, 2.02 results, 5.02 management change, 1.01 material agreement,
    /// 3.02 or 2.03 capital raise, otherwise other filing.
    /// </summary>
    public static (NewsEventType Type, string Basis) EventType(string form, string? itemsDescription)
    {
        var baseForm = form.Split('/')[0].ToUpperInvariant();
        if (baseForm is "10-Q" or "10-K" or "20-F" or "40-F") return (NewsEventType.PeriodicReport, $"SEC form {form}");
        if (baseForm != "8-K") return (NewsEventType.OtherFiling, $"SEC form {form}");
        var codes = itemsDescription is null ? [] : ItemCode().Matches(itemsDescription).Select(m => m.Value).ToHashSet();
        (string Code, NewsEventType Type)[] order =
        [
            ("1.03", NewsEventType.Bankruptcy), ("2.01", NewsEventType.MergerOrAcquisition), ("2.02", NewsEventType.Results),
            ("5.02", NewsEventType.ManagementChange), ("1.01", NewsEventType.MaterialAgreement), ("3.02", NewsEventType.CapitalRaise),
            ("2.03", NewsEventType.CapitalRaise),
        ];
        foreach (var (code, type) in order)
            if (codes.Contains(code)) return (type, $"SEC form {form} item {code}");
        return (NewsEventType.OtherFiling, codes.Count > 0 ? $"SEC form {form} items {string.Join(", ", codes.Order())}" : $"SEC form {form}");
    }

    private static NewsCategory? Category(NewsEventType type) => type switch
    {
        NewsEventType.Results or NewsEventType.PeriodicReport => NewsCategory.Earnings,
        NewsEventType.MergerOrAcquisition => NewsCategory.CorporateTransaction,
        _ => null,
    };

    [GeneratedRegex(@"\b[1-9]\.\d{2}\b")]
    private static partial Regex ItemCode();

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoOffset();
}
