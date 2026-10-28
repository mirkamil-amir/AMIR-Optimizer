namespace PortfolioOptimizer.Optimization.Portfolio;

/// <summary>Result of an independent check of a candidate allocation; <see cref="Violations"/> is empty when it passes.</summary>
public sealed record AllocationValidation(IReadOnlyList<string> Violations, IReadOnlyList<string> Checks)
{
    public bool IsValid => Violations.Count == 0;
}

/// <summary>
/// Independent validation of a candidate allocation against the user's hard constraints, written separately from the
/// solver (it does not reuse the optimizer's internal problem specification). A candidate is rejected – never repaired or
/// relaxed – when any weight is not finite, a bound, the invested share, a sector cap, the maximum number of holdings,
/// the minimum position size, the turnover limit or an excluded symbol is violated, or the risk/return estimates are not finite.
/// </summary>
public static class AllocationValidator
{
    /// <summary>Weights within 1e-6 of a limit count as on the limit (solver precision).</summary>
    public const double Tolerance = 1e-6;

    public static AllocationValidation Validate(IReadOnlyList<string> symbols, IReadOnlyList<double> weights, PortfolioConstraints c,
        double? expectedReturn = null, double? volatility = null, IReadOnlyList<double>? currentWeights = null)
    {
        var v = new List<string>();
        var checks = new List<string>();
        if (symbols.Count != weights.Count)
        {
            v.Add($"{weights.Count} weights for {symbols.Count} symbols.");
            return new AllocationValidation(v, checks);
        }

        checks.Add("weights finite");
        for (var i = 0; i < weights.Count; i++)
            if (!double.IsFinite(weights[i])) v.Add($"{symbols[i]}: weight is not a finite number.");
        if (v.Count > 0) return new AllocationValidation(v, checks);

        checks.Add("bounds");
        for (var i = 0; i < weights.Count; i++)
        {
            var (lo, hi) = c.AssetBounds.TryGetValue(symbols[i], out var b) ? b : (c.MinWeight, c.MaxWeight);
            var w = weights[i];
            if (w < -Tolerance) v.Add($"{symbols[i]}: negative weight {w:P4} (long-only).");
            else if (w > hi + Tolerance) v.Add($"{symbols[i]}: weight {w:P4} above the maximum {hi:P2}.");
            else if (w > Tolerance && w < lo - Tolerance) v.Add($"{symbols[i]}: weight {w:P4} below the minimum {lo:P2}.");
        }

        checks.Add("invested share");
        var sum = weights.Sum();
        if (Math.Abs(sum - c.InvestedFraction) > 1e-5)
            v.Add($"Weights sum to {sum:P4} instead of {c.InvestedFraction:P2} (cash reserve {c.CashReserve:P2}).");

        if (c.ExcludedSymbols.Count > 0)
        {
            checks.Add("excluded symbols");
            for (var i = 0; i < weights.Count; i++)
                if (weights[i] > Tolerance && c.ExcludedSymbols.Contains(symbols[i], StringComparer.OrdinalIgnoreCase))
                    v.Add($"{symbols[i]} is excluded but has weight {weights[i]:P4}.");
        }

        if (c.SectorMaxWeights.Count > 0)
        {
            checks.Add("sector caps");
            foreach (var (sector, cap) in c.SectorMaxWeights)
            {
                var s = Enumerable.Range(0, weights.Count)
                    .Where(i => c.Sectors.TryGetValue(symbols[i], out var sec) && string.Equals(sec, sector, StringComparison.OrdinalIgnoreCase))
                    .Sum(i => weights[i]);
                if (s > cap + Tolerance) v.Add($"Sector {sector}: {s:P4} above the cap {cap:P2}.");
            }
        }

        var held = weights.Count(w => w > Tolerance);
        if (c.MaxHoldings is { } maxHoldings)
        {
            checks.Add("maximum holdings");
            if (held > maxHoldings) v.Add($"{held} positions held; at most {maxHoldings} allowed.");
        }
        if (c.MinPositionWeight is { } minPosition)
        {
            checks.Add("minimum position");
            for (var i = 0; i < weights.Count; i++)
                if (weights[i] > Tolerance && weights[i] < minPosition - Tolerance)
                    v.Add($"{symbols[i]}: position {weights[i]:P4} below the minimum position {minPosition:P2}.");
        }

        // Same rule as the optimizer: the limit applies only when some of the assets are held (otherwise every allocation is a purchase).
        if (c.MaxTurnover is { } maxTurnover && currentWeights is { Count: > 0 } current && current.Count == weights.Count && current.Any(w => w > 0))
        {
            checks.Add("turnover");
            var turnover = 0.5 * weights.Select((w, i) => Math.Abs(w - current[i])).Sum();
            if (turnover > maxTurnover + Tolerance) v.Add($"Turnover {turnover:P4} above the limit {maxTurnover:P2}.");
        }

        checks.Add("estimates finite");
        if (expectedReturn is { } er && !double.IsFinite(er)) v.Add("Expected return is not a finite number.");
        if (volatility is { } vol && (!double.IsFinite(vol) || vol < 0)) v.Add("Volatility is not a finite, non-negative number.");
        return new AllocationValidation(v, checks);
    }
}
