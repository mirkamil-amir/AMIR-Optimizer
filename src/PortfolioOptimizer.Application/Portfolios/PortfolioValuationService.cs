using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Portfolios;

/// <summary>A position valued in the valuation currency.</summary>
/// <param name="Price">Latest price in the asset's own currency.</param>
/// <param name="Fx">Rate from the asset's currency to the valuation currency, dated at the price date (null for the same currency).</param>
/// <param name="UnitValue">Price × rate: value of one share in the valuation currency (null when the price or the rate is missing).</param>
/// <param name="Problem">Why the position cannot be valued, if it cannot.</param>
public sealed record HoldingValue(int AssetId, string Symbol, string Currency, decimal Quantity, LatestPrice? Price, FxQuote? Fx, decimal? UnitValue, string? Problem)
{
    public decimal? Value => UnitValue is { } u ? Quantity * u : null;
}

/// <summary>
/// The portfolio valued in <see cref="Currency"/>. <see cref="MarketValue"/> and <see cref="TotalValue"/> are null when a
/// held position cannot be converted (missing or too old exchange rate): no plausible-looking partial total is reported.
/// A position without any price is excluded with a warning (existing behaviour, see <see cref="Warnings"/>).
/// </summary>
public sealed record PortfolioValuation(
    string Currency,
    string AccountCurrency,
    decimal Cash,
    FxQuote? CashFx,
    IReadOnlyDictionary<int, HoldingValue> Positions,
    decimal? MarketValue,
    decimal? TotalValue,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    string? FxSource,
    bool UsesSimulatedData)
{
    public bool IsReportingConversion => Currency != AccountCurrency;
}

/// <summary>
/// The single place where holdings are valued in a currency. Positions are valued at native price × the FX rate of the
/// price date (native → valuation currency); cash is held in the account currency and converted only for a different
/// reporting currency. Converting for reporting never changes quantities, native prices or the stored cash balance.
/// </summary>
public sealed class PortfolioValuationService(IApplicationDbContext db, PriceQueryService prices, FxService fx, TimeProvider clock)
{
    public async Task<PortfolioValuation> ValueAsync(Portfolio portfolio, string? currency, CancellationToken ct, IReadOnlyCollection<int>? additionalAssetIds = null)
    {
        var target = currency is null ? portfolio.BaseCurrency : Currencies.Normalize(currency);
        var app = await prices.SettingsAsync(ct);
        var held = portfolio.Holdings.Where(h => h.Quantity > 0).ToDictionary(h => h.AssetId, h => h.Quantity);
        var ids = held.Keys.Union(additionalAssetIds ?? []).ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var latest = await prices.GetLatestPricesAsync(ids, app.ActiveProvider, app.StalePriceDays, ct);
        var simulated = latest.Values.Any(l => l.IsSimulated) || await fx.ActiveSourceIsSimulatedAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var currencies = assets.Values.Select(a => a.Currency).Append(portfolio.BaseCurrency).Append(target).Distinct().ToList();
        FxTable? table = null;
        string? tableError = null;
        if (currencies.Any(c => c != target))
        {
            try
            {
                var oldest = latest.Values.Select(l => l.PriceDate).Append(today).Min();
                table = await fx.GetTableAsync(currencies, oldest, today, simulated, ct);
            }
            catch (Common.ValidationException ex)
            {
                tableError = ex.Message;
            }
        }

        var problems = new List<string>();
        var warnings = new List<string>();
        var positions = new Dictionary<int, HoldingValue>();
        foreach (var id in ids)
        {
            if (!assets.TryGetValue(id, out var asset)) continue;
            var quantity = held.GetValueOrDefault(id);
            latest.TryGetValue(id, out var lp);
            FxQuote? quote = null;
            string? problem = null;
            decimal? unit = null;
            if (lp is null)
            {
                problem = "no price available";
                if (quantity > 0) warnings.Add($"{asset.Symbol}: no price available; excluded from market value.");
            }
            else if (asset.Currency == target) unit = lp.Price;
            else if (table is null) problem = tableError ?? $"no {asset.Currency}→{target} exchange rate";
            else if (table.TryRate(asset.Currency, target, lp.PriceDate, out quote, out var why)) unit = lp.Price * (decimal)quote!.Rate;
            else problem = $"no {asset.Currency}→{target} exchange rate for the price date {lp.PriceDate:yyyy-MM-dd}: {why}";

            if (problem is not null && lp is not null && quantity > 0)
                problems.Add($"{asset.Symbol} ({asset.Currency}) cannot be valued in {target}: {problem}.");
            positions[id] = new HoldingValue(id, asset.Symbol, asset.Currency, quantity, lp, quote, unit, problem);
        }

        FxQuote? cashFx = null;
        decimal? cash = portfolio.CashBalance;
        if (portfolio.BaseCurrency != target)
        {
            string? why = tableError ?? "no exchange rates loaded";
            if (table is not null && table.TryRate(portfolio.BaseCurrency, target, today, out cashFx, out why))
                cash = portfolio.CashBalance * (decimal)cashFx!.Rate;
            else
            {
                cash = null;
                problems.Add($"Cash ({portfolio.BaseCurrency}) cannot be shown in {target}: {why}.");
            }
        }

        var valued = positions.Values.Where(p => p.Quantity > 0 && p.Price is not null).ToList();
        decimal? marketValue = valued.Any(p => p.UnitValue is null) ? null : valued.Sum(p => p.Value!.Value);
        decimal? total = marketValue is { } mv && cash is { } c ? mv + c : null;
        return new PortfolioValuation(target, portfolio.BaseCurrency, cash ?? 0, cashFx, positions, marketValue, total, problems, warnings,
            table?.Source, simulated);
    }
}
