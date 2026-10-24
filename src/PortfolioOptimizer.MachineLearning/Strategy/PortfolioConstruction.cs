namespace PortfolioOptimizer.MachineLearning.Strategy;

public enum WeightingMethod
{
    /// <summary>Equal weights across the selected stocks.</summary>
    EqualWeight,
    /// <summary>Linearly decreasing weights by rank: K, K−1, …, 1.</summary>
    RankWeighted,
    /// <summary>Weights ∝ 1 / trailing 63-day volatility (risk scaling); stocks without a volatility estimate are skipped.</summary>
    InverseVolatility,
    /// <summary>Weights ∝ score − lowest selected score (+ a small floor); requires a meaningful score scale.</summary>
    ScoreProportional,
}

/// <summary>Rules that turn ML scores into target weights (independent of the mean-variance optimizer).</summary>
public sealed record PortfolioConstructionSettings
{
    public int TopK { get; init; } = 10;
    public WeightingMethod Weighting { get; init; } = WeightingMethod.EqualWeight;
    public double MaxWeight { get; init; } = 0.2;
    public double CashReserve { get; init; }

    /// <summary>Only stocks whose score is at least this value are eligible (e.g. probability ≥ 0.55 or predicted excess return ≥ 0).</summary>
    public double? MinimumScore { get; init; }

    /// <summary>Maximum one-way turnover ½Σ|w − w₀| per rebalance; the move towards the target is scaled down to respect it.</summary>
    public double? MaxTurnover { get; init; }

    public IReadOnlyDictionary<string, double> SectorLimits { get; init; } = new Dictionary<string, double>();
}

/// <summary>A scored candidate at a decision date (higher score = more attractive).</summary>
public sealed record Candidate(string Symbol, string? Sector, double Score, double TrailingVolatility);

public sealed record ConstructionResult(
    IReadOnlyDictionary<string, double> Weights,
    IReadOnlyList<string> Selected,
    double CashWeight,
    double? Turnover,
    IReadOnlyList<string> Notes);

/// <summary>
/// ML portfolio construction: rank by score → apply the score threshold → take the top K → weight by the chosen rule →
/// enforce the per-stock cap and sector caps by iterative capping (excess goes to uncapped names; what cannot be placed
/// stays in cash) → limit turnover by partial rebalancing. Weights are validated (≥ 0, Σ ≤ 1).
/// </summary>
public static class PortfolioConstructor
{
    public static ConstructionResult Build(IReadOnlyList<Candidate> candidates, PortfolioConstructionSettings settings,
        IReadOnlyDictionary<string, double>? currentWeights = null)
    {
        if (settings.TopK < 1) throw new ArgumentException("TopK must be at least 1.");
        if (settings.MaxWeight is <= 0 or > 1) throw new ArgumentException("MaxWeight must be in (0, 1].");
        if (settings.CashReserve is < 0 or >= 1) throw new ArgumentException("CashReserve must be in [0, 1).");
        var notes = new List<string>();

        var ranked = candidates.Where(c => double.IsFinite(c.Score))
            .OrderByDescending(c => c.Score).ThenBy(c => c.Symbol, StringComparer.Ordinal).ToList();
        var invalid = candidates.Where(c => !double.IsFinite(c.Score)).Select(c => c.Symbol).ToList();
        if (invalid.Count > 0)
            notes.Add($"No valid score (NaN or infinite) for {string.Join(", ", invalid.Take(5))}{(invalid.Count > 5 ? ", …" : "")}; not eligible.");
        if (settings.MinimumScore is { } min)
        {
            var before = ranked.Count;
            ranked = ranked.Where(c => c.Score >= min).ToList();
            if (ranked.Count < before) notes.Add($"{before - ranked.Count} stock(s) below the minimum score {min:G4} were not eligible.");
        }
        if (settings.Weighting == WeightingMethod.InverseVolatility)
        {
            var noVol = ranked.Where(c => !(c.TrailingVolatility > 0) || !double.IsFinite(c.TrailingVolatility)).Select(c => c.Symbol).ToList();
            if (noVol.Count > 0)
            {
                ranked = ranked.Where(c => c.TrailingVolatility > 0 && double.IsFinite(c.TrailingVolatility)).ToList();
                notes.Add($"No volatility estimate for {string.Join(", ", noVol.Take(5))}{(noVol.Count > 5 ? ", …" : "")}; skipped for risk scaling.");
            }
        }

        var selected = ranked.Take(settings.TopK).ToList();
        var invested = 1 - settings.CashReserve;
        if (selected.Count == 0)
        {
            notes.Add("No stock met the selection criteria; the portfolio is held in cash.");
            return Finish(new Dictionary<string, double>(), [], settings, currentWeights, notes);
        }
        if (selected.Count < settings.TopK) notes.Add($"Only {selected.Count} of {settings.TopK} requested stocks were eligible.");

        var raw = settings.Weighting switch
        {
            WeightingMethod.EqualWeight => selected.Select(_ => 1.0).ToArray(),
            WeightingMethod.RankWeighted => selected.Select((_, i) => (double)(selected.Count - i)).ToArray(),
            WeightingMethod.InverseVolatility => selected.Select(c => 1 / c.TrailingVolatility).ToArray(),
            WeightingMethod.ScoreProportional => ScoreProportional(selected, notes),
            _ => throw new ArgumentOutOfRangeException(nameof(settings)),
        };
        var total = raw.Sum();
        var weights = selected.Select((c, i) => (c, w: raw[i] / total * invested)).ToDictionary(x => x.c.Symbol, x => x.w);

        ApplyCaps(weights, selected.ToDictionary(c => c.Symbol, c => c.Sector), settings, notes);
        return Finish(weights, selected.Select(c => c.Symbol).ToList(), settings, currentWeights, notes);
    }

