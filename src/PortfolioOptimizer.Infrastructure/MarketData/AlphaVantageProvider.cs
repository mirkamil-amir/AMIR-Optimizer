using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.Infrastructure.MarketData;

/// <summary>
/// Alpha Vantage (https://www.alphavantage.co/documentation/). Requires an API key. The free tier is limited to a
/// small number of requests per day and does not include the adjusted daily series (TIME_SERIES_DAILY_ADJUSTED is premium),
/// so by default raw closes only are stored – returns then exclude dividends. Covered venues: US, Xetra (.DEX), Euronext
/// Paris (.PAR) and Amsterdam (.AMS), London (.LON, prices in pence); Tokyo is not covered. Responses carry no currency,
/// so the venue's currency is used. See docs/market-data.md.
/// </summary>
public sealed class AlphaVantageProvider(HttpClient http, IOptions<AlphaVantageOptions> options, AlphaVantageQuota quota, TimeProvider clock) : IMarketDataProvider
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
        RateLimit: $"Free tier: {options.Value.DailyRequestLimit} requests/day and about 1 request/second (verified 2026-10-14). The client counts requests and stops at the daily limit.",
        Terms: "API key required; see Alpha Vantage terms of service. Adjusted series require a premium plan.");

    public async Task<ProviderHistory> GetHistoricalPricesAsync(string symbol, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        // "compact" returns the latest 100 points; anything older needs "full".
        var outputSize = today.DayNumber - startDate.DayNumber > CompactDays * 1.4 ? "full" : "compact";
        var function = o.UseAdjustedEndpoint ? "TIME_SERIES_DAILY_ADJUSTED" : "TIME_SERIES_DAILY";
        var avSymbol = Map(symbol);
        using var doc = await GetJsonAsync($"/query?function={function}&symbol={Uri.EscapeDataString(avSymbol)}&outputsize={outputSize}&datatype=json", symbol, cancellationToken);

        if (!doc.RootElement.TryGetProperty("Time Series (Daily)", out var series) || series.ValueKind != JsonValueKind.Object)
            throw new MarketDataProviderException(Name, symbol, "Response contains no daily time series.");

        var venue = Markets.ForSymbol(symbol);
        var unit = venue.Country == "GB" ? 100d : 1d; // London prices are in pence
        var raw = new List<RawBar>();
        foreach (var day in series.EnumerateObject())
        {
            if (!DateOnly.TryParseExact(day.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var v = day.Value;
            raw.Add(new RawBar(date, Num(v, "1. open") / unit, Num(v, "2. high") / unit, Num(v, "3. low") / unit, Num(v, "4. close") / unit,
                o.UseAdjustedEndpoint ? Num(v, "5. adjusted close") / unit : null,
                (long?)(Num(v, o.UseAdjustedEndpoint ? "6. volume" : "5. volume"))));
        }

        var normalized = PriceNormalizer.Normalize(raw, startDate, endDate, today);
        var warnings = normalized.Warnings.ToList();
        if (!o.UseAdjustedEndpoint) warnings.Add("Alpha Vantage free tier: raw closes only (no dividend/split adjustment).");
        // Alpha Vantage does not return the currency; prices are in the venue's currency (pence converted above).
        return new ProviderHistory(symbol, venue.Currency, normalized.Bars, raw.Count, normalized.Rejected, normalized.Duplicates, warnings);
    }

    public async Task<MarketQuote?> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"/query?function=GLOBAL_QUOTE&symbol={Uri.EscapeDataString(Map(symbol))}", symbol, cancellationToken);
        if (!doc.RootElement.TryGetProperty("Global Quote", out var q) || q.ValueKind != JsonValueKind.Object) return null;
        var price = Num(q, "05. price");
        var day = q.TryGetProperty("07. latest trading day", out var d) ? d.GetString() : null;
        // An empty object means the symbol is unknown to Alpha Vantage.
        if (price is not > 0 || !DateOnly.TryParse(day, CultureInfo.InvariantCulture, out var date)) return null;
        var venue = Markets.ForSymbol(symbol);
        var unit = venue.Country == "GB" ? 100m : 1m;
        var previous = Num(q, "08. previous close") is { } pc && pc > 0 ? Math.Round((decimal)pc / unit, 6) : (decimal?)null;
        // GLOBAL_QUOTE only gives the trading day: the time is set to that day's close (end of day, never "live").
        var close = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(17, 30)), Markets.TimeZone(venue));
        return new MarketQuote(symbol, Math.Round((decimal)price.Value / unit, 6), venue.Currency, close, previous);
    }

    public bool Supports(string symbol) => SymbolMapper.ForAlphaVantage(symbol) is not null;

    public int QuoteBatchSize => 1;

    public int? RemainingDailyRequests => quota.Remaining;

    public Task<IReadOnlyList<QuoteFetch>> GetLatestQuotesAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken) =>
        QuoteFetching.OneByOneAsync(this, symbols, cancellationToken);

    private static string Map(string symbol) =>
        SymbolMapper.ForAlphaVantage(symbol) ?? throw new MarketDataProviderException(Name, symbol, "Alpha Vantage does not cover this market (e.g. Tokyo listings or index levels).");

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, string symbol, CancellationToken ct)
    {
        var key = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new MarketDataProviderException(Name, symbol, "No API key configured (MarketData__AlphaVantage__ApiKey).");

        if (!quota.TryAcquire())
            throw new MarketDataProviderException(Name, symbol,
                $"Daily Alpha Vantage request limit ({options.Value.DailyRequestLimit}) reached; try again tomorrow or use another provider.", isTransient: true);

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
