using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Trading;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Pilot;

/// <summary>Backward-looking behaviour of one listing in the analysis currency, computed from stored prices up to the decision date.</summary>
public sealed record PilotAssetMetrics(
    string Symbol,
    string? Sector,
    string Currency,
    string? Country,
    DateOnly? LastPriceDate,
    int Observations,
    double? Return1M,
    double? Return3M,
    double? Return6M,
    double? Return12M,
    double? Volatility12M,
    double? MaxDrawdown12M,
    double? ReturnPerVolatility12M);

public sealed record PilotRiskAnalysis(
    PilotRiskSummary Summary,
    IReadOnlyDictionary<string, double> RiskShares,
    double? DiversificationRatio,
    double? AverageCorrelation,
    string? HighestCorrelation,
    double? ExpectedShortfall95,
    double? WorstDay,
    double? Worst20Days,
    double? BetaToBenchmark,
    double? StressCorrelation,
    IReadOnlyList<string> Excluded,
    string Basis);

/// <summary>
/// The deterministic calculations behind the pilot's tools and its validation. Every price series ends at the cycle's
/// decision date and is converted to the portfolio currency with dated rates (<see cref="FxService"/>); formulas are the
/// platform's (<see cref="PerformanceMetrics"/>, <see cref="DiversificationMath"/>, <see cref="CovarianceEstimator"/>).
/// </summary>
public sealed class PilotAnalytics(IApplicationDbContext db, FxService fx, TradingService trading)
{
    private static readonly PerformanceMetrics Metrics = new(AnalyticsConventions.Default);

    public async Task<(Dictionary<string, PriceSeries> Series, IReadOnlyDictionary<string, string> Excluded)> SeriesAsync(
        PilotToolContext ctx, IReadOnlyCollection<string> symbols, int lastN, CancellationToken ct)
    {
        var conversion = await fx.GetSeriesInCurrencyAsync(symbols, ctx.PriceSource, null, ctx.AsOfDate, ctx.Currency, ct, lastN);
        var series = conversion.PriceSeries();
        // Defensive: nothing dated after the decision date may pass, whatever the store returns.
        foreach (var key in series.Keys.ToList())
            series[key] = series[key] with { Points = series[key].Points.Where(p => p.Date <= ctx.AsOfDate).ToList() };
        return (series, conversion.Excluded);
    }

