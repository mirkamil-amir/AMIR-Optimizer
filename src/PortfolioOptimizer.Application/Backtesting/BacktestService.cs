using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization.Backtesting;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Backtesting;

public sealed class BacktestService(
    IApplicationDbContext db,
    PriceQueryService prices,
    Fx.FxService fx,
    BacktestEngine engine,
    MachineLearning.MlDataLoader mlLoader,
    Portfolios.PortfolioService portfolios,
    Comparisons.PortfolioStrategyService strategies,
    IBackgroundJobQueue queue,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<BacktestService> logger)
{
    public const string Methodology =
        "Signals are computed at the close of each rebalance date using only adjusted closes dated on or before that date; " +
        "orders fill at the next trading day's adjusted close net of proportional transaction costs; holdings earn returns from the fill date. " +
        "An asset becomes investable only once it has a full lookback window. Fractional units, cash earns 0%, dividends reinvested via adjusted prices. " +
        "The universe is today's list of symbols (not point-in-time), so results are subject to survivorship bias. " +
        "Backtested results are hypothetical, are not a forecast and do not guarantee future returns.";

    public const string MlMethodology =
        "ML strategy: at each rebalance the experiment's model family is retrained with its selected hyperparameters and features on labels " +
        "realized before the signal date (walk-forward, as in the experiment), scores the stocks and builds the portfolio with the experiment's " +
        "top-K, weighting and caps. Signals, execution, costs and valuation follow the same rules as every backtest.";

    private static readonly JsonSerializerOptions Json = OptimizationService.Json;

    /// <summary>A custom backtest (settings entered by the user). A strategy snapshot sent by the client is ignored.</summary>
    public async Task<BacktestDto> CreateAsync(CreateBacktestRequest request, CancellationToken ct)
    {
        if (request.PortfolioId is { } pid) await portfolios.EnsureExistsAsync(pid, ct);
        return await QueueAsync(request with { Strategy = null }, ct);
    }

    /// <summary>
    /// Backtest of a stored optimization of one of the caller's portfolios (another user's portfolio, run or experiment → 404),
    /// with its settings frozen now, in the portfolio's currency.
    /// </summary>
    public async Task<BacktestDto> CreateFromStrategyAsync(CreateStrategyBacktestRequest request, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(request.PortfolioId, ct);
        var snapshot = await strategies.SnapshotAsync(portfolio.Id, request.Strategy, ct);
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{(snapshot.Kind == OptimizationKind.MachineLearning ? "ML" : "Math")}: {snapshot.Label} · {request.StartDate:yyyy}-{request.EndDate:yyyy}"
            : request.Name.Trim();
        var m = snapshot.MathSettings;
        var ml = snapshot.MlConfiguration;
        var config = new CreateBacktestRequest
        {
            Name = name.Length > 100 ? name[..99] + "…" : name,
            Universe = [.. snapshot.Universe],
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            InitialCapital = request.InitialCapital,
            RebalanceFrequency = request.RebalanceFrequency,
            TransactionCostBps = request.TransactionCostBps,
            BenchmarkSymbol = snapshot.BenchmarkSymbol,
            Currency = portfolio.BaseCurrency,
            PortfolioId = portfolio.Id,
            Strategy = snapshot,
            // Mathematical: the run's own settings. ML: these fields are not used by the strategy (only for loading history).
            Objective = m?.Objective ?? ObjectiveKind.MaximumSharpe,
            CovarianceMethod = m?.CovarianceMethod ?? CovarianceKind.LedoitWolf,
            LookbackDays = Math.Min(m?.LookbackDays ?? 252, 2520),
            RiskFreeRate = m?.RiskFreeRate ?? ml?.RiskFreeRate ?? 0.04,
            MinWeight = m?.MinWeight ?? 0,
            MaxWeight = m?.MaxWeight ?? ml?.MaxWeight ?? 1,
            CashReserve = m?.CashReserve ?? ml?.CashReserve ?? 0,
            MaxHoldings = m?.MaxHoldings,
            TargetReturn = m?.TargetReturn,
            MaxVolatility = m?.MaxVolatility,
        };
        return await QueueAsync(config, ct);
    }

    private async Task<BacktestDto> QueueAsync(CreateBacktestRequest request, CancellationToken ct)
    {
        var universe = request.Universe.Select(Asset.NormalizeSymbol).Distinct().ToList();
        var known = await db.Assets.Where(a => universe.Contains(a.Symbol)).Select(a => a.Symbol).ToListAsync(ct);
        var unknown = universe.Except(known).ToList();
        if (unknown.Count > 0) throw new ValidationException("universe", $"Unknown asset(s): {string.Join(", ", unknown)}.");
        if (request.Strategy is null && request.MaxWeight * universe.Count < 1 - request.CashReserve - 1e-9)
            throw new ValidationException("maxWeight", $"With {universe.Count} assets, a maximum weight of {request.MaxWeight:P0} cannot reach full investment.");

        var currency = await UniverseCurrency.ResolveAsync(db, universe, request.BenchmarkSymbol, request.Currency, ct);
        var benchmark = string.IsNullOrWhiteSpace(request.BenchmarkSymbol) ? null : currency.Benchmark;
        var settings = await prices.SettingsAsync(ct);
        var run = new BacktestRun
        {
            Id = Guid.NewGuid(),
            OwnerId = currentUser.RequireUserId(),
            PortfolioId = request.PortfolioId,
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? $"{request.Objective} {request.RebalanceFrequency} {request.StartDate:yyyy}-{request.EndDate:yyyy}"
                : request.Name.Trim(),
            Status = JobStatus.Queued,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            ConfigJson = JsonSerializer.Serialize(request with { Universe = universe, BenchmarkSymbol = benchmark, Currency = currency.Currency }, Json),
            PriceSource = settings.ActiveProvider,
            Currency = currency.Currency,
        };
        db.Backtests.Add(run);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.Backtest, run.Id), ct);
        return ToDto(run);
    }

    public async Task<BacktestDto> GetAsync(Guid id, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var run = await db.Backtests.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id && b.OwnerId == owner, ct) ?? throw new NotFoundException("Backtest", id);
        return ToDto(run);
    }

    public async Task<PagedResult<BacktestSummaryDto>> ListAsync(int page, int pageSize, CancellationToken ct, Guid? portfolioId = null)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 100).");
        var owner = currentUser.RequireUserId();
        var mine = db.Backtests.AsNoTracking().Where(b => b.OwnerId == owner);
        if (portfolioId is { } pid) mine = mine.Where(b => b.PortfolioId == pid);
        var total = await mine.CountAsync(ct);
        var rows = await mine.OrderByDescending(b => b.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new { b.Id, b.Name, b.Status, b.CreatedAtUtc, b.CompletedAtUtc, b.PriceSource, b.UsesSimulatedData, b.Error, b.PortfolioId, b.ConfigJson })
            .ToListAsync(ct);
        var items = rows.Select(b =>
        {
            var strategy = JsonSerializer.Deserialize<CreateBacktestRequest>(b.ConfigJson, Json)?.Strategy;
            return new BacktestSummaryDto(b.Id, b.Name, b.Status, b.CreatedAtUtc, b.CompletedAtUtc, b.PriceSource, b.UsesSimulatedData, b.Error,
                b.PortfolioId, strategy?.Kind, strategy?.Label);
        }).ToList();
        return new PagedResult<BacktestSummaryDto>(items, page, pageSize, total);
    }

    /// <summary>Runs a queued backtest (called by the background worker).</summary>
    public async Task ExecuteAsync(Guid id, CancellationToken ct)
    {
        var run = await db.Backtests.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (run is null || run.Status != JobStatus.Queued) return;
        run.Status = JobStatus.Running;
        run.StartedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        try
        {
            var config = JsonSerializer.Deserialize<CreateBacktestRequest>(run.ConfigJson, Json)!;
            if (config.Strategy is { Kind: OptimizationKind.MachineLearning } mlStrategy)
            {
                run.ResultJson = JsonSerializer.Serialize(await RunMlAsync(run, config, mlStrategy, ct), Json);
                run.Status = JobStatus.Succeeded;
                run.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            // Load extra history before the start date for the first lookback window (≈1.5 calendar days per trading day).
            var loadFrom = config.StartDate.AddDays(-(int)Math.Ceiling(config.LookbackDays * 1.5) - 10);
            var conversion = await fx.GetSeriesInCurrencyAsync(config.Universe, run.PriceSource, loadFrom, config.EndDate, run.Currency, ct);
            var series = conversion.PriceSeries();
            run.FxSource = conversion.FxSource;
            var missing = config.Universe.Where(s => !series.TryGetValue(s, out var x) || x.Points.Count == 0).ToList();
            if (missing.Count == config.Universe.Count)
                throw new InvalidOperationException("No stored price data for the selected universe. Fetch price history first.");

            PriceSeries? bench = null;
            if (!string.IsNullOrWhiteSpace(config.BenchmarkSymbol))
            {
                var b = await fx.GetSeriesInCurrencyAsync([Asset.NormalizeSymbol(config.BenchmarkSymbol)], run.PriceSource, config.StartDate, config.EndDate, run.Currency, ct);
                bench = b.PriceSeries().Values.FirstOrDefault();
                conversion = conversion with { Notes = [.. conversion.Notes, .. b.Excluded.Select(e => $"Benchmark {e.Key} not shown – {e.Value}")] };
            }

            var input = new BacktestInput(
                series.Where(kv => kv.Value.Points.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Points),
                config.BenchmarkSymbol, bench?.Points);
            var settings = config.Strategy is { Kind: OptimizationKind.Mathematical, MathSettings: { } stored }
                ? await StoredMathSettingsAsync(stored, config, ct)
                : new BacktestSettings
            {
                Start = config.StartDate,
                End = config.EndDate,
                InitialCapital = config.InitialCapital,
                Frequency = config.RebalanceFrequency.ToOptimization(),
                Objective = config.Objective.ToOptimization(),
                CovarianceMethod = config.CovarianceMethod.ToOptimization(),
                LookbackDays = config.LookbackDays,
                MinimumObservations = Math.Min(60, config.LookbackDays),
                TransactionCostBps = config.TransactionCostBps,
                RiskFreeRate = config.RiskFreeRate,
                Constraints = new PortfolioConstraints
                {
                    MinWeight = config.MinWeight,
                    MaxWeight = config.MaxWeight,
                    CashReserve = config.CashReserve,
                    MaxHoldings = config.MaxHoldings,
                    TargetReturn = config.TargetReturn,
                    MaxVolatility = config.MaxVolatility,
                },
            };

            var result = await Task.Run(() => engine.Run(input, settings, ct), ct);
            var warnings = result.Warnings.ToList();
            if (config.Strategy is { } snapshot)
                warnings.Insert(0, $"Strategy of the optimization run {snapshot.Label}: re-optimized with its stored settings (objective, constraints, sector limits, lookback) at every rebalance.");
            warnings.InsertRange(0, missing.Select(m => conversion.Excluded.TryGetValue(m, out var why) ? $"{m}: excluded – {why}" : $"{m}: no price data; excluded from the backtest."));
            warnings.InsertRange(0, conversion.Notes);
            warnings.AddRange(series.Values.Where(s => !s.UsesAdjustedClose && s.Points.Count > 0)
                .Select(s => $"{s.Symbol}: adjusted closes unavailable; raw closes used (dividends ignored)."));

            var dto = new BacktestResultDto(
                result.Dates,
                result.Values,
                result.Drawdowns,
                result.Metrics.Select(m => new BacktestSeriesMetricsDto(m.Name, m.TotalReturn, MetricDto.From(m.AnnualizedReturn),
                    MetricDto.From(m.AnnualizedVolatility), MetricDto.From(m.SharpeRatio), MetricDto.From(m.SortinoRatio),
                    MetricDto.From(m.MaxDrawdown), m.TotalTurnover, m.TotalTransactionCosts)).ToList(),
                result.Rebalances.Select(e => new BacktestRebalanceDto(e.SignalDate, e.ExecutionDate, e.TargetWeights, e.EligibleSymbols,
                    e.Turnover, e.TransactionCosts, e.PortfolioValue, e.OptimizationSucceeded, e.Message)).ToList(),
                result.MonthlyReturns.Select(m => new PeriodReturnDto(m.Year, m.Month, m.Strategy, m.Benchmark)).ToList(),
                MetricDto.From(result.Beta), MetricDto.From(result.TrackingError), MetricDto.From(result.InformationRatio),
                warnings.Take(50).ToList(),
                Methodology);

            run.ResultJson = JsonSerializer.Serialize(dto, Json);
            run.UsesSimulatedData = series.Values.Any(s => s.IsSimulated);
            run.Status = JobStatus.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = JobStatus.Cancelled;
            run.Error = "Cancelled (application shutting down).";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            run.Status = JobStatus.Failed;
            run.Error = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backtest {BacktestId} failed", id);
            run.Status = JobStatus.Failed;
            run.Error = "Unexpected error while running the backtest. See server logs.";
        }

        run.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The stored run's settings (incl. sector limits) for the backtest period, capital, rebalancing and costs.</summary>
    private async Task<BacktestSettings> StoredMathSettingsAsync(PortfolioSettingsDto stored, CreateBacktestRequest config, CancellationToken ct)
    {
        var sectors = await db.Assets.AsNoTracking().Where(a => config.Universe.Contains(a.Symbol) && a.Sector != null)
            .ToDictionaryAsync(a => a.Symbol, a => a.Sector!, StringComparer.OrdinalIgnoreCase, ct);
        var settings = Comparisons.StrategyComparisonRunner.BuildMathSettings(stored, config.StartDate, config.EndDate, null);
        return settings with
        {
            InitialCapital = config.InitialCapital,
            Frequency = config.RebalanceFrequency.ToOptimization(),
            TransactionCostBps = config.TransactionCostBps,
            Constraints = stored.ToDomain().ToConstraints(sectors) with { MaxTurnover = null },
        };
    }

    /// <summary>
    /// Walk-forward ML backtest: the experiment's panel (prices converted into the backtest currency, the benchmark's calendar),
    /// history before the start for training, and the strategy deciding at the backtest's rebalance dates.
    /// </summary>
    private async Task<BacktestResultDto> RunMlAsync(BacktestRun run, CreateBacktestRequest config, StrategySnapshotDto snapshot, CancellationToken ct)
    {
        var ml = snapshot.MlConfiguration!;
        var data = await mlLoader.LoadAsync(config.Universe, config.BenchmarkSymbol ?? snapshot.BenchmarkSymbol, run.PriceSource,
            config.StartDate.AddYears(-ml.HistoryYears), config.EndDate, run.Currency, ct);
        run.FxSource = data.FxSource;
        run.UsesSimulatedData = data.UsesSimulatedData;
        var panel = data.Panel;
        var series = new Dictionary<string, IReadOnlyList<PricePoint>>();
        for (var a = 0; a < panel.AssetCount; a++)
            series[panel.Symbols[a]] = Enumerable.Range(0, panel.Length).Where(t => !double.IsNaN(panel.Prices[a][t]))
                .Select(t => new PricePoint(panel.Dates[t], panel.Prices[a][t])).ToList();
        var benchmark = Enumerable.Range(0, panel.Length).Select(t => new PricePoint(panel.Dates[t], panel.Benchmark[t])).ToList();
        var mlConfig = MachineLearning.MlExperimentRunner.ToConfiguration(ml) with { TransactionCostBps = config.TransactionCostBps };
        var (strategy, _) = Comparisons.MlStrategyFactory.Create(panel, mlConfig, new PortfolioOptimizer.MachineLearning.Models.Hyperparameters(snapshot.MlHyperparameters ?? new Dictionary<string, double>()),
            snapshot.MlFeatures ?? []);
        var settings = new BacktestSettings
        {
            Start = config.StartDate,
            End = config.EndDate,
            InitialCapital = config.InitialCapital,
            Frequency = config.RebalanceFrequency.ToOptimization(),
            TransactionCostBps = config.TransactionCostBps,
            RiskFreeRate = config.RiskFreeRate,
            Objective = OptimizationObjective.EqualWeight,
            LookbackDays = 252,
            MinimumObservations = 60,
        };
        var result = await Task.Run(() => engine.Run(new BacktestInput(series, panel.BenchmarkSymbol, benchmark), settings, ct, strategy.Decide), ct);
        var warnings = new List<string> { $"Strategy of the ML experiment {snapshot.Label}, retrained walk-forward with its selected hyperparameters." };
        if (snapshot.MlTestStart is { } testStart && config.StartDate < testStart)
            warnings.Add($"Partly in-sample: the experiment selected its hyperparameters on data up to {snapshot.MlValidationEnd:yyyy-MM-dd}; this backtest starts " +
                         $"{config.StartDate:yyyy-MM-dd}, before its out-of-sample test period ({testStart:yyyy-MM-dd}). Only results from {testStart:yyyy-MM-dd} are out-of-sample.");
        warnings.AddRange(data.Warnings);
        warnings.AddRange(result.Warnings);
        return new BacktestResultDto(
            result.Dates,
            result.Values,
            result.Drawdowns,
            result.Metrics.Select(m => new BacktestSeriesMetricsDto(m.Name, m.TotalReturn, MetricDto.From(m.AnnualizedReturn),
                MetricDto.From(m.AnnualizedVolatility), MetricDto.From(m.SharpeRatio), MetricDto.From(m.SortinoRatio),
                MetricDto.From(m.MaxDrawdown), m.TotalTurnover, m.TotalTransactionCosts)).ToList(),
            result.Rebalances.Select(e => new BacktestRebalanceDto(e.SignalDate, e.ExecutionDate, e.TargetWeights, e.EligibleSymbols,
                e.Turnover, e.TransactionCosts, e.PortfolioValue, e.OptimizationSucceeded, e.Message)).ToList(),
            result.MonthlyReturns.Select(m => new PeriodReturnDto(m.Year, m.Month, m.Strategy, m.Benchmark)).ToList(),
            MetricDto.From(result.Beta), MetricDto.From(result.TrackingError), MetricDto.From(result.InformationRatio),
            warnings.Distinct().Take(50).ToList(),
            $"{Methodology} {MlMethodology}");
    }

    private static BacktestDto ToDto(BacktestRun r) => new(
        r.Id, r.Name, r.Status, r.CreatedAtUtc, r.StartedAtUtc, r.CompletedAtUtc, r.PriceSource, r.UsesSimulatedData, r.Error,
        JsonSerializer.Deserialize<CreateBacktestRequest>(r.ConfigJson, Json)!,
        r.ResultJson is null ? null : JsonSerializer.Deserialize<BacktestResultDto>(r.ResultJson, Json));
}
