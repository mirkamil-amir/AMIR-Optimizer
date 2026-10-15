using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;

namespace PortfolioOptimizer.Infrastructure.MarketData;

public sealed class WikipediaProfileOptions
{
    public const string Section = "MarketData:Profiles:Wikipedia";

    public bool Enabled { get; set; } = true;
    /// <summary>Wikimedia asks API clients for a descriptive User-Agent with contact information.</summary>
    public string UserAgent { get; set; } = "AMIR-Optimizer/1.0 (educational portfolio research; https://github.com/mirkamil-amir/AMIR-Optimizer)";
    public int MinRequestIntervalMs { get; set; } = 200;
    public int TimeoutSeconds { get; set; } = 15;
    /// <summary>Extracts are cut at a sentence boundary below this length.</summary>
    public int MaxLength { get; set; } = 1800;
}

public sealed class MockProfileOptions
{
    public const string Section = "MarketData:Profiles:Mock";

    /// <summary>SIMULATED descriptions for automated tests and the e2e stack only.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Company descriptions from the lead section of the company's Wikipedia article (Wikimedia REST <c>page/summary</c>,
/// an official, documented API; text licensed CC BY-SA 4.0, shown with attribution and a link). The article is the one
/// linked from the constituent table the catalogue was built from, so no name search is involved. Articles in other
/// language editions are resolved to the English article through the interlanguage link when one exists.
/// </summary>
public sealed class WikipediaProfileProvider(HttpClient http, IOptions<WikipediaProfileOptions> options) : ICompanyProfileProvider
{
    public const string ProviderName = "Wikipedia";

    public string Name => ProviderName;
    public string SourceName => "Wikipedia";
    public string Licence => "CC BY-SA 4.0";
    public bool IsConfigured => options.Value.Enabled;

    public async Task<ProviderCompanyProfile> GetAsync(string reference, CancellationToken ct)
    {
        var (lang, title) = ParseReference(reference);
        if (lang != "en" && await EnglishTitleAsync(lang, title, ct) is { } english)
            (lang, title) = ("en", english);

        var url = $"https://{lang}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title.Replace(' ', '_'))}?redirect=true";
        using var response = await SendAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ProviderCompanyProfile(false, null, null, lang, title, $"The Wikipedia article \"{title}\" ({lang}) does not exist.");
        await EnsureSuccessAsync(response, ct);
        using var doc = await ParseAsync(response, ct);
        return ParseSummary(doc.RootElement, lang, options.Value.MaxLength);
    }

    /// <summary>"en:Apple Inc." → ("en", "Apple Inc.").</summary>
    public static (string Lang, string Title) ParseReference(string reference)
    {
        var i = reference.IndexOf(':');
        if (i is < 2 or > 3 || i == reference.Length - 1) throw new ArgumentException($"Invalid article reference '{reference}'.", nameof(reference));
        return (reference[..i].ToLowerInvariant(), reference[(i + 1)..].Trim());
    }

    /// <summary>Maps a page/summary response; disambiguation pages and empty or very short extracts are "not found".</summary>
    public static ProviderCompanyProfile ParseSummary(JsonElement root, string lang, int maxLength)
    {
        var title = Str(root, "title");
        var type = Str(root, "type") ?? "standard";
        var pageUrl = root.TryGetProperty("content_urls", out var cu) && cu.TryGetProperty("desktop", out var d) ? Str(d, "page") : null;
        var language = Str(root, "lang") ?? lang;
        if (type == "disambiguation")
            return new ProviderCompanyProfile(false, null, pageUrl, language, title, "The linked Wikipedia page is a disambiguation page, not a company article.");
        var extract = Str(root, "extract")?.Trim();
        if (string.IsNullOrWhiteSpace(extract) || extract.Length < 40)
            return new ProviderCompanyProfile(false, null, pageUrl, language, title, "The Wikipedia article has no usable summary text.");
        return new ProviderCompanyProfile(true, Shorten(extract, maxLength), pageUrl, language, title);
    }

    /// <summary>Cuts long extracts after the last full sentence that fits (never mid-word).</summary>
    public static string Shorten(string text, int max)
    {
        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length <= max) return t;
        var cut = t.LastIndexOf(". ", max, StringComparison.Ordinal);
        if (cut > max / 2) return t[..(cut + 1)];
        var space = t.LastIndexOf(' ', max);
        return t[..(space > 0 ? space : max)] + "…";
    }

    private async Task<string?> EnglishTitleAsync(string lang, string title, CancellationToken ct)
    {
        var url = $"https://{lang}.wikipedia.org/w/api.php?action=query&prop=langlinks&lllang=en&redirects=1&format=json&formatversion=2&titles={Uri.EscapeDataString(title)}";
        using var response = await SendAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        using var doc = await ParseAsync(response, ct);
        return ParseEnglishTitle(doc.RootElement);
    }

    public static string? ParseEnglishTitle(JsonElement root)
    {
        if (!root.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array) return null;
        foreach (var page in pages.EnumerateArray())
            if (page.TryGetProperty("langlinks", out var links) && links.ValueKind == JsonValueKind.Array)
                foreach (var l in links.EnumerateArray())
                    if (Str(l, "lang") == "en" && Str(l, "title") is { } t) return t;
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(options.Value.UserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CompanyProfileProviderException(ProviderName, $"Request failed: {ex.Message}", inner: ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new CompanyProfileProviderException(ProviderName, "Rate limited (HTTP 429). Try again later.", isRateLimit: true);
        _ = await response.Content.ReadAsStringAsync(ct);
        throw new CompanyProfileProviderException(ProviderName, $"HTTP {(int)response.StatusCode}.");
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new CompanyProfileProviderException(ProviderName, "Malformed JSON response.", inner: ex);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Deterministic SIMULATED descriptions (tests and the e2e stack). References containing "Missing" are reported as not
/// found and references containing "Failing" throw, so the unavailable and error paths can be exercised.
/// </summary>
public sealed class MockProfileProvider(IOptions<MockProfileOptions> options) : ICompanyProfileProvider
{
    private int _calls;

    public string Name => "MockProfile";
    public string SourceName => "Simulated profile (tests only)";
    public string Licence => "Synthetic data";
    public bool IsConfigured => options.Value.Enabled;
    public int Calls => _calls;

    public Task<ProviderCompanyProfile> GetAsync(string reference, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var (lang, title) = WikipediaProfileProvider.ParseReference(reference);
        if (title.Contains("Failing", StringComparison.Ordinal)) throw new CompanyProfileProviderException(Name, "Simulated provider failure.");
        if (title.Contains("Missing", StringComparison.Ordinal))
            return Task.FromResult(new ProviderCompanyProfile(false, null, null, lang, title, "Simulated: no article text."));
        return Task.FromResult(new ProviderCompanyProfile(true,
            $"SIMULATED profile of {title}: synthetic text for automated tests. It does not describe the real company.",
            $"https://example.invalid/profile/{Uri.EscapeDataString(title)}", lang, title));
    }
}
