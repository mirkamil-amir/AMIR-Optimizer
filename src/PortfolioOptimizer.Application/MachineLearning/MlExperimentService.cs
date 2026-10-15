using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.MachineLearning.Features;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.MachineLearning.Strategy;
using PortfolioOptimizer.MachineLearning.Training;

namespace PortfolioOptimizer.Application.MachineLearning;

public sealed class MachineLearningOptions
{
    public const string Section = "MachineLearning";

    /// <summary>Queued or running experiments allowed per user (protects the shared worker).</summary>
    public int MaxActiveExperimentsPerUser { get; set; } = 2;

    /// <summary>Hard time limit for one experiment (training + evaluation + backtest).</summary>
    public int MaxRunMinutes { get; set; } = 30;
}

public static class MlJson
{
    /// <summary>Web defaults, string enums, and NaN/Infinity tolerated as named literals (metrics of empty sets).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}

/// <summary>
/// ML experiment lifecycle for the current user: submit (persisted and queued), inspect, list, re-predict with the stored
/// deployment model (no retraining), delete. All queries are restricted to the owner.
/// </summary>
public sealed class MlExperimentService(
    IApplicationDbContext db,
    PriceQueryService prices,
    MlDataLoader loader,
    MarketDataService marketData,
    IModelArtifactStore artifacts,
    IBackgroundJobQueue queue,
    ICurrentUser currentUser,
    IOptions<MachineLearningOptions> options,
    TimeProvider clock,
    ILogger<MlExperimentService> logger)
{
    public const string Disclaimer =
        "Predictions are uncertain statistical estimates from historical data, not guarantees or investment advice. " +
        "Out-of-sample metrics refer to the test period only; backtests are hypothetical and ignore survivorship bias in today's universe.";

    public static MlCatalogDto Catalog() => new(
        ModelRegistry.Trainers.Select(t => new MlModelFamilyInfo(
            t.Family.ToString(),
            Label(t.Family),
            ModelRegistry.Describe(t.Family),
            Enum.GetValues<PredictionTarget>().Where(target => t.Supports(TargetCatalog.TaskOf(target))).Select(x => x.ToString()).ToList(),
            t.Supports(TaskKind.Regression) ? t.DefaultGrid(TaskKind.Regression).Select(h => h.Values).ToList() : [],
            t.Supports(TaskKind.BinaryClassification) ? t.DefaultGrid(TaskKind.BinaryClassification).Select(h => h.Values).ToList() : [])).ToList(),
        Enum.GetValues<PredictionTarget>().Select(t => new NamedOption(t.ToString(), TargetLabel(t), TargetCatalog.Describe(t, 21, "the benchmark"))).ToList(),
        FeatureCatalog.All.Select(f => new MlFeatureInfo(f.Name, f.Group.ToString(), f.Definition, f.WindowDays, f.RequiresVolume, f.RequiresBenchmark, f.RequiresSectorPeers)).ToList(),
        FeatureCatalog.Version,
        FeatureCatalog.MissingValuePolicy,
        [
            new(nameof(WeightingMethod.EqualWeight), "Equal weight", "Equal weights across the selected top-K stocks."),
            new(nameof(WeightingMethod.RankWeighted), "Rank weighted", "Weights K, K−1, …, 1 by rank (more weight to higher-ranked stocks)."),
            new(nameof(WeightingMethod.InverseVolatility), "Inverse volatility", "Weights ∝ 1/trailing 63-day volatility (risk scaling)."),
            new(nameof(WeightingMethod.ScoreProportional), "Score proportional", "Weights ∝ score above the lowest selected score."),
        ],
        ValidationMethodology,
        ExperimentEngine.BacktestMethodology,
        CreateMlExperimentRequest.MaxUniverse);

    public const string ValidationMethodology =
        "Chronological split of decision dates into training (default 60%), validation (20%) and test (20%) periods – never random. " +
        "Training samples whose label window reaches into the next period are purged (plus an optional embargo). Imputation and scaling " +
        "are fitted on training rows only. Hyperparameters are chosen on the validation period; the selected model is refit on " +
        "training + validation and evaluated once on the untouched test period. A deployment model refit on all realized labels produces " +
        "current predictions; its out-of-sample record is the evaluation model's test result.";

    public async Task<MlExperimentDto> CreateAsync(CreateMlExperimentRequest request, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var universe = request.Universe.Select(Asset.NormalizeSymbol).Distinct().ToList();
        var benchmark = Asset.NormalizeSymbol(request.BenchmarkSymbol);
        var errors = new Dictionary<string, string[]>();
        var known = await db.Assets.AsNoTracking().Where(a => universe.Contains(a.Symbol) || a.Symbol == benchmark).Select(a => a.Symbol).ToListAsync(ct);
        var unknown = universe.Append(benchmark).Except(known).ToList();
        if (unknown.Count > 0) errors["universe"] = [$"Unknown symbol(s): {string.Join(", ", unknown)}."];
        if (universe.Count < 5) errors["universe"] = ["Select at least 5 distinct stocks."];
        var family = ModelRegistry.Get(request.ModelFamily);
        if (!family.Supports(TargetCatalog.TaskOf(request.Target)))
            errors["modelFamily"] = [$"{Label(request.ModelFamily)} does not support the target {TargetLabel(request.Target)}."];
        var badFeatures = request.Features.Where(f => FeatureCatalog.All.All(c => c.Name != f)).ToList();
        if (badFeatures.Count > 0) errors["features"] = [$"Unknown feature(s): {string.Join(", ", badFeatures)}."];
        if (errors.Count > 0) throw new ValidationException(errors);

        // Features, targets and the benchmark are computed in one analysis currency (prices converted with dated FX rates).
        var currency = await UniverseCurrency.ResolveAsync(db, universe, benchmark, request.Currency, ct);
        benchmark = currency.Benchmark;

        var active = await db.MlExperiments.CountAsync(e => e.OwnerId == owner && (e.Status == JobStatus.Queued || e.Status == JobStatus.Running), ct);
        if (active >= options.Value.MaxActiveExperimentsPerUser)
            throw new ConflictException($"You already have {active} experiment(s) queued or running (limit {options.Value.MaxActiveExperimentsPerUser}). Wait for them to finish.");

        var settings = await prices.SettingsAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var normalized = request with { Universe = universe, BenchmarkSymbol = benchmark, Currency = currency.Currency };
        var experiment = new MlExperiment
        {
            Id = Guid.NewGuid(),
            OwnerId = owner,
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? $"{Label(request.ModelFamily)} · {TargetLabel(request.Target)} · {request.HorizonDays}d · {universe.Count} stocks"
                : request.Name.Trim(),
            Status = JobStatus.Queued,
            ProgressMessage = "Queued",
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            ModelFamily = request.ModelFamily.ToString(),
            Target = request.Target.ToString(),
            HorizonDays = request.HorizonDays,
            FeatureSetVersion = FeatureCatalog.Version,
            Symbols = universe,
            BenchmarkSymbol = benchmark,
            PriceSource = settings.ActiveProvider,
            Currency = currency.Currency,
            HistoryStart = today.AddYears(-request.HistoryYears),
            HistoryEnd = today,
            ConfigJson = JsonSerializer.Serialize(normalized, MlJson.Options),
        };
        if (currency.Note is not null) experiment.Warnings.Add(currency.Note);
        // Missing history (typically an index benchmark or newly selected stocks) is downloaded first: the worker runs
        // jobs in order, so the experiment starts once the prices are stored.
        var queued = await marketData.EnsureHistoryAsync([.. universe, benchmark], experiment.HistoryStart, MlDataLoader.MinimumObservations, ct);
        if (queued.Count > 0)
            experiment.Warnings.Add($"Price history for {string.Join(", ", queued)} was incomplete and is downloaded before training starts.");
        db.MlExperiments.Add(experiment);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.MlExperiment, experiment.Id), ct);
        logger.LogInformation("ML experiment {ExperimentId} queued by {UserId}: {Family} {Target}", experiment.Id, owner, request.ModelFamily, request.Target);
        return await ToDtoAsync(experiment, ct);
    }

    public async Task<PagedResult<MlExperimentSummaryDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 100).");
        var owner = currentUser.RequireUserId();
        var q = db.MlExperiments.AsNoTracking().Where(e => e.OwnerId == owner);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<MlExperimentSummaryDto>(items.Select(Summary).ToList(), page, pageSize, total);
    }

    public async Task<MlExperimentDto> GetAsync(Guid id, CancellationToken ct) => await ToDtoAsync(await LoadAsync(id, ct), ct);

    public async Task<IReadOnlyList<MlModelVersionDto>> GetModelVersionsAsync(Guid id, CancellationToken ct) =>
        (await LoadAsync(id, ct)).ModelVersions.OrderBy(v => v.Version).Select(ToDto).ToList();

    public async Task<IReadOnlyList<MlPredictionSetDto>> GetPredictionSetsAsync(Guid id, CancellationToken ct)
    {
        var experiment = await LoadAsync(id, ct);
        // Only sets whose model version is part of the experiment snapshot loaded above (the job may commit in between).
        var versionIds = experiment.ModelVersions.Select(v => v.Id).ToList();
        var sets = await db.MlPredictionSets.AsNoTracking().Where(p => p.ExperimentId == id && versionIds.Contains(p.ModelVersionId))
            .OrderByDescending(p => p.CreatedAtUtc).Take(20).ToListAsync(ct);
        var result = new List<MlPredictionSetDto>();
        foreach (var set in sets) result.Add(await ToDtoAsync(set, experiment, ct));
        return result;
    }

    /// <summary>
    /// Scores the latest available data with the experiment's stored deployment model (artifact checksum verified) and
    /// stores the prediction set. The model is not retrained; a different model version is never substituted silently.
    /// </summary>
    public async Task<MlPredictionSetDto> PredictAsync(Guid id, CancellationToken ct)
    {
        var experiment = await LoadAsync(id, ct);
        if (experiment.Status != JobStatus.Succeeded) throw new ConflictException("Only completed experiments can produce predictions.");
        var version = experiment.ModelVersions.Where(v => v.Role == "Deployment").MaxBy(v => v.Version)
                      ?? throw new ConflictException("The experiment has no deployment model.");
        var bytes = await artifacts.ReadAsync(version.ArtifactPath, ct);
        var (manifest, model) = ModelArtifact.Deserialize(bytes, version.ArtifactSha256);
        if (!manifest.FeatureNames.SequenceEqual(version.FeatureNames) || manifest.FeatureSetVersion != FeatureCatalog.Version)
            throw new ConflictException($"Model version {version.Version} was trained with feature set {manifest.FeatureSetVersion}; the current feature definitions ({FeatureCatalog.Version}) differ. Run a new experiment.");

        var request = JsonSerializer.Deserialize<CreateMlExperimentRequest>(experiment.ConfigJson, MlJson.Options)!;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var data = await loader.LoadAsync(experiment.Symbols, experiment.BenchmarkSymbol, experiment.PriceSource, today.AddYears(-2), today, experiment.Currency, ct);
        var engine = new FeatureEngine(data.Panel, manifest.FeatureNames.Select(FeatureCatalog.Get).ToList());
        var dataset = PortfolioOptimizer.MachineLearning.Data.Dataset.Build(data.Panel, engine, manifest.Target, manifest.HorizonDays, sampleStep: 21);
        var snapshot = ExperimentEngine.PredictLatest(dataset, model, MlExperimentRunner.ToConfiguration(request), residualQuantiles: null);

        var set = new MlPredictionSet
        {
            Id = Guid.NewGuid(),
            ExperimentId = experiment.Id,
            ModelVersionId = version.Id,
            AsOfDate = snapshot.AsOf,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            HorizonDays = snapshot.HorizonDays,
            CashWeight = snapshot.CashWeight,
            IntervalMethod = null,
            Notes = [.. snapshot.ConstructionNotes, .. data.Warnings.Take(10)],
            UsesSimulatedData = data.UsesSimulatedData,
            PredictionsJson = JsonSerializer.Serialize(snapshot.Predictions, MlJson.Options),
        };
        db.MlPredictionSets.Add(set);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(set, experiment, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var experiment = await LoadAsync(id, ct, tracking: true);
        if (experiment.Status is JobStatus.Queued or JobStatus.Running) throw new ConflictException("A queued or running experiment cannot be deleted.");
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Prediction sets reference model versions (restrict): remove them before the versions and the experiment.
            await db.MlPredictionSets.Where(p => p.ExperimentId == id).ExecuteDeleteAsync(ct);
            db.MlExperiments.Remove(experiment);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        await artifacts.DeleteExperimentAsync(id, ct);
    }

    /// <summary>Loads an experiment of the current user (other users' experiments are reported as not found).</summary>
    public async Task<MlExperiment> LoadAsync(Guid id, CancellationToken ct, bool tracking = false)
    {
        var owner = currentUser.RequireUserId();
        var q = db.MlExperiments.Include(e => e.ModelVersions).Where(e => e.Id == id && e.OwnerId == owner);
        if (!tracking) q = q.AsNoTracking();
        return await q.SingleOrDefaultAsync(ct) ?? throw new NotFoundException("ML experiment", id);
    }

    private async Task<MlExperimentDto> ToDtoAsync(MlExperiment e, CancellationToken ct)
    {
        // The experiment and its versions were loaded together; a running job may commit sets afterwards, so only use
        // sets that belong to the loaded versions (consistent view).
        var versionIds = e.ModelVersions.Select(v => v.Id).ToList();
        var latestSet = versionIds.Count == 0 ? null : await db.MlPredictionSets.AsNoTracking()
            .Where(p => p.ExperimentId == e.Id && versionIds.Contains(p.ModelVersionId))
            .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return new MlExperimentDto(
            Summary(e),
            JsonSerializer.Deserialize<CreateMlExperimentRequest>(e.ConfigJson, MlJson.Options)!,
            e.FeatureSetVersion,
            e.BenchmarkSymbol,
            e.Symbols,
            e.PriceSource,
            e.HistoryStart,
            e.HistoryEnd,
            e.DataSnapshotHash,
            e.StartedAtUtc,
            e.DurationMs,
            e.Warnings,
            e.ResultJson is null ? null : JsonSerializer.Deserialize<MlResultDocument>(e.ResultJson, MlJson.Options),
            e.ModelVersions.OrderBy(v => v.Version).Select(ToDto).ToList(),
            latestSet is null ? null : await ToDtoAsync(latestSet, e, ct),
            Disclaimer, e.Currency);
    }

    public async Task<MlBacktestDocument?> GetBacktestAsync(Guid id, CancellationToken ct)
    {
        var e = await LoadAsync(id, ct);
        return e.BacktestJson is null ? null : JsonSerializer.Deserialize<MlBacktestDocument>(e.BacktestJson, MlJson.Options);
    }

    private async Task<MlPredictionSetDto> ToDtoAsync(MlPredictionSet set, MlExperiment experiment, CancellationToken ct)
    {
        var version = experiment.ModelVersions.Single(v => v.Id == set.ModelVersionId);
        var predictions = JsonSerializer.Deserialize<List<StockPrediction>>(set.PredictionsJson, MlJson.Options) ?? [];
        var symbols = predictions.Select(p => p.Symbol).ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);
        var latest = await prices.GetLatestPricesAsync(assets.Values.Select(a => a.Id).ToList(), experiment.PriceSource, 5, ct);
        return new MlPredictionSetDto(set.Id, version.Id, version.Version, version.Role, version.TrainedThrough, set.AsOfDate, set.CreatedAtUtc,
            set.HorizonDays, set.CashWeight, set.IntervalMethod, set.Notes, set.UsesSimulatedData,
            predictions.Select(p =>
            {
                assets.TryGetValue(p.Symbol, out var a);
                LatestPrice? lp = a is null ? null : latest.GetValueOrDefault(a.Id);
                return new MlPredictionDto(p.Symbol, a?.Name, p.Sector ?? a?.Sector, lp?.Price, lp?.PriceDate, p.PredictedValue, p.Score, p.Rank,
                    p.Selected, p.TargetWeight, p.IntervalLow, p.IntervalHigh, p.Drivers);
            }).ToList());
    }

    private static MlExperimentSummaryDto Summary(MlExperiment e)
    {
        MlResultDocument? r = null;
        if (e.ResultJson is not null) r = JsonSerializer.Deserialize<MlResultDocument>(e.ResultJson, MlJson.Options);
        double? backtestReturn = null;
        if (e.BacktestJson is not null)
        {
            var bt = JsonSerializer.Deserialize<MlBacktestDocument>(e.BacktestJson, MlJson.Options);
            backtestReturn = bt?.Performance.FirstOrDefault()?.AnnualizedReturn.Value;
        }
        return new MlExperimentSummaryDto(e.Id, e.Name, e.Status, e.ProgressPercent, e.ProgressMessage,
            Enum.Parse<ModelFamily>(e.ModelFamily), Enum.Parse<PredictionTarget>(e.Target), e.HorizonDays, e.Symbols.Count,
            e.CreatedAtUtc, e.CompletedAtUtc, e.UsesSimulatedData, e.IsExploratory,
            r?.TestMetrics.Ranking.MeanInformationCoefficient, r?.TestMetrics.Regression?.OutOfSampleR2, r?.TestMetrics.Classification?.RocAuc,
            backtestReturn, e.Error);
    }

    private static MlModelVersionDto ToDto(MlModelVersion v) => new(v.Id, v.Version, v.Role, v.ModelFamily, v.TrainedFrom, v.TrainedThrough, v.TrainingSamples,
        JsonSerializer.Deserialize<Dictionary<string, double>>(v.HyperparametersJson, MlJson.Options) ?? [], v.FeatureNames, v.FeatureSetVersion,
        v.ArtifactSha256, v.ArtifactSizeBytes, v.CreatedAtUtc);

    public static string Label(ModelFamily f) => f switch
    {
        ModelFamily.RidgeRegression => "Ridge regression",
        ModelFamily.LogisticRegression => "Logistic regression",
        ModelFamily.RandomForest => "Random forest",
        ModelFamily.GradientBoosting => "Gradient boosting",
        _ => f.ToString(),
    };

    public static string TargetLabel(PredictionTarget t) => t switch
    {
        PredictionTarget.ForwardReturn => "Forward return",
        PredictionTarget.ForwardExcessReturn => "Forward excess return",
        PredictionTarget.OutperformBenchmark => "Probability of outperformance",
        PredictionTarget.ForwardVolatility => "Forward volatility",
        _ => t.ToString(),
    };
}
