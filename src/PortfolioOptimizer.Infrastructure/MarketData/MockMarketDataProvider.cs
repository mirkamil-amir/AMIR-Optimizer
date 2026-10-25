using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.Infrastructure.MarketData;

/// <summary>
/// Deterministic SIMULATED prices for offline development, demos and tests. Every series is a one-factor geometric
/// Brownian motion (market factor + idiosyncratic noise) seeded from the symbol, generated on weekdays from a fixed origin,
/// so the same symbol and date always produce the same price. Rows are flagged <c>IsSimulated</c> and must never be
/// presented as market data.
/// </summary>
public sealed class MockMarketDataProvider(IOptions<MockProviderOptions> options, TimeProvider clock) : IMarketDataProvider
{
    public const string Name = "Mock";
    private static readonly ConcurrentDictionary<string, (DateOnly[] Dates, double[] Prices)> Cache = new();

    public ProviderDescriptor Descriptor { get; } = new(
        Name,
        "Simulated data",
        IsSimulated: true,
        RequiresApiKey: false,
        IsConfigured: options.Value.Enabled,
        ProvidesAdjustedClose: true,
        RateLimit: "None (local).",
        Terms: "Synthetic data generated locally. NOT real market data.");

    public Task<ProviderHistory> GetHistoricalPricesAsync(string symbol, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        var (dates, prices) = Generate(symbol);
        var raw = new List<RawBar>();
        for (var i = 0; i < dates.Length; i++)
        {
            if (dates[i] < startDate || dates[i] > endDate) continue;
            var p = prices[i];
            var prev = i > 0 ? prices[i - 1] : p;
            raw.Add(new RawBar(dates[i], prev, Math.Max(p, prev) * 1.004, Math.Min(p, prev) * 0.996, p, p, 1_000_000 + (long)(Hash(symbol + i) % 5_000_000)));
        }
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var n = PriceNormalizer.Normalize(raw, startDate, endDate, today);
        return Task.FromResult(new ProviderHistory(symbol, Markets.ListingCurrency(symbol), n.Bars, raw.Count, n.Rejected, n.Duplicates,
            ["Simulated data – not real market prices."]));
    }

    public Task<MarketQuote?> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        var (dates, prices) = Generate(symbol);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var idx = Array.FindLastIndex(dates, d => d <= today);
        return Task.FromResult<MarketQuote?>(idx < 0 ? null
            : new MarketQuote(symbol, Math.Round((decimal)prices[idx], 4), Markets.ListingCurrency(symbol),
                dates[idx].ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc), idx > 0 ? Math.Round((decimal)prices[idx - 1], 4) : null));
    }

    public bool Supports(string symbol) => true;

    public int QuoteBatchSize => 200;

    public int HistoryParallelism => 8;

    public int? RemainingDailyRequests => null;

    public Task<IReadOnlyList<QuoteFetch>> GetLatestQuotesAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken) =>
        QuoteFetching.OneByOneAsync(this, symbols, cancellationToken);

    private (DateOnly[] Dates, double[] Prices) Generate(string symbol)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var key = $"{symbol}|{options.Value.Origin}|{today}";
        return Cache.GetOrAdd(key, _ =>
        {
            var market = new Random(1234567);
            var rng = new Random((int)(Hash(symbol) & 0x7FFFFFFF));
            var h = Hash(symbol);
            var annualDrift = 0.06 + (h % 1400) / 10_000.0;          // 6% – 20% arithmetic drift
            var idioVol = 0.10 + ((h >> 16) % 2500) / 10_000.0;       // 10% – 35%
            var beta = 0.6 + ((h >> 32) % 900) / 1_000.0;             // 0.6 – 1.5
            const double marketVol = 0.14;
            var isIndex = symbol is "SPY" or "QQQ" or "VTI" or "IVV" || symbol.StartsWith('^');
            if (isIndex) { beta = 1.0; idioVol = 0.01; annualDrift = 0.08; }

            var dates = new List<DateOnly>();
            var prices = new List<double>();
            double price = 20 + (double)(h % 400);
            const double dt = 1.0 / 252;
            for (var d = options.Value.Origin; d <= today; d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                var zm = Normal(market);
                var zi = Normal(rng);
                var sigma2 = beta * beta * marketVol * marketVol + idioVol * idioVol;
                var ret = (annualDrift - 0.5 * sigma2) * dt + Math.Sqrt(dt) * (beta * marketVol * zm + idioVol * zi);
                if (dates.Count > 0) price *= Math.Exp(ret);
                dates.Add(d);
                prices.Add(Math.Round(price, 4));
            }
            return (dates.ToArray(), prices.ToArray());
        });
    }

    private static double Normal(Random r)
    {
        var u1 = 1.0 - r.NextDouble();
        var u2 = r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Stable 64-bit FNV-1a hash (string.GetHashCode is randomized per process).</summary>
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
