using MathNet.Numerics.LinearAlgebra;
using PortfolioOptimizer.Optimization.Solvers;

namespace PortfolioOptimizer.Optimization.Portfolio;

public interface IPortfolioOptimizer
{
    OptimizationResult Optimize(OptimizationInput input);

    /// <summary>Minimum-variance portfolios across a grid of target returns, under the input's base constraints.</summary>
    IReadOnlyList<FrontierPoint> EfficientFrontier(OptimizationInput input, int points);
}

/// <summary>
/// Long-only mean-variance optimizer built on a convex QP solver.
/// <list type="bullet">
/// <item>Minimum variance and target return: exact convex QPs.</item>
/// <item>Maximum Sharpe: exact convex QP via the Cornuejols–Tütüncü homogenization (y = w/κ, (μ − rf)ᵀy = 1).</item>
/// <item>Maximum return for a volatility limit: bisection over target returns on the efficient frontier
/// (σ(r) of the frontier is monotone increasing above the minimum-variance return).</item>
/// <item>Max holdings / minimum position size: non-convex; handled by a documented heuristic that removes the smallest
/// position and re-solves until the limits are met. The result is feasible but not guaranteed globally optimal.</item>
/// </list>
/// </summary>
public sealed class MeanVarianceOptimizer(IQuadraticProgramSolver solver) : IPortfolioOptimizer
{
    private const double WeightTolerance = 1e-6;
    private const double ActiveWeight = 1e-6;
    private const double ZeroVariance = 1e-14;

    public MeanVarianceOptimizer() : this(new AdmmQpSolver()) { }

    public OptimizationResult Optimize(OptimizationInput input)
    {
        var diagnostics = new List<string>();
        var validation = ValidateInput(input);
        if (validation is not null) return Failure(input, OptimizationStatus.InvalidInput, validation, diagnostics);

        var n = input.Symbols.Count;
        var c = input.Constraints;
        var (lb, ub) = Bounds(input, diagnostics);

        var feasibility = PreCheckFeasibility(input, lb, ub);
        if (feasibility is not null) return Failure(input, OptimizationStatus.Infeasible, feasibility, diagnostics);

        var counters = new Counters();

        if (input.Objective == OptimizationObjective.EqualWeight)
            return Finish(input, EqualWeights(lb, ub, c.InvestedFraction), counters, diagnostics, "Equal-weight reference portfolio.");

        var forcedZero = new HashSet<int>(Enumerable.Range(0, n).Where(i => ub[i] <= 0));
        for (var attempt = 0; attempt <= n; attempt++)
        {
            var (lbA, ubA) = (lb.ToArray(), ub.ToArray());
            foreach (var i in forcedZero) { lbA[i] = 0; ubA[i] = 0; }

            var spec = new ProblemSpec(input, lbA, ubA);
            var solved = SolveObjective(spec, input.Objective, counters);
            if (solved.Weights is null)
            {
                var msg = attempt == 0
                    ? solved.Message
                    : $"After removing positions to satisfy the holdings-count/minimum-position limits, the remaining problem is infeasible: {solved.Message}";
                return Failure(input, solved.Status, msg, diagnostics, counters);
            }

            var w = solved.Weights;
            var active = Enumerable.Range(0, n).Where(i => w[i] > ActiveWeight).ToList();
            var tooMany = c.MaxHoldings is { } k && active.Count > k;
            var small = c.MinPositionWeight is { } mp
                ? active.Where(i => w[i] < mp - WeightTolerance).ToList()
                : [];

            if (!tooMany && small.Count == 0)
                return Finish(input, w, counters, diagnostics, solved.Message, spec);

            var candidates = (small.Count > 0 ? small : active).Where(i => lb[i] <= 0).OrderBy(i => w[i]).ToList();
            if (candidates.Count == 0)
            {
                return Failure(input, OptimizationStatus.Infeasible,
                    "The holdings-count or minimum-position limit conflicts with assets that have a positive minimum weight.",
                    diagnostics, counters);
            }

            var remove = candidates[0];
            forcedZero.Add(remove);
            diagnostics.Add(
                $"Heuristic: removed {input.Symbols[remove]} (weight {w[remove]:P2}) to satisfy " +
                (small.Count > 0 ? $"minimum position size {c.MinPositionWeight:P2}" : $"maximum holdings {c.MaxHoldings}") + ".");
        }

        return Failure(input, OptimizationStatus.SolverFailure, "Cardinality heuristic did not converge.", diagnostics, counters);
    }

