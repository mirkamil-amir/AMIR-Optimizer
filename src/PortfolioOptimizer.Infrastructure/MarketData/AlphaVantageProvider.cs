using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.Infrastructure.MarketData;

/// <summary>
/// Alpha Vantage (https://www.alphavantage.co/documentation/). Requires an API key. The free tier is limited to a
/// small number of requests per day and does not include the adjusted daily series (TIME_SERIES_DAILY_ADJUSTED is premium),
/// so by default raw closes only are stored – returns then exclude dividends. US-listed symbols are priced in USD.
/// </summary>
public sealed class AlphaVantageProvider(HttpClient http, IOptions<AlphaVantageOptions> options, TimeProvider clock) : IMarketDataProvider
{
    public const string Name = "AlphaVantage";
    private const int CompactDays = 100;

    public ProviderDescriptor Descriptor { get; } = new(
        Name,
        "Alpha Vantage",
        IsSimulated: false,
        RequiresApiKey: true,
        IsConfigured: !string.IsNullOrWhiteSpace(options.Value.ApiKey),
        ProvidesAdjustedClose: options.Value.UseAdjustedEndpoint,
        RateLimit: "Free tier: 25 requests/day (check current terms). Client self-limits to one request per " + options.Value.MinRequestIntervalMs + " ms.",
        Terms: "API key required; see Alpha Vantage terms of service. Adjusted series require a premium plan.");

    public async Task<ProviderHistory> GetHistoricalPricesAsync(string symbol, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        // "compact" returns the latest 100 points; anything older needs "full".
        var outputSize = today.DayNumber - startDate.DayNumber > CompactDays * 1.4 ? "full" : "compact";
        var function = o.UseAdjustedEndpoint ? "TIME_SERIES_DAILY_ADJUSTED" : "TIME_SERIES_DAILY";
        using var doc = await GetJsonAsync($"/query?function={function}&symbol={Uri.EscapeDataString(symbol)}&outputsize={outputSize}&datatype=json", symbol, cancellationToken);

        if (!doc.RootElement.TryGetProperty("Time Series (Daily)", out var series) || series.ValueKind != JsonValueKind.Object)
            throw new MarketDataProviderException(Name, symbol, "Response contains no daily time series.");

        var raw = new List<RawBar>();
        foreach (var day in series.EnumerateObject())
        {
            if (!DateOnly.TryParseExact(day.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var v = day.Value;
            raw.Add(new RawBar(date, Num(v, "1. open"), Num(v, "2. high"), Num(v, "3. low"), Num(v, "4. close"),
                o.UseAdjustedEndpoint ? Num(v, "5. adjusted close") : null,
                (long?)(Num(v, o.UseAdjustedEndpoint ? "6. volume" : "5. volume"))));
        }

        var normalized = PriceNormalizer.Normalize(raw, startDate, endDate, today);
        var warnings = normalized.Warnings.ToList();
        if (!o.UseAdjustedEndpoint) warnings.Add("Alpha Vantage free tier: raw closes only (no dividend/split adjustment).");
        // Alpha Vantage does not return currency for daily series; US listings are USD.
        return new ProviderHistory(symbol, "USD", normalized.Bars, raw.Count, normalized.Rejected, normalized.Duplicates, warnings);
    }

    public async Task<MarketQuote?> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"/query?function=GLOBAL_QUOTE&symbol={Uri.EscapeDataString(symbol)}", symbol, cancellationToken);
        if (!doc.RootElement.TryGetProperty("Global Quote", out var q) || q.ValueKind != JsonValueKind.Object) return null;
        var price = Num(q, "05. price");
        var day = q.TryGetProperty("07. latest trading day", out var d) ? d.GetString() : null;
        if (price is not > 0 || !DateOnly.TryParse(day, CultureInfo.InvariantCulture, out var date)) return null;
        return new MarketQuote(symbol, Math.Round((decimal)price.Value, 6), "USD", date.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc));
    }

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, string symbol, CancellationToken ct)
    {
        var key = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new MarketDataProviderException(Name, symbol, "No API key configured (MarketData__AlphaVantage__ApiKey).");

        using var response = await http.GetAsync($"{pathAndQuery}&apikey={Uri.EscapeDataString(key)}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new MarketDataProviderException(Name, symbol, $"HTTP {(int)response.StatusCode}.", isTransient: (int)response.StatusCode >= 500);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex) { throw new MarketDataProviderException(Name, symbol, "Response is not valid JSON.", inner: ex); }

        // Alpha Vantage reports errors and throttling with HTTP 200 and a message field.
        foreach (var field in new[] { "Error Message", "Note", "Information" })
        {
            if (doc.RootElement.TryGetProperty(field, out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                var text = msg.GetString() ?? field;
                doc.Dispose();
                throw new MarketDataProviderException(Name, symbol, Redact(text, key), isTransient: field != "Error Message");
            }
        }
        return doc;
    }

    private static string Redact(string text, string key) => text.Replace(key, "***", StringComparison.Ordinal);

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
