using MathNet.Numerics.LinearAlgebra;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Solvers;

namespace PortfolioOptimizer.UnitTests.Optimization;

public class OptimizerTests
{
    private static readonly string[] Three = ["A", "B", "C"];

    // Annualized: vols 20%, 25%, 30%; correlations 0.3, 0.2, 0.4.
    private static readonly double[,] Cov3 = BuildCov([0.20, 0.25, 0.30], new double[,] { { 1, 0.3, 0.2 }, { 0.3, 1, 0.4 }, { 0.2, 0.4, 1 } });
    private static readonly double[] Mu3 = [0.08, 0.10, 0.14];

    private readonly MeanVarianceOptimizer _optimizer = new();

    private static double[,] BuildCov(double[] vol, double[,] corr)
    {
        var n = vol.Length;
        var c = new double[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            c[i, j] = corr[i, j] * vol[i] * vol[j];
        return c;
    }

    private static OptimizationInput Input(OptimizationObjective objective, PortfolioConstraints? c = null, double rf = 0.02,
        string[]? symbols = null, double[]? mu = null, double[,]? cov = null, double[]? current = null) =>
        new(symbols ?? Three, mu ?? Mu3, cov ?? Cov3, objective, c ?? new PortfolioConstraints(), rf, current);

    [Fact]
    public void MaximumReturn_IsTheExactLinearProgramOptimum_WithBoundsAndSectorCaps()
    {
        // Max weight 40 %, Tech (B, C) capped at 50 %: the LP optimum is C 40 %, B 10 % (Tech full), A 40 %, D 10 %.
        string[] symbols = ["A", "B", "C", "D"];
        var c = new PortfolioConstraints
        {
            MaxWeight = 0.4,
            Sectors = new Dictionary<string, string> { ["A"] = "Health", ["B"] = "Tech", ["C"] = "Tech", ["D"] = "Energy" },
            SectorMaxWeights = new Dictionary<string, double> { ["Tech"] = 0.5 },
            TargetReturn = 0.2,
        };
        var cov = BuildCov([0.2, 0.25, 0.3, 0.22], new double[,] { { 1, 0.2, 0.2, 0.1 }, { 0.2, 1, 0.5, 0.1 }, { 0.2, 0.5, 1, 0.1 }, { 0.1, 0.1, 0.1, 1 } });
        double[] mu = [0.10, 0.12, 0.15, 0.05];
        // The highest achievable expected return: 0.4·0.15 + 0.1·0.12 + 0.4·0.10 + 0.1·0.05 = 0.117.
        var result = _optimizer.Optimize(Input(OptimizationObjective.TargetReturn, c, symbols: symbols, mu: mu, cov: cov));
        Assert.Equal(OptimizationStatus.Infeasible, result.Status);
        Assert.Matches(@"maximum achievable expected return 11\.70\s?%", result.Message);
        var frontier = _optimizer.EfficientFrontier(Input(OptimizationObjective.MinimumVariance, c, symbols: symbols, mu: mu, cov: cov), 8);
        Assert.Equal(0.117, frontier[^1].ExpectedReturn, 9);                   // the frontier ends at the exact maximum
        Assert.All(frontier.Zip(frontier.Skip(1)), p => Assert.True(p.Second.ExpectedReturn >= p.First.ExpectedReturn - 1e-9));
    }

    [Fact]
    public void MinimumVariance_TwoAssets_MatchesClosedForm()
    {
        var cov = BuildCov([0.2, 0.3], new double[,] { { 1, 0.25 }, { 0.25, 1 } });
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, symbols: ["X", "Y"], mu: [0.05, 0.08], cov: cov));

