using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Analytics;

/// <summary>
/// Portfolio Reality Check of a saved optimization run: concentration, risk contributions, correlation (also on stress
/// days), in-sample and – when later prices exist – out-of-sample behaviour, sensitivity of the weights to the
/// estimation assumptions, and implementation costs. Every finding comes from a calculation on stored data and is
/// labelled fact / estimate / interpretation / caution. Owner-scoped (another user's run is 404). No recommendation.
/// </summary>
public sealed class RealityCheckService(
    IApplicationDbContext db,
    OptimizationService optimizations,
    PriceQueryService prices,
    Fx.FxService fx,
    IPortfolioOptimizer optimizer,
    AnalyticsConventions conventions,
    TimeProvider clock)
{
    public const string Disclaimer =
        "Educational analysis of historical data and model estimates. It does not predict future returns, does not make a portfolio safe, " +
        "and is not a recommendation to buy or sell. Not investment advice.";

    public const string Methodology =
        "Weights are the saved run's weights. Risk uses the run's covariance method on the run's own estimation window (annualized, " +
        "252 trading days). Risk share = wᵢ(Σw)ᵢ / wᵀΣw; diversification ratio = Σwᵢσᵢ / σₚ; effective holdings = 1/Σwᵢ² of the invested weights. " +
        "Historical figures hold the weights fixed (daily rebalanced, cash earns nothing). Stress days = the worst 10% of days of an " +
        "equal-weight basket of the holdings. Sensitivity re-solves the same objective and constraints with one assumption changed and " +
        "reports one-way turnover ½Σ|Δw| against the saved weights; scenario risk and return are measured with the run's estimates.";

    // Thresholds for interpretations (documented in docs/portfolio-reality-check.md).
    private const double LargePosition = 0.25;
    private const double ConcentratedEffectiveHoldings = 5;
    private const double LargeSector = 0.40;
    private const double DominantRiskShare = 0.35;
    private const double HighCorrelation = 0.6;
    private const double StressCorrelationRise = 0.10;
    private const double FragileTurnover = 0.30;
    private const double StableTurnover = 0.10;

    public async Task<RealityCheckDto> GetAsync(Guid runId, CancellationToken ct)
    {
        var run = await optimizations.LoadRunAsync(runId, ct); // owner check
        return await AnalyzeAsync(run, ct);
    }

    /// <summary>
    /// Reality check of a run record – saved (via <see cref="GetAsync"/>, owner-checked) or an unsaved research hypothesis
    /// produced for the current user by <see cref="OptimizationService.EvaluateAsync"/>. Callers are responsible for ownership.
    /// </summary>
    public async Task<RealityCheckDto> AnalyzeAsync(OptimizationRun run, CancellationToken ct)
    {
        if (run.Status != OptimizationRunStatus.Succeeded || run.Weights.Count == 0)
            throw new ConflictException("Only successful optimization runs can be checked; this run has no weights.");
        if (run.PeriodStart is not { } start || run.PeriodEnd is not { } end)
            throw new ConflictException("The run does not record its estimation window.");

        var settings = run.SettingsSnapshot;
        var symbols = run.Weights.Select(w => w.Symbol).ToList();
        var saved = run.Weights.Select(w => w.Weight).ToArray();
        var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);
        var sectors = assets.Values.Where(a => a.Sector is not null).ToDictionary(a => a.Symbol, a => a.Sector!, StringComparer.OrdinalIgnoreCase);
        // Everything is analysed in the run's own currency (prices converted exactly as in the run).
        var currency = run.Currency;

        // The run's own window, from the run's own price source.
        var series = (await fx.GetSeriesInCurrencyAsync(symbols, run.PriceSource, start, end, currency, ct)).PriceSeries();
        var missing = symbols.Where(s => !series.TryGetValue(s, out var ps) || ps.Points.Count < 2).ToList();
        if (missing.Count > 0)
            throw new ConflictException($"Stored prices for {string.Join(", ", missing)} in the run's window are no longer available from {run.PriceSource}.");
        var matrix = ReturnMatrix.Build(series.ToDictionary(kv => kv.Key, kv => kv.Value.Points, StringComparer.OrdinalIgnoreCase), symbols);
        if (matrix.Observations < OptimizationService.MinimumObservations)
            throw new ConflictException($"Only {matrix.Observations} aligned returns remain in the run's window (minimum {OptimizationService.MinimumObservations}).");
        var dataChanged = OptimizationService.Hash(series, symbols, matrix.PriceDates) != run.DataSnapshotHash;
        var est = CovarianceEstimator.Estimate(matrix.Returns, settings.CovarianceMethod.ToOptimization(), conventions);

        var concentration = DiversificationMath.Concentration(symbols, saved, sectors);
        var risk = DiversificationMath.Risk(symbols, saved, est.Covariance);
        var heldColumns = Enumerable.Range(0, symbols.Count).Where(i => saved[i] >= DiversificationMath.HoldingThreshold).ToList();
        var stress = DiversificationMath.CorrelationUnderStress(matrix.Returns, heldColumns);

        // In-sample: the same window that chose the weights.
        var benchmark = settings.BenchmarkSymbol;
        var inSample = await HistoryAsync("In-sample (the estimation window that chose these weights)", matrix, saved, benchmark, run.PriceSource, currency, ct);

        // Out-of-sample: prices after the window, if any were stored since.
        var (outOfSample, oosReason) = await OutOfSampleAsync(symbols, saved, end, benchmark, run.PriceSource, currency, ct);

        var scenarios = Sensitivity(run, settings, symbols, saved, matrix, est, sectors);
        var costs = Costs(settings, saved, scenarios);
        var findings = Findings(run, concentration, risk, stress, inSample, outOfSample, scenarios, costs, dataChanged);

        return new RealityCheckDto(run.Id, run.Objective.ToString(), run.CreatedAtUtc, currency, matrix.PriceDates[0], matrix.PriceDates[^1],
            matrix.Observations, run.PriceSource, run.UsesSimulatedData, dataChanged,
            new ConcentrationDto(concentration.Holdings, concentration.LargestWeight, concentration.LargestSymbol, concentration.TopThreeWeight,
                concentration.Hhi, concentration.EffectiveHoldings, concentration.Sectors.Select(s => new SectorWeightDto(s.Sector, s.Weight, s.Holdings)).ToList()),
            new RiskBreakdownDto(risk.Volatility, risk.DiversificationRatio, risk.EffectiveRiskContributors, risk.AverageCorrelation,
                risk.HighestCorrelation is { } h ? new CorrelationPairDto(h.A, h.B, h.Correlation) : null, stress.StressDays, stress.StressDayCount,
                symbols.Select((s, i) => new RiskShareDto(s, saved[i], risk.RiskShares[i], est.Volatilities[i]))
                    .Where(r => r.Weight >= DiversificationMath.HoldingThreshold).OrderByDescending(r => r.RiskShare).ToList()),
            inSample, outOfSample, oosReason, scenarios, costs, findings, NextSteps(run, scenarios, outOfSample), Limitations(run, dataChanged),
            Methodology, Disclaimer);
    }

    // ------------------------------------------------------------------ history

    private async Task<HistoricalCheckDto> HistoryAsync(string label, ReturnMatrix matrix, double[] weights, string benchmark, string source, string currency, CancellationToken ct)
    {
        var daily = DiversificationMath.PortfolioReturns(matrix.Returns, weights);
        var h = DiversificationMath.History(daily, conventions);
        double? benchCum = null;
        MetricDto? benchDd = null, beta = null;
        var b = (await fx.GetSeriesInCurrencyAsync([benchmark], source, matrix.PriceDates[0], matrix.PriceDates[^1], currency, ct)).PriceSeries();
        if (b.TryGetValue(benchmark, out var bs))
        {
            var byDate = bs.Points.ToDictionary(p => p.Date, p => p.Price);
            // Benchmark returns on exactly the portfolio's date grid; any missing date → no benchmark comparison.
            if (matrix.PriceDates.All(byDate.ContainsKey))
            {
                var bp = matrix.PriceDates.Select(d => byDate[d]).ToArray();
                var br = ReturnCalculator.SimpleReturns(bp);
                benchCum = bp[^1] / bp[0] - 1;
                benchDd = MetricDto.From(PerformanceMetrics.MaxDrawdown(bp));
                beta = MetricDto.From(PerformanceMetrics.Beta(daily, br));
            }
        }
        return new HistoricalCheckDto(label, matrix.PriceDates[0], matrix.PriceDates[^1], h.Days, h.CumulativeReturn,
            MetricDto.From(h.AnnualizedVolatility), MetricDto.From(h.MaxDrawdown), MetricDto.From(h.ValueAtRisk95), MetricDto.From(h.ExpectedShortfall95),
            h.WorstDay, h.Worst20DayReturn, benchCum is null ? null : benchmark, benchCum, benchDd, beta);
    }

    private async Task<(HistoricalCheckDto?, string?)> OutOfSampleAsync(List<string> symbols, double[] weights, DateOnly windowEnd, string benchmark, string source,
        string currency, CancellationToken ct)
    {
        var after = (await fx.GetSeriesInCurrencyAsync(symbols, source, windowEnd, null, currency, ct)).PriceSeries();
        if (symbols.Any(s => !after.TryGetValue(s, out var ps) || ps.Points.Count < 2))
            return (null, "No stored prices after the estimation window yet for every holding.");
        var matrix = ReturnMatrix.Build(after.ToDictionary(kv => kv.Key, kv => kv.Value.Points, StringComparer.OrdinalIgnoreCase), symbols);
        if (matrix.Observations < 20)
            return (null, $"Only {matrix.Observations} trading day(s) since the estimation window ended; at least 20 are needed for a meaningful comparison.");
        var label = $"Out-of-sample ({matrix.Observations} trading days after the window; weights unchanged, no costs)";
        return (await HistoryAsync(label, matrix, weights, benchmark, source, currency, ct), null);
    }

    // ------------------------------------------------------------------ sensitivity

    private List<SensitivityScenarioDto> Sensitivity(OptimizationRun run, PortfolioSettings settings, List<string> symbols, double[] saved, ReturnMatrix matrix,
        MomentEstimates est, Dictionary<string, string> sectors)
    {
        var constraints = settings.ToConstraints(sectors) with { MaxTurnover = null }; // no holdings context in a what-if
        var objective = settings.Objective.ToOptimization();
        var result = new List<SensitivityScenarioDto>();

        SensitivityScenarioDto Solve(string key, string name, string description, double[] mu, double[,] cov, OptimizationObjective? obj = null)
        {
            var r = optimizer.Optimize(new OptimizationInput(symbols, mu, cov, obj ?? objective, constraints, settings.RiskFreeRate));
            if (!r.Succeeded)
                return new SensitivityScenarioDto(key, name, description, r.Status.ToString(), r.Message, null, null, null, null, null, null, []);
            var w = r.Weights!;
            var turnover = PerformanceMetrics.Turnover(saved, w);
            var largest = Enumerable.Range(0, w.Length).MaxBy(i => Math.Abs(w[i] - saved[i]));
            return new SensitivityScenarioDto(key, name, description, "Optimal", r.Message, turnover, Math.Abs(w[largest] - saved[largest]), symbols[largest],
                w.Count(x => x >= DiversificationMath.HoldingThreshold),
                PortfolioMath.ExpectedReturn(w, est.ExpectedReturns), PortfolioMath.Volatility(w, est.Covariance),
                symbols.Select((s, i) => new ScenarioWeightDto(s, w[i], saved[i])).ToList());
        }

        var baseline = Solve("resolved", "Same assumptions, re-solved",
            "The run's objective, constraints and window solved again – shows solver tolerance and any change in the stored data.",
            est.ExpectedReturns, est.Covariance);
        result.Add(baseline);

        var otherMethod = settings.CovarianceMethod == CovarianceKind.LedoitWolf ? CovarianceMethod.Sample : CovarianceMethod.LedoitWolf;
        var alt = CovarianceEstimator.Estimate(matrix.Returns, otherMethod, conventions);
        result.Add(Solve("covariance", otherMethod == CovarianceMethod.Sample ? "Sample covariance (no shrinkage)" : "Ledoit–Wolf shrinkage covariance",
            "Same window, the other covariance estimator. Shrinkage pulls extreme correlations toward a common value.", alt.ExpectedReturns, alt.Covariance));

        var half = matrix.Observations / 2;
        foreach (var (key, name, from, count) in new[] { ("early", "Earlier half of the window only", 0, half), ("late", "Later half of the window only", half, matrix.Observations - half) })
        {
            if (count < OptimizationService.MinimumObservations)
            {
                result.Add(new SensitivityScenarioDto(key, name, "Re-estimates means and covariance from half of the window.", "Skipped",
                    $"Half the window has only {count} returns (minimum {OptimizationService.MinimumObservations}).", null, null, null, null, null, null, []));
                continue;
            }
            var sub = new double[count, symbols.Count];
            for (var t = 0; t < count; t++)
                for (var j = 0; j < symbols.Count; j++) sub[t, j] = matrix.Returns[from + t, j];
            var e = CovarianceEstimator.Estimate(sub, settings.CovarianceMethod.ToOptimization(), conventions);
            result.Add(Solve(key, name, $"Means and covariance from {count} returns ({(key == "early" ? "first" : "second")} half) – a shorter, different history.",
                e.ExpectedReturns, e.Covariance));
        }

        var avg = est.ExpectedReturns.Average();
        var shrunk = est.ExpectedReturns.Select(m => 0.5 * m + 0.5 * avg).ToArray();
        result.Add(Solve("returns", "Expected returns shrunk 50% toward their average",
            settings.Objective == ObjectiveKind.MinimumVariance
                ? "Minimum variance ignores expected returns, so this scenario should not change the weights."
                : "Historical means are noisy; halving their differences tests how much the weights depend on them.",
            shrunk, est.Covariance));

        result.Add(Solve("equal", "Equal weight (reference)", "Equal weights within the same constraints – no estimates used.",
            est.ExpectedReturns, est.Covariance, OptimizationObjective.EqualWeight));
        return result;
    }

    private static CostCheckDto Costs(PortfolioSettings settings, double[] saved, List<SensitivityScenarioDto> scenarios)
    {
        var rate = settings.TransactionCostBps / 10_000;
        var invested = saved.Sum();
        var worst = scenarios.Where(s => s.Key is not ("resolved" or "equal") && s.Turnover is not null).MaxBy(s => s.Turnover!.Value);
        // One-way turnover T means selling T and buying T of portfolio value: proportional cost 2·T·rate.
        return new CostCheckDto(settings.TransactionCostBps, invested * rate, worst?.Turnover,
            worst?.Turnover is { } t ? 2 * t * rate : null, worst?.Turnover is { } t3 ? 2 * t3 * rate * 3 : null,
            "Proportional costs as a fraction of portfolio value at the configured rate (minimum ticket fees and bid/ask spreads are not included).");
    }

    // ------------------------------------------------------------------ findings

    private static List<RealityFindingDto> Findings(OptimizationRun run, ConcentrationProfile c, RiskProfile r, StressCorrelation stress,
        HistoricalCheckDto inSample, HistoricalCheckDto? oos, List<SensitivityScenarioDto> scenarios, CostCheckDto costs, bool dataChanged)
    {
        var f = new List<RealityFindingDto>();
        void Add(string kind, string topic, string text, string? basis = null) => f.Add(new RealityFindingDto(kind, topic, text, basis));

        Add("Fact", "Concentration",
            $"{c.Holdings} positions; the largest is {c.LargestSymbol} at {c.LargestWeight:0.0%}, the top three hold {c.TopThreeWeight:0.0%}. " +
            $"Effective number of holdings: {c.EffectiveHoldings:F1}.", "weights of the saved run");
        if (c.LargestWeight >= LargePosition)
            Add("Interpretation", "Concentration", $"{c.LargestSymbol} alone is at least a quarter of the portfolio; a large move in that single stock moves the whole portfolio.",
                $"largest weight ≥ {LargePosition:0%}");
        if (c.EffectiveHoldings < ConcentratedEffectiveHoldings)
            Add("Interpretation", "Concentration", $"The weights behave like about {c.EffectiveHoldings:F1} equally weighted positions – concentrated, whatever the number of names.",
                $"effective holdings < {ConcentratedEffectiveHoldings}");
        if (c.Sectors.FirstOrDefault() is { } top && top.Weight >= LargeSector)
            Add("Interpretation", "Sectors", $"{top.Sector} makes up {top.Weight:0.0%}; sector-wide news would affect most of the portfolio.", $"largest sector ≥ {LargeSector:0%}");

        Add("Estimate", "Risk", $"Estimated volatility {r.Volatility:0.0%} per year; diversification ratio {r.DiversificationRatio:F2} " +
            "(1 means no benefit from combining the stocks).", "covariance estimate of the run's window");
        var topRisk = Enumerable.Range(0, r.RiskShares.Length).MaxBy(i => r.RiskShares[i]);
        var weights = run.Weights.Select(w => w.Weight).ToArray();
        if (r.RiskShares[topRisk] >= DominantRiskShare)
            Add("Interpretation", "Risk", $"{run.Weights[topRisk].Symbol} contributes {r.RiskShares[topRisk]:0%} of the estimated risk with {weights[topRisk]:0%} of the weight: " +
                "risk is more concentrated than the weights suggest.", $"risk share ≥ {DominantRiskShare:0%}");
        if (r.AverageCorrelation is { } ac)
        {
            Add("Fact", "Correlation", $"Average pairwise correlation of the holdings: {ac:F2}" +
                (r.HighestCorrelation is { } h ? $"; highest {h.A}/{h.B} at {h.Correlation:F2}." : "."), "correlation matrix of the window");
            if (ac >= HighCorrelation)
                Add("Interpretation", "Correlation", "The holdings tend to move together, so spreading money across them reduces risk less than their number suggests.",
                    $"average correlation ≥ {HighCorrelation}");
        }
        if (stress.AllDays is { } all && stress.StressDays is { } sd)
        {
            Add("Fact", "Stress", $"On the {stress.StressDayCount} worst days of the window the average correlation was {sd:F2} (all days: {all:F2}).",
                "worst 10% of days of an equal-weight basket");
            if (sd - all >= StressCorrelationRise)
                Add("Caution", "Stress", "Correlations were higher on bad days: diversification helped least when it was needed most. Historical correlations can rise further in a crisis.",
                    $"stress correlation exceeds the average by ≥ {StressCorrelationRise}");
        }

        Add("Fact", "History", $"Over the estimation window the weights would have returned {inSample.CumulativeReturn:0.0%} with a maximum drawdown of " +
            $"{(inSample.MaxDrawdown.Value is { } dd ? dd.ToString("0.0%") : "n/a")}" +
            (inSample.BenchmarkCumulativeReturn is { } bc ? $" (benchmark {inSample.BenchmarkSymbol}: {bc:0.0%})." : "."), "fixed weights on the window's returns");
        Add("Caution", "History", "These in-sample figures use the same data that chose the weights, so they flatter the strategy. They are not evidence of future performance.");
        if (oos is not null)
            Add("Fact", "Out-of-sample", $"Since the window ended ({oos.Days} trading days) the weights returned {oos.CumulativeReturn:0.0%}" +
                (oos.BenchmarkCumulativeReturn is { } ob ? $" vs {ob:0.0%} for {oos.BenchmarkSymbol}" : "") + ". One short period says little about skill.",
                "prices stored after the window");

        var moving = scenarios.Where(s => s.Key is not ("resolved" or "equal") && s.Turnover is not null).ToList();
        if (moving.Count > 0)
        {
            var worst = moving.MaxBy(s => s.Turnover!.Value)!;
            Add("Fact", "Sensitivity", $"Changing one assumption moved up to {worst.Turnover:0%} of the portfolio ({worst.Name}; largest change {worst.LargestChangeSymbol} " +
                $"by {worst.LargestChange:0.0%}).", "one-way turnover against the saved weights");
            if (worst.Turnover >= FragileTurnover)
                Add("Interpretation", "Sensitivity", "The allocation is fragile: plausible changes to the inputs produce a substantially different portfolio. Treat the exact weights as one estimate among many.",
                    $"scenario turnover ≥ {FragileTurnover:0%}");
            else if (moving.All(s => s.Turnover <= StableTurnover))
                Add("Interpretation", "Sensitivity", "The weights changed little across the tested assumptions (this does not cover every source of estimation error).",
                    $"all scenario turnovers ≤ {StableTurnover:0%}");
        }
        if (costs.SwitchCost is { } sc)
            Add("Estimate", "Costs", $"Switching to the most different scenario would cost about {sc:0.00%} of the portfolio at {costs.CostBps:G} bps ({costs.SwitchCostAtTripleBps:0.00%} at three times that rate).",
                "2 × turnover × cost rate");

        if (run.UsesSimulatedData) Add("Caution", "Data", "The run used SIMULATED prices from the mock provider; none of these numbers describe real markets.");
        if (dataChanged) Add("Caution", "Data", "The stored prices for the run's window have changed since the run (re-downloaded or revised data); figures here use the current data.");
        return f;
    }

    private static List<string> NextSteps(OptimizationRun run, List<SensitivityScenarioDto> scenarios, HistoricalCheckDto? oos)
    {
        var steps = new List<string>
        {
            "Run a walk-forward backtest of this strategy with costs (Backtest) – it re-estimates the weights only from data available at each rebalancing date.",
            "Compare the strategy against equal weight and the benchmark on the same dates (Strategy comparison).",
        };
        if (scenarios.Any(s => s.Turnover >= FragileTurnover))
            steps.Add("Read about estimation error and covariance shrinkage in the strategy guide, and consider tighter weight limits to see how they change the result.");
        if (oos is null) steps.Add("Come back after more trading days to see how the saved weights behaved outside the window that chose them.");
        if (run.Objective != ObjectiveKind.MinimumVariance)
            steps.Add("Compare with a minimum-variance run on the same stocks: it does not depend on the noisy expected-return estimates.");
        return steps;
    }

    private static List<string> Limitations(OptimizationRun run, bool dataChanged)
    {
        var l = new List<string>
        {
            "Index membership is today's: stocks that left the index or were delisted are missing (survivorship bias).",
            "Volatility, correlation and risk shares are estimates from one historical window; they change over time and usually rise in crises.",
            "Value at risk and expected shortfall are one-day historical figures; losses beyond the observed history are possible.",
            "All prices are in the run's currency; stocks traded in other currencies were converted with the FX rate of each date, so their returns include currency movements (unhedged). Dividends are included only through adjusted closes.",
        };
        if (run.SettingsSnapshot.MaxTurnover is not null)
            l.Add("Sensitivity scenarios ignore the turnover limit, because they have no current holdings to measure turnover against.");
        if (dataChanged) l.Add("Results may differ slightly from the original run because the stored data changed.");
        return l;
    }
}
