using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Optimization;

public sealed class OptimizationService(
    IApplicationDbContext db,
    PortfolioService portfolios,
    PortfolioValuationService valuations,
    PriceQueryService prices,
    FxService fx,
    IPortfolioOptimizer optimizer,
    AnalyticsConventions conventions,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<OptimizationService> logger)
{
    public const int MinimumObservations = 60;

    /// <summary>
    /// Largest Math Optimization universe. Mean-variance weights of more stocks than this are not meaningful with a few
    /// years of daily returns (the covariance matrix has more parameters than observations), and the solve would exceed
    /// the request timeout.
    /// </summary>
    public const int MaxUniverse = 500;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string Methodology =
        "Expected returns: arithmetic mean of daily simple returns of adjusted closes × 252 (historical estimate, not a forecast). " +
        "Covariance: sample (n−1) or Ledoit–Wolf shrinkage towards a scaled identity, annualized × 252. " +
        "Returns are aligned on dates where every selected asset has a price (no forward-filling). " +
        "Optimization: long-only mean-variance QP solved with an ADMM (OSQP-style) solver with active-set polishing; maximum Sharpe via the " +
        "Cornuejols–Tütüncü homogenization; risk-constrained objective via bisection along the efficient frontier. " +
        "Max-holdings and minimum-position limits use a heuristic (remove smallest position, re-solve). Cash earns 0% in the optimizer. " +
        "Sharpe = (expected return − risk-free rate) / volatility. Historical estimates of expected returns are noisy; results are not guarantees.";

    public async Task<OptimizationRunDto> RunAsync(Guid portfolioId, RunOptimizationRequest request, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        portfolio.EnsureActive();
        var settings = request.Settings?.ToDomain() ?? portfolio.Settings;
        if (settings.Universe.Count > MaxUniverse)
            throw new ValidationException("universe",
                $"A Math Optimization can use at most {MaxUniverse} stocks; this universe has {settings.Universe.Count}. Narrow it by market, index or sector.");
        if (request.Settings is not null && request.PersistSettings)
            await portfolios.UpdateSettingsAsync(portfolioId, settings, ct);

        var (run, assets) = await EvaluateAsync(portfolio, portfolio.OwnerId, settings, request.FrontierPoints, ct);
        db.OptimizationRuns.Add(run);
        await db.SaveChangesAsync(ct);
        if (run.Status == OptimizationRunStatus.Succeeded)
            logger.LogInformation("Optimization run {RunId} for portfolio {PortfolioId}: {Objective} E[r]={Return:P2} σ={Vol:P2}",
                run.Id, portfolioId, run.Objective, run.ExpectedReturn, run.Volatility);
        else
            logger.LogInformation("Optimization run {RunId} ended with {Status}: {Message}", run.Id, run.Status, run.Message);
        return ToDto(run, assets);
    }

    /// <summary>
    /// Runs the optimization for <paramref name="settings"/> and returns the complete run record WITHOUT saving it
    /// (successful or not). <see cref="RunAsync"/> saves it; the research workflow keeps it as an unsaved hypothesis until the
    /// user explicitly saves. Without a portfolio, there are no current holdings (no turnover limit context) and
    /// <see cref="OptimizationRun.PortfolioId"/> stays empty until the run is saved to a portfolio.
    /// </summary>
    /// <param name="analysisCurrency">Currency of the analysis when there is no portfolio (the portfolio's base currency wins).</param>
    public async Task<(OptimizationRun Run, Dictionary<string, Asset> Assets)> EvaluateAsync(Portfolio? portfolio, Guid ownerId, PortfolioSettings settings,
        int frontierPoints, CancellationToken ct, string? analysisCurrency = null)
    {
        var sw = Stopwatch.StartNew();
        var app = await prices.SettingsAsync(ct);
        var source = app.ActiveProvider;
        var run = new OptimizationRun
        {
            Id = Guid.NewGuid(),
            PortfolioId = portfolio?.Id ?? Guid.Empty,
            OwnerId = ownerId,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            Objective = settings.Objective,
            PriceSource = source,
            SettingsSnapshot = settings,
        };

        var diagnostics = new List<string>();
        var excludedForData = new List<string>();
        var universe = settings.Universe.Except(settings.ExcludedSymbols, StringComparer.OrdinalIgnoreCase).ToList();
        run.Currency = portfolio?.BaseCurrency ?? analysisCurrency ?? app.DefaultCurrency;
        if (universe.Count > 0)
        {
            // Stocks of any currency are analysed in the portfolio's base currency; the benchmark is converted the same way.
            var currency = await UniverseCurrency.ResolveAsync(db, universe, settings.BenchmarkSymbol, portfolio?.BaseCurrency ?? analysisCurrency, ct);
            if (currency.Note is not null) diagnostics.Add(currency.Note);
            run.Currency = currency.Currency;
            settings = settings with { BenchmarkSymbol = currency.Benchmark };
            run.SettingsSnapshot = settings;
        }
        var assets = await db.Assets.AsNoTracking().Where(a => universe.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);

        var conversion = await fx.GetSeriesInCurrencyAsync(universe, source, null, null, run.Currency, ct, settings.LookbackDays + 1);
        var series = conversion.PriceSeries();
        run.FxSource = conversion.FxSource;
        diagnostics.AddRange(conversion.Notes);
        var usable = new List<string>();
        foreach (var symbol in universe)
        {
            if (conversion.Excluded.TryGetValue(symbol, out var why))
            {
                excludedForData.Add(symbol);
                diagnostics.Add($"{symbol}: excluded – {why}");
            }
            else if (!series.TryGetValue(symbol, out var s) || s.Points.Count == 0)
            {
                excludedForData.Add(symbol);
                diagnostics.Add($"{symbol}: no stored price data; excluded. Fetch its price history first.");
            }
            else if (s.Points.Count < MinimumObservations + 1)
            {
                excludedForData.Add(symbol);
                diagnostics.Add($"{symbol}: only {s.Points.Count} prices (minimum {MinimumObservations + 1}); excluded for insufficient history.");
            }
            else
            {
                usable.Add(symbol);
                if (!s.UsesAdjustedClose) diagnostics.Add($"{symbol}: adjusted closes unavailable; raw closes used (dividends ignored).");
            }
        }
        run.UsesSimulatedData = series.Values.Any(s => s.IsSimulated);

        if (usable.Count == 0)
            return (Fail(run, OptimizationRunStatus.InvalidInput, "No asset in the universe has sufficient price history. Synchronize market data first.", diagnostics, excludedForData, sw), assets);

        // One common window for all assets: recent listings or stale histories would shrink it for every other asset.
        var points = series.Where(kv => usable.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.Points);
        var (kept, outOfWindow) = CommonWindow.Choose(points, usable, Math.Max(MinimumObservations + 1, settings.LookbackDays / 2));
        foreach (var x in outOfWindow)
        {
            excludedForData.Add(x.Symbol);
            diagnostics.Add($"{x.Symbol}: {x.Reason}");
        }
        usable = [.. kept];
        var matrix = ReturnMatrix.Build(points, usable);
        run.Symbols = usable;
        run.PeriodStart = matrix.PriceDates.FirstOrDefault();
        run.PeriodEnd = matrix.PriceDates.LastOrDefault();
        run.Observations = matrix.Observations;
        run.DataSnapshotHash = Hash(series, usable, matrix.PriceDates);
        foreach (var (sym, dropped) in matrix.DroppedObservations.Where(d => d.Value > 0))
            diagnostics.Add($"{sym}: {dropped} observation(s) dropped to align dates with the other assets.");

        if (matrix.Observations < MinimumObservations)
            return (Fail(run, OptimizationRunStatus.InvalidInput,
                $"Only {matrix.Observations} aligned daily returns are available (minimum {MinimumObservations}). Check for assets with short or non-overlapping histories.",
                diagnostics, excludedForData, sw), assets);

        var est = CovarianceEstimator.Estimate(matrix.Returns, settings.CovarianceMethod.ToOptimization(), conventions);
        var sectors = assets.Values.Where(a => a.Sector is not null).ToDictionary(a => a.Symbol, a => a.Sector!, StringComparer.OrdinalIgnoreCase);
        var current = portfolio is null ? null : await CurrentWeightsAsync(portfolio, usable, diagnostics, ct);
        var input = new OptimizationInput(usable, est.ExpectedReturns, est.Covariance, settings.Objective.ToOptimization(),
            settings.ToConstraints(sectors), settings.RiskFreeRate, current);

        var result = optimizer.Optimize(input);
        diagnostics.AddRange(result.Diagnostics);
        run.SolverIterations = result.SolverIterations;
        run.SolverCalls = result.SolverCalls;

        var analytics = BuildAnalytics(input, est, matrix, series, settings, result, excludedForData,
            await BenchmarkSeriesAsync(settings.BenchmarkSymbol, source, matrix.PriceDates, run.Currency, diagnostics, ct), frontierPoints);
        run.AnalyticsJson = JsonSerializer.Serialize(analytics, Json);

        if (!result.Succeeded)
        {
            var status = result.Status switch
            {
                OptimizationStatus.Infeasible => OptimizationRunStatus.Infeasible,
                OptimizationStatus.InvalidInput => OptimizationRunStatus.InvalidInput,
                _ => OptimizationRunStatus.Failed,
            };
            return (Fail(run, status, result.Message, diagnostics, excludedForData, sw, keepAnalytics: true), assets);
        }

        // Independent check of the solver's answer against the user's hard constraints; a violation fails the run (nothing is relaxed).
        var validation = AllocationValidator.Validate(usable, result.Weights!, input.Constraints, result.Statistics!.ExpectedReturn, result.Statistics.Volatility, current);
        if (!validation.IsValid)
            return (Fail(run, OptimizationRunStatus.Failed, $"The solver's allocation failed independent validation: {string.Join(" ", validation.Violations)}",
                diagnostics, excludedForData, sw, keepAnalytics: true), assets);
        diagnostics.Add($"Independent validation passed ({string.Join(", ", validation.Checks)}).");

        run.Status = OptimizationRunStatus.Succeeded;
        run.Message = result.Message;
        run.ExpectedReturn = result.Statistics!.ExpectedReturn;
        run.Volatility = result.Statistics.Volatility;
        run.SharpeRatio = result.Statistics.SharpeRatio;
        run.Diagnostics = diagnostics;
        run.Weights = usable.Select((s, i) => new OptimizationRunWeight
        {
            RunId = run.Id,
            Symbol = s,
            AssetId = assets[s].Id,
            Weight = result.Weights![i],
            ExpectedReturn = est.ExpectedReturns[i],
            Volatility = est.Volatilities[i],
            RiskContribution = result.RiskContributions?[i],
        }).ToList();
        run.DurationMs = sw.Elapsed.TotalMilliseconds;
        return (run, assets);
    }

    /// <summary>The DTO of an unsaved run (research hypotheses use the same shape as saved runs).</summary>
    public async Task<OptimizationRunDto> ToDtoAsync(OptimizationRun run, CancellationToken ct)
    {
        var assets = await db.Assets.AsNoTracking().Where(a => run.Symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);
        return ToDto(run, assets);
    }

    public async Task<PagedResult<OptimizationRunSummaryDto>> ListAsync(Guid portfolioId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 200) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 200).");
        await portfolios.EnsureExistsAsync(portfolioId, ct);
        return await PageAsync(db.OptimizationRuns.AsNoTracking().Where(r => r.PortfolioId == portfolioId), page, pageSize, ct);
    }

    /// <summary>Optimization history of the current user across all portfolios, newest first.</summary>
    public async Task<PagedResult<OptimizationRunSummaryDto>> ListForUserAsync(Guid? portfolioId, OptimizationRunStatus? status, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 200) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 200).");
        var owner = currentUser.RequireUserId();
        var q = db.OptimizationRuns.AsNoTracking().Where(r => r.OwnerId == owner);
        if (portfolioId is { } pid) q = q.Where(r => r.PortfolioId == pid);
        if (status is { } st) q = q.Where(r => r.Status == st);
        return await PageAsync(q, page, pageSize, ct);
    }

    private static async Task<PagedResult<OptimizationRunSummaryDto>> PageAsync(IQueryable<OptimizationRun> q, int page, int pageSize, CancellationToken ct)
    {
        var total = await q.CountAsync(ct);
        var runs = await q.OrderByDescending(r => r.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new { r.Id, r.PortfolioId, r.CreatedAtUtc, r.Objective, r.Status, r.Message, r.ExpectedReturn, r.Volatility, r.SharpeRatio, r.Symbols, r.PeriodStart, r.PeriodEnd, r.PriceSource, r.UsesSimulatedData, r.Currency })
            .ToListAsync(ct);
        var items = runs.Select(r => new OptimizationRunSummaryDto(r.Id, r.PortfolioId, r.CreatedAtUtc, r.Objective, r.Status, r.Message,
            r.ExpectedReturn, r.Volatility, r.SharpeRatio, r.Symbols.Count, r.PeriodStart, r.PeriodEnd, r.PriceSource, r.UsesSimulatedData, r.Currency)).ToList();
        return new PagedResult<OptimizationRunSummaryDto>(items, page, pageSize, total);
    }

    public async Task<OptimizationRunDto> GetAsync(Guid runId, CancellationToken ct)
    {
        var run = await LoadRunAsync(runId, ct);
        var assets = await db.Assets.AsNoTracking().Where(a => run.Symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);
        return ToDto(run, assets);
    }

    public async Task<IReadOnlyList<AllocationDto>> GetAllocationAsync(Guid runId, CancellationToken ct) => (await GetAsync(runId, ct)).Allocation;

    /// <summary>
    /// Deletes one Math Optimization of an active portfolio of the current user (another user's run → 404, archived portfolio →
    /// business-rule error). Its weights go with it; trade proposals made from it stay but lose the link, backtests and comparisons keep
    /// their frozen copy of its settings, and holdings and transactions never change.
    /// </summary>
    public async Task DeleteAsync(Guid runId, CancellationToken ct)
    {
        var run = await LoadRunAsync(runId, ct);
        var portfolio = await portfolios.LoadAsync(run.PortfolioId, ct);
        portfolio.EnsureActive();
        await db.OptimizationRuns.Where(r => r.Id == runId && r.OwnerId == run.OwnerId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Loads one of the current user's runs; runs of other users are reported as not found.</summary>
    public async Task<OptimizationRun> LoadRunAsync(Guid runId, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        return await db.OptimizationRuns.AsNoTracking().Include(r => r.Weights).SingleOrDefaultAsync(r => r.Id == runId && r.OwnerId == owner, ct)
               ?? throw new NotFoundException("Optimization run", runId);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Current weights in the portfolio currency (foreign positions converted at the rate of their price date).</summary>
    private async Task<double[]?> CurrentWeightsAsync(Portfolio portfolio, IReadOnlyList<string> symbols, List<string> diagnostics, CancellationToken ct)
    {
        if (!portfolio.Holdings.Any(h => h.Quantity > 0)) return null;
        var valuation = await valuations.ValueAsync(portfolio, null, ct);
        if (valuation.TotalValue is not { } total || total <= 0)
        {
            diagnostics.AddRange(valuation.Problems);
            diagnostics.Add("Current holdings could not be valued, so turnover against them is not considered.");
            return null;
        }
        var values = valuation.Positions.Values.Where(v => v.Value is not null).ToDictionary(v => v.Symbol, v => v.Value!.Value, StringComparer.OrdinalIgnoreCase);
        return symbols.Select(s => values.TryGetValue(s, out var v) ? (double)(v / total) : 0d).ToArray();
    }

    private async Task<Dictionary<DateOnly, double>?> BenchmarkSeriesAsync(string symbol, string source, IReadOnlyList<DateOnly> dates, string currency,
        List<string> diagnostics, CancellationToken ct)
    {
        if (dates.Count == 0) return null;
        var c = await fx.GetSeriesInCurrencyAsync([symbol], source, dates[0], dates[^1], currency, ct);
        if (c.Excluded.TryGetValue(symbol, out var why))
        {
            diagnostics.Add($"Benchmark {symbol} not shown – {why}");
            return null;
        }
        if (!c.Series.TryGetValue(symbol, out var series) || series.Series.Points.Count == 0) return null;
        if (series.NativeCurrency != currency) diagnostics.Add($"Benchmark {symbol} ({series.NativeCurrency}) converted to {currency} like the stocks.");
        return series.Series.Points.ToDictionary(p => p.Date, p => p.Price);
    }

    private RunAnalyticsDto BuildAnalytics(
        OptimizationInput input, MomentEstimates est, ReturnMatrix matrix, Dictionary<string, PriceSeries> series,
        PortfolioSettings settings, OptimizationResult result, List<string> excludedForData,
        Dictionary<DateOnly, double>? benchmark, int frontierPoints)
    {
        var symbols = input.Symbols;
        var frontier = optimizer.EfficientFrontier(input with { Objective = OptimizationObjective.MinimumVariance }, frontierPoints)
            .Select(p => new FrontierPointDto(p.ExpectedReturn, p.Volatility, p.SharpeRatio,
                symbols.Select((s, i) => (s, w: p.Weights[i])).Where(x => x.w > 1e-6).ToDictionary(x => x.s, x => x.w)))
            .ToList();

        var corr = Enumerable.Range(0, symbols.Count).Select(i => Enumerable.Range(0, symbols.Count).Select(j => est.Correlation[i, j]).ToArray()).ToArray();
        var metrics = new PerformanceMetrics(conventions);
        var assetStats = symbols.Select((s, i) => new AssetStatisticsDto(
            s, est.ExpectedReturns[i], est.Volatilities[i],
            PortfolioMath.Sharpe(est.ExpectedReturns[i], est.Volatilities[i], settings.RiskFreeRate),
            matrix.DroppedObservations.GetValueOrDefault(s), series[s].UsesAdjustedClose)).ToList();

        var references = new List<ReferencePortfolioDto>();
        var eq = optimizer.Optimize(input with { Objective = OptimizationObjective.EqualWeight, Constraints = input.Constraints with { MaxWeight = 1, MaxHoldings = null, MinPositionWeight = null, SectorMaxWeights = new Dictionary<string, double>(), MaxTurnover = null } });
        if (eq.Succeeded) references.Add(new ReferencePortfolioDto("Equal weight", eq.Statistics!.ExpectedReturn, eq.Statistics.Volatility, eq.Statistics.SharpeRatio, "Same universe and cash reserve, 1/N weights."));

        double[]? benchReturns = null;
        if (benchmark is not null && matrix.PriceDates.All(benchmark.ContainsKey))
        {
            var bp = matrix.PriceDates.Select(d => benchmark[d]).ToArray();
            benchReturns = ReturnCalculator.SimpleReturns(bp);
            references.Add(new ReferencePortfolioDto($"Benchmark ({settings.BenchmarkSymbol})",
                metrics.AnnualizedMeanReturn(benchReturns).Value, metrics.AnnualizedVolatility(benchReturns).Value,
                metrics.SharpeRatio(benchReturns, settings.RiskFreeRate).Value, "Historical estimate over the same window."));
        }
        else
        {
            references.Add(new ReferencePortfolioDto($"Benchmark ({settings.BenchmarkSymbol})", null, null, null,
                "Benchmark prices are missing for part of the estimation window; synchronize the benchmark."));
        }

        InSampleSeriesDto? inSample = null;
        if (matrix.Observations > 0)
        {
            var curves = new Dictionary<string, IReadOnlyList<double>>();
            if (result.Weights is not null) curves["Optimized"] = Cumulative(matrix, result.Weights);
            if (eq.Weights is not null) curves["Equal weight"] = Cumulative(matrix, eq.Weights);
            if (benchReturns is not null) curves[$"Benchmark ({settings.BenchmarkSymbol})"] = Wealth(benchReturns);
            inSample = new InSampleSeriesDto(matrix.PriceDates, curves);
        }

        return new RunAnalyticsDto(frontier, symbols, corr, assetStats, references, inSample, est.ShrinkageIntensity, excludedForData);
    }

    /// <summary>Cumulative return of constant (daily rebalanced) weights over the estimation window; cash earns zero.</summary>
    private static IReadOnlyList<double> Cumulative(ReturnMatrix m, double[] w)
    {
        var r = new double[m.Observations];
        for (var t = 0; t < m.Observations; t++)
        for (var j = 0; j < w.Length; j++)
            r[t] += w[j] * m.Returns[t, j];
        return Wealth(r);
    }

    private static IReadOnlyList<double> Wealth(double[] returns) => ReturnCalculator.WealthIndex(returns).Select(v => v - 1).ToArray();

    /// <summary>Fingerprint of the exact price observations used (lets later readers detect re-downloaded or revised data).</summary>
    public static string Hash(Dictionary<string, PriceSeries> series, IReadOnlyList<string> symbols, IReadOnlyList<DateOnly> dates)
    {
        var sb = new StringBuilder();
        var dateSet = dates.ToHashSet();
        foreach (var s in symbols.OrderBy(x => x, StringComparer.Ordinal))
        foreach (var p in series[s].Points.Where(p => dateSet.Contains(p.Date)))
            sb.Append(s).Append('|').Append(p.Date.ToString("yyyy-MM-dd")).Append('|').Append(p.Price.ToString("R")).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    private static OptimizationRun Fail(OptimizationRun run, OptimizationRunStatus status, string message,
        List<string> diagnostics, List<string> excludedForData, Stopwatch sw, bool keepAnalytics = false)
    {
        run.Status = status;
        run.Message = message;
        run.Diagnostics = diagnostics;
        if (!keepAnalytics)
            run.AnalyticsJson = JsonSerializer.Serialize(new RunAnalyticsDto([], [], [], [], [], null, 0, excludedForData), Json);
        run.DurationMs = sw.Elapsed.TotalMilliseconds;
        return run;
    }

    private static OptimizationRunDto ToDto(OptimizationRun r, IReadOnlyDictionary<string, Asset> assets)
    {
        var analytics = JsonSerializer.Deserialize<RunAnalyticsDto>(r.AnalyticsJson, Json)
                        ?? new RunAnalyticsDto([], [], [], [], [], null, 0, []);
        var allocation = r.Weights.OrderByDescending(w => w.Weight).Select(w =>
        {
            assets.TryGetValue(w.Symbol, out var a);
            return new AllocationDto(w.Symbol, a?.Name, a?.Sector, w.Weight, w.ExpectedReturn, w.Volatility, w.RiskContribution, a?.Currency);
        }).ToList();
        var exposure = allocation.Where(x => x.Weight > 1e-9 && x.Currency is not null).GroupBy(x => x.Currency!)
            .Select(g => new CurrencyExposureDto(g.Key, g.Sum(x => x.Weight), g.Count())).OrderByDescending(e => e.Weight).ToList();
        return new OptimizationRunDto(r.Id, r.PortfolioId, r.CreatedAtUtc, r.Objective, r.Status, r.Message, r.Symbols,
            r.PeriodStart, r.PeriodEnd, r.Observations, r.PriceSource, r.UsesSimulatedData, r.DataSnapshotHash, r.SettingsSnapshot.ToDto(),
            r.ExpectedReturn, r.Volatility, r.SharpeRatio, r.SolverIterations, r.SolverCalls, r.DurationMs, r.Diagnostics,
            allocation, analytics, Methodology, r.Currency, r.FxSource, exposure);
    }
}
