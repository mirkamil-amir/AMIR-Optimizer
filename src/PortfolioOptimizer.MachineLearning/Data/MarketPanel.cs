using System.Security.Cryptography;
using System.Text;

namespace PortfolioOptimizer.MachineLearning.Data;

/// <summary>One stored daily observation: total-return (adjusted) price and, when available, traded volume.</summary>
public readonly record struct DailyObservation(DateOnly Date, double Price, double? Volume);

public sealed record InstrumentHistory(string Symbol, string? Sector, IReadOnlyList<DailyObservation> Observations);

/// <summary>
/// Daily prices of the universe aligned on the benchmark's trading calendar. Rules (all point-in-time safe):
/// <list type="bullet">
/// <item>The calendar is the set of benchmark dates; asset observations on other dates are ignored.</item>
/// <item>Before an asset's first observation its price is missing (NaN) – no back-filling.</item>
/// <item>Interior gaps of up to <c>maxForwardFillDays</c> trading days are filled with the last observed price (past
/// information only); longer gaps and dates after the last observation stay missing.</item>
/// <item>Volume is never filled.</item>
/// </list>
/// </summary>
public sealed class MarketPanel
{
    private MarketPanel(DateOnly[] dates, string[] symbols, string?[] sectors, double[][] prices, double[][] volumes, double[] benchmark,
        string benchmarkSymbol, IReadOnlyList<string> notes, string snapshotHash)
    {
        Dates = dates;
        Symbols = symbols;
        Sectors = sectors;
        Prices = prices;
        Volumes = volumes;
        Benchmark = benchmark;
        BenchmarkSymbol = benchmarkSymbol;
        Notes = notes;
        SnapshotHash = snapshotHash;
    }

    public DateOnly[] Dates { get; }
    public string[] Symbols { get; }
    public string?[] Sectors { get; }

    /// <summary>Adjusted prices [asset][t]; NaN when unavailable.</summary>
    public double[][] Prices { get; }

    /// <summary>Volumes [asset][t]; NaN when unavailable.</summary>
    public double[][] Volumes { get; }

    /// <summary>Benchmark adjusted prices on every calendar date.</summary>
    public double[] Benchmark { get; }

    public string BenchmarkSymbol { get; }
    public IReadOnlyList<string> Notes { get; }

    /// <summary>SHA-256 (truncated) over every (symbol, date, price, volume) used – identifies the data snapshot of an experiment.</summary>
    public string SnapshotHash { get; }

    public int Length => Dates.Length;
    public int AssetCount => Symbols.Length;

    /// <summary>Index of the last calendar date on or before <paramref name="date"/>, or -1.</summary>
    public int IndexOnOrBefore(DateOnly date)
    {
        int lo = 0, hi = Dates.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Dates[mid] <= date) lo = mid + 1;
            else hi = mid;
        }
        return lo - 1;
    }

    public static MarketPanel Build(IReadOnlyList<InstrumentHistory> assets, InstrumentHistory benchmark, int maxForwardFillDays = 5)
    {
        if (assets.Count == 0) throw new ArgumentException("At least one asset is required.");
        var benchmarkMap = Clean(benchmark.Observations);
        if (benchmarkMap.Count < 2) throw new ArgumentException($"The benchmark {benchmark.Symbol} has no usable price history.");
        var dates = benchmarkMap.Keys.OrderBy(d => d).ToArray();
        var index = dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        var notes = new List<string>();

        var ordered = assets.OrderBy(a => a.Symbol, StringComparer.Ordinal).ToArray();
        var prices = new double[ordered.Length][];
        var volumes = new double[ordered.Length][];
        var hashBuilder = new StringBuilder();
        for (var a = 0; a < ordered.Length; a++)
        {
            var map = Clean(ordered[a].Observations);
            var p = Enumerable.Repeat(double.NaN, dates.Length).ToArray();
            var v = Enumerable.Repeat(double.NaN, dates.Length).ToArray();
            var offCalendar = 0;
            foreach (var (date, obs) in map)
            {
                if (!index.TryGetValue(date, out var i)) { offCalendar++; continue; }
                p[i] = obs.Price;
                if (obs.Volume is { } vol && vol >= 0 && double.IsFinite(vol)) v[i] = vol;
                hashBuilder.Append(ordered[a].Symbol).Append('|').Append(date.DayNumber).Append('|').Append(obs.Price.ToString("R")).Append('|')
                    .Append(obs.Volume?.ToString("R") ?? "").Append('\n');
            }
            if (offCalendar > 0) notes.Add($"{ordered[a].Symbol}: {offCalendar} observation(s) on dates without a benchmark price were ignored.");

            // Forward-fill short interior gaps only.
            var filled = 0;
            var first = Array.FindIndex(p, x => !double.IsNaN(x));
            var last = Array.FindLastIndex(p, x => !double.IsNaN(x));
            if (first >= 0)
            {
                for (var i = first + 1; i <= last; i++)
                {
                    if (!double.IsNaN(p[i])) continue;
                    var run = i;
                    while (run <= last && double.IsNaN(p[run])) run++;
                    var length = run - i;
                    if (length <= maxForwardFillDays)
                    {
                        for (var k = i; k < run; k++) p[k] = p[i - 1];
                        filled += length;
                    }
                    i = run - 1;
                }
            }
            if (filled > 0) notes.Add($"{ordered[a].Symbol}: {filled} missing day(s) forward-filled from the previous close (gaps ≤ {maxForwardFillDays} days).");
            if (first < 0) notes.Add($"{ordered[a].Symbol}: no prices on the benchmark calendar.");
            prices[a] = p;
            volumes[a] = v;
        }

        var bench = dates.Select(d => benchmarkMap[d].Price).ToArray();
        foreach (var d in dates)
            hashBuilder.Append(benchmark.Symbol).Append('|').Append(d.DayNumber).Append('|').Append(benchmarkMap[d].Price.ToString("R")).Append('\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashBuilder.ToString())))[..16].ToLowerInvariant();

        return new MarketPanel(dates, ordered.Select(a => a.Symbol).ToArray(), ordered.Select(a => a.Sector).ToArray(), prices, volumes, bench,
            benchmark.Symbol, notes, hash);
    }

    private static Dictionary<DateOnly, DailyObservation> Clean(IReadOnlyList<DailyObservation> observations) =>
        observations.Where(o => o.Price > 0 && double.IsFinite(o.Price))
            .GroupBy(o => o.Date)
            .ToDictionary(g => g.Key, g => g.Last());
}
