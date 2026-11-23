using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.Infrastructure.MarketData;

public sealed class YahooFundamentalsOptions
{
    public const string Section = "MarketData:Fundamentals:Yahoo";

    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://query1.finance.yahoo.com";
    public string UserAgent { get; set; } = "Mozilla/5.0 (compatible; PortfolioOptimizer/1.0; personal research)";
    /// <summary>Spacing between request starts; one listing per request, one request at a time (4 listings per second).</summary>
    public int MinRequestIntervalMs { get; set; } = 250;
    public int TimeoutSeconds { get; set; } = 20;
}

public sealed class MockFundamentalsOptions
{
    public const string Section = "MarketData:Fundamentals:Mock";
    public bool Enabled { get; set; }
}

/// <summary>
/// Yahoo Finance fundamentals time series (<c>/ws/fundamentals-timeseries/v1/finance/timeseries/{symbol}</c>): annual
/// income-statement, balance-sheet and cash-flow figures (about four fiscal years) and trailing valuation figures, in the
/// currency the company reports in. Unofficial and undocumented like the chart API: personal, non-commercial use only per
/// Yahoo's terms; the response carries period ends but no publication dates.
/// </summary>
public sealed class YahooFundamentalsProvider(HttpClient http, IOptions<YahooFundamentalsOptions> options) : IFundamentalsProvider
{
    public const string ProviderName = "Yahoo";

    /// <summary>Annual figures requested (also those later cards need); the stock list uses <see cref="FundamentalMetrics.Concepts"/>.</summary>
    public static readonly string[] AnnualConcepts =
    [
        "TotalRevenue", "GrossProfit", "EBITDA", "EBIT", "PretaxIncome", "TaxProvision", "NetIncome", "DilutedEPS",
        "InvestedCapital", "TotalDebt", "StockholdersEquity", "CashAndCashEquivalents", "TotalAssets", "CurrentAssets",
        "CurrentLiabilities", "OrdinarySharesNumber", "OperatingCashFlow", "CapitalExpenditure", "FreeCashFlow", "CashDividendsPaid",
    ];

    /// <summary>Trailing figures requested (dated values, e.g. the market capitalization on the day it was computed).</summary>
    public static readonly string[] TrailingConcepts =
    [
        "MarketCap", "EnterpriseValue", "PeRatio", "ForwardPeRatio", "PbRatio", "PsRatio", "EnterprisesValueEBITDARatio",
    ];

    public string Name => ProviderName;
    public string SourceName => "Yahoo Finance (unofficial fundamentals API)";
    public bool IsConfigured => options.Value.Enabled;
    public bool IsSimulated => false;

