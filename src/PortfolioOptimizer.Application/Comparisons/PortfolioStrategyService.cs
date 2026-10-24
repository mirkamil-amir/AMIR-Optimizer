using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MachineLearning;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.MachineLearning.Features;
using PortfolioOptimizer.MachineLearning.Models;

namespace PortfolioOptimizer.Application.Comparisons;

/// <summary>
/// The optimizations of one portfolio – successful mean-variance runs and successful ML experiments linked to it – and
/// their frozen strategy settings for backtests and comparisons. Everything is resolved owner-scoped through the portfolio
/// (another user's portfolio, run or experiment → 404).
/// </summary>
public sealed partial class PortfolioStrategyService(IApplicationDbContext db, PortfolioService portfolios)
{
    public async Task<IReadOnlyList<PortfolioStrategyDto>> ListAsync(Guid portfolioId, CancellationToken ct)
    {
        await portfolios.EnsureExistsAsync(portfolioId, ct);
        var runs = await db.OptimizationRuns.AsNoTracking()
            .Where(r => r.PortfolioId == portfolioId && r.Status == OptimizationRunStatus.Succeeded)
            .OrderByDescending(r => r.CreatedAtUtc).Take(200).ToListAsync(ct);
        var experiments = await db.MlExperiments.AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId && e.Status == JobStatus.Succeeded)
            .OrderByDescending(e => e.CreatedAtUtc).Take(200).ToListAsync(ct);
        return runs.Select(r => new PortfolioStrategyDto(OptimizationKind.Mathematical, r.Id, RunLabel(r),
                $"Mean-variance, {r.SettingsSnapshot.LookbackDays}-day lookback, max weight {r.SettingsSnapshot.MaxWeight:P0}", r.CreatedAtUtc, r.Symbols.Count, r.Currency, r.UsesSimulatedData,
                r.Symbols, r.SettingsSnapshot.BenchmarkSymbol, (int)Math.Ceiling(r.SettingsSnapshot.LookbackDays * 1.5) + 10))
            .Concat(experiments.Select(e => new PortfolioStrategyDto(OptimizationKind.MachineLearning, e.Id, e.Name,
                $"{MlExperimentService.Label(Enum.Parse<ModelFamily>(e.ModelFamily))} on {MlExperimentService.TargetLabel(Enum.Parse<PortfolioOptimizer.MachineLearning.Labels.PredictionTarget>(e.Target))} ({e.HorizonDays}d)",
                e.CreatedAtUtc, e.Symbols.Count, e.Currency, e.UsesSimulatedData, e.Symbols, e.BenchmarkSymbol, e.HistoryEnd.DayNumber - e.HistoryStart.DayNumber)))
            .OrderByDescending(s => s.CreatedAtUtc).ToList();
    }

    /// <summary>Frozen settings of a successful optimization of <paramref name="portfolioId"/> (409 when it cannot be simulated).</summary>
    public async Task<StrategySnapshotDto> SnapshotAsync(Guid portfolioId, StrategyRef strategy, CancellationToken ct)
    {
        await portfolios.EnsureExistsAsync(portfolioId, ct);
        if (strategy.Kind == OptimizationKind.Mathematical)
        {
            var run = await db.OptimizationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == strategy.Id && r.PortfolioId == portfolioId, ct)
                      ?? throw new NotFoundException("Optimization run", strategy.Id);
            if (run.Status != OptimizationRunStatus.Succeeded) throw new ConflictException("The optimization run did not succeed; choose a successful run.");
            return new StrategySnapshotDto(OptimizationKind.Mathematical, run.Id, RunLabel(run), run.CreatedAtUtc, run.Currency, [.. run.Symbols],
                run.SettingsSnapshot.BenchmarkSymbol, MathSettings: run.SettingsSnapshot.ToDto() with { Universe = [.. run.Symbols] }, MathObjective: run.Objective.ToString());
        }

        var experiment = await db.MlExperiments.AsNoTracking().Include(e => e.ModelVersions)
                             .SingleOrDefaultAsync(e => e.Id == strategy.Id && e.PortfolioId == portfolioId, ct)
                         ?? throw new NotFoundException("ML experiment", strategy.Id);
        if (experiment.Status != JobStatus.Succeeded) throw new ConflictException("The ML experiment has not completed successfully.");
        var evaluation = experiment.ModelVersions.Where(v => v.Role == "Evaluation").MaxBy(v => v.Version)
                         ?? throw new ConflictException("The ML experiment has no evaluation model version.");
        if (evaluation.FeatureSetVersion != FeatureCatalog.Version)
            throw new ConflictException($"The ML experiment used feature set {evaluation.FeatureSetVersion}; the current definitions are {FeatureCatalog.Version}. Run a new experiment.");
        return new StrategySnapshotDto(OptimizationKind.MachineLearning, experiment.Id, experiment.Name, experiment.CreatedAtUtc, experiment.Currency,
            [.. experiment.Symbols], experiment.BenchmarkSymbol,
            MlConfiguration: JsonSerializer.Deserialize<CreateMlExperimentRequest>(experiment.ConfigJson, MlJson.Options)!,
            MlHyperparameters: JsonSerializer.Deserialize<Dictionary<string, double>>(evaluation.HyperparametersJson, MlJson.Options) ?? [],
            MlFeatures: evaluation.FeatureNames, MlValidationEnd: experiment.ValidationEnd, MlTestStart: experiment.TestStart,
            MlFeatureSetVersion: evaluation.FeatureSetVersion, MlModelVersionId: evaluation.Id);
    }

    /// <summary>Short enough for chart legends; the creation time (UTC) tells runs with the same objective apart.</summary>
    public static string RunLabel(OptimizationRun r) => $"{Humanize(r.Objective.ToString())} · {r.CreatedAtUtc:yyyy-MM-dd HH:mm}";

    /// <summary>"MaximumSharpe" → "Maximum Sharpe".</summary>
    public static string Humanize(string name) => CamelBoundary().Replace(name, " $1");

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex CamelBoundary();
}
