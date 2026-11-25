using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Factorization;

namespace PortfolioOptimizer.Optimization.Solvers;

/// <summary>
/// Dense ADMM quadratic-programming solver following the OSQP algorithm
/// (Stellato, Banjac, Goulart, Bemporad, Boyd: "OSQP: an operator splitting solver for quadratic programs", 2020):
/// reduced KKT system with Cholesky factorization, over-relaxation, adaptive step size ρ,
/// primal/dual infeasibility certificates and an active-set polishing step that recovers a high-accuracy solution.
/// Suitable for the small dense problems (tens to low hundreds of variables) produced by portfolio optimization.
/// </summary>
public sealed class AdmmQpSolver : IQuadraticProgramSolver
{
    private const double RhoMin = 1e-6;
    private const double RhoMax = 1e6;
    private const double RhoEqualityScale = 1e3;

    public QpSolution Solve(QuadraticProgram problem, QpSettings? settings = null)
    {
        var s = settings ?? QpSettings.Default;
        var n = problem.Variables;
        var m = problem.Constraints;
        var p = problem.P;
        var q = problem.Q;
        var a = problem.A;
        // The iteration's products A·x and Aᵀ·v use a sparse copy when A is mostly zeros – portfolio problems are mostly
        // bound rows with one entry each (228 stocks: 30 µs instead of 278 µs per iteration, docs/performance.md #19).
        // The KKT factorization and the polish keep the dense matrix.
        var aOp = MostlyZero(a) ? Matrix<double>.Build.SparseOfMatrix(a) : a;
        var l = problem.Lower;
        var u = problem.Upper;

        for (var i = 0; i < m; i++)
        {
            if (l[i] > u[i] + 1e-12)
                return Fail(QpStatus.PrimalInfeasible, n, m, $"Constraint row {i} has lower bound {l[i]} greater than upper bound {u[i]}.");
        }

        var rhoScalar = s.Rho;
        var rho = BuildRho(rhoScalar, l, u);
        Cholesky<double> factor;
        try { factor = Factor(p, a, rho, s.Sigma); }
        catch (Exception ex) when (IsRecoverable(ex)) { return Fail(QpStatus.NumericalError, n, m, $"KKT factorization failed: {ex.Message}"); }

        var x = Vector<double>.Build.Dense(n);
        var z = Vector<double>.Build.Dense(m);
        var y = Vector<double>.Build.Dense(m);
        if (s.WarmX is { } wx && wx.Length == n && s.WarmY is { } wy && wy.Length == m)
        {
            x = Vector<double>.Build.DenseOfArray(wx);
            y = Vector<double>.Build.DenseOfArray(wy);
            z = Project(aOp * x, l, u);
        }
        double primRes = double.PositiveInfinity, dualRes = double.PositiveInfinity;
        var status = QpStatus.MaxIterationsReached;
        var iter = 0;
        var earlyPolished = false;
        // Active sets whose early polish failed in this solve (deterministic: trying one again would fail again).
        var failedEarly = new HashSet<ActiveRows>();

        for (iter = 1; iter <= s.MaxIterations; iter++)
        {
            var xPrev = x;
            var yPrev = y;

            var rhs = s.Sigma * x - q + aOp.TransposeThisAndMultiply(rho.PointwiseMultiply(z) - y);
            var xTilde = factor.Solve(rhs);
            var zTilde = aOp * xTilde;

            x = s.Alpha * xTilde + (1 - s.Alpha) * x;
            var zHat = s.Alpha * zTilde + (1 - s.Alpha) * z;
            var zNew = Project(zHat + y.PointwiseDivide(rho), l, u);
            y = y + rho.PointwiseMultiply(zHat - zNew);
            z = zNew;

            // Residuals and certificates cost several matrix–vector products: like OSQP, check them every few iterations
            // (and always on the last one) instead of after every step.
            if (iter % s.CheckInterval != 0 && iter != s.MaxIterations) continue;

            if (!AllFinite(x) || !AllFinite(y))
                return Fail(QpStatus.NumericalError, n, m, "Iterates diverged to non-finite values.");

            var ax = aOp * x;
            var px = p * x;
            var aty = aOp.TransposeThisAndMultiply(y);
            primRes = m == 0 ? 0 : (ax - z).InfinityNorm();
            dualRes = (px + q + aty).InfinityNorm();

            var primScale = m == 0 ? 0 : Math.Max(ax.InfinityNorm(), z.InfinityNorm());
            var dualScale = Math.Max(Math.Max(px.InfinityNorm(), aty.InfinityNorm()), q.InfinityNorm());
            var epsPrim = s.EpsAbs + s.EpsRel * primScale;
            var epsDual = s.EpsAbs + s.EpsRel * dualScale;

            if (primRes <= epsPrim && dualRes <= epsDual)
            {
                status = QpStatus.Solved;
                break;
            }

            if (IsPrimalInfeasible(aOp, l, u, y - yPrev, s.EpsInfeasible))
            {
                status = QpStatus.PrimalInfeasible;
                break;
            }

            if (IsDualInfeasible(p, q, aOp, l, u, x - xPrev, s.EpsInfeasible))
            {
                status = QpStatus.DualInfeasible;
                break;
            }

            if (s.AdaptiveRhoInterval > 0 && iter % s.AdaptiveRhoInterval == 0 && m > 0)
            {
                var ratio = (primRes / (primScale + 1e-30)) / (dualRes / (dualScale + 1e-30) + 1e-30);
                var newRho = Math.Clamp(rhoScalar * Math.Sqrt(ratio), RhoMin, RhoMax);
                if (newRho > 5 * rhoScalar || newRho < rhoScalar / 5)
                {
                    rhoScalar = newRho;
                    rho = BuildRho(rhoScalar, l, u);
                    try { factor = Factor(p, a, rho, s.Sigma); }
                    catch (Exception ex) when (IsRecoverable(ex)) { return Fail(QpStatus.NumericalError, n, m, $"KKT re-factorization failed: {ex.Message}"); }
                }
            }

            // Early polish: once the iterates are roughly right, the active set is usually already correct and the polish
            // step solves the problem exactly. Accepted only when it meets the strict tolerance (10·EpsAbs) on its own.
            // Its outcome depends only on the active set, so a set that already failed is not tried again (identical
            // result; on frontiers of ~230 stocks 183 of 209 attempts failed, mostly repeats – docs/performance.md #17).
            if (s.Polish && s.EarlyPolishInterval > 0 && iter % s.EarlyPolishInterval == 0 &&
                primRes <= s.EarlyPolishTolerance && dualRes <= s.EarlyPolishTolerance)
            {
                var candidate = ActiveSet(problem, z, y);
                if (!failedEarly.Contains(candidate))
                {
                    if (Polish(problem, candidate, s, 0, 0) is { } early)
                    {
                        (x, y, primRes, dualRes) = early;
                        status = QpStatus.Solved;
                        earlyPolished = true;
                        break;
                    }
                    failedEarly.Add(candidate);
                }
            }
        }

        if (status is QpStatus.PrimalInfeasible)
            return new QpSolution(status, x.ToArray(), y.ToArray(), double.NaN, iter, primRes, dualRes, false,
                "The constraints are infeasible (primal infeasibility certificate found).");
        if (status is QpStatus.DualInfeasible)
            return new QpSolution(status, x.ToArray(), y.ToArray(), double.NaN, iter, primRes, dualRes, false,
                "The problem is unbounded (dual infeasibility certificate found).");

        if (status == QpStatus.MaxIterationsReached && primRes <= s.InaccurateTolerance && dualRes <= s.InaccurateTolerance)
            status = QpStatus.SolvedInaccurate;

        var polished = earlyPolished;
        if (!earlyPolished && s.Polish && status is QpStatus.Solved or QpStatus.SolvedInaccurate)
        {
            var pol = Polish(problem, ActiveSet(problem, z, y), s, primRes, dualRes);
            if (pol is not null)
            {
                (x, y, primRes, dualRes) = pol.Value;
                polished = true;
                status = QpStatus.Solved;
            }
        }

        var message = status switch
        {
            QpStatus.Solved => null,
            QpStatus.SolvedInaccurate => "Solution found to reduced accuracy.",
            QpStatus.MaxIterationsReached => $"Iteration limit reached (primal residual {primRes:E2}, dual residual {dualRes:E2}).",
            _ => null,
        };

        return new QpSolution(status, x.ToArray(), y.ToArray(), problem.Objective(x), iter, primRes, dualRes, polished, message);
    }

