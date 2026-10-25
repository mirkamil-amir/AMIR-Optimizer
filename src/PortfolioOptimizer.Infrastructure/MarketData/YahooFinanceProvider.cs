using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.Infrastructure.MarketData;

/// <summary>
/// Yahoo Finance v8 chart endpoint (<c>/v8/finance/chart/{symbol}</c>). No API key, provides raw and split/dividend
/// adjusted closes. The endpoint is unofficial and undocumented: it may change or throttle without notice, and Yahoo's
/// terms restrict use to personal, non-commercial purposes. See docs/market-data.md.
/// </summary>
public sealed class YahooFinanceProvider(HttpClient http, IOptions<YahooFinanceOptions> options, TimeProvider clock) : IMarketDataProvider
{
    public const string Name = "Yahoo";

    public ProviderDescriptor Descriptor { get; } = new(
        Name,
        "Yahoo Finance (unofficial chart API)",
        IsSimulated: false,
        RequiresApiKey: false,
        IsConfigured: options.Value.Enabled,
        ProvidesAdjustedClose: true,
        RateLimit: $"Unpublished; client self-limits to one request start per {options.Value.MinRequestIntervalMs} ms, at most {options.Value.MaxConcurrentRequests} at once, with retries on 429/5xx.",
        Terms: "Unofficial endpoint. Personal, non-commercial use only per Yahoo terms; availability not guaranteed.");

    public async Task<ProviderHistory> GetHistoricalPricesAsync(string symbol, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        var p1 = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var p2 = new DateTimeOffset(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var url = $"/v8/finance/chart/{Uri.EscapeDataString(ToYahooSymbol(symbol))}?period1={p1}&period2={p2}&interval=1d&events=div%2Csplits&includeAdjustedClose=true";
        JsonDocument doc;
        try
        {
            doc = await GetJsonAsync(url, symbol, cancellationToken);
        }
        catch (MarketDataProviderException ex) when (ex.Message.Contains("Data doesn't exist for startDate", StringComparison.OrdinalIgnoreCase))
        {
            // Yahoo answers HTTP 400 for a period without any trading day – typically before a recent listing (an IPO after
            // the requested start). That is "no prices in this period", not a failure of the symbol.
            return new ProviderHistory(symbol, "", [], 0, 0, 0,
                [$"No prices from {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd} (before the first trading day or not traded)."]);
        }
        using var _ = doc;
        var result = ChartResult(doc, symbol);

        var meta = result.GetProperty("meta");
        var providerCurrency = meta.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
        if (string.IsNullOrEmpty(providerCurrency)) throw new MarketDataProviderException(Name, symbol, "Response has no currency.");
        // London quotes are in pence (GBp): store prices in pounds so every price is in its ISO currency.
        var (currency, divisor) = CurrencyUnits.Normalize(providerCurrency);
        var gmtOffset = meta.TryGetProperty("gmtoffset", out var g) && g.TryGetInt32(out var off) ? off : 0;

        var raw = new List<RawBar>();
        if (result.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Array)
        {
            var quote = result.GetProperty("indicators").GetProperty("quote")[0];
            JsonElement? adj = result.GetProperty("indicators").TryGetProperty("adjclose", out var a) && a.GetArrayLength() > 0
                ? a[0].GetProperty("adjclose")
                : null;
            var open = Arr(quote, "open");
            var high = Arr(quote, "high");
            var low = Arr(quote, "low");
            var close = Arr(quote, "close");
            var volume = Arr(quote, "volume");

            var i = 0;
            foreach (var t in ts.EnumerateArray())
            {
                // Timestamps are the session open in UTC; the exchange-local date is obtained with the exchange offset.
                var local = DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()).AddSeconds(gmtOffset);
                raw.Add(new RawBar(
                    DateOnly.FromDateTime(local.UtcDateTime),
                    Scale(Num(open, i), divisor), Scale(Num(high, i), divisor), Scale(Num(low, i), divisor), Scale(Num(close, i), divisor),
                    adj is { } ad ? Scale(Num(ad, i), divisor) : null,
                    Num(volume, i) is { } v ? (long)v : null));
                i++;
            }
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var normalized = PriceNormalizer.Normalize(raw, startDate, endDate, today);
        return new ProviderHistory(symbol, currency, normalized.Bars, raw.Count, normalized.Rejected, normalized.Duplicates,
            normalized.Warnings,
            CompanyName: Str(meta, "longName") ?? Str(meta, "shortName"),
            Exchange: Str(meta, "fullExchangeName") ?? Str(meta, "exchangeName"),
            InstrumentType: Str(meta, "instrumentType"));
    }

    public async Task<MarketQuote?> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"/v8/finance/chart/{Uri.EscapeDataString(ToYahooSymbol(symbol))}?range=1d&interval=1d", symbol, cancellationToken);
        var meta = ChartResult(doc, symbol).GetProperty("meta");
        if (!meta.TryGetProperty("regularMarketPrice", out var p) || p.ValueKind != JsonValueKind.Number) return null;
        var price = p.GetDouble();
        if (!double.IsFinite(price) || price <= 0) return null;
        var time = meta.TryGetProperty("regularMarketTime", out var t) && t.TryGetInt64(out var sec)
            ? DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime
            : clock.GetUtcNow().UtcDateTime;
        var (currency, divisor) = CurrencyUnits.Normalize(Str(meta, "currency") ?? "");
        var previous = meta.TryGetProperty("chartPreviousClose", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : (double?)null;
        return new MarketQuote(symbol, Math.Round((decimal)price / divisor, 4), currency, time, Div(previous, divisor) is { } prev ? Math.Round(prev, 4) : null);
    }

