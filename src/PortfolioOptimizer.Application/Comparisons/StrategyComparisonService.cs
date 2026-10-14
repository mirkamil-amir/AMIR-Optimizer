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
        var run = await db.OptimizationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.OptimizationRunId && r.OwnerId == owner, ct)
                  ?? throw new NotFoundException("Optimization run", request.OptimizationRunId);
        if (run.Status != OptimizationRunStatus.Succeeded) throw new ConflictException("The optimization run did not succeed; choose a successful run.");
        var experiment = await db.MlExperiments.AsNoTracking().Include(e => e.ModelVersions)
                             .SingleOrDefaultAsync(e => e.Id == request.MlExperimentId && e.OwnerId == owner, ct)
                         ?? throw new NotFoundException("ML experiment", request.MlExperimentId);
        if (experiment.Status != JobStatus.Succeeded) throw new ConflictException("The ML experiment has not completed successfully.");
        var evaluation = experiment.ModelVersions.Where(v => v.Role == "Evaluation").MaxBy(v => v.Version)
                         ?? throw new ConflictException("The ML experiment has no evaluation model version.");
        if (evaluation.FeatureSetVersion != FeatureCatalog.Version)
            throw new ConflictException($"The ML experiment used feature set {evaluation.FeatureSetVersion}; the current definitions are {FeatureCatalog.Version}. Run a new experiment.");

        var benchmark = Asset.NormalizeSymbol(request.BenchmarkSymbol);
        if (!await db.Assets.AnyAsync(a => a.Symbol == benchmark, ct)) throw new ValidationException("benchmarkSymbol", $"Unknown benchmark {benchmark}.");
        // Both strategies and the benchmark must be in one currency (no FX conversion).
        var currency = await UniverseCurrency.ResolveAsync(db, run.Symbols.Union(experiment.Symbols).ToList(), benchmark, ct);
        benchmark = currency.Benchmark;
        var active = await db.StrategyComparisons.CountAsync(c => c.OwnerId == owner && (c.Status == JobStatus.Queued || c.Status == JobStatus.Running), ct);
        if (active >= options.Value.MaxActiveExperimentsPerUser)
            throw new ConflictException($"You already have {active} comparison(s) queued or running. Wait for them to finish.");

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var start = request.StartDate ?? experiment.TestStart ?? today.AddYears(-1);
        var end = request.EndDate ?? today;
        if (end > today) end = today;
        if (end <= start.AddMonths(3)) throw new ValidationException("endDate", "The evaluation period must be longer than three months.");

        var mlConfig = JsonSerializer.Deserialize<CreateMlExperimentRequest>(experiment.ConfigJson, MlJson.Options)!;
        var hyperparameters = JsonSerializer.Deserialize<Dictionary<string, double>>(evaluation.HyperparametersJson, MlJson.Options) ?? [];
        var settings = await prices.SettingsAsync(ct);
        var config = new ComparisonConfigurationDto(
            request with { StartDate = start, EndDate = end, BenchmarkSymbol = benchmark, RiskFreeRate = request.RiskFreeRate ?? run.SettingsSnapshot.RiskFreeRate },
            run.SettingsSnapshot.ToDto() with { Universe = [.. run.Symbols] },
            run.Objective.ToString(),
            run.CreatedAtUtc,
            mlConfig,
            hyperparameters,
            evaluation.FeatureNames,
            experiment.ValidationEnd,
            experiment.TestStart,
            evaluation.FeatureSetVersion);

        var comparison = new StrategyComparison
        {
            Id = Guid.NewGuid(),
            OwnerId = owner,
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"{run.Objective} vs {MlExperimentService.Label(Enum.Parse<ModelFamily>(experiment.ModelFamily))} · {start:yyyy-MM}–{end:yyyy-MM}" : request.Name.Trim(),
            Status = JobStatus.Queued,
            ProgressMessage = "Queued",
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            OptimizationRunId = run.Id,
            MlExperimentId = experiment.Id,
            MlModelVersionId = evaluation.Id,
            EvaluationStart = start,
            EvaluationEnd = end,
            BenchmarkSymbol = benchmark,
            PriceSource = settings.ActiveProvider,
            ConfigJson = JsonSerializer.Serialize(config, MlJson.Options),
        };
        if (currency.Note is not null) comparison.Warnings.Add(currency.Note);
        // The benchmark (and any stock) without enough history is downloaded first; jobs run in order.
        var queued = await marketData.EnsureHistoryAsync([.. run.Symbols.Union(experiment.Symbols), benchmark], start.AddYears(-3), 60, ct);
        if (queued.Count > 0) comparison.Warnings.Add($"Price history for {string.Join(", ", queued)} was incomplete and is downloaded before the comparison runs.");
        db.StrategyComparisons.Add(comparison);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.StrategyComparison, comparison.Id), ct);
        return ToDto(comparison);
    }

    public async Task<PagedResult<StrategyComparisonSummaryDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 100).");
        var owner = currentUser.RequireUserId();
        var q = db.StrategyComparisons.AsNoTracking().Where(c => c.OwnerId == owner);
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
        c.CreatedAtUtc, c.CompletedAtUtc, c.EvaluationStart, c.EvaluationEnd, c.OptimizationRunId, c.MlExperimentId, c.UsesSimulatedData, c.IsFair, c.Error);

    private static StrategyComparisonDto ToDto(StrategyComparison c) => new(
        Summary(c),
        JsonSerializer.Deserialize<ComparisonConfigurationDto>(c.ConfigJson, MlJson.Options)!,
        c.BenchmarkSymbol,
        c.PriceSource,
        c.DataSnapshotHash,
        c.Warnings,
        c.ResultJson is null ? null : JsonSerializer.Deserialize<StrategyComparisonResultDto>(c.ResultJson, MlJson.Options),
        Disclaimer);
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
            var config = JsonSerializer.Deserialize<ComparisonConfigurationDto>(comparison.ConfigJson, MlJson.Options)!;
            var request = config.Request;
            var start = request.StartDate!.Value;
            var end = request.EndDate!.Value;

            // ---- Universe (identical for every strategy).
            var mathUniverse = config.OptimizationSettings.Universe.ToHashSet();
            var mlUniverse = config.MlConfiguration.Universe.ToHashSet();
            var universe = (request.Universe == ComparisonUniverse.Intersection ? mathUniverse.Intersect(mlUniverse) : mathUniverse.Union(mlUniverse))
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            var notes = new List<string>();
            if (request.Universe == ComparisonUniverse.Intersection)
            {
                var dropped = mathUniverse.Union(mlUniverse).Except(universe).OrderBy(s => s).ToList();
                if (dropped.Count > 0) notes.Add($"Intersection universe: {string.Join(", ", dropped)} appear in only one of the two strategies and are excluded for all.");
            }
            else notes.Add("Union universe: every strategy may invest in all stocks of either the optimization run or the ML experiment.");
            if (universe.Count < 5) throw new InvalidOperationException($"Only {universe.Count} stock(s) are common to both strategies; at least 5 are needed. Use the union universe or matching selections.");

            // ---- Data: enough history before the start for the ML training window and the optimizer's lookback.
            var historyYears = Math.Max(config.MlConfiguration.HistoryYears, (int)Math.Ceiling(config.OptimizationSettings.LookbackDays / 252.0) + 1);
            var data = await loader.LoadAsync(universe, comparison.BenchmarkSymbol, comparison.PriceSource, start.AddYears(-historyYears), end, timeout.Token);
            notes.AddRange(data.ExcludedSymbols.Select(s => $"{s}: insufficient stored history; excluded for all strategies."));
            comparison.UsesSimulatedData = data.UsesSimulatedData;
            comparison.DataSnapshotHash = data.Panel.SnapshotHash;

            // ---- Fairness checks known before simulating.
            var issues = new List<string>();
            if (config.MlTestStart is { } testStart && start < testStart)
                issues.Add($"The ML hyperparameters were selected on a validation period ending {config.MlValidationEnd:yyyy-MM-dd}; the evaluation starts {start:yyyy-MM-dd}, " +
                           $"before the experiment's out-of-sample test period ({testStart:yyyy-MM-dd}). The ML result is partly in-sample.");
            if (data.UsesSimulatedData) notes.Add("Prices are SIMULATED (mock provider) – not real market data.");

            var mathSettings = BuildMathSettings(config, data.Panel);
            var mlConfig = MlExperimentRunner.ToConfiguration(config.MlConfiguration) with { TransactionCostBps = request.TransactionCostBps };
            var inputs = new ComparisonInputs(
                data.Panel, start, end, request.InitialCapital, request.TransactionCostBps,
                request.Rebalance == RebalanceKind.Quarterly ? RebalanceFrequency.Quarterly : RebalanceFrequency.Monthly,
                request.RiskFreeRate ?? 0.04,
                mathSettings,
                $"{config.OptimizationObjective} with the optimization run's constraints (max weight {config.OptimizationSettings.MaxWeight:P0}, cash {config.OptimizationSettings.CashReserve:P0}), " +
                $"re-estimated on a trailing {config.OptimizationSettings.LookbackDays}-day window at every rebalance.",
                mlConfig,
                new Hyperparameters(config.MlHyperparameters),
                config.MlFeatures,
                $"{MlExperimentService.Label(config.MlConfiguration.ModelFamily)} on {MlExperimentService.TargetLabel(config.MlConfiguration.Target)} ({config.MlConfiguration.HorizonDays}d); " +
                $"top {config.MlConfiguration.TopK}, {config.MlConfiguration.Weighting}, max weight {config.MlConfiguration.MaxWeight:P0}; retrained every {config.MlConfiguration.RetrainEveryRebalances} rebalances on realized labels.",
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

    private static BacktestSettings BuildMathSettings(ComparisonConfigurationDto config, PortfolioOptimizer.MachineLearning.Data.MarketPanel panel)
    {
        var s = config.OptimizationSettings.ToDomain();
        var sectors = panel.Symbols.Select((sym, i) => (sym, sector: panel.Sectors[i]))
            .Where(x => x.sector is not null).ToDictionary(x => x.sym, x => x.sector!, StringComparer.OrdinalIgnoreCase);
        return new BacktestSettings
        {
            Start = config.Request.StartDate!.Value,
            End = config.Request.EndDate!.Value,
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
