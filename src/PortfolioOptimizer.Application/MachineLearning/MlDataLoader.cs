using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.MachineLearning.Data;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.MachineLearning;

public sealed record MlData(MarketPanel Panel, IReadOnlyList<string> Warnings, bool UsesSimulatedData, IReadOnlyList<string> ExcludedSymbols, string? FxSource = null,
    IReadOnlyList<AssetDataEligibility>? Eligibility = null);

/// <summary>
/// Data-quality stage of one stock, before any model or portfolio rule: stored prices in the requested period, those on the
/// benchmark's calendar span, those without an exchange rate, and the prices that reach the model.
/// </summary>
public sealed record AssetDataEligibility(string Symbol, int StoredPrices, int PricesInBenchmarkSpan, int PricesWithoutFx, int UsablePrices, bool Included, string? Reason);

/// <summary>Loads stored prices of one data source into a <see cref="MarketPanel"/> (never mixing sources or price kinds).</summary>
public sealed class MlDataLoader(PriceQueryService prices, FxService fx)
{
    /// <summary>Stocks with fewer stored observations are excluded (features need ≈ 1 year of history).</summary>
    public const int MinimumObservations = 300;

    /// <param name="currency">Analysis currency: prices are converted with the FX rate of each date; volumes are unchanged.</param>
    public async Task<MlData> LoadAsync(IReadOnlyList<string> symbols, string benchmark, string source, DateOnly from, DateOnly to, string currency, CancellationToken ct)
    {
        var all = symbols.Append(benchmark).Distinct().ToList();
        var stored = await prices.GetDailyObservationsAsync(all, source, from, to, ct);
        // The panel's calendar is the benchmark's dates (MarketPanel.Build); prices outside its span are never used, so they
        // are left out before conversion – otherwise a missing exchange rate on an unused date could exclude a stock.
        var span = stored.TryGetValue(benchmark, out var storedBench) && storedBench.Bars.Count > 0 ? (First: storedBench.Bars[0].Date, Last: storedBench.Bars[^1].Date) : (First: from, Last: to);
        var native = stored.ToDictionary(kv => kv.Key, kv => kv.Value with { Bars = kv.Value.Bars.Where(b => b.Date >= span.First && b.Date <= span.Last).ToList() },
            StringComparer.OrdinalIgnoreCase);
        var conversion = await fx.ConvertAsync(
            native.ToDictionary(kv => kv.Key, kv => new PriceSeries(kv.Key, 0, kv.Value.Bars.Select(b => new PricePoint(b.Date, b.Price)).ToList(), kv.Value.UsesAdjustedClose, kv.Value.IsSimulated)),
            await fx.CurrenciesOfAsync(all, ct), currency, ct);
        if (conversion.Excluded.TryGetValue(benchmark, out var benchWhy))
            throw new InvalidOperationException($"The benchmark {benchmark} cannot be used: {benchWhy}");
        // Converted prices with the original volumes (dates without a rate are dropped, never filled).
        var series = native.Where(kv => conversion.Series.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv =>
        {
            var converted = conversion.Series[kv.Key].Series.Points.ToDictionary(p => p.Date, p => p.Price);
            return kv.Value with { Bars = kv.Value.Bars.Where(b => converted.ContainsKey(b.Date)).Select(b => b with { Price = converted[b.Date] }).ToList() };
        }, StringComparer.OrdinalIgnoreCase);
        if (!series.TryGetValue(benchmark, out var bench) || bench.Bars.Count < MinimumObservations)
            throw new InvalidOperationException(
                $"The benchmark {benchmark} has {(bench?.Bars.Count ?? 0)} stored prices in the period (minimum {MinimumObservations}). Synchronize it first.");

        var warnings = new List<string>(conversion.Notes);
        var outside = symbols.Where(s => stored.TryGetValue(s, out var full) && full.Bars.Count > native[s].Bars.Count).ToList();
        if (outside.Count > 0)
            warnings.Add($"The benchmark {benchmark} has stored prices from {span.First:yyyy-MM-dd} to {span.Last:yyyy-MM-dd}, so the experiment uses that period only; " +
                         $"earlier or later prices of {outside.Count} stock(s) are not used. Fetch more {benchmark} history to use more of the requested period.");
        var excluded = new List<string>();
        var eligibility = new List<AssetDataEligibility>();
        AssetDataEligibility Row(string symbol, bool included, string? reason)
        {
            var storedCount = stored.TryGetValue(symbol, out var full) ? full.Bars.Count : 0;
            var inSpan = native.TryGetValue(symbol, out var clipped) ? clipped.Bars.Count : 0;
            var withoutFx = conversion.Series.TryGetValue(symbol, out var c) ? c.DroppedPoints : conversion.Excluded.ContainsKey(symbol) ? inSpan : 0;
            var usable = series.TryGetValue(symbol, out var s) ? s.Bars.Count : 0;
            return new AssetDataEligibility(symbol, storedCount, inSpan, withoutFx, included ? usable : 0, included, reason);
        }
        foreach (var (symbol, why) in conversion.Excluded)
        {
            excluded.Add(symbol);
            warnings.Add($"{symbol}: excluded – {why}");
            if (symbol != benchmark) eligibility.Add(Row(symbol, false, $"Exchange rates: {why}"));
        }
        var assets = new List<InstrumentHistory>();
        foreach (var symbol in symbols)
        {
            if (conversion.Excluded.ContainsKey(symbol)) continue;
            if (!series.TryGetValue(symbol, out var s) || s.Bars.Count < MinimumObservations)
            {
                excluded.Add(symbol);
                warnings.Add($"{symbol}: {(s?.Bars.Count ?? 0)} stored prices (minimum {MinimumObservations}); excluded. Fetch prices to include it.");
                eligibility.Add(Row(symbol, false, $"Price history: {(s?.Bars.Count ?? 0)} usable prices in the period (minimum {MinimumObservations})."));
                continue;
            }
            eligibility.Add(Row(symbol, true, null));
            if (!s.UsesAdjustedClose) warnings.Add($"{symbol}: adjusted closes unavailable; raw closes used (dividends ignored).");
            assets.Add(new InstrumentHistory(s.Symbol, s.Sector, s.Bars.Select(b => new DailyObservation(b.Date, b.Price, b.Volume)).ToList()));
        }
        if (assets.Count < 5)
            throw new InvalidOperationException($"Only {assets.Count} stock(s) have at least {MinimumObservations} stored prices; at least 5 are needed. Fetch prices for the selection first.");
        if (!bench.UsesAdjustedClose) warnings.Add($"{benchmark}: adjusted closes unavailable; raw closes used for the benchmark.");

        var panel = MarketPanel.Build(assets, new InstrumentHistory(benchmark, null, bench.Bars.Select(b => new DailyObservation(b.Date, b.Price, b.Volume)).ToList()));
        var vintage = series.Values.Where(v => v.LastRetrievedAtUtc.HasValue).Select(v => v.LastRetrievedAtUtc!.Value).DefaultIfEmpty().Max();
        if (vintage != default) warnings.Add($"Data vintage: prices as stored by {vintage:yyyy-MM-dd HH:mm} UTC (adjusted closes reflect adjustments known at retrieval time).");
        return new MlData(panel, warnings, series.Values.Any(v => v.IsSimulated), excluded, conversion.FxSource, eligibility);
    }
}