        Assert.True(result.Succeeded, result.Message);
        var s11 = cov[0, 0]; var s22 = cov[1, 1]; var s12 = cov[0, 1];
        var expected = (s22 - s12) / (s11 + s22 - 2 * s12);
        Assert.Equal(expected, result.Weights![0], 6);
        Assert.Equal(1 - expected, result.Weights[1], 6);
    }

    [Fact]
    public void MinimumVariance_InteriorSolution_MatchesAnalyticalGmv()
    {
        // When no bound is active, the long-only GMV equals Σ⁻¹1 / 1ᵀΣ⁻¹1.
        var sigma = Matrix<double>.Build.DenseOfArray(Cov3);
        var ones = Vector<double>.Build.Dense(3, 1);
        var raw = sigma.Solve(ones);
        var expected = raw / raw.Sum();
        Assert.All(expected, v => Assert.True(v > 0));

        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance));

        Assert.True(result.Succeeded, result.Message);
        for (var i = 0; i < 3; i++) Assert.Equal(expected[i], result.Weights![i], 6);
        Assert.Equal(1.0, result.Weights!.Sum(), 9);
    }

    [Fact]
    public void MaximumSharpe_InteriorSolution_MatchesTangencyPortfolio()
    {
        const double rf = 0.02;
        var sigma = Matrix<double>.Build.DenseOfArray(Cov3);
        var excess = Vector<double>.Build.DenseOfArray(Mu3) - rf;
        var raw = sigma.Solve(excess);
        var expected = raw / raw.Sum();
        Assert.All(expected, v => Assert.True(v > 0));

        var result = _optimizer.Optimize(Input(OptimizationObjective.MaximumSharpe, rf: rf));

        Assert.True(result.Succeeded, result.Message);
        for (var i = 0; i < 3; i++) Assert.Equal(expected[i], result.Weights![i], 5);
    }

    [Fact]
    public void MaximumSharpe_IsAtLeastAsGoodAsEveryFrontierPoint()
    {
        var c = new PortfolioConstraints { MaxWeight = 0.5 };
        var best = _optimizer.Optimize(Input(OptimizationObjective.MaximumSharpe, c));
        var frontier = _optimizer.EfficientFrontier(Input(OptimizationObjective.MinimumVariance, c), 40);

        Assert.True(best.Succeeded, best.Message);
        Assert.NotEmpty(frontier);
        Assert.All(frontier, p => Assert.True(p.SharpeRatio <= best.Statistics!.SharpeRatio + 1e-6));
        Assert.All(best.Weights!, w => Assert.InRange(w, 0, 0.5 + 1e-9));
    }

    [Fact]
    public void MaxWeightConstraint_IsRespected()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MaximumSharpe, new PortfolioConstraints { MaxWeight = 0.4 }));
        Assert.True(result.Succeeded, result.Message);
        Assert.All(result.Weights!, w => Assert.InRange(w, 0, 0.4 + 1e-9));
        Assert.Equal(1.0, result.Weights!.Sum(), 9);
    }

    [Fact]
    public void CashReserve_ReducesInvestedFraction()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, new PortfolioConstraints { CashReserve = 0.1 }));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0.9, result.Weights!.Sum(), 9);
    }

    [Fact]
    public void TargetReturn_IsMetWithMinimumVariance()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.TargetReturn, new PortfolioConstraints { TargetReturn = 0.12 }));
        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Statistics!.ExpectedReturn >= 0.12 - 1e-7);
    }

    [Fact]
    public void TargetReturn_AboveMaximumAchievable_IsInfeasibleWithExplanation()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.TargetReturn, new PortfolioConstraints { TargetReturn = 0.20 }));
        Assert.Equal(OptimizationStatus.Infeasible, result.Status);
        Assert.Null(result.Weights);
        Assert.Contains("exceeds the maximum achievable", result.Message);
    }

    [Fact]
    public void MaxReturnForRisk_RespectsVolatilityLimitAndBinds()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MaximumReturnForRisk, new PortfolioConstraints { MaxVolatility = 0.20 }));
        Assert.True(result.Succeeded, result.Message);
        Assert.InRange(result.Statistics!.Volatility, 0.20 - 1e-4, 0.20 + 1e-6);
    }

    [Fact]
    public void MaxReturnForRisk_BelowMinimumVolatility_IsInfeasible()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MaximumReturnForRisk, new PortfolioConstraints { MaxVolatility = 0.05 }));
        Assert.Equal(OptimizationStatus.Infeasible, result.Status);
        Assert.Contains("lowest achievable volatility", result.Message);
    }

    [Fact]
    public void MaxWeightTooSmall_IsInfeasibleWithExplanation()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, new PortfolioConstraints { MaxWeight = 0.3 }));
        Assert.Equal(OptimizationStatus.Infeasible, result.Status);
        Assert.Null(result.Weights);
        Assert.Contains("cannot reach the invested fraction", result.Message);
    }

    [Fact]
    public void MinimumWeightsAboveBudget_IsInfeasible()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, new PortfolioConstraints { MinWeight = 0.4 }));
        Assert.Equal(OptimizationStatus.Infeasible, result.Status);
    }

    [Fact]
    public void SectorLimit_IsRespected()
    {
        var c = new PortfolioConstraints
        {
            Sectors = new Dictionary<string, string> { ["A"] = "Tech", ["B"] = "Tech", ["C"] = "Energy" },
            SectorMaxWeights = new Dictionary<string, double> { ["Tech"] = 0.6 },
        };
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, c));
        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Weights![0] + result.Weights[1] <= 0.6 + 1e-6);
    }

    [Fact]
    public void ConflictingSectorLimits_AreInfeasible()
    {
        var c = new PortfolioConstraints
        {
            Sectors = new Dictionary<string, string> { ["A"] = "Tech", ["B"] = "Tech", ["C"] = "Energy" },
            SectorMaxWeights = new Dictionary<string, double> { ["Tech"] = 0.3, ["Energy"] = 0.3 },
        };
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, c));
        Assert.Equal(OptimizationStatus.Infeasible, result.Status);
        Assert.Contains("Sector limits", result.Message);
    }

    [Fact]
    public void MaxHoldings_LimitsPositionCount()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, new PortfolioConstraints { MaxHoldings = 2 }));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Weights!.Count(w => w > 1e-6));
        Assert.Contains(result.Diagnostics, d => d.StartsWith("Heuristic"));
    }

    [Fact]
    public void TurnoverLimit_IsRespectedAgainstCurrentWeights()
    {
        var current = new[] { 0.6, 0.2, 0.2 };
        var c = new PortfolioConstraints { MaxTurnover = 0.05 };
        var result = _optimizer.Optimize(Input(OptimizationObjective.MaximumSharpe, c, current: current));
        Assert.True(result.Succeeded, result.Message);
        var turnover = result.Weights!.Zip(current, (a, b) => Math.Abs(a - b)).Sum() / 2;
        Assert.True(turnover <= 0.05 + 1e-6, $"turnover {turnover}");
    }

    [Fact]
    public void SingleAsset_GetsFullWeight()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MaximumSharpe, symbols: ["A"], mu: [0.1], cov: new double[,] { { 0.04 } }));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1.0, result.Weights![0], 9);
    }

    [Fact]
    public void HighlyCorrelatedAssets_ProduceValidWeights()
    {
        var cov = BuildCov([0.2, 0.2001, 0.25], new double[,] { { 1, 0.9999, 0.5 }, { 0.9999, 1, 0.5 }, { 0.5, 0.5, 1 } });
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, cov: cov));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1.0, result.Weights!.Sum(), 6);
        Assert.All(result.Weights!, w => Assert.True(w >= 0));
    }

    [Fact]
    public void ZeroVolatilityAsset_IsExcludedWithDiagnostic()
    {
        var cov = new double[,] { { 0.04, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0.09 } };
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, cov: cov));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0, result.Weights![1]);
        Assert.Contains(result.Diagnostics, d => d.Contains("zero variance"));
    }

    [Fact]
    public void MaxSharpe_WhenNoAssetBeatsRiskFree_IsInfeasible()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MaximumSharpe, rf: 0.5));
        Assert.Equal(OptimizationStatus.Infeasible, result.Status);
        Assert.Contains("risk-free", result.Message);
    }

    [Fact]
    public void ShortSelling_IsRejected()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.MinimumVariance, new PortfolioConstraints { MinWeight = -0.1 }));
        Assert.Equal(OptimizationStatus.InvalidInput, result.Status);
    }

    [Fact]
    public void EfficientFrontier_IsMonotone()
    {
        var frontier = _optimizer.EfficientFrontier(Input(OptimizationObjective.MinimumVariance), 20);
        Assert.Equal(20, frontier.Count);
        for (var i = 1; i < frontier.Count; i++)
        {
            Assert.True(frontier[i].ExpectedReturn >= frontier[i - 1].ExpectedReturn - 1e-9);
            Assert.True(frontier[i].Volatility >= frontier[i - 1].Volatility - 1e-7);
        }
    }

    [Fact]
    public void RiskContributions_SumToOne()
    {
        var result = _optimizer.Optimize(Input(OptimizationObjective.EqualWeight));
        Assert.True(result.Succeeded);
        Assert.Equal(1.0, result.RiskContributions!.Sum(), 9);
    }

    [Fact]
    public void QpSolver_DetectsInfeasibleConstraints()
    {
        // x1 + x2 = 1, x1 ≤ 0.2, x2 ≤ 0.2
        var p = Matrix<double>.Build.DenseIdentity(2);
        var q = Vector<double>.Build.Dense(2);
        var a = Matrix<double>.Build.DenseOfArray(new double[,] { { 1, 1 }, { 1, 0 }, { 0, 1 } });
        var l = Vector<double>.Build.DenseOfArray([1, double.NegativeInfinity, double.NegativeInfinity]);
        var u = Vector<double>.Build.DenseOfArray([1, 0.2, 0.2]);
        var sol = new AdmmQpSolver().Solve(new QuadraticProgram(p, q, a, l, u));
        Assert.Equal(QpStatus.PrimalInfeasible, sol.Status);
    }

    [Fact]
    public void QpSolver_SolvesSimpleBoxProblem()
    {
        // min (x-2)² + (y+1)² s.t. 0 ≤ x,y ≤ 1  → (1, 0)
        var p = Matrix<double>.Build.DenseIdentity(2) * 2;
        var q = Vector<double>.Build.DenseOfArray([-4, 2]);
        var a = Matrix<double>.Build.DenseIdentity(2);
        var l = Vector<double>.Build.Dense(2, 0);
        var u = Vector<double>.Build.Dense(2, 1);
        var sol = new AdmmQpSolver().Solve(new QuadraticProgram(p, q, a, l, u));
        Assert.Equal(QpStatus.Solved, sol.Status);
        Assert.Equal(1, sol.X[0], 8);
        Assert.Equal(0, sol.X[1], 8);
    }
}
