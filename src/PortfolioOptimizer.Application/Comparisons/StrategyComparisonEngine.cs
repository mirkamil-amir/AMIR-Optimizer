using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.MachineLearning.Backtesting;
using PortfolioOptimizer.MachineLearning.Data;
using PortfolioOptimizer.MachineLearning.Features;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.MachineLearning.Training;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Backtesting;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Comparisons;

/// <summary>One strategy to simulate: mean-variance (<see cref="Math"/>) or ML walk-forward (<see cref="Ml"/>).</summary>
public sealed record ComparisonStrategyInput(
    string Key,
    string Name,
    StrategyKind Kind,
    string Description,
    BacktestSettings? Math = null,
    ExperimentConfiguration? Ml = null,
    Hyperparameters? MlHyperparameters = null,
    IReadOnlyList<string>? MlFeatures = null);

/// <summary>Frozen inputs of one comparison run.</summary>
public sealed record ComparisonInputs(
    MarketPanel Panel,
    DateOnly Start,
    DateOnly End,
    double InitialCapital,
    double TransactionCostBps,
    RebalanceFrequency Frequency,
    double RiskFreeRate,
    IReadOnlyList<ComparisonStrategyInput> Strategies,
    bool IncludeHybrid);

/// <summary>Builds the walk-forward ML strategy of a stored configuration on a panel (shared by comparisons and backtests).</summary>
public static class MlStrategyFactory
{
    public static (MlStrategy Strategy, Dataset Dataset) Create(MarketPanel panel, ExperimentConfiguration config, Hyperparameters hyperparameters, IReadOnlyList<string> features)
    {
        var engine = new FeatureEngine(panel, features.Select(FeatureCatalog.Get).ToList());
        var dataset = Dataset.Build(panel, engine, config.Target, config.HorizonDays, config.SampleStepDays);
        var strategy = new MlStrategy(dataset, ModelRegistry.Get(config.Family), hyperparameters, config.Portfolio, config.WalkForward, config.Seed);
        return (strategy, dataset);
    }
}

/// <summary>
/// Evaluates the chosen strategies – any two optimizations of a portfolio: mathematical vs mathematical, ML vs ML or
/// mathematical vs ML – plus the baselines with <see cref="PortfolioSimulator"/> on one price grid: same dates, universe,
/// initial capital, transaction costs, rebalancing dates, next-close execution, cash at 0% and missing-price handling. A
/// mathematical strategy re-optimizes with its stored settings on a trailing window at each rebalance; an ML strategy
/// retrains walk-forward with its stored hyperparameters on labels realized before each rebalance. None sees data dated
/// after the signal date.
/// </summary>
public sealed class StrategyComparisonEngine(IPortfolioOptimizer optimizer, AnalyticsConventions conventions)
{
    public const int RollingSharpeWindow = 63;

    public const string Methodology =
        "All strategies are simulated by the same engine: signals at the close of each rebalance date using data dated on or before it, " +
        "orders filled at the next trading day's close net of the same proportional transaction cost, holdings valued at adjusted closes " +
        "(dividends reinvested), uninvested cash earns 0%, fractional units, identical initial capital, calendar, universe and benchmark. " +
        "A mathematical strategy re-estimates and re-optimizes with its optimization run's stored settings at every rebalance; an ML " +
        "strategy retrains on labels realized before each rebalance with its experiment's selected hyperparameters (no information from " +
        "the evaluation period is used for training at earlier dates). Equal weight rebalances to 1/N of tradable stocks on the same dates; " +
        "the benchmark is bought and held. Results are hypothetical, not a forecast, and subject to survivorship bias in today's universe.";

