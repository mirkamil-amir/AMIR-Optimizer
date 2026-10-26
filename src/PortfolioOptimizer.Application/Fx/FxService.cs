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
/// <param name="Missing">Why those points had no rate (null when none was dropped).</param>
public sealed record ConvertedSeries(PriceSeries Series, string NativeCurrency, int DroppedPoints, FxQuote? LastQuote, FxMissing? Missing = null);

/// <summary>
/// Price dates without a usable rate, by cause – each cause has a different remedy, so they are never reported as one count.
/// </summary>
/// <param name="BeforeFirstRate">Before the first stored rate of a leg: that history is not stored (not downloaded yet, or not published).</param>
/// <param name="InGaps">Between stored rates, more than the carry-forward window after the previous one.</param>
/// <param name="AfterLastRate">More than the carry-forward window after the latest stored rate.</param>
/// <param name="FirstRate">Earliest date on which every leg has a stored rate (null: a leg has none).</param>
/// <param name="LastRate">Latest date on which every leg has a stored rate.</param>
public sealed record FxMissing(int BeforeFirstRate, int InGaps, int AfterLastRate, DateOnly? FirstRate, DateOnly? LastRate)
{
    public string Describe()
    {
        var parts = new List<string>();
        if (FirstRate is null) parts.Add($"{BeforeFirstRate} without any stored rate");
        else if (BeforeFirstRate > 0) parts.Add($"{BeforeFirstRate} dated before the first stored rate ({FirstRate:yyyy-MM-dd})");
        if (InGaps > 0) parts.Add($"{InGaps} in gaps longer than {FxTable.DefaultMaxCarryForwardDays} days between stored rates");
        if (AfterLastRate > 0) parts.Add($"{AfterLastRate} after the latest stored rate ({LastRate:yyyy-MM-dd})");
        return string.Join(", ", parts);
    }
}

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
        if (nativeCurrency == baseCurrency || native.Points.Count == 0) return new ConvertedSeries(native, nativeCurrency, 0, null);
        if (fx is null) throw new FxRateUnavailableException(nativeCurrency, baseCurrency, DateOnly.MinValue, "no exchange rates are loaded");
        var points = new List<PricePoint>(native.Points.Count);
        int before = 0, inGaps = 0, after = 0;
        var legs = new[] { nativeCurrency, baseCurrency }.Where(c => c != FxTable.Pivot).ToList();
        var firsts = legs.Select(fx.FirstObservation).ToList();
        DateOnly? first = firsts.Any(d => d is null) ? null : firsts.Max();
        DateOnly? lastRate = first is null ? null : legs.Select(fx.LastObservation).Min();
        FxQuote? last = null;
        foreach (var p in native.Points)
        {
            if (fx.TryRate(nativeCurrency, baseCurrency, p.Date, out var q, out _))
            {
                points.Add(new PricePoint(p.Date, p.Price * q!.Rate));
                last = q;
            }
            else if (first is null || p.Date < first) before++;
            else if (p.Date.DayNumber - lastRate!.Value.DayNumber > fx.MaxCarryForwardDays) after++;
            else inGaps++;
        }
        var dropped = before + inGaps + after;
        return new ConvertedSeries(native with { Points = points }, nativeCurrency, dropped, last,
            dropped == 0 ? null : new FxMissing(before, inGaps, after, first, lastRate));
    }
}

/// <summary>
/// Central FX access: loads (and caches in <c>FxRates</c>) the observations needed for a conversion from the configured
/// provider, and converts price series. No other code converts currencies. Missing data is an explicit error or an
/// excluded symbol with its reason – never a rate of 1.0 or today's rate for historical dates.
/// </summary>
/// <summary>Remembers recent fetch attempts per range (singleton per host) so that a range the provider has no data for yet
/// (e.g. a holiday) or that just failed is not requested again on every call; holds how long callers wait for a download.</summary>
public sealed class FxFetchThrottle
{
    /// <summary>A range that failed or had no data is requested again after this interval.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, DateTime> _attempts = new();

