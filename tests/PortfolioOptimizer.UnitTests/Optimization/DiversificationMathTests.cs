using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Portfolio;

namespace PortfolioOptimizer.UnitTests.Optimization;

/// <summary>Diversification diagnostics against hand-computed values.</summary>
public sealed class DiversificationMathTests
{
    private static readonly Dictionary<string, string> Sectors = new() { ["A"] = "Tech", ["B"] = "Tech", ["C"] = "Energy" };

    [Fact]
    public void Concentration_UsesInvestedWeights_HhiAndEffectiveHoldings()
    {
        // 1% cash: 0.495/0.297/0.198 normalize to 0.5/0.3/0.2 → HHI = 0.25 + 0.09 + 0.04 = 0.38, effective N = 1/0.38.
        var c = DiversificationMath.Concentration(["A", "B", "C"], [0.495, 0.297, 0.198], Sectors);
        Assert.Equal(0.38, c.Hhi, 12);
        Assert.Equal(1 / 0.38, c.EffectiveHoldings, 12);
        Assert.Equal("A", c.LargestSymbol);
        Assert.Equal(0.5, c.LargestWeight, 12);
        Assert.Equal(1.0, c.TopThreeWeight, 12);
        Assert.Equal(("Tech", 0.8), (c.Sectors[0].Sector, Math.Round(c.Sectors[0].Weight, 12)));
        Assert.Equal(2, c.Sectors[0].Holdings);
    }

    [Fact]
    public void EqualWeights_HaveEffectiveHoldingsEqualToN()
    {
        var c = DiversificationMath.Concentration(["A", "B", "C"], [1 / 3d, 1 / 3d, 1 / 3d], Sectors);
        Assert.Equal(3, c.EffectiveHoldings, 10);
    }

    [Fact]
    public void Risk_UncorrelatedPair_MatchesClosedForm()
    {
        // σ = (20%, 10%), ρ = 0, w = (½, ½): var = ¼·0.04 + ¼·0.01 = 0.0125; RC₁ = ½·0.02 / 0.0125 = 0.8.
        double[,] cov = { { 0.04, 0 }, { 0, 0.01 } };
        var r = DiversificationMath.Risk(["A", "B"], [0.5, 0.5], cov);
        Assert.Equal(Math.Sqrt(0.0125), r.Volatility, 12);
        Assert.Equal(0.8, r.RiskShares[0], 12);
        Assert.Equal(0.2, r.RiskShares[1], 12);
        Assert.Equal(0.15 / Math.Sqrt(0.0125), r.DiversificationRatio, 12);
        Assert.Equal(1 / (0.64 + 0.04), r.EffectiveRiskContributors!.Value, 12);
        Assert.Equal(0, r.AverageCorrelation!.Value, 12);
    }

    [Fact]
    public void Risk_PerfectlyCorrelated_HasNoDiversificationBenefit()
    {
        double[,] cov = { { 0.04, 0.02 }, { 0.02, 0.01 } }; // ρ = 0.02 / (0.2·0.1) = 1
        var r = DiversificationMath.Risk(["A", "B"], [0.5, 0.5], cov);
        Assert.Equal(1, r.DiversificationRatio, 12);
        Assert.Equal(1, r.AverageCorrelation!.Value, 12);
        Assert.Equal(("A", "B"), (r.HighestCorrelation!.Value.A, r.HighestCorrelation.Value.B));
    }

    [Fact]
    public void Weights_MustBeLongOnlyAndMatchTheSymbols()
    {
        Assert.Throws<ArgumentException>(() => DiversificationMath.Concentration(["A", "B"], [0.5, -0.1], Sectors));
        Assert.Throws<ArgumentException>(() => DiversificationMath.Risk(["A"], [0.5, 0.5], new double[2, 2]));
        Assert.Throws<ArgumentException>(() => DiversificationMath.Concentration(["A"], [double.NaN], Sectors));
    }

    [Fact]
    public void History_DrawdownAndCumulativeReturn_ByHand()
    {
        // Wealth 1 → 1.1 → 0.55 → 0.66: drawdown 0.55/1.1 − 1 = −50 %; cumulative 0.66 − 1 = −34 %.
        double[,] returns = { { 0.1, 0.1 }, { -0.5, -0.5 }, { 0.2, 0.2 } };
        var daily = DiversificationMath.PortfolioReturns(returns, [0.5, 0.5]);
        Assert.Equal([0.1, -0.5, 0.2], daily.Select(d => Math.Round(d, 12)).ToArray());
        var h = DiversificationMath.History(daily, AnalyticsConventions.Default);
        Assert.Equal(-0.34, h.CumulativeReturn, 12);
        Assert.Equal(-0.5, h.MaxDrawdown.Value!.Value, 12);
        Assert.Equal(-0.5, h.WorstDay, 12);
        Assert.Null(h.Worst20DayReturn); // fewer than 20 days
    }

    [Fact]
    public void CashReserve_EarnsNothing()
    {
        double[,] returns = { { 0.10, 0.10 } };
        Assert.Equal(0.099, DiversificationMath.PortfolioReturns(returns, [0.495, 0.495])[0], 12);
    }

    [Fact]
    public void CorrelationUnderStress_DetectsCorrelationThatRisesOnBadDays()
    {
        // 80 calm days: the stocks move in opposite directions around +1% (ρ = −1, basket +1%);
        // 20 crash days (every 5th): both fall together by varying amounts (ρ = +1, basket ≤ −4.5%).
        const int days = 100;
        var r = new double[days, 2];
        for (var d = 0; d < days; d++)
        {
            if (d % 5 == 0) { var k = d / 5; r[d, 0] = -0.05 - 0.002 * k; r[d, 1] = -0.04 - 0.003 * k; }
            else { var x = (d % 2 == 0 ? 1 : -1) * 0.01; r[d, 0] = 0.01 + x; r[d, 1] = 0.01 - x; }
        }
        // Use the worst 20% so the stress sample has at least 20 days.
        var s = DiversificationMath.CorrelationUnderStress(r, [0, 1], tailShare: 0.2);
        Assert.True(s.StressDayCount >= 20);
        Assert.NotNull(s.StressDays);
        Assert.True(s.StressDays > s.AllDays, $"stress {s.StressDays} vs all {s.AllDays}");
    }

    [Fact]
    public void CorrelationUnderStress_NeedsEnoughData()
    {
        var s = DiversificationMath.CorrelationUnderStress(new double[10, 2], [0, 1]);
        Assert.Null(s.AllDays);
        Assert.Null(s.StressDays);
    }
}