    public StrategyComparisonResultDto Run(ComparisonInputs input, IReadOnlyList<string> universeNotes, IReadOnlyList<string> fairnessIssues,
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
        if (input.Strategies.Count is < 1 or > 2) throw new ArgumentException("Compare one or two strategies.");
        var panel = input.Panel;
        var warnings = new List<string>(panel.Notes);
        var series = new Dictionary<string, IReadOnlyList<PricePoint>>();
        for (var a = 0; a < panel.AssetCount; a++)
            series[panel.Symbols[a]] = Enumerable.Range(0, panel.Length).Where(t => !double.IsNaN(panel.Prices[a][t]))
                .Select(t => new PricePoint(panel.Dates[t], panel.Prices[a][t])).ToList();
        var grid = PriceGrid.Build(series, input.Start, input.End);
        var signals = grid.SignalIndices(input.Frequency);
        var sectors = panel.Symbols.Select((s, i) => (s, sector: panel.Sectors[i] ?? "Unclassified")).ToDictionary(x => x.s, x => x.sector);

        // Benchmark buy-and-hold on the shared calendar.
        var b0 = panel.IndexOnOrBefore(grid.Calendar[0]);
        var benchmark = grid.Calendar.Select(d => input.InitialCapital * panel.Benchmark[panel.IndexOnOrBefore(d)] / panel.Benchmark[b0]).ToArray();
        var engine = new BacktestEngine(optimizer, conventions);

        var simulated = new List<(ComparisonStrategyInput Spec, IReadOnlyList<double> Values, IReadOnlyList<RebalanceEvent> Events)>();
        Dataset? firstDataset = null;
        for (var k = 0; k < input.Strategies.Count; k++)
        {
            var spec = input.Strategies[k];
            progress?.Report((10 + 30 * k, $"Simulating {spec.Name}"));
            var strategyWarnings = new List<string>();
            Func<SignalContext, PortfolioDecision> rule;
            if (spec.Math is { } math)
                rule = engine.CreateDecisionRule(grid, MathSettings(math, input));
            else
            {
                var (ml, dataset) = MlStrategyFactory.Create(panel, spec.Ml!, spec.MlHyperparameters!, spec.MlFeatures!);
                firstDataset ??= dataset;
                rule = ml.Decide;
            }
            var result = PortfolioSimulator.Run(grid, signals, input.InitialCapital, input.TransactionCostBps, rule, strategyWarnings, ct);
            warnings.AddRange(strategyWarnings.Distinct().Take(10).Select(w => $"{spec.Name}: {w}"));
            simulated.Add((spec, result.Values, result.Events));
        }

        progress?.Report((70, "Simulating the baselines"));
        var cashReserve = input.Strategies.FirstOrDefault(s => s.Math is not null)?.Math!.Constraints.CashReserve ?? 0;
        var equalRule = firstDataset is not null
            ? EqualWeightRule.Create(firstDataset, cashReserve)
            : EqualWeightRule.Create(panel, new FeatureEngine(panel, FeatureCatalog.All), cashReserve);
        var equal = PortfolioSimulator.Run(grid, signals, input.InitialCapital, input.TransactionCostBps, equalRule, null, ct);

        var strategies = simulated.Select(s => (s.Spec.Key, s.Spec.Name, s.Spec.Kind, s.Spec.Description, s.Values, s.Events)).ToList();

        // Hybrid only when one strategy is mathematical and the other ML: the ML model screens, mean-variance weights.
        var mathSpec = input.Strategies.FirstOrDefault(s => s.Math is not null);
        var mlSpec = input.Strategies.FirstOrDefault(s => s.Ml is not null);
        if (input.IncludeHybrid && mathSpec is not null && mlSpec is not null)
        {
            progress?.Report((80, "Simulating the hybrid strategy"));
            var (screen, _) = MlStrategyFactory.Create(panel, mlSpec.Ml!, mlSpec.MlHyperparameters!, mlSpec.MlFeatures!);
            ISet<string>? Selected(SignalContext ctx)
            {
                var decision = screen.Decide(ctx);
                return decision.Weights is null ? null : ctx.Symbols.Where((_, i) => decision.Weights[i] > 0).ToHashSet();
            }
            var settings = MathSettings(mathSpec.Math!, input);
            var hybrid = PortfolioSimulator.Run(grid, signals, input.InitialCapital, input.TransactionCostBps,
                engine.CreateDecisionRule(grid, settings with { Constraints = settings.Constraints with { MaxHoldings = null } }, Selected), null, ct);
            strategies.Add(("hybrid", "Hybrid: ML screen + mean-variance", StrategyKind.Hybrid,
                $"Experimental extra strategy: {mlSpec.Name} selects its top {mlSpec.Ml!.Portfolio.TopK} stocks; the mean-variance optimizer with {mathSpec.Name}'s settings weights them. Neither of the two strategies.",
                hybrid.Values, hybrid.Events));
        }

        strategies.Add(("equal", "Equal weight", StrategyKind.EqualWeight, "1/N of all tradable stocks in the universe, rebalanced on the same dates with the same costs.", equal.Values, equal.Events));
        strategies.Add(("benchmark", $"Benchmark ({panel.BenchmarkSymbol})", StrategyKind.Benchmark, "Buy and hold the benchmark from the first evaluation date.", benchmark, []));

        progress?.Report((88, "Computing metrics"));
        var dtos = strategies.Select(s => new ComparisonStrategyDto(
            s.Key, s.Name, s.Kind, s.Description, s.Values,
            PerformanceMetrics.DrawdownSeries(s.Values),
            StrategyMetrics.RollingSharpe(s.Values, RollingSharpeWindow, input.RiskFreeRate, conventions),
            StrategyMetrics.Compute(s.Name, s.Values, s.Events, input.RiskFreeRate, benchmark, conventions),
            StrategyMetrics.MonthlyReturns(grid.Calendar, s.Values).Select(m => new MonthlyReturnPoint(s.Key, m.Year, m.Month, m.Return)).ToList(),
            s.Events.LastOrDefault(e => e.TargetWeights.Count > 0)?.TargetWeights ?? new Dictionary<string, double>(),
            AverageWeights(s.Events),
            SectorExposure(s.Events, sectors),
            s.Events.Select(e => new ComparisonRebalanceDto(e.SignalDate, e.ExecutionDate, e.Turnover, e.TransactionCosts, e.TargetWeights.Count, e.OptimizationSucceeded, e.Message)).ToList()))
            .ToList();

        var overlaps = simulated.Count == 2
            ? new List<HoldingsOverlapDto> { Overlap(dtos[0], dtos[1], simulated[0].Events, simulated[1].Events) }
            : [];

        // Fairness: issues detected before the run (period/hyperparameters/universe) plus data coverage found here.
        var issues = new List<string>(fairnessIssues);
        foreach (var (s, events) in simulated.Select((x, i) => (dtos[i], x.Events)))
        {
            var held = events.Count(e => e.TargetWeights.Count > 0);
            if (events.Count > 0 && held < events.Count / 2.0)
                issues.Add($"{s.Name} was invested at fewer than half of the rebalance dates ({held} of {events.Count}); its record mostly reflects cash.");
        }
        var shared = new[]
        {
            $"Evaluation period {grid.Calendar[0]:yyyy-MM-dd} – {grid.Calendar[^1]:yyyy-MM-dd} ({grid.Calendar.Length} trading days) for every strategy",
            $"Initial capital {input.InitialCapital:N0}, transaction cost {input.TransactionCostBps} bps of traded value",
            $"{input.Frequency} rebalancing on identical signal dates; execution at the next close",
            $"Universe of {panel.AssetCount} stocks; benchmark {panel.BenchmarkSymbol}; cash earns 0%",
            "Adjusted closes (total return) from one data source",
        };
        var fair = issues.Count == 0;
        var ranking = fair
            ? dtos.Where(d => d.Kind != StrategyKind.Benchmark && d.Performance.SharpeRatio.IsAvailable)
                .OrderByDescending(d => d.Performance.SharpeRatio.Value).Select(d => d.Name).ToList()
            : null;

        return new StrategyComparisonResultDto(grid.Calendar, dtos, overlaps, panel.Symbols, universeNotes, new FairnessAssessment(fair, shared, issues),
            ranking, RollingSharpeWindow, Methodology, warnings.Distinct().Take(40).ToList());
    }

