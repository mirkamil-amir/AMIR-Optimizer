using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MachineLearning;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.MachineLearning.Features;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.Optimization.Backtesting;

namespace PortfolioOptimizer.Application.Comparisons;

/// <summary>Saved strategy comparisons of the current user: submit (queued), list, inspect, delete.</summary>
public sealed class StrategyComparisonService(
    IApplicationDbContext db,
    PriceQueryService prices,
    MarketDataService marketData,
    Portfolios.PortfolioService portfolios,
    PortfolioStrategyService strategies,
    IBackgroundJobQueue queue,
    ICurrentUser currentUser,
    IOptions<MachineLearningOptions> options,
    TimeProvider clock)
{
    public const string Disclaimer =
        "Hypothetical historical simulation on stored data. Differences between strategies over one period can be due to chance; " +
        "past performance does not indicate future results. Not investment advice.";

    public async Task<StrategyComparisonDto> CreateAsync(CreateStrategyComparisonRequest request, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        // Both optimizations must belong to the same portfolio of the caller (another user's portfolio or optimization → 404).
        var portfolio = await portfolios.LoadAsync(request.PortfolioId, ct);
        var a = await strategies.SnapshotAsync(portfolio.Id, request.StrategyA, ct);
        var b = await strategies.SnapshotAsync(portfolio.Id, request.StrategyB, ct);

        var benchmark = Asset.NormalizeSymbol(request.BenchmarkSymbol);
        if (!await db.Assets.AnyAsync(a => a.Symbol == benchmark, ct)) throw new ValidationException("benchmarkSymbol", $"Unknown benchmark {benchmark}.");
        // Every strategy and the benchmark are evaluated in the portfolio's currency (prices converted with dated FX rates).
        var currency = await UniverseCurrency.ResolveAsync(db, a.Universe.Union(b.Universe).ToList(), benchmark, portfolio.BaseCurrency, ct);
        benchmark = currency.Benchmark;
        var active = await db.StrategyComparisons.CountAsync(c => c.OwnerId == owner && (c.Status == JobStatus.Queued || c.Status == JobStatus.Running), ct);
        if (active >= options.Value.MaxActiveExperimentsPerUser)
            throw new ConflictException($"You already have {active} comparison(s) queued or running. Wait for them to finish.");

        var snapshots = new[] { a, b };
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        // Default: out-of-sample for every ML strategy (from the latest test-period start); without ML, three years.
        var start = request.StartDate ?? snapshots.Select(s => s.MlTestStart).Where(d => d is not null).Max() ?? today.AddYears(-3);
        var end = request.EndDate ?? today;
        if (end > today) end = today;
        if (end <= start.AddMonths(3)) throw new ValidationException("endDate", "The evaluation period must be longer than three months.");
        var riskFree = request.RiskFreeRate ?? snapshots.Select(s => s.MathSettings?.RiskFreeRate ?? s.MlConfiguration?.RiskFreeRate).FirstOrDefault(r => r is not null) ?? 0.04;

        var settings = await prices.SettingsAsync(ct);
        var config = new ComparisonConfigurationDto(
            request with { StartDate = start, EndDate = end, BenchmarkSymbol = benchmark, RiskFreeRate = riskFree }, snapshots);
        var name = string.IsNullOrWhiteSpace(request.Name) ? $"{a.Label} vs {b.Label}" : request.Name.Trim();
        var comparison = new StrategyComparison
        {
            Id = Guid.NewGuid(),
            OwnerId = owner,
            PortfolioId = portfolio.Id,
            Name = name.Length > 100 ? name[..99] + "…" : name,
            Status = JobStatus.Queued,
            ProgressMessage = "Queued",
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            // References kept for navigation (set to null if deleted); the frozen settings are in the configuration.
            OptimizationRunId = snapshots.FirstOrDefault(s => s.Kind == OptimizationKind.Mathematical)?.SourceId,
            MlExperimentId = snapshots.FirstOrDefault(s => s.Kind == OptimizationKind.MachineLearning)?.SourceId,
            MlModelVersionId = snapshots.FirstOrDefault(s => s.Kind == OptimizationKind.MachineLearning)?.MlModelVersionId,
            EvaluationStart = start,
            EvaluationEnd = end,
            BenchmarkSymbol = benchmark,
            PriceSource = settings.ActiveProvider,
            Currency = currency.Currency,
            ConfigJson = JsonSerializer.Serialize(config, MlJson.Options),
        };
        foreach (var s in snapshots.Where(s => s.Currency != currency.Currency))
            comparison.Warnings.Add($"{s.Label} was computed in {s.Currency}; in the comparison it is re-evaluated on {currency.Currency} returns.");
        if (currency.Note is not null) comparison.Warnings.Add(currency.Note);
        // The benchmark (and any stock) without enough history is downloaded first; jobs run in order.
        var queued = await marketData.EnsureHistoryAsync([.. a.Universe.Union(b.Universe), benchmark], start.AddYears(-3), 60, ct);
        if (queued.Count > 0) comparison.Warnings.Add($"Price history for {string.Join(", ", queued)} was incomplete and is downloaded before the comparison runs.");
        db.StrategyComparisons.Add(comparison);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.StrategyComparison, comparison.Id), ct);
        return ToDto(comparison);
    }

    public async Task<PagedResult<StrategyComparisonSummaryDto>> ListAsync(int page, int pageSize, CancellationToken ct, Guid? portfolioId = null)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 100).");
        var owner = currentUser.RequireUserId();
        var q = db.StrategyComparisons.AsNoTracking().Where(c => c.OwnerId == owner);
        if (portfolioId is { } pid) q = q.Where(c => c.PortfolioId == pid);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(c => c.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<StrategyComparisonSummaryDto>(items.Select(Summary).ToList(), page, pageSize, total);
    }

    public async Task<StrategyComparisonDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, ct));

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var c = await LoadAsync(id, ct, tracking: true);
        if (c.Status is JobStatus.Queued or JobStatus.Running) throw new ConflictException("A queued or running comparison cannot be deleted.");
        db.StrategyComparisons.Remove(c);
        await db.SaveChangesAsync(ct);
    }

    private async Task<StrategyComparison> LoadAsync(Guid id, CancellationToken ct, bool tracking = false)
    {
        var owner = currentUser.RequireUserId();
        var q = db.StrategyComparisons.Where(c => c.Id == id && c.OwnerId == owner);
        if (!tracking) q = q.AsNoTracking();
        return await q.SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Strategy comparison", id);
    }

    private static StrategyComparisonSummaryDto Summary(StrategyComparison c) => new(c.Id, c.Name, c.Status, c.ProgressPercent, c.ProgressMessage,
        c.CreatedAtUtc, c.CompletedAtUtc, c.EvaluationStart, c.EvaluationEnd, c.OptimizationRunId, c.MlExperimentId, c.UsesSimulatedData, c.IsFair, c.Error,
        c.PortfolioId, Configuration(c).Strategies!.Select(s => s.Label).ToList());

    /// <summary>The stored configuration with its strategies resolved (also for the format before 2026-10-24).</summary>
    internal static ComparisonConfigurationDto Configuration(StrategyComparison c)
    {
        var config = JsonSerializer.Deserialize<ComparisonConfigurationDto>(c.ConfigJson, MlJson.Options)!;
        return config with { Strategies = config.Resolve(c.OptimizationRunId, c.MlExperimentId, c.Currency, c.BenchmarkSymbol) };
    }

    private static StrategyComparisonDto ToDto(StrategyComparison c) => new(
        Summary(c),
        Configuration(c),
        c.BenchmarkSymbol,
        c.PriceSource,
        c.DataSnapshotHash,
        c.Warnings,
        c.ResultJson is null ? null : JsonSerializer.Deserialize<StrategyComparisonResultDto>(c.ResultJson, MlJson.Options),
        Disclaimer, c.Currency);
}