    public bool Supports(string symbol) => true;

    /// <summary>The spark endpoint accepts at most 20 symbols per request.</summary>
    public int QuoteBatchSize => 20;

    public int HistoryParallelism => Math.Max(1, options.Value.MaxConcurrentRequests);

    public int? RemainingDailyRequests => null;

    /// <summary>
    /// Batch quotes from the spark endpoint (5-minute bars of the latest session): last price, its bar time and the previous
    /// close for up to 20 symbols in one request. Spark omits the currency, so the venue's currency is used; London
    /// listings are quoted in pence there as in the chart endpoint.
    /// </summary>
    public async Task<IReadOnlyList<QuoteFetch>> GetLatestQuotesAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return [];
        if (symbols.Count > QuoteBatchSize) throw new ArgumentException($"At most {QuoteBatchSize} symbols per request.", nameof(symbols));
        var bySymbol = symbols.ToDictionary(ToYahooSymbol, s => s, StringComparer.OrdinalIgnoreCase);
        var url = $"/v8/finance/spark?symbols={Uri.EscapeDataString(string.Join(',', bySymbol.Keys))}&range=1d&interval=5m";
        JsonDocument? doc = null;
        for (var attempt = 0; attempt < 2 && doc is null; attempt++)
        {
            try
            {
                doc = await GetJsonAsync(url, string.Join(',', symbols), cancellationToken);
            }
            catch (MarketDataProviderException) when (attempt == 0)
            {
                // Yahoo occasionally rejects a batch (even with HTTP 400) under load; pause and try once more.
                await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken);
            }
            catch (MarketDataProviderException)
            {
                // Still failing: fall back to one chart request per symbol so a bad batch never blanks 20 prices.
                return await QuoteFetching.OneByOneAsync(this, symbols, cancellationToken);
            }
        }

        using (doc!)
        {
            var results = new List<QuoteFetch>(symbols.Count);
            foreach (var (yahooSymbol, symbol) in bySymbol)
            {
                if (!doc.RootElement.TryGetProperty(yahooSymbol, out var item) || item.ValueKind != JsonValueKind.Object)
                {
                    results.Add(QuoteFetch.Fail(symbol, "No data for this symbol (unknown, delisted or not traded)."));
                    continue;
                }
                var closes = Arr(item, "close");
                var stamps = Arr(item, "timestamp");
                double? last = null;
                long? lastTime = null;
                if (closes is { } cl && stamps is { } st)
                {
                    for (var i = Math.Min(cl.GetArrayLength(), st.GetArrayLength()) - 1; i >= 0; i--)
                    {
                        if (Num(cl, i) is { } v && double.IsFinite(v) && v > 0 && st[i].TryGetInt64(out var ts))
                        {
                            last = v;
                            lastTime = ts;
                            break;
                        }
                    }
                }
                if (last is null || lastTime is null)
                {
                    results.Add(QuoteFetch.Fail(symbol, "No recent trade for this symbol."));
                    continue;
                }
                var venue = Markets.ForSymbol(symbol);
                var divisor = venue.Country == "GB" && !symbol.StartsWith('^') ? 100m : 1m;
                var previous = item.TryGetProperty("chartPreviousClose", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : (double?)null;
                results.Add(QuoteFetch.Ok(symbol, new MarketQuote(symbol, Math.Round((decimal)last.Value / divisor, 4), venue.Currency,
                    DateTimeOffset.FromUnixTimeSeconds(lastTime.Value).UtcDateTime, Div(previous, divisor) is { } prev ? Math.Round(prev, 4) : null)));
            }
            return results;
        }
    }

    /// <summary>Canonical symbols use Yahoo conventions; only US share classes need translating (BRK.B → BRK-B).</summary>
    public static string ToYahooSymbol(string symbol) => SymbolMapper.ForYahoo(symbol);

    private async Task<JsonDocument> GetJsonAsync(string url, string symbol, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(options.Value.UserAgent);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new MarketDataProviderException(Name, symbol, $"Symbol not found ({ErrorDescription(body) ?? "404"}).");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new MarketDataProviderException(Name, symbol, "The data source is rate limiting requests. Try again later.", isTransient: true);
        if (!response.IsSuccessStatusCode)
            throw new MarketDataProviderException(Name, symbol, $"HTTP {(int)response.StatusCode}: {ErrorDescription(body) ?? response.ReasonPhrase}", isTransient: (int)response.StatusCode >= 500);

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new MarketDataProviderException(Name, symbol, "Response is not valid JSON (the endpoint may have changed or returned a bot-check page).", inner: ex);
        }
    }

    private static JsonElement ChartResult(JsonDocument doc, string symbol)
    {
        if (!doc.RootElement.TryGetProperty("chart", out var chart))
            throw new MarketDataProviderException(Name, symbol, "Unexpected response shape (no 'chart').");
        if (chart.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            throw new MarketDataProviderException(Name, symbol, Str(err, "description") ?? "Unknown provider error.");
        if (!chart.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            throw new MarketDataProviderException(Name, symbol, "Response contains no result.");
        return result[0];
    }

    private static string? ErrorDescription(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("chart", out var c) && c.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                ? Str(e, "description")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? Scale(double? value, decimal divisor) => value / (double)divisor;

    private static decimal? Div(double? value, decimal divisor) =>
        value is { } v && double.IsFinite(v) ? (decimal)v / divisor : null;

    private static JsonElement? Arr(JsonElement quote, string name) =>
        quote.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    private static double? Num(JsonElement? arr, int i)
    {
        if (arr is not { } a || i >= a.GetArrayLength()) return null;
        var e = a[i];
        return e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