    private static BacktestSettings MathSettings(BacktestSettings math, ComparisonInputs input) =>
        math with { Start = input.Start, End = input.End, Frequency = input.Frequency, TransactionCostBps = input.TransactionCostBps };

    private static IReadOnlyDictionary<string, double> AverageWeights(IReadOnlyList<RebalanceEvent> events)
    {
        var invested = events.Where(e => e.TargetWeights.Count > 0).ToList();
        if (invested.Count == 0) return new Dictionary<string, double>();
        return invested.SelectMany(e => e.TargetWeights).GroupBy(kv => kv.Key)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value) / invested.Count);
    }

    private static IReadOnlyDictionary<string, double> SectorExposure(IReadOnlyList<RebalanceEvent> events, IReadOnlyDictionary<string, string> sectors) =>
        AverageWeights(events).GroupBy(kv => sectors.GetValueOrDefault(kv.Key, "Unclassified")).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

    private static HoldingsOverlapDto Overlap(ComparisonStrategyDto a, ComparisonStrategyDto b, IReadOnlyList<RebalanceEvent> ea, IReadOnlyList<RebalanceEvent> eb)
    {
        var sa = a.FinalWeights.Keys.ToHashSet();
        var sb = b.FinalWeights.Keys.ToHashSet();
        var union = sa.Union(sb).Count();
        var weightOverlap = sa.Intersect(sb).Sum(k => Math.Min(a.FinalWeights[k], b.FinalWeights[k]));
        var byDate = eb.ToDictionary(e => e.SignalDate, e => e.TargetWeights);
        var perDate = ea.Where(e => byDate.ContainsKey(e.SignalDate) && e.TargetWeights.Count > 0 && byDate[e.SignalDate].Count > 0)
            .Select(e => e.TargetWeights.Keys.Intersect(byDate[e.SignalDate].Keys).Sum(k => Math.Min(e.TargetWeights[k], byDate[e.SignalDate][k])))
            .ToList();
        return new HoldingsOverlapDto(a.Name, b.Name,
            sa.Intersect(sb).OrderBy(x => x).ToList(), sa.Except(sb).OrderBy(x => x).ToList(), sb.Except(sa).OrderBy(x => x).ToList(),
            union == 0 ? 0 : (double)sa.Intersect(sb).Count() / union, weightOverlap, perDate.Count == 0 ? 0 : perDate.Average());
    }
}
