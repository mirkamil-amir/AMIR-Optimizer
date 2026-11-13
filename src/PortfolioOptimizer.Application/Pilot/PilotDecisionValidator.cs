using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization.Portfolio;

namespace PortfolioOptimizer.Application.Pilot;

/// <summary>
/// What the platform knows about an investable symbol at the decision time. <c>Priced</c>: a fresh price and (for another
/// currency) an exchange rate exist at the decision time; otherwise <c>PriceProblem</c> says why not.
/// </summary>
public sealed record PilotAssetInfo(string Symbol, string? Name, string? Sector, string Currency, string? Country, bool Priced, string? PriceProblem);

/// <summary>
/// Independent checks of a Matrix Pilot decision – structure first, then the owner's mandate. Written apart from the
/// model's instructions and never relaxes or repairs a decision: a violation blocks it, and the journal shows the proposed
/// value next to the allowed one. Pure (no I/O); the risk figures and trade estimate are computed by the caller.
/// </summary>
public static class PilotDecisionValidator
{
    public const double SumTolerance = 0.001;
    public static readonly string[] Approaches =
        ["optimizer", "minimum_variance", "risk_based", "momentum", "low_volatility", "quality_trend", "ml_signals", "equal_weight", "hybrid", "discretionary", "hold"];
    public static readonly string[] Confidences = ["low", "medium", "high"];