    /// <summary>Longest wait for a download inside one web request; stored rates are used after it (the download continues).</summary>
    public TimeSpan ProviderWait { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Longest wait inside a background job (ML experiment, backtest, comparison), which would otherwise exclude
    /// assets for rates that are still arriving.</summary>
    public TimeSpan BackgroundProviderWait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>True when the range may be fetched now (and records the attempt).</summary>
    public bool TryBegin(string key, DateTime now)
    {
        if (_attempts.TryGetValue(key, out var last) && now - last < RetryInterval) return false;
        _attempts[key] = now;
        return true;
    }
}

public sealed class FxService(IApplicationDbContext db, IEnumerable<IFxRateProvider> providers, PriceQueryService prices, IMarketDataProviderRegistry marketData,
    FxFetchThrottle throttle, FxDownloader downloader, TimeProvider clock,
    ILogger<FxService> logger)
{
    private bool _backgroundWait;
    /// <summary>Unfinished or failed downloads met by this scope (user-facing sentences, added to conversion notes).</summary>
    private readonly List<string> _downloadNotes = [];

    /// <summary>Called for background-job scopes: wait up to <see cref="FxFetchThrottle.BackgroundProviderWait"/> for missing rates.</summary>
    public void UseBackgroundWait() => _backgroundWait = true;

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

    /// <summary>Currently published or discontinued with stored history (historical conversions only).</summary>
    private static bool HasRates(IFxRateProvider provider, string currency) =>
        currency == FxTable.Pivot || provider.SupportedCurrencies.Contains(currency) || provider.DiscontinuedCurrencies.ContainsKey(currency);

    /// <summary>
    /// A missing currency is fetched together with every other published currency when the range is at most this long: one
    /// ECB request for all 29 currencies took 0.2–0.4 s for 52 days and 0.3–1.2 s for a year (measured 2026-10-19), so the
    /// rate board and further currencies need no provider call afterwards. Longer ranges fetch only what is missing.
    /// </summary>
    public const int FetchAllCurrenciesMaxDays = 400;


    /// <summary>Observations for <paramref name="currencies"/> covering [from − carry-forward window, to], fetched if missing.</summary>
    public async Task<FxTable> GetTableAsync(IEnumerable<string> currencies, DateOnly from, DateOnly to, bool simulatedPrices, CancellationToken ct)
    {
        var provider = ProviderFor(simulatedPrices);
        var needed = currencies.Select(Domain.Currencies.Normalize).Where(c => c != FxTable.Pivot).Distinct().ToList();
        var unsupported = needed.Where(c => !HasRates(provider, c)).ToList();
        if (unsupported.Count > 0)
            throw new ValidationException("currency", $"No reference rates are published for {string.Join(", ", unsupported)}; such assets cannot be converted.");
        var start = from.AddDays(-(FxTable.DefaultMaxCarryForwardDays + 3));
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var end = to > today ? today : to;
        if (needed.Count > 0) await EnsureAsync(provider, needed, start, end, ct);

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
            var unsupported = currencies.Where(c => !HasRates(provider, c)).ToHashSet();
            foreach (var (symbol, _) in foreign.Where(kv => unsupported.Contains(nativeCurrencyBySymbol[kv.Key])))
                excluded[symbol] = $"No {nativeCurrencyBySymbol[symbol]} reference rates are published, so its prices cannot be converted to {baseCurrency}.";
            if (unsupported.Contains(baseCurrency))
                throw new ValidationException("currency", $"No {baseCurrency} reference rates are published; choose another base currency.");
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
                excluded[symbol] = $"{c.DroppedPoints} of {series.Points.Count} prices have no {native}→{baseCurrency} rate within {FxTable.DefaultMaxCarryForwardDays} days ({c.Missing!.Describe()}).";
                continue;
            }
            if (c.DroppedPoints > 0) notes.Add($"{symbol}: {c.DroppedPoints} price(s) dropped – no {native}→{baseCurrency} rate within {FxTable.DefaultMaxCarryForwardDays} days ({c.Missing!.Describe()}).");
            converted[symbol] = c;
        }

