using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.MachineLearning.Data;

namespace PortfolioOptimizer.Application.MachineLearning;

public sealed record MlData(MarketPanel Panel, IReadOnlyList<string> Warnings, bool UsesSimulatedData, IReadOnlyList<string> ExcludedSymbols);

/// <summary>Loads stored prices of one data source into a <see cref="MarketPanel"/> (never mixing sources or price kinds).</summary>
public sealed class MlDataLoader(PriceQueryService prices)
{
    /// <summary>Stocks with fewer stored observations are excluded (features need ≈ 1 year of history).</summary>
    public const int MinimumObservations = 300;

    public async Task<MlData> LoadAsync(IReadOnlyList<string> symbols, string benchmark, string source, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var series = await prices.GetDailyObservationsAsync(symbols.Append(benchmark).Distinct().ToList(), source, from, to, ct);
        if (!series.TryGetValue(benchmark, out var bench) || bench.Bars.Count < MinimumObservations)
            throw new InvalidOperationException(
                $"The benchmark {benchmark} has {(bench?.Bars.Count ?? 0)} stored {source} prices in the period (minimum {MinimumObservations}). Synchronize it first.");

        var warnings = new List<string>();
        var excluded = new List<string>();
        var assets = new List<InstrumentHistory>();
        foreach (var symbol in symbols)
        {
            if (!series.TryGetValue(symbol, out var s) || s.Bars.Count < MinimumObservations)
            {
                excluded.Add(symbol);
                warnings.Add($"{symbol}: {(s?.Bars.Count ?? 0)} stored {source} prices (minimum {MinimumObservations}); excluded. Fetch prices to include it.");
                continue;
            }
            if (!s.UsesAdjustedClose) warnings.Add($"{symbol}: adjusted closes unavailable from {source}; raw closes used (dividends ignored).");
            assets.Add(new InstrumentHistory(s.Symbol, s.Sector, s.Bars.Select(b => new DailyObservation(b.Date, b.Price, b.Volume)).ToList()));
        }
        if (assets.Count < 5)
            throw new InvalidOperationException($"Only {assets.Count} stock(s) have at least {MinimumObservations} stored prices; at least 5 are needed. Fetch prices for the selection first.");
        if (!bench.UsesAdjustedClose) warnings.Add($"{benchmark}: adjusted closes unavailable; raw closes used for the benchmark.");

        var panel = MarketPanel.Build(assets, new InstrumentHistory(benchmark, null, bench.Bars.Select(b => new DailyObservation(b.Date, b.Price, b.Volume)).ToList()));
        var vintage = series.Values.Where(v => v.LastRetrievedAtUtc.HasValue).Select(v => v.LastRetrievedAtUtc!.Value).DefaultIfEmpty().Max();
        if (vintage != default) warnings.Add($"Data vintage: prices as stored by {vintage:yyyy-MM-dd HH:mm} UTC (adjusted closes reflect adjustments known at retrieval time).");
        return new MlData(panel, warnings, series.Values.Any(v => v.IsSimulated), excluded);
    }
}
