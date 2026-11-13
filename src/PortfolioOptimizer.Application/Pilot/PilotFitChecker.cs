using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Pilot;

/// <summary>Result of checking a candidate against the mandate: the validator's verdict plus the risk it was judged on.</summary>
public sealed record PilotFitResult(PilotValidationResult Validation, PilotRiskAnalysis? Risk)
{
    public bool Fits => Validation.Passed;
}

/// <summary>
/// The one mandate check used everywhere: the model's <c>check_allocation</c> tool during research, the fit loop after each
/// proposal and the final validation before booking. A HOLD is checked as "the current portfolio stays as it is", so holding
/// a portfolio that breaks the mandate is a breach too.
/// </summary>
public sealed class PilotFitChecker(PilotAnalytics analytics)
{
    public async Task<PilotFitResult> CheckAsync(PilotToolContext ctx, PilotDecision decision, bool requireTexts, CancellationToken ct)
    {
        var current = CurrentWeights(ctx);
        if (decision.IsHold && current.Count == 0)
        {
            // Holding an all-cash portfolio: say what that breaks instead of a generic "no holdings" structure error.
            var m = ctx.Mandate;
            List<PilotViolation> breaches =
            [
                new("min_holdings", $"Holding keeps 0 holdings; the mandate requires at least {m.MinHoldings}.", null, 0, m.MinHoldings),
            ];
            if (m.MaxCashWeight < 1 - PilotDecisionValidator.SumTolerance)
                breaches.Add(new("cash", $"Holding keeps 100.00 % cash; the mandate allows at most {m.MaxCashWeight:P2}.", null, 1, m.MaxCashWeight));
            return new PilotFitResult(new PilotValidationResult(false,
                [new PilotCheck("hold", false, "Holding would keep an all-cash portfolio that breaks the mandate – it has to be invested.")], breaches, null, null), null);
        }
        var candidate = decision.IsHold ? CurrentAsDecision(ctx, decision) : decision;
        var structure = PilotDecisionValidator.CheckStructure(candidate, ctx.Universe, requireTexts && !decision.IsHold);
        if (structure.Count > 0)
            return new PilotFitResult(PilotDecisionValidator.Validate(candidate, ctx.Mandate, ctx.Universe, current, null, null, requireTexts && !decision.IsHold), null);

        var weights = candidate.Targets.Where(t => t.Weight > 0).ToDictionary(t => t.Symbol, t => t.Weight);
        PilotRiskAnalysis? risk = weights.Count == 0 ? null : await analytics.AnalyzeAsync(ctx, weights, 504, ct);
        var trades = decision.IsHold || weights.Count == 0 ? null : await analytics.EstimateTradesAsync(ctx, weights, ct);
        var result = PilotDecisionValidator.Validate(candidate, ctx.Mandate, ctx.Universe, current, risk?.Summary, trades, requireTexts && !decision.IsHold);
        if (decision.IsHold)
            result = result with
            {
                Checks = [new PilotCheck("hold", result.Passed, result.Passed
                    ? "Holding keeps a portfolio that fits the mandate."
                    : "Holding would keep a portfolio that breaks the mandate – it has to be rebalanced."), .. result.Checks],
            };
        return new PilotFitResult(result, risk);
    }

    public static Dictionary<string, double> CurrentWeights(PilotToolContext ctx) =>
        ctx.Observation.Holdings.Where(h => h.Weight is > 0).ToDictionary(h => h.Symbol, h => h.Weight!.Value, StringComparer.Ordinal);

    /// <summary>The current portfolio written as a decision (weights from the observation; cash = the rest).</summary>
    public static PilotDecision CurrentAsDecision(PilotToolContext ctx, PilotDecision hold)
    {
        var weights = CurrentWeights(ctx);
        return hold with
        {
            Action = "REBALANCE",
            Targets = weights.Select(kv => new PilotTarget { Symbol = kv.Key, Weight = kv.Value, Thesis = "current holding" }).ToList(),
            CashWeight = Math.Max(0, 1 - weights.Values.Sum()),
        };
    }
}

/// <summary>Loads the catalogue for the unconstrained portfolio: enabled listings whose stored prices are recent at the decision date.</summary>
public static class PilotCatalogue
{
    public const int CalendarDaysFresh = 10;

    public static async Task EnsureLoadedAsync(PilotToolContext ctx, IApplicationDbContext db, CancellationToken ct)
    {
        if (ctx.Catalogue is not null) return;
        var lo = ctx.AsOfDate.AddDays(-CalendarDaysFresh);
        var hi = ctx.AsOfDate;
        var rows = await db.PriceCoverage.AsNoTracking()
            .Where(c => c.Source == ctx.PriceSource && c.LastDate >= lo && c.LastDate <= hi && c.Observations >= 61)
            .Join(db.Assets.AsNoTracking().Where(a => a.IsEnabled), c => c.AssetId, a => a.Id,
                (c, a) => new { a.Id, a.Symbol, a.Name, a.Sector, a.Currency, a.Country })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var memberships = (await db.IndexMemberships.AsNoTracking().Where(m => ids.Contains(m.AssetId)).Select(m => new { m.AssetId, m.IndexCode }).ToListAsync(ct))
            .ToLookup(m => m.AssetId, m => m.IndexCode);
        ctx.Catalogue = rows.ToDictionary(r => r.Symbol, r => ctx.Universe.TryGetValue(r.Symbol, out var u) ? u
            : new PilotAssetInfo(r.Symbol, r.Name, r.Sector, r.Currency, r.Country, true, null), StringComparer.Ordinal);
        ctx.Memberships = rows.ToDictionary(r => r.Symbol, r => (IReadOnlyList<string>)memberships[r.Id].ToList(), StringComparer.Ordinal);
    }
}