        var nativeCurrencies = converted.Values.Where(v => v.NativeCurrency != baseCurrency).Select(v => v.NativeCurrency).Distinct().Order().ToList();
        if (nativeCurrencies.Count > 0 && fx is not null)
            notes.Insert(0, $"Prices in {string.Join(", ", nativeCurrencies)} were converted to {baseCurrency} with the reference rate of each price date " +
                            $"(units of {baseCurrency} per unit of the local currency; weekends/holidays use the previous rate, at most {FxTable.DefaultMaxCarryForwardDays} days old). " +
                            "Returns in the base currency include currency movements. Closing prices of different markets are not simultaneous.");
        notes.AddRange(_downloadNotes.Distinct());
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
    /// <summary>Published daily rates of a pair over the last <paramref name="days"/> calendar days (for charts).</summary>
    public async Task<Dtos.FxHistoryDto> HistoryAsync(string from, string to, int days, CancellationToken ct)
    {
        from = Domain.Currencies.Normalize(from);
        to = Domain.Currencies.Normalize(to);
        if (days is < 7 or > 3700) throw new ValidationException("days", "Choose between 7 and 3700 days.");
        var simulated = await ActiveSourceIsSimulatedAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var start = today.AddDays(-days);
        var table = await GetTableAsync([from, to], start, today, simulated, ct);
        var points = table.ObservedSeries(from, to, start, today).Select(p => new Dtos.FxPointDto(p.Date, p.Rate)).ToList();
        return new Dtos.FxHistoryDto(from, to, table.Source, table.IsSimulated, points,
            $"{(table.IsSimulated ? "SIMULATED rates" : "Euro reference rates")}: one rate per business day, units of {to} per one {from}. " +
            "Reference rates for information – not rates at which money can be exchanged.");
    }

    /// <summary>Latest published rate of each quote currency against <paramref name="baseCurrency"/>, with 1-day and 1-month changes.</summary>
    public async Task<Dtos.FxBoardDto> BoardAsync(string baseCurrency, IReadOnlyCollection<string> quotes, CancellationToken ct)
    {
        baseCurrency = Domain.Currencies.Normalize(baseCurrency);
        var list = quotes.Select(Domain.Currencies.Normalize).Where(q => q != baseCurrency).Distinct().Take(32).ToList();
        var simulated = await ActiveSourceIsSimulatedAsync(ct);
        var provider = ProviderFor(simulated);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (provider.DiscontinuedCurrencies.TryGetValue(baseCurrency, out var baseUntil))
            throw new ValidationException("base", FxDiscontinued.Reason(baseCurrency, baseUntil));
        // Discontinued currencies have no current rate: explained, never fetched (their series ended).
        var unsupported = list.Where(q => q != FxTable.Pivot && !provider.SupportedCurrencies.Contains(q)).ToHashSet();
        var table = await GetTableAsync(list.Where(q => !unsupported.Contains(q)).Append(baseCurrency), today.AddDays(-45), today, simulated, ct);
        var rows = new List<Dtos.FxBoardRowDto>();
        foreach (var q in list)
        {
            if (provider.DiscontinuedCurrencies.TryGetValue(q, out var until))
            {
                rows.Add(new(q, null, null, null, null, FxDiscontinued.Reason(q, until)));
                continue;
            }
            if (unsupported.Contains(q))
            {
                rows.Add(new(q, null, null, null, null, $"No {q} reference rates are published."));
                continue;
            }
            var series = table.ObservedSeries(baseCurrency, q, today.AddDays(-45), today);
            if (series.Count == 0 || today.DayNumber - series[^1].Date.DayNumber > FxTable.DefaultMaxCarryForwardDays)
            {
                rows.Add(new(q, null, series.Count == 0 ? null : series[^1].Date, null, null,
                    series.Count == 0 ? "No recent rate." : $"Latest rate is from {series[^1].Date:yyyy-MM-dd} – too old to use."));
                continue;
            }
            var last = series[^1];
            double? d1 = series.Count > 1 ? last.Rate / series[^2].Rate - 1 : null;
            var monthAgo = series.LastOrDefault(p => p.Date <= last.Date.AddMonths(-1));
            double? m1 = monthAgo.Rate > 0 ? last.Rate / monthAgo.Rate - 1 : null;
            rows.Add(new(q, last.Rate, last.Date, d1, m1, null));
        }
        return new Dtos.FxBoardDto(baseCurrency, table.Source, table.IsSimulated, rows,
            $"{(table.IsSimulated ? "SIMULATED" : "Reference")} rates, units per one {baseCurrency}; published once per business day (not live).");
    }