    private static double[] ScoreProportional(List<Candidate> selected, List<string> notes)
    {
        var floor = selected.Min(c => c.Score);
        var spread = selected.Max(c => c.Score) - floor;
        if (spread <= 1e-12)
        {
            notes.Add("Selected scores are identical; score-proportional weighting fell back to equal weights.");
            return selected.Select(_ => 1.0).ToArray();
        }
        // Lowest selected score gets 10% of the spread so every selected stock keeps a positive weight.
        return selected.Select(c => c.Score - floor + 0.1 * spread).ToArray();
    }

    /// <summary>Iterative capping: clip names above the cap and sectors above their limit, redistribute to the rest.</summary>
    private static void ApplyCaps(Dictionary<string, double> w, Dictionary<string, string?> sectors, PortfolioConstructionSettings s, List<string> notes)
    {
        var frozen = new HashSet<string>();
        var target = w.Values.Sum();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var changed = false;
            foreach (var key in w.Keys.ToList())
            {
                if (w[key] > s.MaxWeight + 1e-12) { w[key] = s.MaxWeight; frozen.Add(key); changed = true; }
            }
            foreach (var (sector, limit) in s.SectorLimits)
            {
                var members = w.Keys.Where(k => string.Equals(sectors[k], sector, StringComparison.OrdinalIgnoreCase)).ToList();
                var sum = members.Sum(k => w[k]);
                if (sum > limit + 1e-12 && sum > 0)
                {
                    foreach (var k in members) { w[k] *= limit / sum; frozen.Add(k); }
                    changed = true;
                }
            }
            var deficit = target - w.Values.Sum();
            var free = w.Keys.Where(k => !frozen.Contains(k)).ToList();
            var freeSum = free.Sum(k => w[k]);
            if (deficit > 1e-12 && freeSum > 0)
            {
                foreach (var k in free) w[k] += deficit * w[k] / freeSum;
                changed = true;
            }
            if (!changed) break;
        }
        var shortfall = target - w.Values.Sum();
        if (shortfall > 1e-6)
            notes.Add($"{shortfall:P1} could not be allocated within the weight and sector caps and is held in cash.");
    }

    private static ConstructionResult Finish(Dictionary<string, double> weights, IReadOnlyList<string> selected, PortfolioConstructionSettings s,
        IReadOnlyDictionary<string, double>? current, List<string> notes)
    {
        double? turnover = null;
        if (current is not null)
        {
            var keys = weights.Keys.Union(current.Keys).ToList();
            turnover = keys.Sum(k => Math.Abs(weights.GetValueOrDefault(k) - current.GetValueOrDefault(k))) / 2;
            if (s.MaxTurnover is { } maxTurnover && turnover > maxTurnover + 1e-12 && current.Values.Sum() > 1e-9)
            {
                var alpha = maxTurnover / turnover.Value;
                weights = keys.ToDictionary(k => k, k => current.GetValueOrDefault(k) + alpha * (weights.GetValueOrDefault(k) - current.GetValueOrDefault(k)));
                notes.Add($"Turnover limited to {maxTurnover:P0}: moved {alpha:P0} of the way from current to target weights.");
                turnover = maxTurnover;
            }
        }

        var cleaned = weights.Where(kv => kv.Value > 1e-9).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (cleaned.Values.Any(v => !double.IsFinite(v) || v < 0)) throw new InvalidOperationException("Constructed weights are invalid.");
        var sum = cleaned.Values.Sum();
        if (sum > 1 + 1e-9) throw new InvalidOperationException($"Constructed weights sum to {sum:F6} > 1.");
        return new ConstructionResult(cleaned, selected, Math.Max(0, 1 - sum), turnover, notes);
    }
}