    public async Task<ProviderFundamentals> GetAsync(string symbol, CancellationToken ct)
    {
        var types = string.Join(',', AnnualConcepts.Select(c => "annual" + c).Concat(TrailingConcepts.Select(c => "trailing" + c)));
        // Ten years back; the source returns what it has (about four fiscal years).
        var p2 = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var p1 = DateTimeOffset.UtcNow.AddYears(-10).ToUnixTimeSeconds();
        var url = $"/ws/fundamentals-timeseries/v1/finance/timeseries/{Uri.EscapeDataString(SymbolMapper.ForYahoo(symbol))}?type={types}&period1={p1}&period2={p2}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(options.Value.UserAgent);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new FundamentalsProviderException(ProviderName, $"Request failed: {ex.Message}", inner: ex);
        }
        using var _ = response;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new ProviderFundamentals(false, [], "the source does not know this listing.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new FundamentalsProviderException(ProviderName, "Rate limited (HTTP 429). Try again later.", isRateLimit: true);
        if (!response.IsSuccessStatusCode) throw new FundamentalsProviderException(ProviderName, $"HTTP {(int)response.StatusCode}.");
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new FundamentalsProviderException(ProviderName, "Response is not valid JSON (the endpoint may have changed).", inner: ex);
        }
    }

    /// <summary>Parses a timeseries response; empty series are skipped, London pence are converted to pounds.</summary>
    public static ProviderFundamentals Parse(JsonElement root)
    {
        if (!root.TryGetProperty("timeseries", out var ts) || ts.ValueKind != JsonValueKind.Object)
            throw new FundamentalsProviderException(ProviderName, "Unexpected response shape (no 'timeseries').");
        if (ts.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            throw new FundamentalsProviderException(ProviderName, Str(err, "description") ?? "Unknown provider error.");
        var facts = new List<ProviderFundamentalFact>();
        if (ts.TryGetProperty("result", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("meta", out var meta) || !meta.TryGetProperty("type", out var typeArr)
                    || typeArr.ValueKind != JsonValueKind.Array || typeArr.GetArrayLength() == 0) continue;
                var type = typeArr[0].GetString() ?? "";
                var (periodType, concept) = type.StartsWith("annual", StringComparison.Ordinal) ? (FundamentalMetrics.FiscalYear, type["annual".Length..])
                    : type.StartsWith("trailing", StringComparison.Ordinal) ? (FundamentalMetrics.Trailing, type["trailing".Length..])
                    : ("", "");
                if (concept.Length == 0 || !r.TryGetProperty(type, out var points) || points.ValueKind != JsonValueKind.Array) continue;
                foreach (var p in points.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue; // the source pads missing years with null
                    if (!DateOnly.TryParseExact(Str(p, "asOfDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) continue;
                    if (!p.TryGetProperty("reportedValue", out var rv) || !rv.TryGetProperty("raw", out var raw) || !raw.TryGetDouble(out var value)
                        || !double.IsFinite(value)) continue;
                    var currency = Str(p, "currencyCode");
                    var amount = (decimal)value;
                    if (currency is not null)
                    {
                        var (iso, divisor) = CurrencyUnits.Normalize(currency);
                        currency = iso;
                        amount /= divisor;
                    }
                    facts.Add(new ProviderFundamentalFact(concept, periodType, end, amount, currency));
                }
            }
        }
        return facts.Count == 0 ? new ProviderFundamentals(false, [], "the source has no figures for this listing.") : new ProviderFundamentals(true, facts);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Deterministic SIMULATED figures (tests and the e2e stack). Symbols containing "MISSING" have none, symbols containing
/// "FAIL" throw, and symbols starting with "BANK" have no EBIT (like banks), so every path can be exercised.
/// </summary>
public sealed class MockFundamentalsProvider(IOptions<MockFundamentalsOptions> options) : IFundamentalsProvider
{
    private int _calls;

    public string Name => "MockFundamentals";
    public string SourceName => "Simulated figures (tests only)";
    public bool IsConfigured => options.Value.Enabled;
    public bool IsSimulated => true;
    public int Calls => _calls;

    public Task<ProviderFundamentals> GetAsync(string symbol, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (symbol.Contains("FAIL", StringComparison.OrdinalIgnoreCase)) throw new FundamentalsProviderException(Name, "Simulated provider failure.");
        if (symbol.Contains("MISSING", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new ProviderFundamentals(false, [], "Simulated: no figures."));
        // Scale from the symbol so stocks differ but stay reproducible.
        var scale = 1 + symbol.Sum(c => c) % 50;
        const string ccy = "USD";
        decimal M(decimal v) => v * scale * 1_000_000m;
        var y2 = new DateOnly(2025, 12, 31);
        var y1 = new DateOnly(2024, 12, 31);
        var facts = new List<ProviderFundamentalFact>
        {
            new("MarketCap", FundamentalMetrics.Trailing, new DateOnly(2026, 1, 15), M(5000), ccy),
            new("TotalRevenue", FundamentalMetrics.FiscalYear, y1, M(1000), ccy),
            new("TotalRevenue", FundamentalMetrics.FiscalYear, y2, M(1100), ccy),
            new("FreeCashFlow", FundamentalMetrics.FiscalYear, y2, M(150), ccy),
            new("PretaxIncome", FundamentalMetrics.FiscalYear, y2, M(200), ccy),
            new("TaxProvision", FundamentalMetrics.FiscalYear, y2, M(50), ccy),
            new("InvestedCapital", FundamentalMetrics.FiscalYear, y1, M(1400), ccy),
            new("InvestedCapital", FundamentalMetrics.FiscalYear, y2, M(1600), ccy),
        };
        if (!symbol.StartsWith("BANK", StringComparison.OrdinalIgnoreCase))
        {
            facts.Add(new("EBIT", FundamentalMetrics.FiscalYear, y2, M(240), ccy));
            facts.Add(new("EBITDA", FundamentalMetrics.FiscalYear, y2, M(300), ccy));
            facts.Add(new("PeRatio", FundamentalMetrics.Trailing, new DateOnly(2026, 1, 15), 25m, null));
        }
        return Task.FromResult(new ProviderFundamentals(true, facts));
    }
}
