using MathNet.Numerics.LinearAlgebra;

namespace PortfolioOptimizer.Optimization.Statistics;

public enum CovarianceMethod
{
    /// <summary>Sample covariance with n-1 denominator.</summary>
    Sample,
    /// <summary>Ledoit-Wolf (2004) shrinkage towards a scaled identity matrix.</summary>
    LedoitWolf,
}

/// <summary>Estimated annualized moments of an aligned return matrix.</summary>
public sealed record MomentEstimates(
    double[] ExpectedReturns,
    double[,] Covariance,
    double[,] Correlation,
    double[] Volatilities,
    double ShrinkageIntensity);

public static class CovarianceEstimator
{
    /// <summary>
    /// Estimates annualized expected returns and covariance from a T×N matrix of aligned daily simple returns
    /// (rows = dates, columns = assets).
    /// </summary>
    public static MomentEstimates Estimate(double[,] returns, CovarianceMethod method, AnalyticsConventions conventions)
    {
        var t = returns.GetLength(0);
        var n = returns.GetLength(1);
        if (n == 0) throw new ArgumentException("At least one asset is required.", nameof(returns));
        if (t < 2) throw new ArgumentException("At least two return observations are required.", nameof(returns));

        var x = Matrix<double>.Build.DenseOfArray(returns);
        var means = Vector<double>.Build.Dense(n, j => x.Column(j).Average());
        var centered = Matrix<double>.Build.Dense(t, n, (i, j) => x[i, j] - means[j]);
        var sample = centered.TransposeThisAndMultiply(centered) / (t - 1);

        var shrinkage = 0d;
        var cov = sample;
        if (method == CovarianceMethod.LedoitWolf)
            (cov, shrinkage) = LedoitWolf(centered);

        var annual = conventions.TradingDaysPerYear;
        var covArr = new double[n, n];
        var vols = new double[n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            covArr[i, j] = 0.5 * (cov[i, j] + cov[j, i]) * annual; // enforce exact symmetry
        for (var i = 0; i < n; i++) vols[i] = Math.Sqrt(Math.Max(0, covArr[i, i]));

        return new MomentEstimates(
            means.Select(m => m * annual).ToArray(),
            covArr,
            ToCorrelation(covArr),
            vols,
            shrinkage);
    }

    public static double[,] ToCorrelation(double[,] covariance)
    {
        var n = covariance.GetLength(0);
        var corr = new double[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            var d = Math.Sqrt(covariance[i, i] * covariance[j, j]);
            corr[i, j] = i == j ? 1d : d > 1e-18 ? Math.Clamp(covariance[i, j] / d, -1d, 1d) : 0d;
        }
        return corr;
    }

    /// <summary>Ledoit &amp; Wolf (2004), "A well-conditioned estimator for large-dimensional covariance matrices".</summary>
    private static (Matrix<double> Covariance, double Shrinkage) LedoitWolf(Matrix<double> centered)
    {
        var t = centered.RowCount;
        var n = centered.ColumnCount;
        var s = centered.TransposeThisAndMultiply(centered) / t; // MLE normalization as in the paper
        var mu = s.Trace() / n;
        var target = Matrix<double>.Build.DenseIdentity(n) * mu;
        var d2 = (s - target).FrobeniusNorm();
        d2 *= d2;

        var b2Bar = 0d;
        for (var k = 0; k < t; k++)
        {
            var row = centered.Row(k);
            var diff = row.OuterProduct(row) - s;
            var f = diff.FrobeniusNorm();
            b2Bar += f * f;
        }
        b2Bar /= (double)t * t;

        var b2 = Math.Min(b2Bar, d2);
        var shrink = d2 > 0 ? b2 / d2 : 1d;
        // Rescale to the unbiased (n-1) normalization so that zero shrinkage equals the sample estimator.
        var est = (target * shrink + s * (1 - shrink)) * ((double)t / (t - 1));
        return (est, shrink);
    }
}
