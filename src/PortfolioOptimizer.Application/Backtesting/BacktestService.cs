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
    BacktestEngine engine,
    IBackgroundJobQueue queue,
    TimeProvider clock,
    ILogger<BacktestService> logger)
{
    public const string Methodology =
        "Signals are computed at the close of each rebalance date using only adjusted closes dated on or before that date; " +
        "orders fill at the next trading day's adjusted close net of proportional transaction costs; holdings earn returns from the fill date. " +
        "An asset becomes investable only once it has a full lookback window. Fractional units, cash earns 0%, dividends reinvested via adjusted prices. " +
        "The universe is today's list of symbols (not point-in-time), so results are subject to survivorship bias. " +
        "Backtested results are hypothetical, are not a forecast and do not guarantee future returns.";

    private static readonly JsonSerializerOptions Json = OptimizationService.Json;

    public async Task<BacktestDto> CreateAsync(CreateBacktestRequest request, CancellationToken ct)
    {
        var universe = request.Universe.Select(Asset.NormalizeSymbol).Distinct().ToList();
        var known = await db.Assets.Where(a => universe.Contains(a.Symbol)).Select(a => a.Symbol).ToListAsync(ct);
        var unknown = universe.Except(known).ToList();
        if (unknown.Count > 0) throw new ValidationException("universe", $"Unknown asset(s): {string.Join(", ", unknown)}.");
        if (request.MaxWeight * universe.Count < 1 - request.CashReserve - 1e-9)
            throw new ValidationException("maxWeight", $"With {universe.Count} assets, a maximum weight of {request.MaxWeight:P0} cannot reach full investment.");

        var settings = await prices.SettingsAsync(ct);
        var run = new BacktestRun
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? $"{request.Objective} {request.RebalanceFrequency} {request.StartDate:yyyy}-{request.EndDate:yyyy}"
                : request.Name.Trim(),
            Status = JobStatus.Queued,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            ConfigJson = JsonSerializer.Serialize(request with { Universe = universe }, Json),
            PriceSource = settings.ActiveProvider,
        };
        db.Backtests.Add(run);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.Backtest, run.Id), ct);
        return ToDto(run);
    }

    public async Task<BacktestDto> GetAsync(Guid id, CancellationToken ct)
    {
        var run = await db.Backtests.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException("Backtest", id);
        return ToDto(run);
    }

    public async Task<PagedResult<BacktestSummaryDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 100).");
        var total = await db.Backtests.CountAsync(ct);
        var items = await db.Backtests.AsNoTracking().OrderByDescending(b => b.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new BacktestSummaryDto(b.Id, b.Name, b.Status, b.CreatedAtUtc, b.CompletedAtUtc, b.PriceSource, b.UsesSimulatedData, b.Error))
            .ToListAsync(ct);
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
            // Load extra history before the start date for the first lookback window (≈1.5 calendar days per trading day).
            var loadFrom = config.StartDate.AddDays(-(int)Math.Ceiling(config.LookbackDays * 1.5) - 10);
            var series = await prices.GetSeriesAsync(config.Universe, run.PriceSource, loadFrom, config.EndDate, ct);
            var missing = config.Universe.Where(s => !series.TryGetValue(s, out var x) || x.Points.Count == 0).ToList();
            if (missing.Count == config.Universe.Count)
                throw new InvalidOperationException($"No {run.PriceSource} price data for the selected universe. Synchronize market data first.");

            PriceSeries? bench = null;
            if (!string.IsNullOrWhiteSpace(config.BenchmarkSymbol))
            {
                var b = await prices.GetSeriesAsync([Asset.NormalizeSymbol(config.BenchmarkSymbol)], run.PriceSource, config.StartDate, config.EndDate, ct);
                bench = b.Values.FirstOrDefault();
            }

            var input = new BacktestInput(
                series.Where(kv => kv.Value.Points.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Points),
                config.BenchmarkSymbol, bench?.Points);
            var settings = new BacktestSettings
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
            warnings.InsertRange(0, missing.Select(m => $"{m}: no price data; excluded from the backtest."));
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

    private static BacktestDto ToDto(BacktestRun r) => new(
        r.Id, r.Name, r.Status, r.CreatedAtUtc, r.StartedAtUtc, r.CompletedAtUtc, r.PriceSource, r.UsesSimulatedData, r.Error,
        JsonSerializer.Deserialize<CreateBacktestRequest>(r.ConfigJson, Json)!,
        r.ResultJson is null ? null : JsonSerializer.Deserialize<BacktestResultDto>(r.ResultJson, Json));
}
