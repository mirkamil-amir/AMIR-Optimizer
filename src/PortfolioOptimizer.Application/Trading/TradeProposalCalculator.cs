using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Trading;

/// <param name="Price">Latest price in the asset's own currency.</param>
/// <param name="Currency">Trading currency (empty = the portfolio currency).</param>
/// <param name="FxRate">Portfolio-currency units per unit of <paramref name="Currency"/>, dated at the price date (1 for the same currency).</param>
/// <param name="FxProblem">Why no rate is available; such an asset is treated like one without a price.</param>
public sealed record ProposalAsset(int AssetId, string Symbol, string? Name, decimal CurrentQuantity, LatestPrice? Price,
    string Currency = "", decimal FxRate = 1, DateOnly? FxRateDate = null, string? FxProblem = null)
{
    public bool IsPriced => Price is not null && FxProblem is null;
    /// <summary>Price in the portfolio currency.</summary>
    public decimal BasePrice => Price!.Price * FxRate;
}

public sealed record ProposalParameters(
    decimal Cash,
    IReadOnlyList<ProposalAsset> Assets,
    IReadOnlyDictionary<int, double> TargetWeights,
    bool AllowFractionalShares,
    double TransactionCostBps,
    decimal MinimumFee,
    double DriftThreshold);

public sealed record ProposalComputation(
    decimal PortfolioValue,
    decimal CashBefore,
    decimal CashAfter,
    decimal TotalCosts,
    decimal TargetCashWeight,
    IReadOnlyList<TradeProposalLine> Lines,
    IReadOnlyList<string> Notes);

/// <summary>
/// Converts target weights into executable (simulated) orders.
/// <list type="number">
/// <item>All values are in the portfolio currency: price × FX rate (native → portfolio currency, dated at the price date).</item>
/// <item>Portfolio value V = cash + Σ quantity × latest price (assets without a price or exchange rate are excluded and left unchanged).</item>
/// <item>Target quantity = floor(V × weight / price) (whole shares), or floor to 4 decimals when fractional shares are enabled.</item>
/// <item>Sells are processed first; their proceeds net of fees fund the buys.</item>
/// <item>Buys are processed in descending target value; a buy that would exceed available cash after fees is reduced to the
/// largest affordable quantity. The allocation can therefore never exceed available cash.</item>
/// </list>
/// </summary>
public static class TradeProposalCalculator
{
    private const int FractionalDecimals = 4;