    public async Task<IReadOnlyDictionary<string, PilotAssetMetrics>> MetricsAsync(PilotToolContext ctx, IReadOnlyCollection<string> symbols, CancellationToken ct)
    {
        var missing = symbols.Where(s => !ctx.MetricsCache.ContainsKey(s)).ToList();
        if (missing.Count > 0)
        {
            var (series, _) = await SeriesAsync(ctx, missing, 253, ct);
            foreach (var s in missing)
            {
                ctx.Universe.TryGetValue(s, out var info);
                var pts = series.TryGetValue(s, out var ps) ? ps.Points : [];
                ctx.MetricsCache[s] = Compute(s, info, pts);
            }
        }
        return symbols.Where(ctx.MetricsCache.ContainsKey).ToDictionary(s => s, s => ctx.MetricsCache[s], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Trailing returns over 21/63/126/252 trading days, 12-month volatility and drawdown (pure).</summary>
    public static PilotAssetMetrics Compute(string symbol, PilotAssetInfo? info, IReadOnlyList<PricePoint> points)
    {
        var prices = points.Select(p => p.Price).ToList();
        double? Ret(int n) => prices.Count > n && prices[^(n + 1)] > 0 ? prices[^1] / prices[^(n + 1)] - 1 : null;
        double? vol = null, dd = null;
        if (prices.Count >= 61 && prices.All(p => p > 0))
        {
            var window = prices.TakeLast(253).ToList();
            vol = Metrics.AnnualizedVolatility(ReturnCalculator.SimpleReturns(window)).Value;
            dd = PerformanceMetrics.MaxDrawdown(window).Value;
        }
        var r12 = Ret(252);
        return new PilotAssetMetrics(symbol, info?.Sector, info?.Currency ?? "", info?.Country, points.Count > 0 ? points[^1].Date : null, points.Count,
            Ret(21), Ret(63), Ret(126), r12, vol, dd, r12 is { } r && vol is > 0 ? r / vol : null);
    }

    /// <summary>Risk of fixed weights over a trailing window (weights are fractions of the whole portfolio; the rest is cash at 0 %).</summary>
    public async Task<PilotRiskAnalysis> AnalyzeAsync(PilotToolContext ctx, IReadOnlyDictionary<string, double> weights, int lookbackDays, CancellationToken ct)
    {
        var symbols = weights.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        var (series, excludedFx) = await SeriesAsync(ctx, symbols.Append(ctx.Benchmark).Distinct().ToList(), lookbackDays + 1, ct);
        var excluded = new List<string>();
        foreach (var s in symbols.ToList())
        {
            if (excludedFx.TryGetValue(s, out var why)) { excluded.Add($"{s}: {why}"); symbols.Remove(s); }
            else if (!series.TryGetValue(s, out var ps) || ps.Points.Count < 61) { excluded.Add($"{s}: fewer than 61 stored prices up to {ctx.AsOfDate:yyyy-MM-dd}."); symbols.Remove(s); }
        }

        var exposures = Exposures(ctx, weights);
        if (symbols.Count == 0)
            return new PilotRiskAnalysis(new PilotRiskSummary(null, null, null, null, exposures.Sectors, exposures.Currencies, exposures.Countries, null, null, 0, "no data"),
                new Dictionary<string, double>(), null, null, null, null, null, null, null, null, excluded, "No position has enough stored prices.");

        var points = symbols.ToDictionary(s => s, s => series[s].Points, StringComparer.OrdinalIgnoreCase);
        var matrix = ReturnMatrix.Build(points, symbols);
        var w = symbols.Select(s => weights[s]).ToList();
        if (matrix.Observations < 30)
            return new PilotRiskAnalysis(new PilotRiskSummary(null, null, null, null, exposures.Sectors, exposures.Currencies, exposures.Countries, null, null, matrix.Observations, "too short"),
                new Dictionary<string, double>(), null, null, null, null, null, null, null, null, excluded, $"Only {matrix.Observations} aligned daily returns.");

        var est = CovarianceEstimator.Estimate(matrix.Returns, CovarianceMethod.LedoitWolf, AnalyticsConventions.Default);
        var risk = DiversificationMath.Risk(symbols, w, est.Covariance);
        var sectors = symbols.Where(s => ctx.Universe.GetValueOrDefault(s)?.Sector is not null).ToDictionary(s => s, s => ctx.Universe[s].Sector!, StringComparer.OrdinalIgnoreCase);
        var conc = DiversificationMath.Concentration(symbols, w, sectors);
        var daily = DiversificationMath.PortfolioReturns(matrix.Returns, w);
        var hist = DiversificationMath.History(daily, AnalyticsConventions.Default);
        var stress = symbols.Count > 1 ? DiversificationMath.CorrelationUnderStress(matrix.Returns, Enumerable.Range(0, symbols.Count).ToList()) : null;

        double? beta = null;
        if (series.TryGetValue(ctx.Benchmark, out var bench) && bench.Points.Count > 61)
        {
            var bmap = bench.Points.ToDictionary(p => p.Date, p => p.Price);
            var pairs = new List<(double P, double B)>();
            for (var i = 1; i < matrix.PriceDates.Count; i++)
                if (bmap.TryGetValue(matrix.PriceDates[i], out var b1) && bmap.TryGetValue(matrix.PriceDates[i - 1], out var b0) && b0 > 0)
                    pairs.Add((daily[i - 1], b1 / b0 - 1));
            if (pairs.Count >= 30) beta = PerformanceMetrics.Beta(pairs.Select(p => p.P).ToList(), pairs.Select(p => p.B).ToList()).Value;
        }

        var window = $"{matrix.PriceDates[0]:yyyy-MM-dd} – {matrix.PriceDates[^1]:yyyy-MM-dd}";
        return new PilotRiskAnalysis(
            new PilotRiskSummary(risk.Volatility, conc.EffectiveHoldings, w.Max(), symbols[w.IndexOf(w.Max())],
                exposures.Sectors, exposures.Currencies, exposures.Countries, hist.ValueAtRisk95.Value, hist.MaxDrawdown.Value, matrix.Observations, window),
            symbols.Select((s, i) => (s, risk.RiskShares[i])).ToDictionary(x => x.s, x => Math.Round(x.Item2, 4)),
            risk.DiversificationRatio, risk.AverageCorrelation,
            risk.HighestCorrelation is { } hc ? $"{hc.A}/{hc.B} {hc.Correlation:0.00}" : null,
            hist.ExpectedShortfall95.Value, hist.WorstDay, hist.Worst20DayReturn, beta, stress?.StressDays, excluded,
            $"Historical estimates over {window} ({matrix.Observations} daily returns, Ledoit–Wolf covariance, cash at 0 %); not forecasts.");
    }

    public static (Dictionary<string, double> Sectors, Dictionary<string, double> Currencies, Dictionary<string, double> Countries) Exposures(
        PilotToolContext ctx, IReadOnlyDictionary<string, double> weights)
    {
        Dictionary<string, double> By(Func<PilotAssetInfo?, string?> key) => weights.Where(kv => kv.Value > 0)
            .GroupBy(kv => key(ctx.Universe.GetValueOrDefault(kv.Key)) ?? "Unknown", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(kv => kv.Value), 4), StringComparer.OrdinalIgnoreCase);
        return (By(a => a?.Sector), By(a => a?.Currency), By(a => a?.Country));
    }

    /// <summary>What the target weights would trade, using the same calculator as a real proposal (nothing is stored).</summary>
    public async Task<PilotTradeEstimate> EstimateTradesAsync(PilotToolContext ctx, IReadOnlyDictionary<string, double> targets, CancellationToken ct)
    {
        var symbols = targets.Keys.ToList();
        var ids = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, a => a.Id, ct);
        var unknown = symbols.Except(ids.Keys).ToList();
        if (unknown.Count > 0) throw new PilotToolException("invalid_arguments", $"Unknown symbols: {string.Join(", ", unknown)}.");
        var computation = await trading.PreviewAsync(ctx.Portfolio, targets.ToDictionary(kv => ids[kv.Key], kv => kv.Value), ct);
        var lines = computation.Lines.Select(l => new PilotTradeLine(l.Symbol, l.Action.ToString(), l.OrderQuantity, Math.Round(l.EstimatedTradeValue, 2),
            Math.Round(l.CurrentWeight, 4), Math.Round(l.TargetWeight, 4), l.Note)).ToList();
        var buys = computation.Lines.Where(l => l.Action == ProposalAction.Buy).ToList();
        var sells = computation.Lines.Where(l => l.Action == ProposalAction.Sell).ToList();
        var turnover = computation.PortfolioValue > 0
            ? (double)((buys.Sum(l => l.EstimatedTradeValue) + sells.Sum(l => l.EstimatedTradeValue)) / computation.PortfolioValue) / 2
            : 0;
        return new PilotTradeEstimate(buys.Count, sells.Count, Math.Round(buys.Sum(l => l.EstimatedTradeValue), 2), Math.Round(sells.Sum(l => l.EstimatedTradeValue), 2),
            Math.Round(computation.TotalCosts, 2), Math.Round(turnover, 4), lines, computation.Notes);
    }
}
