using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Fx;

/// <summary>Provider names and the source chosen for a price source (simulated prices are always paired with simulated FX).</summary>
public static class FxSources
{
    public const string Ecb = "ECB";
    public const string Mock = "MockFx";
}

/// <summary>One series converted into the base currency, with what was needed to do it.</summary>
/// <param name="DroppedPoints">Price points without a usable rate (they are removed, never filled).</param>
public sealed record ConvertedSeries(PriceSeries Series, string NativeCurrency, int DroppedPoints, FxQuote? LastQuote);

/// <summary>The result of converting several series; <see cref="Excluded"/> lists symbols that could not be converted, with the reason.</summary>
public sealed record CurrencyConversionResult(
    string BaseCurrency,
    IReadOnlyDictionary<string, ConvertedSeries> Series,
    IReadOnlyDictionary<string, string> Excluded,
    IReadOnlyList<string> Notes,
    string? FxSource,
    bool FxSimulated)
{
    public Dictionary<string, PriceSeries> PriceSeries() => Series.ToDictionary(kv => kv.Key, kv => kv.Value.Series, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> NativeCurrencies => Series.Values.Select(s => s.NativeCurrency).Distinct().Order().ToList();
}

/// <summary>Pure conversion of native-currency series (unit tested without a database).</summary>
public static class CurrencyConverter
{
    /// <summary>A symbol is excluded (not silently thinned) when more than this share of its points has no usable rate.</summary>
    public const double MaxDroppedShare = 0.05;

    /// <summary>
    /// <c>base_price[t] = native_price[t] × rate(native→base, t)</c>, the rate dated like the price (carry-forward policy of
    /// <see cref="FxTable"/>). Returns from the converted prices therefore satisfy
    /// <c>1 + r_base = (1 + r_native) × (1 + r_fx)</c>. A series already in the base currency is returned unchanged
    /// (no conversion, no rounding), so converting twice is impossible.
    /// </summary>
    public static ConvertedSeries Convert(PriceSeries native, string nativeCurrency, string baseCurrency, FxTable? fx)
    {
        if (nativeCurrency == baseCurrency) return new ConvertedSeries(native, nativeCurrency, 0, null);
        if (fx is null) throw new FxRateUnavailableException(nativeCurrency, baseCurrency, DateOnly.MinValue, "no exchange rates are loaded");
        var points = new List<PricePoint>(native.Points.Count);
        var dropped = 0;
        FxQuote? last = null;
        foreach (var p in native.Points)
        {
            if (fx.TryRate(nativeCurrency, baseCurrency, p.Date, out var q, out _))
            {
                points.Add(new PricePoint(p.Date, p.Price * q!.Rate));
                last = q;
            }
            else dropped++;
        }
        return new ConvertedSeries(native with { Points = points }, nativeCurrency, dropped, last);
    }
}

/// <summary>
/// Central FX access: loads (and caches in <c>FxRates</c>) the observations needed for a conversion from the configured
/// provider, and converts price series. No other code converts currencies. Missing data is an explicit error or an
/// excluded symbol with its reason – never a rate of 1.0 or today's rate for historical dates.
/// </summary>
/// <summary>Remembers recent fetch attempts per range (singleton per host) so that a range the provider has no data for yet
/// (e.g. a holiday) is not requested again on every call.</summary>
public sealed class FxFetchThrottle
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, DateTime> _attempts = new();

    /// <summary>True when the range may be fetched now (and records the attempt).</summary>
    public bool TryBegin(string key, DateTime now)
    {
        if (_attempts.TryGetValue(key, out var last) && now - last < Interval) return false;
        _attempts[key] = now;
        return true;
    }
}

