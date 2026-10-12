namespace PortfolioOptimizer.Optimization.Statistics;

public sealed record PricePoint(DateOnly Date, double Price);

/// <summary>
/// Daily simple returns for several assets on a common date grid.
/// Dates are the intersection of all assets' observation dates, so every return in a row spans the same interval
/// for every asset (no forward filling, no pairwise-complete estimation – this keeps the covariance matrix PSD).
/// </summary>
public sealed record ReturnMatrix(
    IReadOnlyList<string> Symbols,
    IReadOnlyList<DateOnly> PriceDates,
    double[,] Returns,
    IReadOnlyDictionary<string, int> DroppedObservations)
{
    public int Observations => Returns.GetLength(0);

    public double[] Column(int j)
    {
        var col = new double[Observations];
        for (var i = 0; i < Observations; i++) col[i] = Returns[i, j];
        return col;
    }

    /// <param name="series">Per-symbol price observations (any order, must be strictly positive).</param>
    public static ReturnMatrix Build(IReadOnlyDictionary<string, IReadOnlyList<PricePoint>> series, IReadOnlyList<string> symbols)
    {
        if (symbols.Count == 0) throw new ArgumentException("No symbols supplied.", nameof(symbols));
        HashSet<DateOnly>? common = null;
        foreach (var s in symbols)
        {
            if (!series.TryGetValue(s, out var pts)) throw new ArgumentException($"No price series for {s}.", nameof(series));
            var dates = pts.Select(p => p.Date).ToHashSet();
            if (common is null) common = dates;
            else common.IntersectWith(dates);
        }

        var grid = common!.OrderBy(d => d).ToList();
        var dropped = symbols.ToDictionary(s => s, s => series[s].Count - grid.Count);
        var t = Math.Max(0, grid.Count - 1);
        var returns = new double[t, symbols.Count];
        for (var j = 0; j < symbols.Count; j++)
        {
            var map = new Dictionary<DateOnly, double>();
            foreach (var p in series[symbols[j]]) map[p.Date] = p.Price;
            var prices = grid.Select(d => map[d]).ToArray();
            var r = ReturnCalculator.SimpleReturns(prices);
            for (var i = 0; i < t; i++) returns[i, j] = r[i];
        }
        return new ReturnMatrix(symbols, grid, returns, dropped);
    }
}