    public IReadOnlyList<FrontierPoint> EfficientFrontier(OptimizationInput input, int points)
    {
        if (points < 2) throw new ArgumentOutOfRangeException(nameof(points));
        if (ValidateInput(input) is not null) return [];
        var (lb, ub) = Bounds(input, []);
        if (PreCheckFeasibility(input, lb, ub) is not null) return [];

        var spec = new ProblemSpec(input, lb, ub);
        var counters = new Counters();
        var gmv = SolveMinVariance(spec, null, counters);
        var maxRet = SolveMaxReturn(spec, counters);
        if (gmv.Weights is null || maxRet.Weights is null) return [];

        var rMin = PortfolioMath.ExpectedReturn(gmv.Weights, input.ExpectedReturns);
        var rMax = PortfolioMath.ExpectedReturn(maxRet.Weights, input.ExpectedReturns);
        var result = new List<FrontierPoint> { Point(input, Clean(gmv.Weights, spec)) };
        if (rMax - rMin < 1e-8) return result;

        // Interior points: each solve starts from the previous point (neighbouring problems, same rows), with a looser
        // ADMM stopping rule – the active-set polish then restores full accuracy. The last point is the maximum-return
        // portfolio itself (a min-variance problem pinned to the maximum return is degenerate and slow to solve).
        double[]? warmX = null, warmY = null;
        for (var k = 1; k < points - 1; k++)
        {
            var target = rMin + (rMax - rMin) * k / (points - 1);
            var rows = spec.Rows.Append(new Row(Pad(input.ExpectedReturns, spec.VariableCount), target, double.PositiveInfinity, "target return")).ToList();
            var sol = SolveRows(spec.CovarianceBlock(2.0), Vector<double>.Build.Dense(spec.VariableCount), rows, counters,
                FrontierSettings with { WarmX = warmX, WarmY = warmY });
            if (!sol.IsOptimal) continue;
            (warmX, warmY) = (sol.X, sol.Y);
            result.Add(Point(input, Clean(sol.X.Take(spec.N).ToArray(), spec)));
        }
        result.Add(Point(input, Clean(maxRet.Weights, spec)));
        return result;
    }

    // ---------------------------------------------------------------- objectives

    private SolveOutcome SolveObjective(ProblemSpec spec, OptimizationObjective objective, Counters counters)
    {
        var input = spec.Input;
        switch (objective)
        {
            case OptimizationObjective.MinimumVariance:
                return SolveMinVariance(spec, null, counters);

            case OptimizationObjective.TargetReturn:
            {
                var target = input.Constraints.TargetReturn!.Value;
                var maxRet = SolveMaxReturn(spec, counters);
                if (maxRet.Weights is null) return maxRet;
                var rMax = PortfolioMath.ExpectedReturn(maxRet.Weights, input.ExpectedReturns);
                if (target > rMax + 1e-9)
                    return SolveOutcome.Fail(OptimizationStatus.Infeasible,
                        $"Target return {target:P2} exceeds the maximum achievable expected return {rMax:P2} under the current constraints.");
                return SolveMinVariance(spec, Math.Min(target, rMax), counters);
            }

            case OptimizationObjective.MaximumSharpe:
                return SolveMaxSharpe(spec, counters);

            case OptimizationObjective.MaximumReturnForRisk:
                return SolveMaxReturnForRisk(spec, input.Constraints.MaxVolatility!.Value, counters);

            default:
                return SolveOutcome.Fail(OptimizationStatus.InvalidInput, $"Unsupported objective {objective}.");
        }
    }