    public static ProposalComputation Compute(ProposalParameters p)
    {
        var notes = new List<string>();
        var priced = p.Assets.Where(a => a.IsPriced).ToList();
        var portfolioValue = p.Cash + priced.Sum(a => a.CurrentQuantity * a.BasePrice);
        if (portfolioValue <= 0) throw new Domain.DomainException("Portfolio value is zero; nothing to allocate.");

        foreach (var a in p.Assets.Where(a => !a.IsPriced))
        {
            var why = a.Price is null ? "no price available" : a.FxProblem;
            notes.Add(a.CurrentQuantity > 0
                ? $"{a.Symbol}: {why}; the existing position is excluded from the valuation and left unchanged."
                : $"{a.Symbol}: {why}; its target weight stays in cash.");
        }
        foreach (var a in priced.Where(a => a.Price!.IsStale))
            notes.Add($"{a.Symbol}: latest price is from {a.Price!.PriceDate:yyyy-MM-dd} and may be stale.");

        var lines = new List<TradeProposalLine>();
        foreach (var a in p.Assets)
        {
            var w = p.TargetWeights.GetValueOrDefault(a.AssetId);
            var line = new TradeProposalLine
            {
                AssetId = a.AssetId,
                Symbol = a.Symbol,
                CompanyName = a.Name,
                Price = a.Price?.Price,
                PriceDate = a.Price?.PriceDate,
                PriceIsStale = a.Price?.IsStale ?? false,
                Currency = a.Currency,
                FxRate = a.IsPriced ? a.FxRate : null,
                FxRateDate = a.IsPriced ? a.FxRateDate : null,
                TargetWeight = w,
                CurrentQuantity = a.CurrentQuantity,
            };
            lines.Add(line);

            if (!a.IsPriced)
            {
                line.Action = ProposalAction.NoPrice;
                line.TargetQuantity = a.CurrentQuantity;
                line.Note = a.Price is null ? "No price available." : $"No exchange rate: {a.FxProblem}.";
                continue;
            }

            var price = a.BasePrice;
            line.CurrentWeight = (double)(a.CurrentQuantity * price / portfolioValue);
            line.TargetValue = Math.Round(portfolioValue * (decimal)w, 2);
            line.TargetQuantity = RoundDown(portfolioValue * (decimal)w / price, p.AllowFractionalShares);

            if (p.DriftThreshold > 0 && a.CurrentQuantity > 0 && w > 0 && Math.Abs(line.CurrentWeight - w) < p.DriftThreshold)
            {
                line.TargetQuantity = a.CurrentQuantity;
                line.Note = $"Within the {p.DriftThreshold:P1} drift threshold; no trade.";
            }

            var delta = line.TargetQuantity - a.CurrentQuantity;
            line.Action = delta > 0 ? ProposalAction.Buy : delta < 0 ? ProposalAction.Sell : ProposalAction.Hold;
            line.OrderQuantity = Math.Abs(delta);
        }

        var cash = p.Cash;
        var costs = 0m;

        foreach (var line in lines.Where(l => l.Action == ProposalAction.Sell))
        {
            var value = line.OrderQuantity * BasePrice(line);
            var fee = TransactionCostModel.Estimate(value, p.TransactionCostBps, p.MinimumFee);
            line.EstimatedTradeValue = value;
            line.EstimatedCost = fee;
            cash += value - fee;
            costs += fee;
        }

        foreach (var line in lines.Where(l => l.Action == ProposalAction.Buy).OrderByDescending(l => l.TargetValue))
        {
            var price = BasePrice(line);
            var qty = line.OrderQuantity;
            var fee = TransactionCostModel.Estimate(qty * price, p.TransactionCostBps, p.MinimumFee);
            if (qty * price + fee > cash)
            {
                var original = qty;
                qty = Math.Min(qty, MaxAffordable(cash, price, p));
                fee = TransactionCostModel.Estimate(qty * price, p.TransactionCostBps, p.MinimumFee);
                line.Note = qty > 0
                    ? $"Reduced from {original} to {qty} shares: available cash after fees."
                    : "Not enough cash remaining after fees for a single unit.";
                line.TargetQuantity = line.CurrentQuantity + qty;
                line.OrderQuantity = qty;
                if (qty == 0)
                {
                    line.Action = ProposalAction.Hold;
                    fee = 0;
                }
            }
            line.EstimatedTradeValue = qty * price;
            line.EstimatedCost = fee;
            cash -= qty * price + fee;
            costs += fee;
        }

        foreach (var line in lines.Where(l => l.Price is not null && l.FxRate is not null))
        {
            line.ResultingWeight = (double)(line.TargetQuantity * BasePrice(line) / portfolioValue);
            if (line.Note is null && line.Action is ProposalAction.Buy or ProposalAction.Hold && line.TargetWeight > 0)
            {
                var gap = line.ResultingWeight - line.TargetWeight;
                if (Math.Abs(gap) > 0.0005)
                    line.Note = $"Resulting weight differs from target by {gap:+0.00%;-0.00%} due to {(p.AllowFractionalShares ? "rounding" : "whole-share rounding")}.";
            }
        }

        var targetCashWeight = 1m - p.TargetWeights.Values.Sum(v => (decimal)v);
        var cashWeightAfter = cash / portfolioValue;
        if (cashWeightAfter - targetCashWeight > 0.001m)
        {
            notes.Add($"{cash:N2} remains in cash ({cashWeightAfter:P2} of value vs. {targetCashWeight:P2} targeted) because of " +
                      $"{(p.AllowFractionalShares ? "rounding" : "whole-share rounding")}, transaction costs and any missing prices.");
        }
        if (costs > 0) notes.Add($"Estimated transaction costs: {costs:N2} ({p.TransactionCostBps} bps, minimum {p.MinimumFee:N2} per order).");

        return new ProposalComputation(portfolioValue, p.Cash, cash, costs, targetCashWeight, lines, notes);
    }

    /// <summary>Price of one unit in the portfolio currency.</summary>
    public static decimal BasePrice(TradeProposalLine line) => line.Price!.Value * (line.FxRate ?? throw new InvalidOperationException($"{line.Symbol}: no exchange rate."));

    private static decimal MaxAffordable(decimal cash, decimal price, ProposalParameters p) =>
        MaxAffordable(cash, price, p.AllowFractionalShares, p.TransactionCostBps, p.MinimumFee);

    /// <summary>Largest quantity whose cost including fees fits in <paramref name="cash"/>.</summary>
    public static decimal MaxAffordable(decimal cash, decimal price, bool fractional, double bps, decimal minimumFee)
    {
        if (cash <= 0 || price <= 0) return 0;
        // Closed-form estimate for both fee regimes (proportional and minimum), then correct for rounding.
        var proportional = cash / (price * (1 + (decimal)bps / 10_000m));
        var withMinimum = (cash - minimumFee) / price;
        var qty = RoundDown(Math.Min(proportional, withMinimum), fractional);
        var step = fractional ? 1m / (decimal)Math.Pow(10, FractionalDecimals) : 1m;
        while (qty > 0 && qty * price + TransactionCostModel.Estimate(qty * price, bps, minimumFee) > cash) qty -= step;
        return Math.Max(0, qty);
    }

    public static decimal RoundDown(decimal value, bool fractional)
    {
        if (value <= 0) return 0;
        if (!fractional) return Math.Floor(value);
        var factor = (decimal)Math.Pow(10, FractionalDecimals);
        return Math.Floor(value * factor) / factor;
    }
}
