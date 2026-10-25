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
        catch (Exception ex) { return Fail(QpStatus.NumericalError, n, m, $"KKT factorization failed: {ex.Message}"); }

        var x = Vector<double>.Build.Dense(n);
        var z = Vector<double>.Build.Dense(m);
        var y = Vector<double>.Build.Dense(m);
        if (s.WarmX is { } wx && wx.Length == n && s.WarmY is { } wy && wy.Length == m)
        {
            x = Vector<double>.Build.DenseOfArray(wx);
            y = Vector<double>.Build.DenseOfArray(wy);
            z = Project(a * x, l, u);
        }
        double primRes = double.PositiveInfinity, dualRes = double.PositiveInfinity;
        var status = QpStatus.MaxIterationsReached;
        var iter = 0;
        var earlyPolished = false;

        for (iter = 1; iter <= s.MaxIterations; iter++)
        {
            var xPrev = x;
            var yPrev = y;

            var rhs = s.Sigma * x - q + a.TransposeThisAndMultiply(rho.PointwiseMultiply(z) - y);
            var xTilde = factor.Solve(rhs);
            var zTilde = a * xTilde;

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

            var ax = a * x;
            var px = p * x;
            var aty = a.TransposeThisAndMultiply(y);
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

            if (IsPrimalInfeasible(a, l, u, y - yPrev, s.EpsInfeasible))
            {
                status = QpStatus.PrimalInfeasible;
                break;
            }

            if (IsDualInfeasible(p, q, a, l, u, x - xPrev, s.EpsInfeasible))
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
                    catch (Exception ex) { return Fail(QpStatus.NumericalError, n, m, $"KKT re-factorization failed: {ex.Message}"); }
                }
            }

            // Early polish: once the iterates are roughly right, the active set is usually already correct and the polish
            // step solves the problem exactly. Accepted only when it meets the strict tolerance (10·EpsAbs) on its own.
            if (s.Polish && s.EarlyPolishInterval > 0 && iter % s.EarlyPolishInterval == 0 &&
                primRes <= s.EarlyPolishTolerance && dualRes <= s.EarlyPolishTolerance &&
                Polish(problem, x, z, y, s, 0, 0) is { } early)
            {
                (x, y, primRes, dualRes) = early;
                status = QpStatus.Solved;
                earlyPolished = true;
                break;
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
            var pol = Polish(problem, x, z, y, s, primRes, dualRes);
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

    private static (Vector<double> X, Vector<double> Y, double Prim, double Dual)? Polish(
        QuadraticProgram problem, Vector<double> x, Vector<double> z, Vector<double> y, QpSettings s, double primAdmm, double dualAdmm)
    {
        var n = problem.Variables;
        var m = problem.Constraints;
        var a = problem.A;
        var l = problem.Lower;
        var u = problem.Upper;

        var lowActive = new List<int>();
        var upActive = new List<int>();
        for (var i = 0; i < m; i++)
        {
            if (z[i] - l[i] < -y[i]) lowActive.Add(i);
            else if (u[i] - z[i] < y[i]) upActive.Add(i);
        }

        var active = lowActive.Concat(upActive).ToArray();
        var k = active.Length;
        var size = n + k;
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
            kktReg[i, i] += s.PolishDelta;
            rhs[i] = -problem.Q[i];
        }

        for (var r = 0; r < k; r++)
        {
            var row = active[r];
            for (var j = 0; j < n; j++)
            {
                kkt[n + r, j] = kkt[j, n + r] = a[row, j];
                kktReg[n + r, j] = kktReg[j, n + r] = a[row, j];
            }
            kktReg[n + r, n + r] = -s.PolishDelta;
            rhs[n + r] = r < lowActive.Count ? l[row] : u[row];
        }

        Vector<double> sol;
        try
        {
            var lu = kktReg.LU();
            sol = lu.Solve(rhs);
            for (var it = 0; it < s.PolishRefinementSteps; it++)
            {
                var residual = rhs - kkt * sol;
                sol += lu.Solve(residual);
            }
        }
        catch
        {
            return null;
        }

        if (!AllFinite(sol)) return null;

        var xPol = sol.SubVector(0, n);
        var yPol = Vector<double>.Build.Dense(m);
        for (var r = 0; r < k; r++) yPol[active[r]] = sol[n + r];

        // Dual sign conditions: multipliers of lower-active rows must be <= 0, upper-active rows >= 0.
        const double signTol = 1e-7;
        for (var r = 0; r < k; r++)
        {
            var row = active[r];
            var isEquality = Math.Abs(u[row] - l[row]) < 1e-12;
            if (isEquality) continue;
            if (r < lowActive.Count && yPol[row] > signTol) return null;
            if (r >= lowActive.Count && yPol[row] < -signTol) return null;
        }

        var ax = a * xPol;
        var prim = 0d;
        for (var i = 0; i < m; i++)
            prim = Math.Max(prim, Math.Max(l[i] - ax[i], ax[i] - u[i]));
        var dual = (problem.P * xPol + problem.Q + a.TransposeThisAndMultiply(yPol)).InfinityNorm();

        var primOk = prim <= Math.Max(primAdmm, s.EpsAbs * 10);
        var dualOk = dual <= Math.Max(dualAdmm, s.EpsAbs * 10);
        return primOk && dualOk ? (xPol, yPol, Math.Max(prim, 0), dual) : null;
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

    private static bool AllFinite(Vector<double> v)
    {
        for (var i = 0; i < v.Count; i++) if (!double.IsFinite(v[i])) return false;
        return true;
    }

    private static QpSolution Fail(QpStatus status, int n, int m, string message) =>
        new(status, new double[n], new double[m], double.NaN, 0, double.NaN, double.NaN, false, message);
}
