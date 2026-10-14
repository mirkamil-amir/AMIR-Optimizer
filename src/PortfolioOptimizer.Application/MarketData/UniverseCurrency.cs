using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>The currency of an analysis universe and the benchmark that matches it.</summary>
/// <param name="Note">Set when the requested benchmark was replaced because it is priced in another currency.</param>
public sealed record UniverseCurrencyContext(string Currency, string Benchmark, string? Note);

/// <summary>
/// Analyses work on returns in each stock's own currency without FX conversion, so a universe must use one currency and
/// the benchmark must be priced in that currency. Mixing (say) USD and EUR stocks would silently combine currency effects
/// with stock returns; such requests are rejected with an explanation. A benchmark in another currency (the default SPY
/// for a DAX universe) is replaced by the universe's index benchmark, and the replacement is reported.
/// </summary>
public static class UniverseCurrency
{
    public static async Task<UniverseCurrencyContext> ResolveAsync(IApplicationDbContext db, IReadOnlyCollection<string> universe, string? requestedBenchmark,
        CancellationToken ct)
    {
        var symbols = universe.Select(Asset.NormalizeSymbol).Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol))
            .Select(a => new { a.Symbol, a.Currency, Indices = a.Memberships.Select(m => m.IndexCode).ToList() })
            .ToListAsync(ct);
        var byCurrency = assets.GroupBy(a => a.Currency).OrderByDescending(g => g.Count()).ToList();
        if (byCurrency.Count == 0) throw new ValidationException("universe", "None of the selected symbols is in the catalogue.");
        if (byCurrency.Count > 1)
        {
            var groups = string.Join("; ", byCurrency.Select(g => $"{g.Key}: {string.Join(", ", g.Select(a => a.Symbol).Order().Take(6))}{(g.Count() > 6 ? ", …" : "")}"));
            throw new ValidationException("universe",
                $"The selection mixes currencies ({groups}). Returns are computed in each stock's own currency without FX conversion, " +
                "so one analysis can only use stocks traded in one currency. Analyse each market separately.");
        }

        var currency = byCurrency[0].Key;
        var requested = string.IsNullOrWhiteSpace(requestedBenchmark) ? null : Asset.NormalizeSymbol(requestedBenchmark);
        if (requested is not null)
        {
            var benchmarkCurrency = await db.Assets.AsNoTracking().Where(a => a.Symbol == requested).Select(a => a.Currency).SingleOrDefaultAsync(ct);
            if (benchmarkCurrency == currency) return new UniverseCurrencyContext(currency, requested, null);
        }

        // The benchmark of the index most of the selection belongs to (ties: alphabetical index code), in the same currency.
        var indexCode = assets.SelectMany(a => a.Indices).GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
        var fallback = indexCode is null ? null : await db.MarketIndices.AsNoTracking()
            .Where(i => i.Code == indexCode && i.Currency == currency).Select(i => i.BenchmarkSymbol).SingleOrDefaultAsync(ct);
        fallback ??= await db.MarketIndices.AsNoTracking().Where(i => i.Currency == currency && !i.IsHistorical && i.BenchmarkSymbol != null)
            .OrderBy(i => i.Code).Select(i => i.BenchmarkSymbol).FirstOrDefaultAsync(ct);
        if (fallback is null)
            throw new ValidationException("benchmarkSymbol", $"No benchmark priced in {currency} is configured; choose a benchmark in {currency}.");
        return new UniverseCurrencyContext(currency, fallback, requested is null
            ? null
            : $"Benchmark {requested} is not priced in {currency}; {fallback} is used instead so that the comparison is in one currency.");
    }
}