    private SolveOutcome SolveMinVariance(ProblemSpec spec, double? targetReturn, Counters counters)
    {
        var rows = spec.Rows.ToList();
        if (targetReturn is { } target)
            rows.Add(new Row(Pad(spec.Input.ExpectedReturns, spec.VariableCount), target, double.PositiveInfinity, "target return"));

        var p = spec.CovarianceBlock(2.0);
        var q = Vector<double>.Build.Dense(spec.VariableCount);
        return Run(spec, p, q, rows, counters, "minimum variance");
    }

    /// <summary>
    /// Display-only frontier points: looser ADMM tolerance (the polish step makes them exact) and an iteration cap, so a
    /// slow point near the maximum return is left out instead of holding up the whole optimization.
    /// </summary>
    private static readonly QpSettings FrontierSettings = QpSettings.Default with { EpsAbs = 1e-7, EpsRel = 1e-7, MaxIterations = 1500 };

    /// <summary>
    /// Maximum expected return – a linear program. With budget, weight bounds and sector caps only (no turnover rows) it is
    /// solved exactly by the greedy rule: start at the lower bounds and give the remaining budget to the highest expected
    /// returns first, up to each asset's and each sector's limit (sectors are disjoint, so greedy is optimal). Otherwise –
    /// or if the greedy fill cannot place the whole budget – the QP solver with a tiny ε·wᵀΣw regularization is used.
    /// </summary>
    private SolveOutcome SolveMaxReturn(ProblemSpec spec, Counters counters)
    {
        if (!spec.TurnoverApplied && GreedyMaxReturn(spec) is { } exact) return SolveOutcome.Ok(exact, null);
        var p = spec.CovarianceBlock(2e-6);
        var q = Vector<double>.Build.DenseOfArray(Pad(spec.Input.ExpectedReturns, spec.VariableCount)).Negate();
        return Run(spec, p, q, spec.Rows, counters, "maximum return");
    }

