using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.MachineLearning.Backtesting;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.MachineLearning.Strategy;
using PortfolioOptimizer.MachineLearning.Training;
using PortfolioOptimizer.MachineLearning.Validation;
using PortfolioOptimizer.Optimization.Backtesting;

namespace PortfolioOptimizer.Application.MachineLearning;

/// <summary>
/// Executes a queued experiment in the background worker: loads data, runs the pure ML engine on a thread-pool thread with a
/// time limit, stores model artifacts (checksummed), model versions, predictions and results. A failure marks only this
/// experiment as failed; nothing else is modified.
/// </summary>
public sealed class MlExperimentRunner(
    IApplicationDbContext db,
    MlDataLoader loader,
    IModelArtifactStore artifacts,
    IOptions<MachineLearningOptions> options,
    TimeProvider clock,
    ILogger<MlExperimentRunner> logger)
{
    public async Task ExecuteAsync(Guid id, CancellationToken stoppingToken)
    {
        var experiment = await db.MlExperiments.SingleOrDefaultAsync(e => e.Id == id, stoppingToken);
        if (experiment is null || experiment.Status != JobStatus.Queued) return;
        experiment.Status = JobStatus.Running;
        experiment.StartedAtUtc = clock.GetUtcNow().UtcDateTime;
        experiment.ProgressPercent = 1;
        experiment.ProgressMessage = "Loading market data";
        await db.SaveChangesAsync(stoppingToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.Value.MaxRunMinutes));
        var sw = Stopwatch.StartNew();
        try
        {
            var request = JsonSerializer.Deserialize<CreateMlExperimentRequest>(experiment.ConfigJson, MlJson.Options)!;
            var data = await loader.LoadAsync(experiment.Symbols, experiment.BenchmarkSymbol, experiment.PriceSource, experiment.HistoryStart, experiment.HistoryEnd, timeout.Token);
            experiment.UsesSimulatedData = data.UsesSimulatedData;
            var config = ToConfiguration(request);

            var progress = new LatestProgress();
            var run = Task.Run(() => new ExperimentEngine().Run(data.Panel, config, progress, timeout.Token), timeout.Token);
            while (!run.IsCompleted)
            {
                await Task.WhenAny(run, Task.Delay(1000, CancellationToken.None));
                if (progress.Current is { } p && p.Percent != experiment.ProgressPercent)
                {
                    await db.MlExperiments.Where(e => e.Id == id).ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.ProgressPercent, p.Percent).SetProperty(e => e.ProgressMessage, p.Message), CancellationToken.None);
                    experiment.ProgressPercent = p.Percent;
                }
            }
            var result = await run;
            await StoreAsync(experiment, result, data, timeout.Token);
            experiment.Status = JobStatus.Succeeded;
            experiment.ProgressPercent = 100;
            experiment.ProgressMessage = "Completed";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            experiment.Status = JobStatus.Cancelled;
            experiment.Error = "Cancelled because the application is shutting down.";
        }
        catch (OperationCanceledException)
        {
            experiment.Status = JobStatus.Failed;
            experiment.Error = $"Stopped after the time limit of {options.Value.MaxRunMinutes} minutes. Use fewer stocks, less history or a larger sample step.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ValidationException)
        {
            experiment.Status = JobStatus.Failed;
            experiment.Error = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ML experiment {ExperimentId} failed", id);
            experiment.Status = JobStatus.Failed;
            experiment.Error = "Unexpected error while running the experiment. The details were logged on the server.";
        }

        experiment.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        experiment.DurationMs = sw.Elapsed.TotalMilliseconds;
        if (experiment.Status != JobStatus.Succeeded) experiment.ProgressMessage = experiment.Status.ToString();
        db.MlExperiments.Update(experiment);
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("ML experiment {ExperimentId} finished with {Status} in {Elapsed:0} ms", id, experiment.Status, sw.Elapsed.TotalMilliseconds);
    }

    private async Task StoreAsync(MlExperiment experiment, ExperimentResult result, MlData data, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var versions = new List<MlModelVersion>();
        var number = 1;
        foreach (var info in new[] { result.EvaluationModel, result.DeploymentModel })
        {
            var versionId = Guid.NewGuid();
            var manifest = new ModelManifest
            {
                Family = info.Model.Family,
                Task = info.Model.Task,
                Target = result.Configuration.Target,
                HorizonDays = result.Configuration.HorizonDays,
                FeatureSetVersion = result.FeatureSetVersion,
                FeatureNames = result.FeatureNames,
                Hyperparameters = info.Model.Hyperparameters.Values,
                TrainedFrom = info.TrainedFrom,
                TrainedThrough = info.TrainedThrough,
                TrainingSamples = info.TrainingSamples,
                Seed = result.Configuration.Seed,
                Role = info.Role,
                CreatedAtUtc = now,
                DataSnapshotHash = result.DataSnapshotHash,
            };
            var stored = await artifacts.SaveAsync(experiment.Id, versionId, ModelArtifact.Serialize(info.Model, manifest), ct);
            versions.Add(new MlModelVersion
            {
                Id = versionId,
                ExperimentId = experiment.Id,
                Version = number++,
                Role = info.Role,
                ModelFamily = info.Model.Family.ToString(),
                TrainedFrom = info.TrainedFrom,
                TrainedThrough = info.TrainedThrough,
                TrainingSamples = info.TrainingSamples,
                HyperparametersJson = JsonSerializer.Serialize(info.Model.Hyperparameters.Values, MlJson.Options),
                FeatureNames = [.. result.FeatureNames],
                FeatureSetVersion = result.FeatureSetVersion,
                ArtifactPath = stored.RelativePath,
                ArtifactSha256 = stored.Sha256,
                ArtifactSizeBytes = stored.SizeBytes,
                CreatedAtUtc = now,
            });
        }

        experiment.DataSnapshotHash = result.DataSnapshotHash;
        experiment.TrainStart = result.TrainPeriod.Start;
        experiment.TrainEnd = result.TrainPeriod.End;
        experiment.ValidationStart = result.ValidationPeriod.Start;
        experiment.ValidationEnd = result.ValidationPeriod.End;
        experiment.TestStart = result.TestPeriod.Start;
        experiment.TestEnd = result.TestPeriod.End;
        experiment.TrainSamples = result.TrainPeriod.Samples;
        experiment.ValidationSamples = result.ValidationPeriod.Samples;
        experiment.TestSamples = result.TestPeriod.Samples;
        experiment.IsExploratory = result.IsExploratory;
        experiment.Warnings = [.. experiment.Warnings, .. data.Warnings, .. result.Warnings]; // keep notes added at creation (e.g. benchmark switch)
        experiment.ResultJson = JsonSerializer.Serialize(new MlResultDocument(
            result.TargetDefinition, result.FeatureNames, result.Trials, result.SelectedHyperparameters, result.TrainMetrics, result.ValidationMetrics,
            result.TestMetrics, result.Baselines, result.PermutationImportance, result.NativeExplanation, result.ContributionMethod,
            result.TrainPeriod, result.ValidationPeriod, result.TestPeriod, result.TotalSamples, result.DroppedSamples,
            MlExperimentService.ValidationMethodology), MlJson.Options);
        var bt = result.Backtest;
        experiment.BacktestJson = JsonSerializer.Serialize(new MlBacktestDocument(bt.Dates, bt.Values, bt.Drawdowns, bt.Performance, bt.Rebalances,
            bt.Values.SelectMany(kv => StrategyMetrics.MonthlyReturns(bt.Dates, kv.Value).Select(m => new MonthlyReturnPoint(kv.Key, m.Year, m.Month, m.Return))).ToList(),
            bt.Warnings, bt.Methodology), MlJson.Options);

        foreach (var v in versions) db.MlModelVersions.Add(v);
        var deployment = versions.Single(v => v.Role == "Deployment");
        db.MlPredictionSets.Add(new MlPredictionSet
        {
            Id = Guid.NewGuid(),
            ExperimentId = experiment.Id,
            ModelVersionId = deployment.Id,
            AsOfDate = result.LatestPredictions.AsOf,
            CreatedAtUtc = now,
            HorizonDays = result.LatestPredictions.HorizonDays,
            CashWeight = result.LatestPredictions.CashWeight,
            IntervalMethod = result.LatestPredictions.IntervalMethod,
            Notes = [.. result.LatestPredictions.ConstructionNotes],
            UsesSimulatedData = data.UsesSimulatedData,
            PredictionsJson = JsonSerializer.Serialize(result.LatestPredictions.Predictions, MlJson.Options),
        });
    }

    public static ExperimentConfiguration ToConfiguration(CreateMlExperimentRequest r) => new()
    {
        Family = r.ModelFamily,
        Target = r.Target,
        HorizonDays = r.HorizonDays,
        Features = r.Features,
        SampleStepDays = r.SampleStepDays,
        Split = new SplitSettings { TrainFraction = r.TrainFraction, ValidationFraction = r.ValidationFraction, EmbargoDays = r.EmbargoDays },
        Seed = r.Seed,
        Hyperparameters = r.Hyperparameters,
        Portfolio = new PortfolioConstructionSettings
        {
            TopK = r.TopK,
            Weighting = r.Weighting,
            MaxWeight = r.MaxWeight,
            CashReserve = r.CashReserve,
            MinimumScore = r.MinimumScore,
            MaxTurnover = r.MaxTurnover,
            SectorLimits = r.SectorLimits,
        },
        Rebalance = r.Rebalance == RebalanceKind.Quarterly ? RebalanceFrequency.Quarterly : RebalanceFrequency.Monthly,
        WalkForward = new WalkForwardSettings { RetrainEveryRebalances = r.RetrainEveryRebalances, TrainingWindowDays = r.TrainingWindowDays },
        TransactionCostBps = r.TransactionCostBps,
        InitialCapital = r.InitialCapital,
        RiskFreeRate = r.RiskFreeRate,
    };

    /// <summary>Keeps only the latest progress value; read by the polling loop (no cross-thread DbContext use).</summary>
    private sealed class LatestProgress : IProgress<(int Percent, string Message)>
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
