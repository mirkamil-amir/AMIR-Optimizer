using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>The currency an analysis is computed in, the trading currencies of its universe and the benchmark used.</summary>
/// <param name="Currency">Analysis (base) currency: all prices and the benchmark are converted into it with dated FX rates.</param>
/// <param name="Note">Explains a benchmark replacement or the choice of currency.</param>
public sealed record UniverseCurrencyContext(string Currency, IReadOnlyList<string> NativeCurrencies, string Benchmark, string? Note)
{
    public bool NeedsConversion => NativeCurrencies.Any(c => c != Currency);
}

/// <summary>
/// Resolves the analysis currency and benchmark of a universe. Stocks of different currencies are allowed: their prices
/// are converted into the analysis currency (FxService) – nothing is rejected for mixing currencies any more.
/// The analysis currency is the portfolio's base currency (or the explicitly requested one); without either, a
/// single-currency universe is analysed in its own currency and a mixed one in the application default currency.
/// </summary>
public static class UniverseCurrency
{
    public static async Task<UniverseCurrencyContext> ResolveAsync(IApplicationDbContext db, IReadOnlyCollection<string> universe, string? requestedBenchmark,
        string? baseCurrency, CancellationToken ct)
    {
        var symbols = universe.Select(Asset.NormalizeSymbol).Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol))
            .Select(a => new { a.Symbol, a.Currency, Indices = a.Memberships.Select(m => m.IndexCode).ToList() })
            .ToListAsync(ct);
        if (assets.Count == 0) throw new ValidationException("universe", "None of the selected symbols is in the catalogue.");
        var native = assets.Select(a => a.Currency).Distinct().Order().ToList();

        string? note = null;
        string currency;
        if (baseCurrency is not null) currency = Currencies.Normalize(baseCurrency);
        else if (native.Count == 1) currency = native[0];
        else
        {
            currency = (await db.Settings.AsNoTracking().Select(s => s.DefaultCurrency).FirstOrDefaultAsync(ct)) ?? "USD";
            note = $"The selection trades in {string.Join(", ", native)}; it is analysed in {currency} (the default currency).";
        }

        var requested = string.IsNullOrWhiteSpace(requestedBenchmark) ? null : Asset.NormalizeSymbol(requestedBenchmark);
        var requestedCurrency = requested is null ? null
            : await db.Assets.AsNoTracking().Where(a => a.Symbol == requested).Select(a => a.Currency).SingleOrDefaultAsync(ct);

        // A single-market analysis in that market's currency keeps comparing against that market (as before multi-currency
        // support): a foreign default benchmark such as SPY for a DAX selection is replaced by the index benchmark.
        var singleMarket = native.Count == 1 && native[0] == currency;
        if (requestedCurrency is not null && (!singleMarket || requestedCurrency == currency))
            return new UniverseCurrencyContext(currency, native, requested!, note);

        var indexCode = assets.SelectMany(a => a.Indices).GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
        var fallback = indexCode is null ? null : await db.MarketIndices.AsNoTracking()
            .Where(i => i.Code == indexCode && i.BenchmarkSymbol != null).Select(i => i.BenchmarkSymbol).SingleOrDefaultAsync(ct);
        fallback ??= await db.MarketIndices.AsNoTracking().Where(i => i.Currency == currency && !i.IsHistorical && i.BenchmarkSymbol != null)
            .OrderBy(i => i.Code).Select(i => i.BenchmarkSymbol).FirstOrDefaultAsync(ct);
        fallback ??= requestedCurrency is not null ? requested : null;
        if (fallback is null)
            throw new ValidationException("benchmarkSymbol", requested is null
                ? "No benchmark is configured for this selection; choose a benchmark."
                : $"Benchmark {requested} is not in the catalogue; choose another benchmark.");
        var replaced = requested is not null && fallback != requested
            ? requestedCurrency is null
                ? $"Benchmark {requested} is not in the catalogue; {fallback} is used instead."
                : $"Benchmark {requested} is priced in {requestedCurrency}; the {currency} market benchmark {fallback} is used for this {currency} selection."
            : null;
        return new UniverseCurrencyContext(currency, native, fallback, string.Join(" ", new[] { note, replaced }.Where(n => n is not null)) is { Length: > 0 } n ? n : null);
    }
}