    public async Task<FxQuote> LatestAsync(string from, string to, bool simulatedPrices, CancellationToken ct)
    {
        from = Domain.Currencies.Normalize(from);
        to = Domain.Currencies.Normalize(to);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (from == to) return new FxQuote(from, to, 1, today, today, 0, "identity", simulatedPrices);
        var table = await GetTableAsync([from, to], today, today, simulatedPrices, ct);
        return table.Rate(from, to, today);
    }

    /// <summary>
    /// Rate ranges not stored yet for converting prices from <paramref name="from"/> to <paramref name="to"/> (incl. the
    /// carry-forward window before the start), per currency; nothing is downloaded. Used to ask before an analysis.
    /// </summary>
    public async Task<IReadOnlyList<FxGap>> MissingRatesAsync(IEnumerable<string> currencies, DateOnly from, DateOnly to, bool simulatedPrices, CancellationToken ct)
    {
        var (provider, needed, start, end) = Window(currencies, from, to, simulatedPrices);
        return needed.Count == 0 ? [] : await GapsAsync(provider, needed, start, end, ct);
    }

    /// <summary>Starts (or joins) the download of the missing rates without waiting; true when one is running afterwards.</summary>
    public async Task<bool> StartDownloadAsync(IEnumerable<string> currencies, DateOnly from, DateOnly to, bool simulatedPrices, CancellationToken ct)
    {
        var (provider, needed, start, end) = Window(currencies, from, to, simulatedPrices);
        if (needed.Count == 0) return false;
        var gaps = await GapsAsync(provider, needed, start, end, ct);
        if (gaps.Count > 0) downloader.Start(provider.Name, gaps);
        return downloader.IsRunning(provider.Name);
    }

    /// <summary>
    /// Downloads, in the background, the missing rates of every currency the active FX source publishes for [from, today]
    /// (Market Data → Currencies → Sync). Only gaps are fetched, in the downloader's contiguous chunks; nothing is filled in.
    /// </summary>
    public async Task<Dtos.FxSyncDto> SyncAllAsync(DateOnly? startDate, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = startDate ?? today.AddYears(-5);
        if (from > today || from < new DateOnly(1999, 1, 4))
            throw new ValidationException("startDate", "Choose a start between 1999-01-04 (the first euro reference rates) and today.");
        var simulated = await ActiveSourceIsSimulatedAsync(ct);
        var provider = ProviderFor(simulated);
        var currencies = SupportedCurrencies(simulated).Where(c => c != FxTable.Pivot).Order().ToList();
        var started = await StartDownloadAsync(currencies, from, today, simulated, ct);
        return new Dtos.FxSyncDto(started, provider.Name, currencies, from, today);
    }

    /// <summary>True while rates of the source used with these prices are being downloaded.</summary>
    public bool IsDownloading(bool simulatedPrices) => downloader.IsRunning(ProviderFor(simulatedPrices).Name);

    /// <summary>Provider, convertible non-EUR currencies and the stored-rate window GetTableAsync would need.</summary>
    private (IFxRateProvider Provider, List<string> Needed, DateOnly Start, DateOnly End) Window(IEnumerable<string> currencies, DateOnly from, DateOnly to, bool simulatedPrices)
    {
        var provider = ProviderFor(simulatedPrices);
        var needed = currencies.Select(Domain.Currencies.Normalize).Where(c => c != FxTable.Pivot && HasRates(provider, c)).Distinct().ToList();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return (provider, needed, from.AddDays(-(FxTable.DefaultMaxCarryForwardDays + 3)), to > today ? today : to);
    }

