using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Analytics;

/// <summary>Holdings valuation, realized portfolio performance and ex-ante risk of the simulated portfolio.</summary>
public sealed class PortfolioAnalyticsService(
    IApplicationDbContext db,
    PortfolioService portfolios,
    PriceQueryService prices,
    AnalyticsConventions conventions)
{
    public const string PerformanceMethodology =
        "Daily portfolio value = cash + Σ quantity × raw close (the same price convention used for simulated fills). " +
        "Trades are dated at the close of the price used for execution. Returns are time-weighted (external cash flows removed). " +
        "Dividends are not credited to the paper portfolio. Benchmark returns use adjusted closes (total return).";

    public const string RiskMethodology =
        "Ex-ante risk of current weights (market value / total value, cash at zero risk) applied to the trailing window of daily " +
        "adjusted-close returns (historical simulation). VaR/CVaR are one-day historical estimates at the stated confidence level.";

    // ------------------------------------------------------------------ holdings

    public async Task<HoldingsResponse> GetHoldingsAsync(Guid portfolioId, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var app = await prices.SettingsAsync(ct);
        var source = app.ActiveProvider;
        var warnings = new List<string>();

        var holdings = portfolio.Holdings.Where(h => h.Quantity > 0 || h.RealizedPnl != 0).ToList();
        var ids = holdings.Select(h => h.AssetId).ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var latest = await prices.GetLatestPricesAsync(ids, source, app.StalePriceDays, ct);
        var lastTwo = await LastTwoClosesAsync(ids, source, ct);
        var targets = await LatestTargetsAsync(portfolioId, ct);
        var executions = await db.Executions.AsNoTracking().Where(e => e.PortfolioId == portfolioId).ToListAsync(ct);

        var marketValue = 0m;
        foreach (var h in holdings.Where(h => h.Quantity > 0))
        {
            if (latest.TryGetValue(h.AssetId, out var lp)) marketValue += h.Quantity * lp.Price;
            else warnings.Add($"{h.Symbol}: no price available; excluded from market value.");
        }
        var totalValue = marketValue + portfolio.CashBalance;

        var rows = holdings.OrderByDescending(h => latest.TryGetValue(h.AssetId, out var lp) ? h.Quantity * lp.Price : 0).Select(h =>
        {
            assets.TryGetValue(h.AssetId, out var a);
            latest.TryGetValue(h.AssetId, out var lp);
            decimal? mv = lp is null ? null : h.Quantity * lp.Price;
            double? weight = mv is { } v && totalValue > 0 ? (double)(v / totalValue) : null;
            var target = targets.TryGetValue(h.AssetId, out var t) ? t : (double?)null;
            // Daily change only applies to shares held over the last close-to-close interval (not shares bought at the last close).
            decimal? daily = null;
            if (lastTwo.TryGetValue(h.AssetId, out var two) && two.Prev is { } prev)
            {
                var boughtSince = executions.Where(e => e.AssetId == h.AssetId && e.PriceDate >= two.LastDate)
                    .Sum(e => e.Side == OrderSide.Buy ? e.Quantity : -e.Quantity);
                daily = (h.Quantity - boughtSince) * (two.Last - prev);
            }
            if (lp?.IsStale == true) warnings.Add($"{h.Symbol}: price from {lp.PriceDate:yyyy-MM-dd} is stale.");
            return new HoldingDto(h.AssetId, h.Symbol, a?.Name, a?.Sector, h.Quantity, h.AverageCost, h.CostBasis,
                lp?.Price, lp?.PriceDate, lp?.IsStale ?? false, mv, weight,
                mv is { } m ? m - h.CostBasis : null,
                mv is { } m2 && h.CostBasis > 0 ? (double)(m2 / h.CostBasis - 1) : null,
                h.RealizedPnl, target, weight is { } w && target is { } tw ? w - tw : null, daily, h.UpdatedAtUtc);
        }).ToList();

        // Targets for assets not (or no longer) held.
        foreach (var (assetId, w) in targets.Where(t => holdings.All(h => h.AssetId != t.Key || h.Quantity == 0)))
        {
            if (rows.Any(r => r.AssetId == assetId)) continue;
            var a = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == assetId, ct);
            rows.Add(new HoldingDto(assetId, a.Symbol, a.Name, a.Sector, 0, 0, 0, null, null, false, 0, 0, null, null, 0, w, -w, null, portfolio.UpdatedAtUtc));
        }

        var deposits = await db.CashLedger.AsNoTracking()
            .Where(c => c.PortfolioId == portfolioId && (c.Type == CashEntryType.Deposit || c.Type == CashEntryType.Withdrawal))
            .Select(c => c.Amount).ToListAsync(ct);
        var netContrib = deposits.Sum();
        var dailyChange = rows.Any(r => r.DailyChange is not null) ? rows.Sum(r => r.DailyChange ?? 0) : (decimal?)null;

        double? benchReturn = null;
        var firstTrade = executions.OrderBy(e => e.PriceDate).FirstOrDefault();
        if (firstTrade is not null)
        {
            var b = await prices.GetSeriesAsync([portfolio.Settings.BenchmarkSymbol], source, firstTrade.PriceDate, null, ct);
            if (b.TryGetValue(portfolio.Settings.BenchmarkSymbol, out var bs) && bs.Points.Count >= 1 && bs.Points[0].Date == firstTrade.PriceDate)
                benchReturn = bs.Points[^1].Price / bs.Points[0].Price - 1;
            else warnings.Add($"Benchmark {portfolio.Settings.BenchmarkSymbol} has no price on the first trade date; relative return unavailable.");
        }

        var totalReturn = netContrib > 0 ? (double)(totalValue / netContrib - 1) : 0;
        var summary = new PortfolioValuationDto(
            marketValue, portfolio.CashBalance, totalValue,
            totalValue > 0 ? (double)(marketValue / totalValue) : 0,
            holdings.Sum(h => h.CostBasis),
            rows.Sum(r => r.UnrealizedPnl ?? 0),
            holdings.Sum(h => h.RealizedPnl),
            executions.Sum(e => e.Fees),
            netContrib,
            dailyChange,
            dailyChange is { } dc && totalValue - dc > 0 ? (double)(dc / (totalValue - dc)) : null,
            totalReturn,
            benchReturn,
            benchReturn is { } br ? totalReturn - br : null,
            portfolio.Settings.BenchmarkSymbol,
            latest.Count > 0 ? latest.Values.Max(l => l.PriceDate) : null,
            source,
            latest.Values.Any(l => l.IsSimulated),
            warnings.Distinct().ToList());
        return new HoldingsResponse(summary, rows);
    }

    // ------------------------------------------------------------------ performance

    public async Task<PerformanceDto> GetPerformanceAsync(Guid portfolioId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var app = await prices.SettingsAsync(ct);
        var source = app.ActiveProvider;
        var benchmark = portfolio.Settings.BenchmarkSymbol;
        var warnings = new List<string>();
        var metrics = new PerformanceMetrics(conventions);

        var executions = await db.Executions.AsNoTracking().Where(e => e.PortfolioId == portfolioId).OrderBy(e => e.ExecutedAtUtc).ToListAsync(ct);
        var deposits = await db.CashLedger.AsNoTracking()
            .Where(c => c.PortfolioId == portfolioId && (c.Type == CashEntryType.Deposit || c.Type == CashEntryType.Withdrawal))
            .ToListAsync(ct);

        PerformanceDto Empty(string reason)
        {
            var na = new MetricDto(null, reason);
            return new PerformanceDto(portfolioId, benchmark, null, null, [], na, na, na, na, na, na, na, [], PerformanceMethodology, source, false, [reason]);
        }

        if (executions.Count == 0) return Empty("No simulated trades yet; performance history starts with the first execution.");

        var start = executions.Min(e => e.PriceDate);
        var assetIds = executions.Select(e => e.AssetId).Distinct().ToList();
        var closes = await prices.GetRawClosesAsync(assetIds, source, start, ct);
        var calendar = closes.Values.SelectMany(c => c.Keys).Where(d => d >= start).Distinct().OrderBy(d => d).ToList();
        if (from is { } f) calendar = calendar.Where(d => d >= f).ToList();
        if (to is { } t) calendar = calendar.Where(d => d <= t).ToList();
        if (calendar.Count == 0) return Empty("No price data in the selected range.");

        // Deposits made before the first trade form the starting capital; later deposits are external flows.
        var firstExecution = executions.Min(e => e.ExecutedAtUtc);
        var initialCash = deposits.Where(d => d.TimestampUtc <= firstExecution).Sum(d => d.Amount);
        var values = new List<double>();
        var flows = new List<double>();
        var execIdx = 0;
        var qty = new Dictionary<int, decimal>();
        var cash = initialCash;
        var ordered = executions.OrderBy(e => e.PriceDate).ThenBy(e => e.ExecutedAtUtc).ToList();
        var lastPx = new Dictionary<int, decimal>();
        var depositsAfter = deposits.Where(d => d.TimestampUtc > firstExecution).ToList();

        // Replay all executions dated before the visible window so the first visible value is correct.
        foreach (var d in closes.Values.SelectMany(c => c.Keys).Where(d => d >= start && d < calendar[0]).Distinct().OrderBy(d => d))
            Apply(d);

        foreach (var d in calendar)
        {
            var flow = Apply(d);
            var mv = qty.Where(kv => kv.Value != 0).Sum(kv => lastPx.TryGetValue(kv.Key, out var px) ? kv.Value * px : 0);
            values.Add((double)(cash + mv));
            flows.Add((double)flow);
        }

        decimal Apply(DateOnly d)
        {
            foreach (var (id, series) in closes)
                if (series.TryGetValue(d, out var px)) lastPx[id] = px;
            while (execIdx < ordered.Count && ordered[execIdx].PriceDate <= d)
            {
                var e = ordered[execIdx++];
                qty[e.AssetId] = qty.GetValueOrDefault(e.AssetId) + (e.Side == OrderSide.Buy ? e.Quantity : -e.Quantity);
                cash += e.NetCashFlow;
            }
            var flow = depositsAfter.Where(x => DateOnly.FromDateTime(x.TimestampUtc) == d).Sum(x => x.Amount);
            cash += flow;
            return flow;
        }

        // Time-weighted daily returns: r_t = (V_t − F_t) / V_{t−1} − 1.
        var returns = new List<double>();
        for (var i = 1; i < values.Count; i++)
            returns.Add(values[i - 1] > 0 ? (values[i] - flows[i]) / values[i - 1] - 1 : 0);
        var wealth = ReturnCalculator.WealthIndex(returns);
        var drawdowns = PerformanceMetrics.DrawdownSeries(wealth);

        var benchSeries = await prices.GetSeriesAsync([benchmark], source, calendar[0], calendar[^1], ct);
        Dictionary<DateOnly, double>? bmap = null;
        if (benchSeries.TryGetValue(benchmark, out var bs) && bs.Points.Count > 0 && bs.Points[0].Date == calendar[0])
            bmap = bs.Points.ToDictionary(p => p.Date, p => p.Price);
        else warnings.Add($"Benchmark {benchmark} has no price on {calendar[0]:yyyy-MM-dd}; benchmark comparison unavailable.");

        var series = new List<PerformancePointDto>();
        double? lastBench = null;
        for (var i = 0; i < calendar.Count; i++)
        {
            double? bcum = null;
            if (bmap is not null)
            {
                if (bmap.TryGetValue(calendar[i], out var bp)) lastBench = bp;
                bcum = lastBench / bmap[calendar[0]] - 1;
            }
            series.Add(new PerformancePointDto(calendar[i], values[i], wealth[i] - 1, bcum, drawdowns[i]));
        }

        if (returns.Count < 20)
            warnings.Add($"Only {returns.Count} daily return(s) since the first trade; annualized statistics are not meaningful yet.");

        var monthly = MonthlyReturns(calendar, wealth, series.Select(s => s.BenchmarkCumulativeReturn).ToList());
        var benchTotal = series[^1].BenchmarkCumulativeReturn;
        var simulated = (await prices.GetLatestPricesAsync(assetIds, source, app.StalePriceDays, ct)).Values.Any(l => l.IsSimulated);
        return new PerformanceDto(
            portfolioId, benchmark, calendar[0], calendar[^1], series,
            MetricDto.From(metrics.CumulativeReturn(returns)),
            MetricDto.From(returns.Count < 20 ? MetricValue.Unavailable("Fewer than 20 daily returns.") : metrics.AnnualizedReturn(returns)),
            MetricDto.From(metrics.AnnualizedVolatility(returns)),
            MetricDto.From(metrics.SharpeRatio(returns, portfolio.Settings.RiskFreeRate)),
            MetricDto.From(metrics.SortinoRatio(returns, portfolio.Settings.RiskFreeRate)),
            MetricDto.From(PerformanceMetrics.MaxDrawdown(wealth)),
            benchTotal is { } bt ? new MetricDto(bt, null) : new MetricDto(null, "Benchmark data unavailable."),
            monthly, PerformanceMethodology, source, simulated, warnings);
    }

    // ------------------------------------------------------------------ risk

    public async Task<RiskDto> GetRiskAsync(Guid portfolioId, int lookbackDays, double confidence, CancellationToken ct)
    {
        if (lookbackDays is < 20 or > 2520) throw new Common.ValidationException("lookbackDays", "Lookback must be between 20 and 2520 trading days.");
        if (confidence is <= 0.5 or >= 1) throw new Common.ValidationException("confidence", "Confidence must be between 0.5 and 1 (exclusive).");

        var holdings = await GetHoldingsAsync(portfolioId, ct);
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var app = await prices.SettingsAsync(ct);
        var warnings = new List<string>();
        var na = (string r) => new MetricDto(null, r);

        var positions = holdings.Holdings.Where(h => h.Quantity > 0 && h.Weight is > 0).ToList();
        if (positions.Count == 0)
        {
            var reason = "The portfolio has no priced holdings.";
            return new RiskDto(portfolioId, lookbackDays, null, null, 0, confidence, na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), [], [], [], RiskMethodology, [reason]);
        }

        var symbols = positions.Select(p => p.Symbol).ToList();
        var series = await prices.GetSeriesAsync(symbols, app.ActiveProvider, null, null, ct, lookbackDays + 1);
        var usable = symbols.Where(s => series.TryGetValue(s, out var x) && x.Points.Count >= 3).ToList();
        foreach (var s in symbols.Except(usable)) warnings.Add($"{s}: insufficient history; excluded from risk estimates.");
        if (usable.Count == 0)
        {
            var reason = "Insufficient price history for current holdings.";
            return new RiskDto(portfolioId, lookbackDays, null, null, 0, confidence, na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), [], [], [], RiskMethodology, [reason]);
        }

        var matrix = ReturnMatrix.Build(usable.ToDictionary(s => s, s => series[s].Points), usable);
        var w = usable.Select(s => positions.Single(p => p.Symbol == s).Weight!.Value).ToArray();
        var portReturns = new double[matrix.Observations];
        for (var t = 0; t < matrix.Observations; t++)
        for (var j = 0; j < w.Length; j++)
            portReturns[t] += w[j] * matrix.Returns[t, j];

        var metrics = new PerformanceMetrics(conventions);
        var est = matrix.Observations >= 2 ? CovarianceEstimator.Estimate(matrix.Returns, CovarianceMethod.Sample, conventions) : null;
        var contributions = est is null ? null : PortfolioMath.RiskContributions(w, est.Covariance);

        MetricValue beta, te, ir;
        var bench = portfolio.Settings.BenchmarkSymbol;
        var bseries = await prices.GetSeriesAsync([bench], app.ActiveProvider, matrix.PriceDates.FirstOrDefault(), matrix.PriceDates.LastOrDefault(), ct);
        if (bseries.TryGetValue(bench, out var b) && matrix.PriceDates.All(d => b.Points.Any(p => p.Date == d)))
        {
            var bmap = b.Points.ToDictionary(p => p.Date, p => p.Price);
            var br = ReturnCalculator.SimpleReturns(matrix.PriceDates.Select(d => bmap[d]).ToArray());
            beta = PerformanceMetrics.Beta(portReturns, br);
            te = metrics.TrackingError(portReturns, br);
            ir = metrics.InformationRatio(portReturns, br);
        }
        else
        {
            beta = te = ir = MetricValue.Unavailable($"Benchmark {bench} prices are missing for part of the window.");
        }

        return new RiskDto(
            portfolioId, lookbackDays, matrix.PriceDates.FirstOrDefault(), matrix.PriceDates.LastOrDefault(), matrix.Observations, confidence,
            MetricDto.From(metrics.AnnualizedVolatility(portReturns)),
            MetricDto.From(PerformanceMetrics.HistoricalVaR(portReturns, confidence)),
            MetricDto.From(PerformanceMetrics.HistoricalCVaR(portReturns, confidence)),
            MetricDto.From(beta), MetricDto.From(te), MetricDto.From(ir),
            MetricDto.From(metrics.SharpeRatio(portReturns, portfolio.Settings.RiskFreeRate)),
            MetricDto.From(PerformanceMetrics.MaxDrawdown(ReturnCalculator.WealthIndex(portReturns))),
            usable.Select((s, i) => new RiskContributionDto(s, w[i], contributions?[i], est?.Volatilities[i] ?? 0)).ToList(),
            usable,
            est is null ? [] : Enumerable.Range(0, usable.Count).Select(i => Enumerable.Range(0, usable.Count).Select(j => est.Correlation[i, j]).ToArray()).ToArray(),
            RiskMethodology,
            warnings);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Dictionary<int, double>> LatestTargetsAsync(Guid portfolioId, CancellationToken ct)
    {
        var proposal = await db.TradeProposals.AsNoTracking().Include(p => p.Lines)
            .Where(p => p.PortfolioId == portfolioId && p.Status == ProposalStatus.Executed)
            .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return proposal?.Lines.Where(l => l.TargetWeight > 0).ToDictionary(l => l.AssetId, l => l.TargetWeight) ?? [];
    }

    private async Task<Dictionary<int, (DateOnly LastDate, decimal Last, decimal? Prev)>> LastTwoClosesAsync(List<int> ids, string source, CancellationToken ct)
    {
        var result = new Dictionary<int, (DateOnly, decimal, decimal?)>();
        foreach (var id in ids)
        {
            var two = await db.HistoricalPrices.AsNoTracking().Where(p => p.AssetId == id && p.Source == source)
                .OrderByDescending(p => p.Date).Take(2).Select(p => new { p.Date, p.Close }).ToListAsync(ct);
            if (two.Count > 0) result[id] = (two[0].Date, two[0].Close, two.Count > 1 ? two[1].Close : null);
        }
        return result;
    }

    private static List<PeriodReturnDto> MonthlyReturns(List<DateOnly> dates, double[] wealth, List<double?> benchCum)
    {
        var result = new List<PeriodReturnDto>();
        var startIdx = 0;
        for (var t = 1; t <= dates.Count; t++)
        {
            if (t == dates.Count || dates[t].Month != dates[t - 1].Month)
            {
                var end = t - 1;
                var baseIdx = startIdx == 0 ? 0 : startIdx - 1;
                double? b = benchCum[end] is { } be && benchCum[baseIdx] is { } bb ? (1 + be) / (1 + bb) - 1 : null;
                result.Add(new PeriodReturnDto(dates[end].Year, dates[end].Month, wealth[end] / wealth[baseIdx] - 1, b));
                startIdx = t;
            }
        }
        return result;
    }
}
