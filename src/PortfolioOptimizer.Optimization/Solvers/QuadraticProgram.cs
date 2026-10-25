using MathNet.Numerics.LinearAlgebra;

namespace PortfolioOptimizer.Optimization.Solvers;

/// <summary>
/// Convex quadratic program in the form
/// <c>minimize ½ xᵀ P x + qᵀ x  subject to  l ≤ A x ≤ u</c>,
/// where P is symmetric positive semi-definite. Equality constraints use l = u; one-sided constraints use ±∞.
/// </summary>
public sealed class QuadraticProgram
{
    public QuadraticProgram(Matrix<double> p, Vector<double> q, Matrix<double> a, Vector<double> lower, Vector<double> upper)
    {
        if (p.RowCount != p.ColumnCount) throw new ArgumentException("P must be square.");
        if (q.Count != p.RowCount) throw new ArgumentException("q must match the dimension of P.");
        if (a.ColumnCount != p.RowCount) throw new ArgumentException("A must have one column per variable.");
        if (lower.Count != a.RowCount || upper.Count != a.RowCount) throw new ArgumentException("Bounds must have one entry per constraint row.");
        P = p;
        Q = q;
        A = a;
        Lower = lower;
        Upper = upper;
    }

    public Matrix<double> P { get; }
    public Vector<double> Q { get; }
    public Matrix<double> A { get; }
    public Vector<double> Lower { get; }
    public Vector<double> Upper { get; }

    public int Variables => P.RowCount;
    public int Constraints => A.RowCount;

    public double Objective(Vector<double> x) => 0.5 * x.DotProduct(P * x) + Q.DotProduct(x);
}

public enum QpStatus
{
    Solved,
    SolvedInaccurate,
    PrimalInfeasible,
    DualInfeasible,
    MaxIterationsReached,
    NumericalError,
}

public sealed record QpSettings
{
    public static QpSettings Default { get; } = new();

    public double Rho { get; init; } = 0.1;
    public double Sigma { get; init; } = 1e-6;
    public double Alpha { get; init; } = 1.6;
    public double EpsAbs { get; init; } = 1e-9;
    public double EpsRel { get; init; } = 1e-9;
    public double EpsInfeasible { get; init; } = 1e-9;
    public double InaccurateTolerance { get; init; } = 1e-5;
    public int MaxIterations { get; init; } = 100_000;
    public int AdaptiveRhoInterval { get; init; } = 25;
    /// <summary>Iterations between early polish attempts (0: polish only after convergence).</summary>
    public int EarlyPolishInterval { get; init; } = 50;
    /// <summary>Residuals below which an early polish is attempted.</summary>
    public double EarlyPolishTolerance { get; init; } = 1e-3;
    /// <summary>Iterations between termination and infeasibility checks (OSQP's check_termination).</summary>
    public int CheckInterval { get; init; } = 5;
    public bool Polish { get; init; } = true;
    public double PolishDelta { get; init; } = 1e-9;
    public int PolishRefinementSteps { get; init; } = 5;
    /// <summary>
    /// Warm start: primal and dual iterates of a closely related problem (same variables and rows), e.g. the previous
    /// point of an efficient frontier. Ignored when the dimensions differ.
    /// </summary>
    public double[]? WarmX { get; init; }
    public double[]? WarmY { get; init; }
}

public sealed record QpSolution(
    QpStatus Status,
    double[] X,
    double[] Y,
    double Objective,
    int Iterations,
    double PrimalResidual,
    double DualResidual,
    bool Polished,
    string? Message)
{
    public bool IsOptimal => Status is QpStatus.Solved or QpStatus.SolvedInaccurate;
}

public interface IQuadraticProgramSolver
{
    QpSolution Solve(QuadraticProgram problem, QpSettings? settings = null);
}