    /// <summary>Structural checks that make a decision usable at all (types, ranges, symbols, sums, required explanations).</summary>
    /// <param name="requireTexts">false for a candidate the model checks during research (numbers and rules only, no explanations needed).</param>
    public static List<PilotViolation> CheckStructure(PilotDecision d, IReadOnlyDictionary<string, PilotAssetInfo> universe, bool requireTexts = true)
    {
        var v = new List<PilotViolation>();
        if (d.Action is not ("REBALANCE" or "HOLD")) v.Add(new("schema", $"Action must be REBALANCE or HOLD, not “{PilotText.Clip(d.Action, 30)}”."));
        if (requireTexts)
        {
            if (!Approaches.Contains(d.Strategy.Approach)) v.Add(new("schema", $"Unknown strategy approach “{PilotText.Clip(d.Strategy.Approach, 40)}”."));
            if (!Confidences.Contains(d.Confidence)) v.Add(new("schema", "Confidence must be low, medium or high."));
            if (d.Rationale.Count == 0 || d.Rationale.All(string.IsNullOrWhiteSpace)) v.Add(new("schema", "The decision has no rationale."));
            if (d.ExpectedHoldingDays is < 1 or > 1500) v.Add(new("schema", "Expected holding period must be 1–1500 days."));
        }
        if (d.IsHold) return v;

        if (d.Targets.Count == 0) v.Add(new("schema", "A rebalance needs at least one target holding."));
        if (!double.IsFinite(d.CashWeight) || d.CashWeight < 0 || d.CashWeight > 1) v.Add(new("schema", $"Cash weight {d.CashWeight} is not between 0 and 1."));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in d.Targets)
        {
            var s = Asset.NormalizeSymbol(t.Symbol);
            if (!seen.Add(s)) v.Add(new("duplicate", $"{s} appears more than once.", s));
            if (!double.IsFinite(t.Weight)) v.Add(new("schema", $"{s}: weight is not a finite number.", s));
            else if (t.Weight <= 0) v.Add(new("negative_weight", $"{s}: weight {t.Weight:P2} – targets must be positive (long-only; leave a symbol out to sell it).", s, t.Weight, 0));
            if (!universe.ContainsKey(s)) v.Add(new("universe", $"{s} is not in the investable universe of the mandate.", s));
            if (requireTexts && string.IsNullOrWhiteSpace(t.Thesis)) v.Add(new("schema", $"{s}: no thesis given.", s));
        }
        var sum = d.Targets.Where(t => double.IsFinite(t.Weight)).Sum(t => t.Weight) + (double.IsFinite(d.CashWeight) ? d.CashWeight : 0);
        if (Math.Abs(sum - 1) > SumTolerance) v.Add(new("sum", $"Targets plus cash sum to {sum:P2}, not 100 %.", null, sum, 1));
        return v;
    }

    /// <summary>Mandate and data checks of a structurally valid rebalance (a HOLD keeps the current portfolio and is not checked here).</summary>
    public static PilotValidationResult Validate(PilotDecision d, PilotMandate m, IReadOnlyDictionary<string, PilotAssetInfo> universe,
        IReadOnlyDictionary<string, double> currentWeights, PilotRiskSummary? risk, PilotTradeEstimate? trades, bool requireTexts = true)
    {
        var checks = new List<PilotCheck>();
        var v = CheckStructure(d, universe, requireTexts);
        checks.Add(new("structure", v.Count == 0, v.Count == 0 ? "Schema, symbols, uniqueness and sum are valid." : $"{v.Count} problem(s)."));
        if (d.IsHold || v.Count > 0) return new PilotValidationResult(v.Count == 0, checks, v, risk, trades);

        var targets = d.Targets.Select(t => (Symbol: Asset.NormalizeSymbol(t.Symbol), t.Weight)).ToList();

        // The platform's independent allocation validator (bounds, invested share, excluded symbols, sectors, holdings, turnover).
        var symbols = targets.Select(t => t.Symbol).Union(currentWeights.Keys).ToList();
        var weights = symbols.Select(s => targets.FirstOrDefault(t => t.Symbol == s).Weight).ToList();
        var current = symbols.Select(s => currentWeights.GetValueOrDefault(s)).ToList();
        var sectors = symbols.Where(s => universe.TryGetValue(s, out var a) && a.Sector is not null)
            .ToDictionary(s => s, s => universe[s].Sector!, StringComparer.OrdinalIgnoreCase);
        var constraints = new PortfolioConstraints
        {
            MinWeight = 0,
            MaxWeight = m.MaxPositionWeight,
            MaxHoldings = m.MaxHoldings,
            MinPositionWeight = m.MinPositionWeight > 0 ? m.MinPositionWeight : null,
            CashReserve = d.CashWeight,
            MaxTurnover = m.MaxTurnover,
            Sectors = sectors,
            SectorMaxWeights = m.SectorLimits,
            ExcludedSymbols = m.ExcludedSymbols,
        };
        var allocation = AllocationValidator.Validate(symbols, weights, constraints, null, risk?.Volatility, current);
        checks.Add(new("allocation validator", allocation.IsValid, $"Checked: {string.Join(", ", allocation.Checks)}."));
        foreach (var msg in allocation.Violations) v.Add(new("allocation", msg));

        // Per-target bound violations again as structured rows (proposed vs allowed) – the journal shows them side by side.
        foreach (var (s, w) in targets)
        {
            if (w > m.MaxPositionWeight + AllocationValidator.Tolerance)
                v.Add(new("max_position", $"{s}: proposed {w:P2}, allowed at most {m.MaxPositionWeight:P2}.", s, w, m.MaxPositionWeight));
            if (w > AllocationValidator.Tolerance && w < m.MinPositionWeight - AllocationValidator.Tolerance)
                v.Add(new("min_position", $"{s}: proposed {w:P2}, the minimum position is {m.MinPositionWeight:P2}.", s, w, m.MinPositionWeight));
            if (m.ExcludedSymbols.Contains(s)) v.Add(new("excluded", $"{s} is excluded by the mandate.", s, w, 0));
            if (universe.TryGetValue(s, out var a) && !a.Priced) v.Add(new("data", $"{s}: {a.PriceProblem ?? "no fresh price at the decision time"}.", s));
        }

        var held = targets.Count(t => t.Weight > AllocationValidator.Tolerance);
        checks.Add(new("holdings", held >= m.MinHoldings && held <= m.MaxHoldings, $"{held} holdings (allowed {m.MinHoldings}–{m.MaxHoldings})."));
        if (held < m.MinHoldings) v.Add(new("min_holdings", $"{held} holdings proposed; the mandate requires at least {m.MinHoldings}.", null, held, m.MinHoldings));

        var cashOk = d.CashWeight >= m.MinCashWeight - SumTolerance && d.CashWeight <= m.MaxCashWeight + SumTolerance;
        checks.Add(new("cash", cashOk, $"Cash {d.CashWeight:P2} (allowed {m.MinCashWeight:P2}–{m.MaxCashWeight:P2})."));
        if (!cashOk) v.Add(new("cash", $"Cash {d.CashWeight:P2} is outside {m.MinCashWeight:P2}–{m.MaxCashWeight:P2}.", null, d.CashWeight,
            d.CashWeight < m.MinCashWeight ? m.MinCashWeight : m.MaxCashWeight));

        Exposure("currency", m.CurrencyLimits, s => universe.GetValueOrDefault(s)?.Currency);
        Exposure("country", m.CountryLimits, s => universe.GetValueOrDefault(s)?.Country);

        if (m.MaxVolatility is { } maxVol)
        {
            if (risk?.Volatility is not { } vol)
            {
                checks.Add(new("volatility", false, "The proposal's volatility could not be computed."));
                v.Add(new("risk", "The mandate limits volatility, but the proposal's volatility could not be computed from stored prices."));
            }
            else
            {
                checks.Add(new("volatility", vol <= maxVol + 1e-9, $"Ex-ante volatility {vol:P2} (limit {maxVol:P2})."));
                if (vol > maxVol + 1e-9) v.Add(new("risk", $"Ex-ante volatility {vol:P2} above the limit {maxVol:P2}.", null, vol, maxVol));
            }
        }

        if (trades is not null)
        {
            var small = trades.Lines.Where(l => l.Action is "Buy" or "Sell" && l.Value > 0 && l.Value < m.MinTradeValue).ToList();
            checks.Add(new("minimum trade", small.Count == 0, $"Smallest order {(trades.Lines.Where(l => l.Value > 0).Select(l => l.Value).DefaultIfEmpty().Min()):N2} (minimum {m.MinTradeValue:N2})."));
            foreach (var l in small)
                v.Add(new("min_trade", $"{l.Symbol}: {l.Action.ToLowerInvariant()} order of {l.Value:N2} is below the minimum trade value {m.MinTradeValue:N2}.", l.Symbol, (double)l.Value, (double)m.MinTradeValue));
        }

        return new PilotValidationResult(v.Count == 0, checks, v, risk, trades);

        void Exposure(string name, Dictionary<string, double> limits, Func<string, string?> key)
        {
            if (limits.Count == 0) return;
            var totals = targets.GroupBy(t => key(t.Symbol) ?? "Unknown", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.Weight), StringComparer.OrdinalIgnoreCase);
            var bad = 0;
            foreach (var (k, cap) in limits)
                if (totals.TryGetValue(k, out var w) && w > cap + AllocationValidator.Tolerance)
                {
                    bad++;
                    v.Add(new($"{name}_limit", $"{name[0].ToString().ToUpperInvariant()}{name[1..]} {k}: proposed {w:P2}, allowed at most {cap:P2}.", null, w, cap));
                }
            checks.Add(new($"{name} limits", bad == 0, string.Join(", ", totals.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value:P1}"))));
        }
    }
}