    private static double[]? GreedyMaxReturn(ProblemSpec spec)
    {
        var input = spec.Input;
        var c = input.Constraints;
        var w = (double[])spec.Lb.Clone();
        var remaining = c.InvestedFraction - w.Sum();
        if (remaining < -1e-12) return null;

        // Room left in each capped sector after the lower bounds.
        var sectorOf = new string?[spec.N];
        var room = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sector, cap) in c.SectorMaxWeights) room[sector] = cap;
        for (var i = 0; i < spec.N; i++)
        {
            var sector = c.Sectors.GetValueOrDefault(input.Symbols[i]);
            if (sector is not null && room.ContainsKey(sector)) { sectorOf[i] = sector; room[sector] -= w[i]; }
        }
        if (room.Values.Any(r => r < -1e-12)) return null;

        foreach (var i in Enumerable.Range(0, spec.N).OrderByDescending(i => input.ExpectedReturns[i]).ThenBy(i => i))
        {
            if (remaining <= 1e-15) break;
            var add = Math.Min(spec.Ub[i] - w[i], remaining);
            if (sectorOf[i] is { } s) add = Math.Min(add, room[s]);
            if (add <= 0) continue;
            w[i] += add;
            remaining -= add;
            if (sectorOf[i] is { } s2) room[s2] -= add;
        }
        return remaining > 1e-9 ? null : w;
    }

    private SolveOutcome SolveMaxSharpe(ProblemSpec spec, Counters counters)
    {
        var input = spec.Input;
        var rf = input.RiskFreeRate;
        var maxRet = SolveMaxReturn(spec, counters);
        if (maxRet.Weights is null) return maxRet;
        var rMax = PortfolioMath.ExpectedReturn(maxRet.Weights, input.ExpectedReturns);
        if (rMax - rf <= 1e-9)
        {
            return SolveOutcome.Fail(OptimizationStatus.Infeasible,
                $"No feasible portfolio has an expected return ({rMax:P2} at most) above the risk-free rate ({rf:P2}); the maximum Sharpe portfolio is undefined. " +
                "Lower the risk-free rate, extend the lookback window or choose minimum variance.");
        }

        // Homogenized problem in v = [y (n), t (n, optional), κ]. Every row lo ≤ a·x ≤ hi becomes lo·κ ≤ a·v ≤ hi·κ.
        var nv = spec.VariableCount + 1;
        var kIdx = nv - 1;
        var rows = new List<Row>();
        foreach (var r in spec.Rows)
        {
            var a = Pad(r.Coefficients, nv);
            if (Math.Abs(r.Upper - r.Lower) < 1e-15)
            {
                var eq = (double[])a.Clone(); eq[kIdx] = -r.Lower;
                rows.Add(new Row(eq, 0, 0, r.Label));
                continue;
            }
            if (double.IsFinite(r.Lower))
            {
                var lo = (double[])a.Clone(); lo[kIdx] = -r.Lower;
                rows.Add(new Row(lo, 0, double.PositiveInfinity, r.Label));
            }
            if (double.IsFinite(r.Upper))
            {
                var hi = (double[])a.Clone(); hi[kIdx] = -r.Upper;
                rows.Add(new Row(hi, double.NegativeInfinity, 0, r.Label));
            }
        }

        // Excess return of the invested portfolio: (μ − rf / invested)ᵀ y = 1, since Σw = invested.
        var excess = new double[nv];
        for (var i = 0; i < spec.N; i++) excess[i] = input.ExpectedReturns[i] - rf / input.Constraints.InvestedFraction;
        rows.Add(new Row(excess, 1, 1, "normalized excess return"));
        var kappa = new double[nv]; kappa[kIdx] = 1;
        rows.Add(new Row(kappa, 0, double.PositiveInfinity, "κ ≥ 0"));

        var p = Matrix<double>.Build.Dense(nv, nv);
        var cov = spec.CovarianceBlock(2.0);
        p.SetSubMatrix(0, 0, cov);
        var q = Vector<double>.Build.Dense(nv);

        var sol = SolveRows(p, q, rows, counters);
        if (!sol.IsOptimal)
            return SolveOutcome.FromQp(sol, "maximum Sharpe");

        var k = sol.X[kIdx];
        if (k <= 1e-12)
            return SolveOutcome.Fail(OptimizationStatus.SolverFailure, "Maximum Sharpe solver returned a degenerate scaling factor (κ ≈ 0).");
        var w = new double[spec.N];
        for (var i = 0; i < spec.N; i++) w[i] = sol.X[i] / k;
        return SolveOutcome.Ok(w, null);
    }

    private SolveOutcome SolveMaxReturnForRisk(ProblemSpec spec, double maxVol, Counters counters)
    {
        var input = spec.Input;
        var gmv = SolveMinVariance(spec, null, counters);
        if (gmv.Weights is null) return gmv;
        var gmvVol = PortfolioMath.Volatility(gmv.Weights, input.Covariance);
        if (gmvVol > maxVol + 1e-9)
        {
            return SolveOutcome.Fail(OptimizationStatus.Infeasible,
                $"The lowest achievable volatility under the constraints is {gmvVol:P2}, above the volatility limit of {maxVol:P2}.");
        }

        var maxRet = SolveMaxReturn(spec, counters);
        if (maxRet.Weights is null) return maxRet;
        var rLo = PortfolioMath.ExpectedReturn(gmv.Weights, input.ExpectedReturns);
        var rHi = PortfolioMath.ExpectedReturn(maxRet.Weights, input.ExpectedReturns);

        if (PortfolioMath.Volatility(maxRet.Weights, input.Covariance) <= maxVol)
            return SolveOutcome.Ok(maxRet.Weights, "Volatility limit is not binding; the maximum-return efficient portfolio is selected.");

        var best = gmv.Weights;
        for (var it = 0; it < 60 && rHi - rLo > 1e-9; it++)
        {
            var mid = 0.5 * (rLo + rHi);
            var sol = SolveMinVariance(spec, mid, counters);
            if (sol.Weights is null) { rHi = mid; continue; }
            if (PortfolioMath.Volatility(sol.Weights, input.Covariance) <= maxVol)
            {
                rLo = mid;
                best = sol.Weights;
            }
            else rHi = mid;
        }
        return SolveOutcome.Ok(best, null);
    }

    // ---------------------------------------------------------------- QP plumbing

    private SolveOutcome Run(ProblemSpec spec, Matrix<double> p, Vector<double> q, IReadOnlyList<Row> rows, Counters counters, string label)
    {
        var sol = SolveRows(p, q, rows, counters);
        if (!sol.IsOptimal) return SolveOutcome.FromQp(sol, label);
        return SolveOutcome.Ok(sol.X.Take(spec.N).ToArray(), null);
    }

    private QpSolution SolveRows(Matrix<double> p, Vector<double> q, IReadOnlyList<Row> rows, Counters counters, QpSettings? settings = null)
    {
        var nv = p.RowCount;
        var a = Matrix<double>.Build.Dense(rows.Count, nv);
        var l = Vector<double>.Build.Dense(rows.Count);
        var u = Vector<double>.Build.Dense(rows.Count);
        for (var r = 0; r < rows.Count; r++)
        {
            for (var j = 0; j < nv; j++) a[r, j] = rows[r].Coefficients[j];
            l[r] = rows[r].Lower;
            u[r] = rows[r].Upper;
        }
        var sol = solver.Solve(new QuadraticProgram(p, q, a, l, u), settings);
        counters.Calls++;
        counters.Iterations += sol.Iterations;
        return sol;
    }

    // ---------------------------------------------------------------- validation & feasibility

    private static string? ValidateInput(OptimizationInput input)
    {
        var n = input.Symbols.Count;
        var c = input.Constraints;
        if (n == 0) return "The asset universe is empty.";
        if (input.ExpectedReturns.Length != n) return "Expected return vector length does not match the number of assets.";
        if (input.Covariance.GetLength(0) != n || input.Covariance.GetLength(1) != n) return "Covariance matrix dimensions do not match the number of assets.";
        if (input.ExpectedReturns.Any(v => !double.IsFinite(v))) return "Expected returns contain non-finite values.";
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            if (!double.IsFinite(input.Covariance[i, j])) return "Covariance matrix contains non-finite values.";
            if (Math.Abs(input.Covariance[i, j] - input.Covariance[j, i]) > 1e-10) return "Covariance matrix is not symmetric.";
        }
        if (c.MinWeight < 0 || c.AssetBounds.Values.Any(b => b.Min < 0))
            return "Negative minimum weights (short selling) are not supported in this release.";
        if (c.MaxWeight <= 0 || c.MaxWeight > 1) return "Maximum weight must be in (0, 1].";
        if (c.MinWeight > c.MaxWeight) return "Minimum weight exceeds maximum weight.";
        if (c.CashReserve is < 0 or >= 1) return "Cash reserve must be in [0, 1).";
        if (c.MaxHoldings is < 1) return "Maximum holdings must be at least 1.";
        if (c.MinPositionWeight is < 0 or > 1) return "Minimum position size must be in [0, 1].";
        if (c.MinPositionWeight is { } mp && mp > c.MaxWeight) return "Minimum position size exceeds the maximum weight per asset.";
        if (c.MaxTurnover is < 0) return "Maximum turnover must be non-negative.";
        if (c.SectorMaxWeights.Values.Any(v => v is < 0 or > 1)) return "Sector limits must be in [0, 1].";
        if (input.CurrentWeights is { } cw && cw.Length != n) return "Current weights length does not match the number of assets.";
        if (input.Objective == OptimizationObjective.TargetReturn && c.TargetReturn is null) return "A target return is required for the target-return objective.";
        if (input.Objective == OptimizationObjective.MaximumReturnForRisk && c.MaxVolatility is not > 0) return "A positive volatility limit is required for the risk-constrained objective.";
        if (!double.IsFinite(input.RiskFreeRate)) return "Risk-free rate must be finite.";
        return null;
    }

    private static (double[] Lb, double[] Ub) Bounds(OptimizationInput input, List<string> diagnostics)
    {
        var n = input.Symbols.Count;
        var c = input.Constraints;
        var excluded = new HashSet<string>(c.ExcludedSymbols, StringComparer.OrdinalIgnoreCase);
        var lb = new double[n];
        var ub = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sym = input.Symbols[i];
            (lb[i], ub[i]) = c.AssetBounds.TryGetValue(sym, out var b) ? (b.Min, b.Max) : (c.MinWeight, c.MaxWeight);
            if (excluded.Contains(sym))
            {
                lb[i] = ub[i] = 0;
                diagnostics.Add($"{sym} excluded by configuration.");
            }
            else if (input.Covariance[i, i] < ZeroVariance)
            {
                lb[i] = ub[i] = 0;
                diagnostics.Add($"{sym} excluded: zero variance over the estimation window (constant or stale prices).");
            }
        }
        return (lb, ub);
    }

    private static string? PreCheckFeasibility(OptimizationInput input, double[] lb, double[] ub)
    {
        var c = input.Constraints;
        var invested = c.InvestedFraction;
        var eligible = Enumerable.Range(0, lb.Length).Count(i => ub[i] > 0);
        if (eligible == 0) return "No eligible assets remain after exclusions.";
        var sumLb = lb.Sum();
        var sumUb = ub.Sum();
        if (sumLb > invested + 1e-9)
            return $"The sum of minimum weights ({sumLb:P2}) exceeds the invested fraction ({invested:P2}).";
        if (sumUb < invested - 1e-9)
            return $"The maximum weights of the {eligible} eligible assets add up to {sumUb:P2}, which cannot reach the invested fraction of {invested:P2}. Increase the maximum weight, add assets or raise the cash reserve.";
        if (c.MaxHoldings is { } k)
        {
            var topK = ub.OrderByDescending(v => v).Take(k).Sum();
            if (topK < invested - 1e-9)
                return $"With at most {k} holdings and the configured maximum weights, at most {topK:P2} can be invested (needs {invested:P2}).";
        }

        if (c.SectorMaxWeights.Count > 0)
        {
            // Capacity per sector is min(cap, Σ ub in sector); unclassified assets have no sector cap.
            var capacity = 0d;
            var groups = Enumerable.Range(0, lb.Length).GroupBy(i => c.Sectors.GetValueOrDefault(input.Symbols[i]) ?? "");
            foreach (var g in groups)
            {
                var gUb = g.Sum(i => ub[i]);
                var gLb = g.Sum(i => lb[i]);
                if (g.Key != "" && c.SectorMaxWeights.TryGetValue(g.Key, out var cap))
                {
                    if (gLb > cap + 1e-9) return $"Minimum weights in sector {g.Key} ({gLb:P2}) exceed the sector limit ({cap:P2}).";
                    capacity += Math.Min(cap, gUb);
                }
                else capacity += gUb;
            }
            if (capacity < invested - 1e-9)
                return $"Sector limits allow at most {capacity:P2} to be invested (needs {invested:P2}). Relax sector limits or add assets from other sectors.";
        }
        return null;
    }

    private static double[] EqualWeights(double[] lb, double[] ub, double invested)
    {
        var eligible = Enumerable.Range(0, lb.Length).Where(i => ub[i] > 0).ToArray();
        var w = new double[lb.Length];
        foreach (var i in eligible) w[i] = invested / eligible.Length;
        return w;
    }

    private OptimizationResult Finish(OptimizationInput input, double[] raw, Counters counters, List<string> diagnostics, string? note, ProblemSpec? spec = null)
    {
        var (lb, ub) = Bounds(input, []);
        spec ??= new ProblemSpec(input, lb, ub);
        var w = Clean(raw, spec);
        var violation = Verify(input, w, spec);
        if (violation is not null)
        {
            var status = input.Objective == OptimizationObjective.EqualWeight ? OptimizationStatus.Infeasible : OptimizationStatus.SolverFailure;
            return Failure(input, status, $"The resulting allocation violates a constraint: {violation}", diagnostics, counters);
        }

        if (!string.IsNullOrWhiteSpace(note)) diagnostics.Add(note);
        if (spec.TurnoverIgnored) diagnostics.Add("Turnover limit not applied: the portfolio holds none of these assets.");
        var stats = PortfolioMath.Statistics(w, input.ExpectedReturns, input.Covariance, input.RiskFreeRate);
        return new OptimizationResult(
            OptimizationStatus.Optimal, input.Objective, input.Symbols, w, stats,
            PortfolioMath.RiskContributions(w, input.Covariance),
            "Optimization succeeded.", diagnostics, counters.Iterations, counters.Calls);
    }

    private static double[] Clean(double[] raw, ProblemSpec spec)
    {
        var w = new double[spec.N];
        for (var i = 0; i < spec.N; i++)
        {
            var v = raw[i] < 1e-8 ? 0 : raw[i];
            w[i] = Math.Clamp(v, spec.Lb[i], spec.Ub[i]);
        }
        var sum = w.Sum();
        var target = spec.Input.Constraints.InvestedFraction;
        if (sum > 0 && Math.Abs(sum - target) < 1e-5)
        {
            for (var i = 0; i < spec.N; i++) w[i] *= target / sum;
        }
        return w;
    }

    private static string? Verify(OptimizationInput input, double[] w, ProblemSpec spec)
    {
        var c = input.Constraints;
        for (var i = 0; i < w.Length; i++)
        {
            if (w[i] < spec.Lb[i] - WeightTolerance || w[i] > spec.Ub[i] + WeightTolerance)
                return $"{input.Symbols[i]} weight {w[i]:P4} is outside [{spec.Lb[i]:P2}, {spec.Ub[i]:P2}].";
        }
        if (Math.Abs(w.Sum() - c.InvestedFraction) > WeightTolerance)
            return $"weights sum to {w.Sum():P4} instead of {c.InvestedFraction:P2}.";
        foreach (var (sector, cap) in c.SectorMaxWeights)
        {
            var s = Enumerable.Range(0, w.Length)
                .Where(i => string.Equals(c.Sectors.GetValueOrDefault(input.Symbols[i]), sector, StringComparison.OrdinalIgnoreCase))
                .Sum(i => w[i]);
            if (s > cap + WeightTolerance) return $"sector {sector} weight {s:P4} exceeds {cap:P2}.";
        }
        if (spec.TurnoverApplied && input.CurrentWeights is { } cw)
        {
            var to = Statistics.PerformanceMetrics.Turnover(cw, w);
            if (to > c.MaxTurnover!.Value + WeightTolerance) return $"turnover {to:P4} exceeds {c.MaxTurnover:P2}.";
        }
        if (input.Objective == OptimizationObjective.TargetReturn &&
            PortfolioMath.ExpectedReturn(w, input.ExpectedReturns) < c.TargetReturn!.Value - 1e-6)
            return "expected return is below the target.";
        if (input.Objective == OptimizationObjective.MaximumReturnForRisk &&
            PortfolioMath.Volatility(w, input.Covariance) > c.MaxVolatility!.Value + 1e-6)
            return "volatility exceeds the limit.";
        return null;
    }

    private static OptimizationResult Failure(OptimizationInput input, OptimizationStatus status, string message, List<string> diagnostics, Counters? counters = null) =>
        new(status, input.Objective, input.Symbols, null, null, null, message, diagnostics, counters?.Iterations ?? 0, counters?.Calls ?? 0);

    private static FrontierPoint Point(OptimizationInput input, double[] w)
    {
        var s = PortfolioMath.Statistics(w, input.ExpectedReturns, input.Covariance, input.RiskFreeRate);
        return new FrontierPoint(s.ExpectedReturn, s.Volatility, s.SharpeRatio, w);
    }

    private static double[] Pad(IReadOnlyList<double> v, int length)
    {
        var r = new double[length];
        for (var i = 0; i < Math.Min(v.Count, length); i++) r[i] = v[i];
        return r;
    }

    // ---------------------------------------------------------------- problem representation

    private sealed record Row(double[] Coefficients, double Lower, double Upper, string Label);

    private sealed class Counters
    {
        public int Iterations;
        public int Calls;
    }

    private sealed record SolveOutcome(OptimizationStatus Status, double[]? Weights, string Message)
    {
        public static SolveOutcome Ok(double[] w, string? note) => new(OptimizationStatus.Optimal, w, note ?? "");
        public static SolveOutcome Fail(OptimizationStatus status, string message) => new(status, null, message);

        public static SolveOutcome FromQp(QpSolution sol, string label) => sol.Status switch
        {
            QpStatus.PrimalInfeasible => Fail(OptimizationStatus.Infeasible,
                $"The {label} problem is infeasible: the combination of weight bounds, sector limits, turnover limit and return/risk targets cannot be satisfied simultaneously."),
            _ => Fail(OptimizationStatus.SolverFailure,
                $"The {label} solver did not converge ({sol.Status}). {sol.Message}"),
        };
    }

    /// <summary>
    /// Variables x = [w (n), t (n, only when a turnover limit applies)]; linear rows lo ≤ a·x ≤ hi.
    /// </summary>
    private sealed class ProblemSpec
    {
        public ProblemSpec(OptimizationInput input, double[] lb, double[] ub)
        {
            Input = input;
            Lb = lb;
            Ub = ub;
            N = input.Symbols.Count;
            var c = input.Constraints;
            var hasHoldings = input.CurrentWeights is { } cw && cw.Any(v => v > 0);
            TurnoverApplied = c.MaxTurnover.HasValue && hasHoldings;
            TurnoverIgnored = c.MaxTurnover.HasValue && !hasHoldings;
            VariableCount = TurnoverApplied ? 2 * N : N;

            var rows = new List<Row>();
            var budget = new double[VariableCount];
            for (var i = 0; i < N; i++) budget[i] = 1;
            rows.Add(new Row(budget, c.InvestedFraction, c.InvestedFraction, "budget"));

            for (var i = 0; i < N; i++)
            {
                var a = new double[VariableCount];
                a[i] = 1;
                rows.Add(new Row(a, lb[i], ub[i], $"bounds {input.Symbols[i]}"));
            }

            foreach (var (sector, cap) in c.SectorMaxWeights)
            {
                var a = new double[VariableCount];
                var any = false;
                for (var i = 0; i < N; i++)
                {
                    if (string.Equals(c.Sectors.GetValueOrDefault(input.Symbols[i]), sector, StringComparison.OrdinalIgnoreCase))
                    {
                        a[i] = 1;
                        any = true;
                    }
                }
                if (any) rows.Add(new Row(a, double.NegativeInfinity, cap, $"sector {sector}"));
            }

            if (TurnoverApplied)
            {
                var w0 = input.CurrentWeights!;
                var sumT = new double[VariableCount];
                for (var i = 0; i < N; i++)
                {
                    var a1 = new double[VariableCount]; a1[N + i] = 1; a1[i] = -1; // t − w ≥ −w0
                    rows.Add(new Row(a1, -w0[i], double.PositiveInfinity, "turnover+"));
                    var a2 = new double[VariableCount]; a2[N + i] = 1; a2[i] = 1;  // t + w ≥ w0
                    rows.Add(new Row(a2, w0[i], double.PositiveInfinity, "turnover-"));
                    sumT[N + i] = 1;
                }
                // ½ Σ|w − w0| ≤ T  ⇔  Σ t ≤ 2T
                rows.Add(new Row(sumT, double.NegativeInfinity, 2 * c.MaxTurnover!.Value, "turnover"));
            }

            Rows = rows;
        }

        public OptimizationInput Input { get; }
        public double[] Lb { get; }
        public double[] Ub { get; }
        public int N { get; }
        public int VariableCount { get; }
        public bool TurnoverApplied { get; }
        public bool TurnoverIgnored { get; }
        public IReadOnlyList<Row> Rows { get; }

        public Matrix<double> CovarianceBlock(double scale)
        {
            var p = Matrix<double>.Build.Dense(VariableCount, VariableCount);
            for (var i = 0; i < N; i++)
            for (var j = 0; j < N; j++)
                p[i, j] = Input.Covariance[i, j] * scale;
            return p;
        }
    }
}
