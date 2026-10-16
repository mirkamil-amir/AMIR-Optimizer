using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Fx;

namespace PortfolioOptimizer.Infrastructure.Fx;

public sealed class EcbFxOptions
{
    public const string Section = "Fx:Ecb";
    public string BaseUrl { get; set; } = "https://data-api.ecb.europa.eu/service/";
    public int TimeoutSeconds { get; set; } = 30;
    public int MinRequestIntervalMs { get; set; } = 250;
}

/// <summary>
/// Euro foreign exchange reference rates of the European Central Bank (SDMX REST API, dataset EXR, daily, currency per 1 EUR,
/// published around 16:00 CET for each TARGET business day, history since 1999). Official, free, no key; ECB terms allow
/// reuse with attribution. They are reference rates for information, not transaction rates.
/// </summary>
public sealed class EcbFxProvider(HttpClient http, IOptions<EcbFxOptions> options) : IFxRateProvider
{
    /// <summary>Currencies with an ECB daily reference rate (2026).</summary>
    public static readonly IReadOnlySet<string> Currencies = new HashSet<string>
    {
        "USD", "JPY", "BGN", "CZK", "DKK", "GBP", "HUF", "PLN", "RON", "SEK", "CHF", "ISK", "NOK", "TRY", "AUD", "BRL", "CAD", "CNY",
        "HKD", "IDR", "ILS", "INR", "KRW", "MXN", "MYR", "NZD", "PHP", "SGD", "THB", "ZAR",
    };

    public string Name => FxSources.Ecb;
    public bool IsSimulated => false;
    public string Description => "Euro foreign exchange reference rates (daily, ~16:00 CET; reference rates, not transaction rates)";
    public IReadOnlySet<string> SupportedCurrencies => Currencies;

    public async Task<IReadOnlyList<FxObservation>> GetRatesAsync(IReadOnlyCollection<string> currencies, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var codes = currencies.Where(Currencies.Contains).Distinct().Order().ToList();
        if (codes.Count == 0 || to < from) return [];
        var url = $"{options.Value.BaseUrl.TrimEnd('/')}/data/EXR/D.{string.Join('+', codes)}.EUR.SP00.A" +
                  $"?startPeriod={from:yyyy-MM-dd}&endPeriod={to:yyyy-MM-dd}&format=csvdata&detail=dataonly";
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FxProviderException("The ECB data API could not be reached.", ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return [];   // "No results found" (e.g. a range of holidays)
            if (!response.IsSuccessStatusCode) throw new FxProviderException($"The ECB data API returned HTTP {(int)response.StatusCode}.");
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
    }

    /// <summary>Parses SDMX csvdata: header row, columns CURRENCY, CURRENCY_DENOM, TIME_PERIOD, OBS_VALUE (currency per EUR).</summary>
    public static IReadOnlyList<FxObservation> Parse(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return [];
        var header = lines[0].Split(',');
        int Col(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i : throw new FxProviderException($"ECB response has no {name} column.");
        int cur = Col("CURRENCY"), denom = Col("CURRENCY_DENOM"), date = Col("TIME_PERIOD"), value = Col("OBS_VALUE");
        var result = new List<FxObservation>();
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            if (f.Length <= Math.Max(Math.Max(cur, denom), Math.Max(date, value))) continue;
            // Only the documented direction is accepted: currency per 1 EUR.
            if (f[denom] != "EUR") throw new FxProviderException($"Unexpected ECB quote denominator {f[denom]}.");
            if (!DateOnly.TryParseExact(f[date], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (!decimal.TryParse(f[value], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) continue;   // NaN/blank on closures
            result.Add(new FxObservation(f[cur], d, v));
        }
        return result;
    }
}

/// <summary>
/// Simulated daily rates (weekdays only) for tests, the e2e stack and simulated prices. Deterministic: a random walk per
/// currency from a fixed origin, around realistic levels, so the same date always gives the same rate. NOT real rates.
/// </summary>
public sealed class MockFxProvider(TimeProvider clock) : IFxRateProvider
{
    public static readonly DateOnly Origin = new(1999, 1, 4);
    private static readonly ConcurrentDictionary<string, (DateOnly Until, DateOnly[] Dates, double[] Values)> Cache = new();

    private static readonly Dictionary<string, double> Levels = new()
    {
        ["USD"] = 1.10, ["GBP"] = 0.86, ["JPY"] = 158.0, ["CHF"] = 0.95, ["SEK"] = 11.3, ["NOK"] = 11.6, ["DKK"] = 7.46,
        ["PLN"] = 4.30, ["CAD"] = 1.50, ["AUD"] = 1.65, ["HKD"] = 8.6, ["SGD"] = 1.45, ["CNY"] = 7.8, ["INR"] = 92.0,
    };

    public string Name => FxSources.Mock;
    public bool IsSimulated => true;
    public string Description => "Simulated exchange rates – NOT real rates";
    public IReadOnlySet<string> SupportedCurrencies => EcbFxProvider.Currencies;

    public Task<IReadOnlyList<FxObservation>> GetRatesAsync(IReadOnlyCollection<string> currencies, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var result = new List<FxObservation>();
        foreach (var c in currencies.Where(SupportedCurrencies.Contains).Distinct())
        {
            var (_, dates, values) = Series(c, today);
            for (var i = 0; i < dates.Length; i++)
                if (dates[i] >= from && dates[i] <= to)
                    result.Add(new FxObservation(c, dates[i], Math.Round((decimal)values[i], 6)));
        }
        return Task.FromResult<IReadOnlyList<FxObservation>>(result);
    }

    private static (DateOnly, DateOnly[], double[]) Series(string currency, DateOnly today) =>
        Cache.AddOrUpdate(currency, _ => Generate(currency, today), (_, old) => old.Until >= today ? old : Generate(currency, today));

    private static (DateOnly, DateOnly[], double[]) Generate(string currency, DateOnly today)
    {
        var h = Hash(currency);
        var rng = new Random((int)(h & 0x7FFFFFFF));
        var level = Levels.GetValueOrDefault(currency, 1 + h % 50);
        var vol = 0.06 + (h >> 20) % 60 / 1000.0;   // 6% – 12% a year
        var dates = new List<DateOnly>();
        var values = new List<double>();
        var x = Math.Log(level);
        var mean = x;
        for (var d = Origin; d <= today; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (dates.Count > 0) x += 0.002 * (mean - x) + vol / Math.Sqrt(252) * Normal(rng);   // mean-reverting around the level
            dates.Add(d);
            values.Add(Math.Exp(x));
        }
        return (today, dates.ToArray(), values.ToArray());
    }

    private static double Normal(Random r)
    {
        var u1 = 1.0 - r.NextDouble();
        var u2 = r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static ulong Hash(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }
}