    /// <summary>
    /// Solves the KKT system of <paramref name="set"/> as equalities; null when it is singular, a dual sign is wrong or
    /// the result misses the tolerance. A function of the active set alone (plus the problem and settings).
    /// </summary>
    private static (Vector<double> X, Vector<double> Y, double Prim, double Dual)? Polish(
        QuadraticProgram problem, ActiveRows set, QpSettings s, double primAdmm, double dualAdmm)
    {
        var n = problem.Variables;
        var (active, lowCount) = (set.Rows, set.LowCount);
        Vector<double> xPol, yPol;
        if (SolveReduced(problem, active, lowCount, s) is { } reduced)
            (xPol, yPol) = reduced;
        else
        {
            var (kkt, kktReg, rhs) = BuildKkt(problem, active, lowCount, s.PolishDelta);
            if (SolveRefined(kkt, kktReg, rhs, s.PolishRefinementSteps) is not { } sol) return null;
            xPol = sol.SubVector(0, n);
            yPol = Vector<double>.Build.Dense(problem.Constraints);
            for (var r = 0; r < active.Length; r++) yPol[active[r]] = sol[n + r];
        }
        if (!DualSignsHold(problem, active, lowCount, yPol)) return null;

        var prim = PrimalResidual(problem, problem.A * xPol);
        var dual = (problem.P * xPol + problem.Q + problem.A.TransposeThisAndMultiply(yPol)).InfinityNorm();

        var primOk = prim <= Math.Max(primAdmm, s.EpsAbs * 10);
        var dualOk = dual <= Math.Max(dualAdmm, s.EpsAbs * 10);
        return primOk && dualOk ? (xPol, yPol, Math.Max(prim, 0), dual) : null;
    }

