using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Fx;
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
    PortfolioValuationService valuations,
    PriceQueryService prices,
    FxService fx,
    AnalyticsConventions conventions)
{
    public const string PerformanceMethodology =
        "Daily portfolio value = cash + Σ quantity × raw close × FX rate of that day (local currency → account currency; the same price " +
        "convention used for simulated fills). For another reporting currency the daily value and flows are converted with that day's rate. " +
        "Trades are dated at the close of the price used for execution. Returns are time-weighted: deposits and withdrawals are " +
        "external cash flows and are removed, so they are never counted as gains or losses. Dividends you record and standalone fees " +
        "are part of the return; dividends are not credited automatically. A booking on a non-trading day takes effect on the next " +
        "trading day. Benchmark returns use adjusted closes (total return).";

    public const string RiskMethodology =
        "Ex-ante risk of current weights (market value / total value, cash at zero risk) applied to the trailing window of daily " +
        "adjusted-close returns (historical simulation). VaR/CVaR are one-day historical estimates at the stated confidence level.";

    // ------------------------------------------------------------------ holdings

    /// <param name="currency">Reporting currency (default: the portfolio's account currency). Reporting never changes holdings.</param>
    public async Task<HoldingsResponse> GetHoldingsAsync(Guid portfolioId, CancellationToken ct, string? currency = null)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var app = await prices.SettingsAsync(ct);
        var source = app.ActiveProvider;
        var account = portfolio.BaseCurrency;

        var holdings = portfolio.Holdings.Where(h => h.Quantity > 0 || h.RealizedPnl != 0).ToList();
        var ids = holdings.Select(h => h.AssetId).ToList();
        var targets = await LatestTargetsAsync(portfolioId, ct);
        var valuation = await valuations.ValueAsync(portfolio, currency, ct, ids);
        var accountValuation = valuation.IsReportingConversion ? await valuations.ValueAsync(portfolio, null, ct, ids) : valuation;
        var ccy = valuation.Currency;
        var warnings = new List<string>(valuation.Warnings);
        warnings.AddRange(valuation.Problems);

        // Amounts booked in the account currency (cost basis, realized P&L, fees, contributions) at today's reporting rate.
        decimal? accountRate = valuation.IsReportingConversion ? valuation.CashFx is { } cf ? (decimal)cf.Rate : null : 1m;
        decimal? Rep(decimal accountAmount) => accountRate is { } r ? accountAmount * r : null;
        if (valuation.IsReportingConversion)
            warnings.Add(accountRate is null
                ? $"No {account}→{ccy} rate: amounts booked in {account} (cost, realized P&L, fees) cannot be shown in {ccy}."
                : $"Reporting view in {ccy}: positions are converted at the rate of their price date; cost basis, realized P&L, fees and contributions " +
                  $"(booked in {account}) at today's {account}→{ccy} rate {accountRate:0.######}. Returns are measured in {account}. Nothing was exchanged.");

        var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var lastTwo = await LastTwoClosesAsync(ids, source, ct);
        var executions = await db.Executions.AsNoTracking().Where(e => e.PortfolioId == portfolioId).ToListAsync(ct);
        var dailyFx = await DailyFxAsync(assets.Values.Select(a => a.Currency).Append(ccy), lastTwo.Values.Select(v => v.LastDate), valuation.UsesSimulatedData, ct);

        var marketValue = valuation.MarketValue;
        var totalValue = valuation.TotalValue;
        var rows = holdings.OrderByDescending(h => valuation.Positions.GetValueOrDefault(h.AssetId)?.Value ?? 0).Select(h =>
        {
            assets.TryGetValue(h.AssetId, out var a);
            var pos = valuation.Positions.GetValueOrDefault(h.AssetId);
            var lp = pos?.Price;
            var mv = h.Quantity > 0 ? pos?.Value : 0m;
            double? weight = mv is { } v && totalValue is > 0 ? (double)(v / totalValue.Value) : null;
            var target = targets.TryGetValue(h.AssetId, out var t) ? t : (double?)null;
            var costBasis = Rep(h.CostBasis);
            // Daily change only applies to shares held over the last close-to-close interval (not shares bought at the last close),
            // valued at each day's exchange rate (so it includes the currency move).
            decimal? daily = null;
            if (a is not null && lastTwo.TryGetValue(h.AssetId, out var two) && two.Prev is { } prev && two.PrevDate is { } prevDate
                && dailyFx is not null && dailyFx.TryRate(a.Currency, ccy, two.LastDate, out var qLast, out _) && dailyFx.TryRate(a.Currency, ccy, prevDate, out var qPrev, out _))
            {
                var boughtSince = executions.Where(e => e.AssetId == h.AssetId && e.PriceDate >= two.LastDate)
                    .Sum(e => e.Side == OrderSide.Buy ? e.Quantity : -e.Quantity);
                daily = (h.Quantity - boughtSince) * (two.Last * (decimal)qLast!.Rate - prev * (decimal)qPrev!.Rate);
            }
            if (lp?.IsStale == true) warnings.Add($"{h.Symbol}: price from {lp.PriceDate:yyyy-MM-dd} is stale.");
            return new HoldingDto(h.AssetId, h.Symbol, a?.Name, a?.Sector, h.Quantity, Rep(h.AverageCost) ?? 0, costBasis ?? 0,
                lp?.Price, lp?.PriceDate, lp?.IsStale ?? false, mv, weight,
                mv is { } m && costBasis is { } cb ? m - cb : null,
                mv is { } m2 && costBasis is > 0 ? (double)(m2 / costBasis.Value - 1) : null,
                Rep(h.RealizedPnl) ?? 0, target, weight is { } w && target is { } tw ? w - tw : null, daily, h.UpdatedAtUtc,
                a?.Currency ?? ccy, pos?.Fx is { } fq ? (decimal)fq.Rate : null, pos?.Fx?.ObservationDate, h.Quantity > 0 ? pos?.Problem : null);
        }).ToList();

        // Targets for assets not (or no longer) held.
        foreach (var (assetId, w) in targets.Where(t => holdings.All(h => h.AssetId != t.Key || h.Quantity == 0)))
        {
            if (rows.Any(r => r.AssetId == assetId)) continue;
            var a = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == assetId, ct);
            rows.Add(new HoldingDto(assetId, a.Symbol, a.Name, a.Sector, 0, 0, 0, null, null, false, 0, 0, null, null, 0, w, -w, null, portfolio.UpdatedAtUtc, a.Currency));
        }

        var deposits = await db.CashLedger.AsNoTracking()
            .Where(c => c.PortfolioId == portfolioId && (c.Type == CashEntryType.Deposit || c.Type == CashEntryType.Withdrawal))
            .Select(c => c.Amount).ToListAsync(ct);
        var netContrib = deposits.Sum();
        var dailyChange = rows.Any(r => r.DailyChange is not null) ? rows.Sum(r => r.DailyChange ?? 0) : (decimal?)null;

        // Returns are measured in the account currency (where the money was contributed), benchmark converted into it.
        double? benchReturn = null;
        var firstTrade = executions.OrderBy(e => e.PriceDate).FirstOrDefault();
        var bench = portfolio.Settings.BenchmarkSymbol;
        if (firstTrade is not null)
        {
            var b = await fx.GetSeriesInCurrencyAsync([bench], source, firstTrade.PriceDate, null, account, ct);
            if (b.Series.TryGetValue(bench, out var bs) && bs.Series.Points.Count >= 1 && bs.Series.Points[0].Date == firstTrade.PriceDate)
                benchReturn = bs.Series.Points[^1].Price / bs.Series.Points[0].Price - 1;
            else warnings.Add($"Benchmark {bench} has no price{(b.Excluded.TryGetValue(bench, out var why) ? $" in {account} ({why})" : "")} on the first trade date; relative return unavailable.");
        }

        double? totalReturn = accountValuation.TotalValue is { } accountTotal && netContrib > 0 ? (double)(accountTotal / netContrib - 1) : netContrib > 0 ? null : 0;
        var exposure = totalValue is > 0
            ? valuation.Positions.Values.Where(p => p.Quantity > 0 && p.Value is not null).GroupBy(p => p.Currency)
                .Select(g => new CurrencyExposureDto(g.Key, (double)(g.Sum(p => p.Value!.Value) / totalValue.Value), g.Count()))
                .Append(new CurrencyExposureDto($"{account} cash", (double)(valuation.Cash / totalValue.Value), 0))
                .Where(e => e.Weight > 0).OrderByDescending(e => e.Weight).ToList()
            : null;
        var summary = new PortfolioValuationDto(
            marketValue, valuation.Cash, totalValue,
            totalValue is > 0 && marketValue is { } mvAll ? (double)(mvAll / totalValue.Value) : 0,
            Rep(holdings.Sum(h => h.CostBasis)) ?? 0,
            rows.Sum(r => r.UnrealizedPnl ?? 0),
            Rep(holdings.Sum(h => h.RealizedPnl)) ?? 0,
            Rep(executions.Sum(e => e.Fees)) ?? 0,
            Rep(netContrib) ?? 0,
            dailyChange,
            dailyChange is { } dc && totalValue is { } tv && tv - dc > 0 ? (double)(dc / (tv - dc)) : null,
            totalReturn ?? 0,
            benchReturn,
            benchReturn is { } br && totalReturn is { } tr ? tr - br : null,
            bench,
            valuation.Positions.Values.Where(p => p.Price is not null).Select(p => (DateOnly?)p.Price!.PriceDate).DefaultIfEmpty().Max(),
            source,
            valuation.UsesSimulatedData && valuation.Positions.Values.Any(p => p.Price?.IsSimulated == true),
            warnings.Distinct().ToList(),
            ccy, account, accountRate, valuation.FxSource, exposure, valuation.Problems);
        return new HoldingsResponse(summary, rows);
    }

    /// <summary>Rates for the last two price dates of the holdings (daily change in the valuation currency).</summary>
    private async Task<FxTable?> DailyFxAsync(IEnumerable<string> currencies, IEnumerable<DateOnly> lastDates, bool simulated, CancellationToken ct)
    {
        var list = currencies.Distinct().ToList();
        var dates = lastDates.ToList();
        if (list.Count < 2 || dates.Count == 0) return null;
        try { return await fx.GetTableAsync(list, dates.Min().AddDays(-10), dates.Max(), simulated, ct); }
        catch (Common.ValidationException) { return null; }
    }

    // ------------------------------------------------------------------ performance

    public async Task<PerformanceDto> GetPerformanceAsync(Guid portfolioId, DateOnly? from, DateOnly? to, CancellationToken ct, string? currency = null)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var app = await prices.SettingsAsync(ct);
        var source = app.ActiveProvider;
        var account = portfolio.BaseCurrency;
        var ccy = currency is null ? account : Currencies.Normalize(currency);
        var benchmark = portfolio.Settings.BenchmarkSymbol;
        var warnings = new List<string>();
        var metrics = new PerformanceMetrics(conventions);

        var executions = await db.Executions.AsNoTracking().Where(e => e.PortfolioId == portfolioId).OrderBy(e => e.ExecutedAtUtc).ToListAsync(ct);
        // Cash entries not belonging to a trade: deposits and withdrawals (external flows), dividends and standalone fees (part of
        // the return). A trade's settlement and commission are already in its execution's net cash flow.
        var executionIds = executions.Select(e => e.Id).ToHashSet();
        var standalone = (await db.CashLedger.AsNoTracking()
                .Where(c => c.PortfolioId == portfolioId && c.Type != CashEntryType.BuySettlement && c.Type != CashEntryType.SellSettlement)
                .ToListAsync(ct))
            .Where(c => c.Type != CashEntryType.Fee || c.ReferenceId is not { } r || !executionIds.Contains(r))
            .OrderBy(c => c.TimestampUtc).ToList();
        static bool IsExternal(CashLedgerEntry c) => c.Type is CashEntryType.Deposit or CashEntryType.Withdrawal;

        PerformanceDto Empty(string reason)
        {
            var na = new MetricDto(null, reason);
            return new PerformanceDto(portfolioId, benchmark, null, null, [], na, na, na, na, na, na, na, [], PerformanceMethodology, source, false, [reason], ccy);
        }

        if (executions.Count == 0) return Empty("No simulated trades yet; performance history starts with the first execution.");

        var start = executions.Min(e => e.PriceDate);
        var assetIds = executions.Select(e => e.AssetId).Distinct().ToList();
        var closes = await prices.GetRawClosesAsync(assetIds, source, start, ct);
        var assetCurrency = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Currency, ct);
        var simulated = (await prices.GetLatestPricesAsync(assetIds, source, app.StalePriceDays, ct)).Values.Any(l => l.IsSimulated);
        var calendar = closes.Values.SelectMany(c => c.Keys).Where(d => d >= start).Distinct().OrderBy(d => d).ToList();
        FxTable? rates = null;
        var needed = assetCurrency.Values.Append(account).Append(ccy).Distinct().ToList();
        if (needed.Count > 1 && calendar.Count > 0)
        {
            try { rates = await fx.GetTableAsync(needed, start, calendar[^1], simulated, ct); }
            catch (Common.ValidationException ex) { return Empty(ex.Message); }
        }
        if (from is { } f) calendar = calendar.Where(d => d >= f).ToList();
        if (to is { } t) calendar = calendar.Where(d => d <= t).ToList();
        if (calendar.Count == 0) return Empty("No price data in the selected range.");

        // Cash booked before the first trade forms the starting capital; later deposits and withdrawals are external flows.
        var firstExecution = executions.Min(e => e.ExecutedAtUtc);
        var initialCash = standalone.Where(c => c.TimestampUtc <= firstExecution).Sum(c => c.Amount);
        var values = new List<double>();
        var flows = new List<double>();
        var cashSeries = new List<double>();
        var execIdx = 0;
        var qty = new Dictionary<int, decimal>();
        var cash = initialCash;
        var ordered = executions.OrderBy(e => e.PriceDate).ThenBy(e => e.ExecutedAtUtc).ToList();
        var lastPx = new Dictionary<int, decimal>();
        var cashAfter = standalone.Where(c => c.TimestampUtc > firstExecution).ToList();
        var cashIdx = 0;

        // Replay all executions dated before the visible window so the first visible value is correct.
        foreach (var d in closes.Values.SelectMany(c => c.Keys).Where(d => d >= start && d < calendar[0]).Distinct().OrderBy(d => d))
            Apply(d);

        // A day on which a held position or the reporting conversion has no rate within the carry-forward window is left out
        // (its external flow moves to the next valued day) – it is never valued with a guessed rate.
        var valued = new List<DateOnly>();
        var skipped = 0;
        var pendingFlow = 0m;
        foreach (var d in calendar)
        {
            var flow = Apply(d) + pendingFlow;
            decimal? mv = 0;
            foreach (var (id, q) in qty.Where(kv => kv.Value != 0))
            {
                if (!lastPx.TryGetValue(id, out var px)) continue;
                var c = assetCurrency.GetValueOrDefault(id, account);
                if (c == account) mv += q * px;
                else if (rates is not null && rates.TryRate(c, account, d, out var fq, out _)) mv += q * px * (decimal)fq!.Rate;
                else { mv = null; break; }
            }
            var toReporting = 1m;
            if (ccy != account)
            {
                if (rates is not null && rates.TryRate(account, ccy, d, out var rq, out _)) toReporting = (decimal)rq!.Rate;
                else mv = null;
            }
            if (mv is null)
            {
                skipped++;
                pendingFlow = flow;
                continue;
            }
            pendingFlow = 0;
            valued.Add(d);
            values.Add((double)((cash + mv.Value) * toReporting));
            flows.Add((double)(flow * toReporting));
            cashSeries.Add((double)(cash * toReporting));
        }
        if (skipped > 0)
            warnings.Add($"{skipped} day(s) left out: no exchange rate within {FxTable.DefaultMaxCarryForwardDays} days for a held position or for {account}→{ccy}.");
        calendar = valued;
        if (calendar.Count == 0) return Empty("No day in the range could be valued: exchange rates are missing.");
        if (assetCurrency.Values.Any(c => c != ccy))
            warnings.Insert(0, $"Values in {ccy}: positions in other currencies are converted with the reference rate of each day, so returns include currency movements.");

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
            // Every entry up to this day is applied – also one booked on a weekend or holiday (it takes effect on the next valued day).
            var flow = 0m;
            while (cashIdx < cashAfter.Count && DateOnly.FromDateTime(cashAfter[cashIdx].TimestampUtc) <= d)
            {
                var c = cashAfter[cashIdx++];
                cash += c.Amount;
                if (IsExternal(c)) flow += c.Amount;
            }
            return flow;
        }

        // Time-weighted daily returns: r_t = (V_t − F_t) / V_{t−1} − 1.
        var returns = new List<double>();
        for (var i = 1; i < values.Count; i++)
            returns.Add(values[i - 1] > 0 ? (values[i] - flows[i]) / values[i - 1] - 1 : 0);
        var wealth = ReturnCalculator.WealthIndex(returns);
        var drawdowns = PerformanceMetrics.DrawdownSeries(wealth);

        var benchConversion = await fx.GetSeriesInCurrencyAsync([benchmark], source, calendar[0], calendar[^1], ccy, ct);
        Dictionary<DateOnly, double>? bmap = null;
        if (benchConversion.Series.TryGetValue(benchmark, out var bc) && bc.Series.Points is { Count: > 0 } bp0 && bp0[0].Date == calendar[0])
            bmap = bp0.ToDictionary(p => p.Date, p => p.Price);
        else warnings.Add($"Benchmark {benchmark} has no {ccy} price on {calendar[0]:yyyy-MM-dd}; benchmark comparison unavailable.");

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
            series.Add(new PerformancePointDto(calendar[i], values[i], wealth[i] - 1, bcum, drawdowns[i], cashSeries[i]));
        }

        if (returns.Count < 20)
            warnings.Add($"Only {returns.Count} daily return(s) since the first trade; annualized statistics are not meaningful yet.");

        var monthly = MonthlyReturns(calendar, wealth, series.Select(s => s.BenchmarkCumulativeReturn).ToList());
        var benchTotal = series[^1].BenchmarkCumulativeReturn;
        return new PerformanceDto(
            portfolioId, benchmark, calendar[0], calendar[^1], series,
            MetricDto.From(metrics.CumulativeReturn(returns)),
            MetricDto.From(returns.Count < 20 ? MetricValue.Unavailable("Fewer than 20 daily returns.") : metrics.AnnualizedReturn(returns)),
            MetricDto.From(metrics.AnnualizedVolatility(returns)),
            MetricDto.From(metrics.SharpeRatio(returns, portfolio.Settings.RiskFreeRate)),
            MetricDto.From(metrics.SortinoRatio(returns, portfolio.Settings.RiskFreeRate)),
            MetricDto.From(PerformanceMetrics.MaxDrawdown(wealth)),
            benchTotal is { } bt ? new MetricDto(bt, null) : new MetricDto(null, "Benchmark data unavailable."),
            monthly, PerformanceMethodology, source, simulated, warnings, ccy);
    }

    // ------------------------------------------------------------------ risk

    public async Task<RiskDto> GetRiskAsync(Guid portfolioId, int lookbackDays, double confidence, CancellationToken ct)
    {
        if (lookbackDays is < 20 or > 2520) throw new Common.ValidationException("lookbackDays", "Lookback must be between 20 and 2520 trading days.");
        if (confidence is <= 0.5 or >= 1) throw new Common.ValidationException("confidence", "Confidence must be between 0.5 and 1 (exclusive).");

        var holdings = await GetHoldingsAsync(portfolioId, ct);
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var account = portfolio.BaseCurrency;
        var app = await prices.SettingsAsync(ct);
        var warnings = new List<string>();
        var na = (string r) => new MetricDto(null, r);

        var positions = holdings.Holdings.Where(h => h.Quantity > 0 && h.Weight is > 0).ToList();
        if (positions.Count == 0)
        {
            var reason = holdings.Summary.TotalValue is null
                ? $"The holdings cannot be valued in {account}: {string.Join(" ", holdings.Summary.ValuationProblems ?? [])}"
                : "The portfolio has no priced holdings.";
            return new RiskDto(portfolioId, lookbackDays, null, null, 0, confidence, na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), [], [], [], RiskMethodology, [reason], account);
        }

        var symbols = positions.Select(p => p.Symbol).ToList();
        var conversion = await fx.GetSeriesInCurrencyAsync(symbols, app.ActiveProvider, null, null, account, ct, lookbackDays + 1);
        var series = conversion.PriceSeries();
        warnings.AddRange(conversion.Notes);
        foreach (var (s, why) in conversion.Excluded) warnings.Add($"{s}: excluded – {why}");
        var usable = symbols.Where(s => series.TryGetValue(s, out var x) && x.Points.Count >= 3).ToList();
        foreach (var s in symbols.Except(usable)) warnings.Add($"{s}: insufficient history; excluded from risk estimates.");
        if (usable.Count == 0)
        {
            var reason = "Insufficient price history for current holdings.";
            return new RiskDto(portfolioId, lookbackDays, null, null, 0, confidence, na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), na(reason), [], [], [], RiskMethodology, [reason], account);
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
        var bseries = (await fx.GetSeriesInCurrencyAsync([bench], app.ActiveProvider, matrix.PriceDates.FirstOrDefault(), matrix.PriceDates.LastOrDefault(), account, ct)).PriceSeries();
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
            warnings,
            account);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Dictionary<int, double>> LatestTargetsAsync(Guid portfolioId, CancellationToken ct)
    {
        var proposal = await db.TradeProposals.AsNoTracking().Include(p => p.Lines)
            .Where(p => p.PortfolioId == portfolioId && p.Status == ProposalStatus.Executed)
            .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return proposal?.Lines.Where(l => l.TargetWeight > 0).ToDictionary(l => l.AssetId, l => l.TargetWeight) ?? [];
    }

    private async Task<Dictionary<int, (DateOnly LastDate, decimal Last, decimal? Prev, DateOnly? PrevDate)>> LastTwoClosesAsync(List<int> ids, string source, CancellationToken ct)
    {
        var result = new Dictionary<int, (DateOnly, decimal, decimal?, DateOnly?)>();
        // Last two closes of every asset in one statement (index lookup per asset), not one query per asset.
        var rows = (await db.LatestPricesPerAsset(ids, source, 2, DateOnly.MinValue, DateOnly.MaxValue)
                .Select(p => new { p.AssetId, p.Date, p.Close }).ToListAsync(ct))
            .ToLookup(r => r.AssetId);
        foreach (var id in ids)
        {
            var two = rows[id].OrderByDescending(r => r.Date).ToList();
            if (two.Count > 0) result[id] = (two[0].Date, two[0].Close, two.Count > 1 ? two[1].Close : null, two.Count > 1 ? two[1].Date : null);
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
