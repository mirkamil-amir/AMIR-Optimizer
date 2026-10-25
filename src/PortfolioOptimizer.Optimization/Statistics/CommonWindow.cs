namespace PortfolioOptimizer.Optimization.Statistics;

/// <summary>Why an asset was left out of the common window, for the run's diagnostics.</summary>
public sealed record WindowExclusion(string Symbol, string Reason);

/// <summary>
/// Chooses which assets share one return window. <see cref="ReturnMatrix"/> keeps only the dates every asset traded, so
/// one recent listing or one asset whose prices stopped long ago shrinks the window of all others (with 35 markets, a
/// stock with 100 prices left 30 common returns for 1,000 stocks). Assets are left out – never filled in – in two steps:
/// (1) prices ending more than <paramref name="staleDays"/> before the latest asset's, (2) the fewest most recently
/// listed assets until the common window reaches <paramref name="targetDates"/> dates (found by binary search: leaving
/// out more assets never shortens the window). If no choice reaches the target, every asset is kept and the caller
/// reports the short window as before.
/// </summary>
public static class CommonWindow
{
    public static (IReadOnlyList<string> Kept, IReadOnlyList<WindowExclusion> Excluded) Choose(
        IReadOnlyDictionary<string, IReadOnlyList<PricePoint>> series, IReadOnlyList<string> symbols, int targetDates, int staleDays = 10)
    {
        if (symbols.Count < 2) return (symbols, []);
        var excluded = new List<WindowExclusion>();
        var first = symbols.ToDictionary(s => s, s => series[s].Min(p => p.Date));
        var last = symbols.ToDictionary(s => s, s => series[s].Max(p => p.Date));

        var latest = last.Values.Max();
        var current = symbols.Where(s => last[s] >= latest.AddDays(-staleDays)).ToList();
        foreach (var s in symbols.Except(current))
            excluded.Add(new WindowExclusion(s, $"prices end {last[s]:yyyy-MM-dd}, more than {staleDays} days before the other assets' ({latest:yyyy-MM-dd}); excluded so they keep a common window."));
        if (current.Count < 2) return (symbols, []);
        if (CommonDates(series, current) >= targetDates) return (current, excluded);

        // Most recently listed first; leaving out the first k of them is monotone in the common window's length.
        var byStart = current.OrderByDescending(s => first[s]).ThenBy(s => s, StringComparer.Ordinal).ToList();
        int lo = 1, hi = byStart.Count - 2, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (CommonDates(series, byStart.Skip(mid).ToList()) >= targetDates) { found = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        if (found < 0) return (symbols, []);
        var window = CommonDates(series, byStart.Skip(found).ToList());
        foreach (var s in byStart.Take(found))
            excluded.Add(new WindowExclusion(s, $"price history starts {first[s]:yyyy-MM-dd} – too recent to share a window of {window} trading days with the other assets; excluded."));
        return (current.Where(s => !byStart.Take(found).Contains(s)).ToList(), excluded);
    }

    /// <summary>Number of dates on which every asset has a price.</summary>
    public static int CommonDates(IReadOnlyDictionary<string, IReadOnlyList<PricePoint>> series, IReadOnlyList<string> symbols)
    {
        var counts = new Dictionary<DateOnly, int>();
        foreach (var s in symbols)
            foreach (var d in series[s].Select(p => p.Date).Distinct())
                counts[d] = counts.GetValueOrDefault(d) + 1;
        return counts.Values.Count(c => c == symbols.Count);
    }
}