public sealed class FxService(IApplicationDbContext db, IEnumerable<IFxRateProvider> providers, PriceQueryService prices, IMarketDataProviderRegistry marketData,
    FxFetchThrottle throttle, TimeProvider clock,
    ILogger<FxService> logger)
{

    /// <summary>Simulated prices are converted with simulated rates, real prices with the ECB reference rates.</summary>
    public IFxRateProvider ProviderFor(bool simulatedPrices)
    {
        var name = simulatedPrices ? FxSources.Mock : FxSources.Ecb;
        return providers.FirstOrDefault(p => p.Name == name)
               ?? throw new InvalidOperationException($"FX provider {name} is not registered.");
    }

    /// <summary>True when the active market-data source is simulated (its prices are then converted with simulated rates).</summary>
    public async Task<bool> ActiveSourceIsSimulatedAsync(CancellationToken ct)
    {
        var app = await prices.SettingsAsync(ct);
        return marketData.Providers.FirstOrDefault(p => p.Name == app.ActiveProvider)?.IsSimulated ?? false;
    }

    /// <summary>Currencies that can be converted with the FX source matching the active price source (EUR included).</summary>
    public async Task<IReadOnlySet<string>> SupportedCurrenciesAsync(CancellationToken ct) => SupportedCurrencies(await ActiveSourceIsSimulatedAsync(ct));

    public IReadOnlySet<string> SupportedCurrencies(bool simulatedPrices) =>
        new HashSet<string>(ProviderFor(simulatedPrices).SupportedCurrencies.Append(FxTable.Pivot));

    /// <summary>Observations for <paramref name="currencies"/> covering [from − carry-forward window, to], fetched if missing.</summary>
    public async Task<FxTable> GetTableAsync(IEnumerable<string> currencies, DateOnly from, DateOnly to, bool simulatedPrices, CancellationToken ct)
    {
        var provider = ProviderFor(simulatedPrices);
        var needed = currencies.Select(Domain.Currencies.Normalize).Where(c => c != FxTable.Pivot).Distinct().ToList();
        var unsupported = needed.Where(c => !provider.SupportedCurrencies.Contains(c)).ToList();
        if (unsupported.Count > 0)
            throw new ValidationException("currency", $"{provider.Name} publishes no rates for {string.Join(", ", unsupported)}; such assets cannot be converted.");
        var start = from.AddDays(-(FxTable.DefaultMaxCarryForwardDays + 3));
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var end = to > today ? today : to;
        if (needed.Count > 0) await EnsureAsync(provider, needed, start, end, today, ct);

        var rows = await db.FxRates.AsNoTracking()
            .Where(r => r.Source == provider.Name && needed.Contains(r.Currency) && r.Date >= start && r.Date <= end)
            .Select(r => new { r.Currency, r.Date, r.UnitsPerEur })
            .ToListAsync(ct);
        // The last observation before the window as well, so that a gap is reported with its real age instead of "no rates".
        var earlier = await db.FxRates.AsNoTracking()
            .Where(r => r.Source == provider.Name && needed.Contains(r.Currency) && r.Date < start)
            .GroupBy(r => r.Currency)
            .Select(g => g.OrderByDescending(r => r.Date).Select(r => new { r.Currency, r.Date, r.UnitsPerEur }).First())
            .ToListAsync(ct);
        return new FxTable(provider.Name, provider.IsSimulated, rows.Concat(earlier).Select(r => (r.Currency, r.Date, (double)r.UnitsPerEur)));
    }

    /// <summary>Converts native series of the given assets into <paramref name="baseCurrency"/>.</summary>
    public async Task<CurrencyConversionResult> ConvertAsync(IReadOnlyDictionary<string, PriceSeries> nativeSeries, IReadOnlyDictionary<string, string> nativeCurrencyBySymbol,
        string baseCurrency, CancellationToken ct)
    {
        baseCurrency = Domain.Currencies.Normalize(baseCurrency);
        var converted = new Dictionary<string, ConvertedSeries>(StringComparer.OrdinalIgnoreCase);
        var excluded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var foreign = nativeSeries.Where(kv => kv.Value.Points.Count > 0 && nativeCurrencyBySymbol.TryGetValue(kv.Key, out var c) && c != baseCurrency).ToList();
        var simulated = nativeSeries.Values.Any(s => s.IsSimulated);
        FxTable? fx = null;
        if (foreign.Count > 0)
        {
            var provider = ProviderFor(simulated);
            var currencies = foreign.Select(kv => nativeCurrencyBySymbol[kv.Key]).Append(baseCurrency).Distinct().ToList();
            var unsupported = currencies.Where(c => c != FxTable.Pivot && !provider.SupportedCurrencies.Contains(c)).ToHashSet();
            foreach (var (symbol, _) in foreign.Where(kv => unsupported.Contains(nativeCurrencyBySymbol[kv.Key])))
                excluded[symbol] = $"{provider.Name} publishes no {nativeCurrencyBySymbol[symbol]} rates, so its prices cannot be converted to {baseCurrency}.";
            if (unsupported.Contains(baseCurrency))
                throw new ValidationException("currency", $"{provider.Name} publishes no {baseCurrency} rates; choose another base currency.");
            var dated = foreign.Where(kv => !excluded.ContainsKey(kv.Key)).ToList();
            if (dated.Count > 0)
                fx = await GetTableAsync(currencies.Where(c => !unsupported.Contains(c)), dated.Min(kv => kv.Value.Points[0].Date), dated.Max(kv => kv.Value.Points[^1].Date), simulated, ct);
        }

        foreach (var (symbol, series) in nativeSeries)
        {
            if (excluded.ContainsKey(symbol)) continue;
            if (!nativeCurrencyBySymbol.TryGetValue(symbol, out var native))
            {
                excluded[symbol] = "the listing has no currency";
                continue;
            }
            var c = CurrencyConverter.Convert(series, native, baseCurrency, native == baseCurrency ? null : fx);
            if (series.Points.Count > 0 && c.DroppedPoints > series.Points.Count * CurrencyConverter.MaxDroppedShare)
            {
                excluded[symbol] = $"{c.DroppedPoints} of {series.Points.Count} prices have no {native}→{baseCurrency} rate within {FxTable.DefaultMaxCarryForwardDays} days ({fx?.Source}).";
                continue;
            }
            if (c.DroppedPoints > 0) notes.Add($"{symbol}: {c.DroppedPoints} price(s) dropped – no {native}→{baseCurrency} rate within {FxTable.DefaultMaxCarryForwardDays} days.");
            converted[symbol] = c;
        }

        var nativeCurrencies = converted.Values.Where(v => v.NativeCurrency != baseCurrency).Select(v => v.NativeCurrency).Distinct().Order().ToList();
        if (nativeCurrencies.Count > 0 && fx is not null)
            notes.Insert(0, $"Prices in {string.Join(", ", nativeCurrencies)} were converted to {baseCurrency} with the {fx.Source} rate of each price date " +
                            $"(units of {baseCurrency} per unit of the local currency; weekends/holidays use the previous rate, at most {FxTable.DefaultMaxCarryForwardDays} days old). " +
                            "Returns in the base currency include currency movements. Closing prices of different markets are not simultaneous.");
        return new CurrencyConversionResult(baseCurrency, converted, excluded, notes, fx?.Source, fx?.IsSimulated ?? false);
    }

    public async Task<Dictionary<string, string>> CurrenciesOfAsync(IReadOnlyCollection<string> symbols, CancellationToken ct) =>
        await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, a => a.Currency, StringComparer.OrdinalIgnoreCase, ct);

    /// <summary>Loads native series for <paramref name="symbols"/> and converts them (the usual entry point for analyses).</summary>
    public async Task<CurrencyConversionResult> GetSeriesInCurrencyAsync(IReadOnlyCollection<string> symbols, string source, DateOnly? from, DateOnly? to,
        string baseCurrency, CancellationToken ct, int? lastN = null)
    {
        var series = await prices.GetSeriesAsync(symbols, source, from, to, ct, lastN);
        var currencies = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, a => a.Currency, StringComparer.OrdinalIgnoreCase, ct);
        return await ConvertAsync(series, currencies, baseCurrency, ct);
    }

    /// <summary>
    /// Latest usable rate for valuing current holdings. A rate older than the carry-forward window counts as unavailable
    /// (the caller shows "cannot be valued" instead of a total).
    /// </summary>
    public async Task<FxQuote> LatestAsync(string from, string to, bool simulatedPrices, CancellationToken ct)
    {
        from = Domain.Currencies.Normalize(from);
        to = Domain.Currencies.Normalize(to);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (from == to) return new FxQuote(from, to, 1, today, today, 0, "identity", simulatedPrices);
        var table = await GetTableAsync([from, to], today, today, simulatedPrices, ct);
        return table.Rate(from, to, today);
    }

    private async Task EnsureAsync(IFxRateProvider provider, List<string> currencies, DateOnly start, DateOnly end, DateOnly today, CancellationToken ct)
    {
        var coverage = await db.FxRates.AsNoTracking()
            .Where(r => r.Source == provider.Name && currencies.Contains(r.Currency))
            .GroupBy(r => r.Currency)
            .Select(g => new { Currency = g.Key, Min = g.Min(r => r.Date), Max = g.Max(r => r.Date) })
            .ToListAsync(ct);
        var missing = currencies.Where(c =>
        {
            var cov = coverage.FirstOrDefault(x => x.Currency == c);
            // Covered when stored data reaches back to the start and forward to the end (or to yesterday's publication).
            // Rates are published on weekdays (the day's rate in the afternoon): data up to the previous weekday counts as complete.
            return cov is null || cov.Min > start || cov.Max < PreviousWeekday(end);
        }).ToList();
        if (missing.Count == 0) return;

        var key = $"{provider.Name}:{string.Join(",", missing.Order())}:{start}:{end}";
        var now = clock.GetUtcNow().UtcDateTime;
        if (!throttle.TryBegin(key, now)) return;

        // Fetch only the uncovered part per currency (one request for the union of ranges).
        var fetchFrom = missing.Select(c => coverage.FirstOrDefault(x => x.Currency == c) is { } cov && cov.Min <= start ? cov.Max.AddDays(1) : start).Min();
        IReadOnlyList<FxObservation> observations;
        try
        {
            observations = await provider.GetRatesAsync(missing, fetchFrom, end, ct);
        }
        catch (FxProviderException ex)
        {
            // Stored rates (if any) are still used; lookups beyond them fail explicitly.
            logger.LogWarning("FX provider {Provider} failed for {Currencies}: {Message}", provider.Name, string.Join(",", missing), ex.Message);
            return;
        }

        var rows = observations.Where(o => o.UnitsPerEur > 0 && missing.Contains(o.Currency))
            .Select(o => new FxRate
            {
                Source = provider.Name, Currency = o.Currency, Date = o.Date, UnitsPerEur = o.UnitsPerEur,
                IsSimulated = provider.IsSimulated, RetrievedAtUtc = now,
            })
            .ToList();
        await db.UpsertFxRatesAsync(rows, ct);
        logger.LogInformation("Stored {Count} {Provider} FX observations for {Currencies} ({From}–{To})", rows.Count, provider.Name, string.Join(",", missing), fetchFrom, end);
    }

    private static DateOnly PreviousWeekday(DateOnly d)
    {
        var p = d.AddDays(-1);
        while (p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) p = p.AddDays(-1);
        return p;
    }
}
