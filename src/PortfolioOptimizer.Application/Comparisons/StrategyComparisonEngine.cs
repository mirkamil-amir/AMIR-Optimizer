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

/// <summary>Frozen inputs of one comparison run.</summary>
public sealed record ComparisonInputs(
    MarketPanel Panel,
    DateOnly Start,
    DateOnly End,
    double InitialCapital,
    double TransactionCostBps,
    RebalanceFrequency Frequency,
    double RiskFreeRate,
    BacktestSettings MathSettings,
    string MathDescription,
    ExperimentConfiguration MlConfiguration,
    Hyperparameters MlHyperparameters,
    IReadOnlyList<string> MlFeatures,
    string MlDescription,
    bool IncludeHybrid);

/// <summary>
/// Evaluates every strategy with <see cref="PortfolioSimulator"/> on one price grid: same dates, universe, initial
/// capital, transaction costs, rebalancing dates, next-close execution, cash at 0% and missing-price handling. The
/// classical strategy re-optimizes with its stored settings on a trailing window at each rebalance; the ML strategy
/// retrains walk-forward with its stored hyperparameters on labels realized before each rebalance. Neither sees data
/// dated after the signal date.
/// </summary>
public sealed class StrategyComparisonEngine(IPortfolioOptimizer optimizer, AnalyticsConventions conventions)
{
    public const int RollingSharpeWindow = 63;

    public const string Methodology =
        "All strategies are simulated by the same engine: signals at the close of each rebalance date using data dated on or before it, " +
        "orders filled at the next trading day's close net of the same proportional transaction cost, holdings valued at adjusted closes " +
        "(dividends reinvested), uninvested cash earns 0%, fractional units, identical initial capital, calendar, universe and benchmark. " +
        "The mathematical strategy re-estimates and re-optimizes with the optimization run's stored settings at every rebalance; the ML " +
        "strategy retrains on labels realized before each rebalance with the experiment's selected hyperparameters (no information from " +
        "the evaluation period is used for training at earlier dates). Equal weight rebalances to 1/N of tradable stocks on the same dates; " +
        "the benchmark is bought and held. Results are hypothetical, not a forecast, and subject to survivorship bias in today's universe.";

    public StrategyComparisonResultDto Run(ComparisonInputs input, IReadOnlyList<string> universeNotes, IReadOnlyList<string> fairnessIssues,
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
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

        var mathSettings = input.MathSettings with { Start = input.Start, End = input.End, Frequency = input.Frequency, TransactionCostBps = input.TransactionCostBps };
        var engine = new BacktestEngine(optimizer, conventions);

        progress?.Report((10, "Simulating the mathematical strategy"));
        var mathWarnings = new List<string>();
        var math = PortfolioSimulator.Run(grid, signals, input.InitialCapital, input.TransactionCostBps, engine.CreateDecisionRule(grid, mathSettings), mathWarnings, ct);

        progress?.Report((35, "Simulating the ML strategy (walk-forward retraining)"));
        var features = new FeatureEngine(panel, input.MlFeatures.Select(FeatureCatalog.Get).ToList());
        var dataset = Dataset.Build(panel, features, input.MlConfiguration.Target, input.MlConfiguration.HorizonDays, input.MlConfiguration.SampleStepDays);
        var trainer = ModelRegistry.Get(input.MlConfiguration.Family);
        var mlStrategy = new MlStrategy(dataset, trainer, input.MlHyperparameters, input.MlConfiguration.Portfolio, input.MlConfiguration.WalkForward, input.MlConfiguration.Seed);
        var mlWarnings = new List<string>();
        var ml = PortfolioSimulator.Run(grid, signals, input.InitialCapital, input.TransactionCostBps, mlStrategy.Decide, mlWarnings, ct);

        progress?.Report((65, "Simulating the baselines"));
        var equal = PortfolioSimulator.Run(grid, signals, input.InitialCapital, input.TransactionCostBps,
            EqualWeightRule.Create(dataset, mathSettings.Constraints.CashReserve), null, ct);

        var strategies = new List<(string Key, string Name, StrategyKind Kind, string Description, IReadOnlyList<double> Values, IReadOnlyList<RebalanceEvent> Events)>
        {
            ("math", "Mathematical optimization", StrategyKind.Mathematical, input.MathDescription, math.Values, math.Events),
            ("ml", "Machine-learning strategy", StrategyKind.MachineLearning, input.MlDescription, ml.Values, ml.Events),
        };

        if (input.IncludeHybrid)
        {
            progress?.Report((75, "Simulating the hybrid strategy"));
            var screen = new MlStrategy(dataset, trainer, input.MlHyperparameters, input.MlConfiguration.Portfolio, input.MlConfiguration.WalkForward, input.MlConfiguration.Seed);
            ISet<string>? Selected(SignalContext ctx)
            {
                var decision = screen.Decide(ctx);
                return decision.Weights is null ? null : ctx.Symbols.Where((_, i) => decision.Weights[i] > 0).ToHashSet();
            }
            var hybrid = PortfolioSimulator.Run(grid, signals, input.InitialCapital, input.TransactionCostBps,
                engine.CreateDecisionRule(grid, mathSettings with { Constraints = mathSettings.Constraints with { MaxHoldings = null } }, Selected), null, ct);
            strategies.Add(("hybrid", "Hybrid: ML screen + mean-variance", StrategyKind.Hybrid,
                $"Experimental third strategy: the ML model selects its top {input.MlConfiguration.Portfolio.TopK} stocks; the mean-variance optimizer with the run's settings weights them. Not the ML strategy and not the classical strategy.",
                hybrid.Values, hybrid.Events));
        }

        strategies.Add(("equal", "Equal weight", StrategyKind.EqualWeight, "1/N of all tradable stocks in the universe, rebalanced on the same dates with the same costs.", equal.Values, equal.Events));
        strategies.Add(("benchmark", $"Benchmark ({panel.BenchmarkSymbol})", StrategyKind.Benchmark, "Buy and hold the benchmark from the first evaluation date.", benchmark, []));

        progress?.Report((85, "Computing metrics"));
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

        var overlaps = new List<HoldingsOverlapDto> { Overlap(dtos[0], dtos[1], math.Events, ml.Events) };

        // Fairness: issues detected before the run (period/hyperparameters/universe) plus data coverage found here.
        var issues = new List<string>(fairnessIssues);
        foreach (var (s, events) in new[] { (dtos[0], math.Events), (dtos[1], ml.Events) })
        {
            var held = events.Count(e => e.TargetWeights.Count > 0);
            if (events.Count > 0 && held < events.Count / 2.0)
                issues.Add($"{s.Name} was invested at fewer than half of the rebalance dates ({held} of {events.Count}); its record mostly reflects cash.");
        }
        warnings.AddRange(mathWarnings.Distinct().Take(10));
        warnings.AddRange(mlWarnings.Distinct().Take(10));
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