/// <summary>Background execution of a queued comparison (inputs were frozen at submission).</summary>
public sealed class StrategyComparisonRunner(
    IApplicationDbContext db,
    MlDataLoader loader,
    StrategyComparisonEngine engine,
    IOptions<MachineLearningOptions> options,
    TimeProvider clock,
    ILogger<StrategyComparisonRunner> logger)
{
    public async Task ExecuteAsync(Guid id, CancellationToken stoppingToken)
    {
        var comparison = await db.StrategyComparisons.SingleOrDefaultAsync(c => c.Id == id, stoppingToken);
        if (comparison is null || comparison.Status != JobStatus.Queued) return;
        comparison.Status = JobStatus.Running;
        comparison.StartedAtUtc = clock.GetUtcNow().UtcDateTime;
        comparison.ProgressPercent = 1;
        comparison.ProgressMessage = "Loading market data";
        await db.SaveChangesAsync(stoppingToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.Value.MaxRunMinutes));
        var sw = Stopwatch.StartNew();
        try
        {
            var config = StrategyComparisonService.Configuration(comparison);
            var request = config.Request;
            var start = request.StartDate!.Value;
            var end = request.EndDate!.Value;
            var snapshots = config.Strategies!;

            // ---- Universe (identical for every strategy).
            var universes = snapshots.Select(s => s.Universe.ToHashSet()).ToList();
            var all = universes.SelectMany(u => u).ToHashSet();
            var universe = (request.Universe == ComparisonUniverse.Intersection ? universes.Aggregate((x, y) => x.Intersect(y).ToHashSet()) : all)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            var notes = new List<string>();
            if (request.Universe == ComparisonUniverse.Intersection)
            {
                var dropped = all.Except(universe).OrderBy(s => s).ToList();
                if (dropped.Count > 0) notes.Add($"Intersection universe: {string.Join(", ", dropped)} appear in only one of the strategies and are excluded for all.");
            }
            else notes.Add("Union universe: every strategy may invest in all stocks of either optimization.");
            if (universe.Count < 5) throw new InvalidOperationException($"Only {universe.Count} stock(s) are common to both strategies; at least 5 are needed. Use the union universe or matching selections.");

            // ---- Data: enough history before the start for every ML training window and every optimizer's lookback.
            var historyYears = snapshots.Max(s => s.MlConfiguration?.HistoryYears ?? (int)Math.Ceiling((s.MathSettings?.LookbackDays ?? 252) / 252.0) + 1);
            var data = await loader.LoadAsync(universe, comparison.BenchmarkSymbol, comparison.PriceSource, start.AddYears(-historyYears), end, comparison.Currency, timeout.Token);
            comparison.FxSource = data.FxSource;
            notes.AddRange(data.ExcludedSymbols.Select(s => $"{s}: insufficient stored history; excluded for all strategies."));
            comparison.UsesSimulatedData = data.UsesSimulatedData;
            comparison.DataSnapshotHash = data.Panel.SnapshotHash;

            // ---- Fairness checks known before simulating.
            var issues = new List<string>();
            foreach (var s in snapshots.Where(s => s.MlTestStart is { } t && start < t))
                issues.Add($"{s.Label}: hyperparameters were selected on a validation period ending {s.MlValidationEnd:yyyy-MM-dd}; the evaluation starts {start:yyyy-MM-dd}, " +
                           $"before the experiment's out-of-sample test period ({s.MlTestStart:yyyy-MM-dd}). Its result is partly in-sample.");
            if (data.UsesSimulatedData) notes.Add("Prices are SIMULATED – not real market data.");

            var inputs = new ComparisonInputs(
                data.Panel, start, end, request.InitialCapital, request.TransactionCostBps,
                request.Rebalance == RebalanceKind.Quarterly ? RebalanceFrequency.Quarterly : RebalanceFrequency.Monthly,
                request.RiskFreeRate ?? 0.04,
                snapshots.Select((s, i) => StrategyInput(s, i, request, data.Panel)).ToList(),
                request.IncludeHybrid);

            var progress = new ProgressRelay();
            var task = Task.Run(() => engine.Run(inputs, notes, issues, progress, timeout.Token), timeout.Token);
            while (!task.IsCompleted)
            {
                await Task.WhenAny(task, Task.Delay(1000, CancellationToken.None));
                if (progress.Current is { } p && p.Percent != comparison.ProgressPercent)
                {
                    await db.StrategyComparisons.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.ProgressPercent, p.Percent).SetProperty(c => c.ProgressMessage, p.Message), CancellationToken.None);
                    comparison.ProgressPercent = p.Percent;
                }
            }
            var result = await task;
            comparison.ResultJson = JsonSerializer.Serialize(result, MlJson.Options);
            comparison.IsFair = result.Fairness.IsFair;
            comparison.Warnings = [.. comparison.Warnings, .. data.Warnings.Take(20), .. result.Warnings.Take(20)];
            comparison.Status = JobStatus.Succeeded;
            comparison.ProgressPercent = 100;
            comparison.ProgressMessage = "Completed";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            comparison.Status = JobStatus.Cancelled;
            comparison.Error = "Cancelled because the application is shutting down.";
        }
        catch (OperationCanceledException)
        {
            comparison.Status = JobStatus.Failed;
            comparison.Error = $"Stopped after the time limit of {options.Value.MaxRunMinutes} minutes.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ValidationException)
        {
            comparison.Status = JobStatus.Failed;
            comparison.Error = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Strategy comparison {ComparisonId} failed", id);
            comparison.Status = JobStatus.Failed;
            comparison.Error = "Unexpected error while running the comparison. The details were logged on the server.";
        }
        comparison.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        if (comparison.Status != JobStatus.Succeeded) comparison.ProgressMessage = comparison.Status.ToString();
        db.StrategyComparisons.Update(comparison);
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("Strategy comparison {ComparisonId} finished with {Status} in {Elapsed:0} ms", id, comparison.Status, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Strategy "A"/"B" of a comparison from its frozen snapshot.</summary>
    private static ComparisonStrategyInput StrategyInput(StrategySnapshotDto s, int index, CreateStrategyComparisonRequest request, PortfolioOptimizer.MachineLearning.Data.MarketPanel panel)
    {
        var key = index == 0 ? "a" : "b";
        var name = $"{(index == 0 ? "A" : "B")}: {s.Label}";
        if (s.Kind == OptimizationKind.Mathematical)
        {
            var m = s.MathSettings!;
            return new ComparisonStrategyInput(key, name, StrategyKind.Mathematical,
                $"{s.MathObjective} with the optimization run's constraints (max weight {m.MaxWeight:P0}, cash {m.CashReserve:P0}), " +
                $"re-estimated on a trailing {m.LookbackDays}-day window at every rebalance.",
                Math: BuildMathSettings(m, start: request.StartDate!.Value, end: request.EndDate!.Value, panel));
        }
        var c = s.MlConfiguration!;
        return new ComparisonStrategyInput(key, name, StrategyKind.MachineLearning,
            $"{MlExperimentService.Label(c.ModelFamily)} on {MlExperimentService.TargetLabel(c.Target)} ({c.HorizonDays}d); " +
            $"top {c.TopK}, {c.Weighting}, max weight {c.MaxWeight:P0}; retrained every {c.RetrainEveryRebalances} rebalances on realized labels.",
            Ml: MlExperimentRunner.ToConfiguration(c) with { TransactionCostBps = request.TransactionCostBps },
            MlHyperparameters: new Hyperparameters(s.MlHyperparameters ?? new Dictionary<string, double>()),
            MlFeatures: s.MlFeatures ?? []);
    }

    /// <summary>Mean-variance settings of a stored optimization run for a simulation of [start, end] (shared with backtests).</summary>
    internal static BacktestSettings BuildMathSettings(PortfolioSettingsDto settings, DateOnly start, DateOnly end, PortfolioOptimizer.MachineLearning.Data.MarketPanel? panel)
    {
        var s = settings.ToDomain();
        var sectors = panel is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : panel.Symbols.Select((sym, i) => (sym, sector: panel.Sectors[i]))
                .Where(x => x.sector is not null).ToDictionary(x => x.sym, x => x.sector!, StringComparer.OrdinalIgnoreCase);
        return new BacktestSettings
        {
            Start = start,
            End = end,
            Objective = s.Objective.ToOptimization(),
            CovarianceMethod = s.CovarianceMethod.ToOptimization(),
            LookbackDays = s.LookbackDays,
            MinimumObservations = Math.Min(60, s.LookbackDays),
            RiskFreeRate = s.RiskFreeRate,
            // Turnover caps relative to a live portfolio do not apply to a simulation that starts in cash.
            Constraints = s.ToConstraints(sectors) with { MaxTurnover = null },
        };
    }

    private sealed class ProgressRelay : IProgress<(int Percent, string Message)>
    {
        private readonly Lock _gate = new();
        private (int Percent, string Message)? _current;

        public (int Percent, string Message)? Current
        {
            get { lock (_gate) return _current; }
        }

        public void Report((int Percent, string Message) value)
        {
            lock (_gate) _current = value;
        }
    }
}