    /// <summary>Active constraint rows (lower-active first, then upper-active) with value equality, so failed sets can be remembered.</summary>
    private sealed record ActiveRows(int[] Rows, int LowCount)
    {
        public bool Equals(ActiveRows? other) => other is not null && LowCount == other.LowCount && Rows.AsSpan().SequenceEqual(other.Rows);
        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(LowCount);
            foreach (var r in Rows) h.Add(r);
            return h.ToHashCode();
        }
    }

    /// <summary>Constraints the ADMM iterate treats as active: lower-active rows first, then upper-active rows.</summary>
    private static ActiveRows ActiveSet(QuadraticProgram problem, Vector<double> z, Vector<double> y)
    {
        var l = problem.Lower;
        var u = problem.Upper;
        var lowActive = new List<int>();
        var upActive = new List<int>();
        for (var i = 0; i < problem.Constraints; i++)
        {
            if (z[i] - l[i] < -y[i]) lowActive.Add(i);
            else if (u[i] - z[i] < y[i]) upActive.Add(i);
        }
        return new ActiveRows(lowActive.Concat(upActive).ToArray(), lowActive.Count);
    }

    /// <summary>
    /// The KKT system of the active constraints as equalities, plain and regularized (± delta on the diagonal), with its
    /// right-hand side (−q, then the lower bound of lower-active rows and the upper bound of upper-active rows).
    /// </summary>
    private static (Matrix<double> Kkt, Matrix<double> KktReg, Vector<double> Rhs) BuildKkt(
        QuadraticProgram problem, int[] active, int lowCount, double delta)
    {
        var n = problem.Variables;
        var size = n + active.Length;
        var kktReg = Matrix<double>.Build.Dense(size, size);
        var kkt = Matrix<double>.Build.Dense(size, size);
        var rhs = Vector<double>.Build.Dense(size);

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                kkt[i, j] = problem.P[i, j];
                kktReg[i, j] = problem.P[i, j];
            }
            kktReg[i, i] += delta;
            rhs[i] = -problem.Q[i];
        }

        for (var r = 0; r < active.Length; r++)
        {
            var row = active[r];
            for (var j = 0; j < n; j++)
            {
                kkt[n + r, j] = kkt[j, n + r] = problem.A[row, j];
                kktReg[n + r, j] = kktReg[j, n + r] = problem.A[row, j];
            }
            kktReg[n + r, n + r] = -delta;
            rhs[n + r] = r < lowCount ? problem.Lower[row] : problem.Upper[row];
        }
        return (kkt, kktReg, rhs);
    }

    /// <summary>
    /// The polish KKT system with the variables pinned by active single-entry rows (bounds) eliminated (AMIR-46): an active
    /// row c·x_j = b fixes x_j = b/c, the free variables and the other active rows solve
    /// [P_ff, A_Gfᵀ; A_Gf, 0]·[x_f; y_G] = [−q_f − P_fF·x_F; b_G − A_GF·x_F] (same regularization and refinement as the
    /// full system), and each pinned row's multiplier follows from its variable's stationarity:
    /// y_r = −(P·x + q + A_Gᵀ·y_G)_j / c. Exactly the full system's solution, but of the size of the free variables
    /// (~30 instead of ~460 on a 228-stock frontier). Null – use the full system – when a variable is pinned by two active
    /// rows, no variable is free, or the reduced solve fails.
    /// </summary>
    private static (Vector<double> X, Vector<double> Y)? SolveReduced(QuadraticProgram problem, int[] active, int lowCount, QpSettings s)
    {
        var n = problem.Variables;
        var a = problem.A;
        var fixedValue = new double?[n];
        var pinRow = new int[n];
        var general = new List<int>(); // active rows with several entries, in active-set order
        var generalLow = new List<bool>();
        for (var r = 0; r < active.Length; r++)
        {
            var row = active[r];
            var bound = r < lowCount ? problem.Lower[row] : problem.Upper[row];
            var column = -1;
            for (var j = 0; j < n; j++)
            {
                if (a[row, j] == 0) continue;
                if (column >= 0) { column = -2; break; }
                column = j;
            }
            if (column == -1) return null; // an empty active row: leave it to the full system
            if (column == -2)
            {
                general.Add(row);
                generalLow.Add(r < lowCount);
                continue;
            }
            if (fixedValue[column] is not null) return null; // pinned twice
            fixedValue[column] = bound / a[row, column];
            pinRow[column] = row;
        }
        var free = Enumerable.Range(0, n).Where(j => fixedValue[j] is null).ToArray();
        if (free.Length == 0) return null;

        var nf = free.Length;
        var size = nf + general.Count;
        var kkt = Matrix<double>.Build.Dense(size, size);
        var kktReg = Matrix<double>.Build.Dense(size, size);
        var rhs = Vector<double>.Build.Dense(size);
        for (var i = 0; i < nf; i++)
        {
            var fi = free[i];
            var r0 = -problem.Q[fi];
            for (var j = 0; j < n; j++)
                if (fixedValue[j] is { } v) r0 -= problem.P[fi, j] * v;
            rhs[i] = r0;
            for (var k = 0; k < nf; k++)
                kkt[i, k] = kktReg[i, k] = problem.P[fi, free[k]];
            kktReg[i, i] += s.PolishDelta;
        }
        for (var g = 0; g < general.Count; g++)
        {
            var row = general[g];
            var b = generalLow[g] ? problem.Lower[row] : problem.Upper[row];
            for (var j = 0; j < n; j++)
                if (fixedValue[j] is { } v) b -= a[row, j] * v;
            for (var k = 0; k < nf; k++)
                kkt[nf + g, k] = kkt[k, nf + g] = kktReg[nf + g, k] = kktReg[k, nf + g] = a[row, free[k]];
            kktReg[nf + g, nf + g] = -s.PolishDelta;
            rhs[nf + g] = b;
        }
        if (SolveRefined(kkt, kktReg, rhs, s.PolishRefinementSteps) is not { } sol) return null;

        var x = Vector<double>.Build.Dense(n);
        for (var j = 0; j < n; j++) x[j] = fixedValue[j] ?? 0;
        for (var k = 0; k < nf; k++) x[free[k]] = sol[k];
        var y = Vector<double>.Build.Dense(problem.Constraints);
        for (var g = 0; g < general.Count; g++) y[general[g]] = sol[nf + g];
        // Stationarity of each pinned variable gives its row's multiplier.
        var gradient = problem.P * x + problem.Q + a.TransposeThisAndMultiply(y);
        for (var j = 0; j < n; j++)
            if (fixedValue[j] is not null) y[pinRow[j]] = -gradient[j] / a[pinRow[j], j];
        return AllFinite(x) && AllFinite(y) ? (x, y) : null;
    }

    /// <summary>Solves the regularized system and refines against the plain one; null when it fails or is not finite.</summary>
    private static Vector<double>? SolveRefined(Matrix<double> kkt, Matrix<double> kktReg, Vector<double> rhs, int refinementSteps)
    {
        Vector<double> sol;
        try
        {
            var lu = kktReg.LU();
            sol = lu.Solve(rhs);
            for (var it = 0; it < refinementSteps; it++)
            {
                var residual = rhs - kkt * sol;
                sol += lu.Solve(residual);
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return null; // polishing is optional: the unpolished ADMM solution stands
        }
        return AllFinite(sol) ? sol : null;
    }

    /// <summary>Dual sign conditions: multipliers of lower-active rows must be ≤ 0, upper-active rows ≥ 0 (equalities are free).</summary>
    private static bool DualSignsHold(QuadraticProgram problem, int[] active, int lowCount, Vector<double> yPol)
    {
        const double signTol = 1e-7;
        for (var r = 0; r < active.Length; r++)
        {
            var row = active[r];
            var isEquality = Math.Abs(problem.Upper[row] - problem.Lower[row]) < 1e-12;
            if (isEquality) continue;
            if (r < lowCount && yPol[row] > signTol) return false;
            if (r >= lowCount && yPol[row] < -signTol) return false;
        }
        return true;
    }

    /// <summary>Largest violation of l ≤ Ax ≤ u (0 when every constraint holds).</summary>
    private static double PrimalResidual(QuadraticProgram problem, Vector<double> ax)
    {
        var prim = 0d;
        for (var i = 0; i < problem.Constraints; i++)
            prim = Math.Max(prim, Math.Max(problem.Lower[i] - ax[i], ax[i] - problem.Upper[i]));
        return prim;
    }

    /// <summary>At most a quarter of the entries are non-zero (then sparse products are cheaper than dense ones).</summary>
    private static bool MostlyZero(Matrix<double> a)
    {
        if (a.RowCount == 0 || a.ColumnCount == 0) return false;
        var nonZero = 0L;
        for (var i = 0; i < a.RowCount; i++)
            for (var j = 0; j < a.ColumnCount; j++)
                if (a[i, j] != 0) nonZero++;
        return nonZero * 4 <= (long)a.RowCount * a.ColumnCount;
    }

    private static Cholesky<double> Factor(Matrix<double> p, Matrix<double> a, Vector<double> rho, double sigma)
    {
        var n = p.RowCount;
        var k = p + Matrix<double>.Build.DenseIdentity(n) * sigma;
        if (a.RowCount > 0)
            k += a.TransposeThisAndMultiply(Matrix<double>.Build.DenseOfDiagonalVector(rho) * a);
        // Symmetrize to protect the factorization from round-off asymmetry.
        k = 0.5 * (k + k.Transpose());
        return k.Cholesky();
    }

    private static Vector<double> BuildRho(double rho, Vector<double> l, Vector<double> u)
    {
        var v = Vector<double>.Build.Dense(l.Count);
        for (var i = 0; i < l.Count; i++)
        {
            if (double.IsNegativeInfinity(l[i]) && double.IsPositiveInfinity(u[i])) v[i] = RhoMin;
            else if (Math.Abs(u[i] - l[i]) < 1e-12) v[i] = Math.Min(rho * RhoEqualityScale, RhoMax);
            else v[i] = rho;
        }
        return v;
    }

    private static Vector<double> Project(Vector<double> v, Vector<double> l, Vector<double> u)
    {
        var r = Vector<double>.Build.Dense(v.Count);
        for (var i = 0; i < v.Count; i++) r[i] = Math.Min(Math.Max(v[i], l[i]), u[i]);
        return r;
    }

    private static bool IsPrimalInfeasible(Matrix<double> a, Vector<double> l, Vector<double> u, Vector<double> dy, double eps)
    {
        var norm = dy.InfinityNorm();
        if (norm < 1e-12) return false;
        if (a.TransposeThisAndMultiply(dy).InfinityNorm() > eps * norm) return false;
        var support = 0d;
        for (var i = 0; i < dy.Count; i++)
        {
            if (dy[i] > eps * norm)
            {
                if (double.IsPositiveInfinity(u[i])) return false;
                support += u[i] * dy[i];
            }
            else if (dy[i] < -eps * norm)
            {
                if (double.IsNegativeInfinity(l[i])) return false;
                support += l[i] * dy[i];
            }
        }
        return support < -eps * norm;
    }

    private static bool IsDualInfeasible(Matrix<double> p, Vector<double> q, Matrix<double> a, Vector<double> l, Vector<double> u, Vector<double> dx, double eps)
    {
        var norm = dx.InfinityNorm();
        if (norm < 1e-12) return false;
        if (q.DotProduct(dx) > -eps * norm) return false;
        if ((p * dx).InfinityNorm() > eps * norm) return false;
        var adx = a * dx;
        for (var i = 0; i < adx.Count; i++)
        {
            var lowerFinite = !double.IsNegativeInfinity(l[i]);
            var upperFinite = !double.IsPositiveInfinity(u[i]);
            if (upperFinite && adx[i] > eps * norm) return false;
            if (lowerFinite && adx[i] < -eps * norm) return false;
        }
        return true;
    }

    /// <summary>A failed factorization is reported as a numerical error; fatal runtime errors are not caught (same rule as
    /// <c>ExceptionFilters</c> in the application layer, which this pure library does not reference).</summary>
    private static bool IsRecoverable(Exception ex) =>
        ex is not (OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException);

    private static bool AllFinite(Vector<double> v)
    {
        for (var i = 0; i < v.Count; i++) if (!double.IsFinite(v[i])) return false;
        return true;
    }

    private static QpSolution Fail(QpStatus status, int n, int m, string message) =>
        new(status, new double[n], new double[m], double.NaN, 0, double.NaN, double.NaN, false, message);
}
