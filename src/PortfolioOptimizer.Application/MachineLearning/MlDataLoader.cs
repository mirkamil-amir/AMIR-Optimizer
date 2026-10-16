using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.MachineLearning.Data;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.MachineLearning;

public sealed record MlData(MarketPanel Panel, IReadOnlyList<string> Warnings, bool UsesSimulatedData, IReadOnlyList<string> ExcludedSymbols, string? FxSource = null);

/// <summary>Loads stored prices of one data source into a <see cref="MarketPanel"/> (never mixing sources or price kinds).</summary>
public sealed class MlDataLoader(PriceQueryService prices, FxService fx)
{
    /// <summary>Stocks with fewer stored observations are excluded (features need ≈ 1 year of history).</summary>
    public const int MinimumObservations = 300;

    /// <param name="currency">Analysis currency: prices are converted with the FX rate of each date; volumes are unchanged.</param>
    public async Task<MlData> LoadAsync(IReadOnlyList<string> symbols, string benchmark, string source, DateOnly from, DateOnly to, string currency, CancellationToken ct)
    {
        var all = symbols.Append(benchmark).Distinct().ToList();
        var native = await prices.GetDailyObservationsAsync(all, source, from, to, ct);
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
        var excluded = new List<string>();
        foreach (var (symbol, why) in conversion.Excluded)
        {
            excluded.Add(symbol);
            warnings.Add($"{symbol}: excluded – {why}");
        }
        var assets = new List<InstrumentHistory>();
        foreach (var symbol in symbols)
        {
            if (conversion.Excluded.ContainsKey(symbol)) continue;
            if (!series.TryGetValue(symbol, out var s) || s.Bars.Count < MinimumObservations)
            {
                excluded.Add(symbol);
                warnings.Add($"{symbol}: {(s?.Bars.Count ?? 0)} stored prices (minimum {MinimumObservations}); excluded. Fetch prices to include it.");
                continue;
            }
            if (!s.UsesAdjustedClose) warnings.Add($"{symbol}: adjusted closes unavailable; raw closes used (dividends ignored).");
            assets.Add(new InstrumentHistory(s.Symbol, s.Sector, s.Bars.Select(b => new DailyObservation(b.Date, b.Price, b.Volume)).ToList()));
        }
        if (assets.Count < 5)
            throw new InvalidOperationException($"Only {assets.Count} stock(s) have at least {MinimumObservations} stored prices; at least 5 are needed. Fetch prices for the selection first.");
        if (!bench.UsesAdjustedClose) warnings.Add($"{benchmark}: adjusted closes unavailable; raw closes used for the benchmark.");

        var panel = MarketPanel.Build(assets, new InstrumentHistory(benchmark, null, bench.Bars.Select(b => new DailyObservation(b.Date, b.Price, b.Volume)).ToList()));
        var vintage = series.Values.Where(v => v.LastRetrievedAtUtc.HasValue).Select(v => v.LastRetrievedAtUtc!.Value).DefaultIfEmpty().Max();
        if (vintage != default) warnings.Add($"Data vintage: prices as stored by {vintage:yyyy-MM-dd HH:mm} UTC (adjusted closes reflect adjustments known at retrieval time).");
        return new MlData(panel, warnings, series.Values.Any(v => v.IsSimulated), excluded, conversion.FxSource);
    }
}
