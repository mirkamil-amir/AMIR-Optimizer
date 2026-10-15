using PortfolioOptimizer.Optimization.Portfolio;

namespace PortfolioOptimizer.UnitTests.Optimization;

public sealed class AllocationValidatorTests
{
    private static readonly string[] Symbols = ["AAPL", "SAP.DE", "7203.T", "AZN.L"];

    private static readonly PortfolioConstraints Constraints = new()
    {
        MaxWeight = 0.4,
        CashReserve = 0.02,
        Sectors = new Dictionary<string, string> { ["AAPL"] = "Information Technology", ["SAP.DE"] = "Information Technology", ["7203.T"] = "Consumer Discretionary", ["AZN.L"] = "Health Care" },
        SectorMaxWeights = new Dictionary<string, double> { ["Information Technology"] = 0.5 },
        MaxHoldings = 4,
    };

    [Fact]
    public void AFeasibleAllocation_Passes_AndListsTheChecks()
    {
        var r = AllocationValidator.Validate(Symbols, [0.25, 0.25, 0.23, 0.25], Constraints, 0.07, 0.15);
        Assert.True(r.IsValid, string.Join(" ", r.Violations));
        Assert.Contains("sector caps", r.Checks);
        Assert.Contains("invested share", r.Checks);
    }

    [Theory]
    [InlineData(new[] { 0.45, 0.10, 0.23, 0.20 }, "above the maximum")]
    [InlineData(new[] { 0.30, 0.30, 0.18, 0.20 }, "Sector Information Technology")]
    [InlineData(new[] { 0.25, 0.25, 0.25, 0.25 }, "cash reserve")]
    [InlineData(new[] { -0.05, 0.40, 0.33, 0.30 }, "negative weight")]
    [InlineData(new[] { double.NaN, 0.40, 0.33, 0.25 }, "not a finite number")]
    public void ViolatingCandidates_AreRejected_NotRepaired(double[] weights, string expected)
    {
        var r = AllocationValidator.Validate(Symbols, weights, Constraints);
        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, x => x.Contains(expected));
    }

    [Fact]
    public void HoldingsMinimumPositionTurnoverAndExclusions_AreChecked()
    {
        var c = Constraints with { MaxHoldings = 2, MinPositionWeight = 0.1, MaxTurnover = 0.2, ExcludedSymbols = ["AZN.L"], SectorMaxWeights = new Dictionary<string, double>() };
        var r = AllocationValidator.Validate(Symbols, [0.4, 0.4, 0.05, 0.13], c, currentWeights: [0.98, 0, 0, 0]);
        Assert.Contains(r.Violations, x => x.Contains("4 positions held"));
        Assert.Contains(r.Violations, x => x.Contains("7203.T: position"));
        Assert.Contains(r.Violations, x => x.Contains("Turnover"));
        Assert.Contains(r.Violations, x => x.Contains("AZN.L is excluded"));
    }

    [Fact]
    public void NonFiniteEstimates_AreRejected()
    {
        var r = AllocationValidator.Validate(Symbols, [0.25, 0.25, 0.23, 0.25], Constraints, double.PositiveInfinity, double.NaN);
        Assert.Equal(2, r.Violations.Count);
    }

    [Fact]
    public void TheOptimizersOwnResults_PassIndependentValidation()
    {
        // Covariance of four assets; minimum variance with the constraints above.
        double[,] cov = { { 0.09, 0.02, 0.01, 0.01 }, { 0.02, 0.06, 0.01, 0.01 }, { 0.01, 0.01, 0.05, 0.005 }, { 0.01, 0.01, 0.005, 0.04 } };
        var input = new OptimizationInput(Symbols, [0.10, 0.08, 0.06, 0.07], cov, OptimizationObjective.MinimumVariance, Constraints, 0.02);
        var result = new MeanVarianceOptimizer().Optimize(input);
        Assert.True(result.Succeeded, result.Message);
        var r = AllocationValidator.Validate(Symbols, result.Weights!, Constraints, result.Statistics!.ExpectedReturn, result.Statistics.Volatility);
        Assert.True(r.IsValid, string.Join(" ", r.Violations));
    }
}