    /// <summary>
    /// Makes sure the rates for [start, end] are stored: starts (or joins) the download of what is missing and waits for it up
    /// to the request's budget. After the budget the stored rates are used – lookups beyond them fail explicitly – and the
    /// download continues in the background, so the next request finds the rates.
    /// </summary>
    private async Task EnsureAsync(IFxRateProvider provider, List<string> currencies, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var budget = _backgroundWait ? throttle.BackgroundProviderWait : throttle.ProviderWait;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        // A joined download was started for someone else: check again when it ends (at most twice, then start our own).
        for (var round = 0; round < 3; round++)
        {
            var gaps = await GapsAsync(provider, currencies, start, end, ct);
            if (gaps.Count == 0) return;
            var (download, joined) = downloader.Start(provider.Name, gaps);
            var remaining = budget - waited.Elapsed;
            FxDownloadReport report;
            try
            {
                report = await download.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct);
            }
            catch (TimeoutException)
            {
                var what = string.Join(", ", gaps.Select(g => g.Currency).Distinct().Order());
                logger.LogWarning("FX download from {Provider} for {Currencies} not finished after {Seconds:0} s; stored rates used, the download continues",
                    provider.Name, what, waited.Elapsed.TotalSeconds);
                _downloadNotes.Add($"{what} exchange rates for {start:yyyy-MM-dd} – {end:yyyy-MM-dd} were still being downloaded from {provider.Name} after " +
                                   $"{budget.TotalSeconds:0} s; the download continues in the background. Until it has finished, prices without a rate are left out – run again in a few minutes.");
                return;
            }
            if (!joined)
            {
                _downloadNotes.AddRange(report.Problems);
                return;
            }
        }
    }

    /// <summary>
    /// Per currency, the parts of [start, end] not stored yet: older history before the first stored rate (backward) and
    /// days after the latest (forward). Stored rates of a currency form one continuous range – the download only extends it
    /// at its ends (<see cref="FxDownloadPlan.Connected"/>) – so its first and last date describe it. Nothing is published on
    /// weekends and TARGET holidays: the first stored rate may lie up to the carry-forward window after the start (longest
    /// closure: Good Friday–Easter Monday), and data up to the previous weekday counts as complete at the end. Without that
    /// tolerance a range starting on a Saturday was never "covered" and refetched every hour. A discontinued currency is only
    /// expected up to its last publication, so its ended series is never requested again.
    /// </summary>
    private async Task<List<FxGap>> GapsAsync(IFxRateProvider provider, List<string> currencies, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var coverage = await db.FxRates.AsNoTracking()
            .Where(r => r.Source == provider.Name && currencies.Contains(r.Currency))
            .GroupBy(r => r.Currency)
            .Select(g => new { Currency = g.Key, Min = g.Min(r => r.Date), Max = g.Max(r => r.Date) })
            .ToListAsync(ct);
        var gaps = new List<FxGap>();
        foreach (var c in currencies)
        {
            var last = provider.DiscontinuedCurrencies.TryGetValue(c, out var until) && until < end ? until : end;
            if (last < start) continue;
            var cov = coverage.FirstOrDefault(x => x.Currency == c);
            if (cov is null)
            {
                gaps.Add(new FxGap(c, start, last, Backward: true));
                continue;
            }
            // Older history is downloaded up to the stored range, even when [start, end] ends earlier, so no hole remains.
            if (cov.Min > start.AddDays(FxTable.DefaultMaxCarryForwardDays)) gaps.Add(new FxGap(c, start, cov.Min.AddDays(-1), Backward: true));
            if (cov.Max < (last < end ? last : PreviousWeekday(last))) gaps.Add(new FxGap(c, cov.Max.AddDays(1), last, Backward: false));
        }
        return gaps;
    }

    private static DateOnly PreviousWeekday(DateOnly d)
    {
        var p = d.AddDays(-1);
        while (p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) p = p.AddDays(-1);
        return p;
    }
}
