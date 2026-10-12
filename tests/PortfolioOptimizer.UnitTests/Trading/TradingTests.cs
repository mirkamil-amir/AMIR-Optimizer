using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Trading;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.UnitTests.Trading;

public class TradeProposalCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static LatestPrice Px(int id, string s, decimal p, bool stale = false) =>
        new(id, s, p, Today, Today.ToDateTime(TimeOnly.MinValue), "Daily close", stale, false);

    private static ProposalParameters Params(decimal cash, IReadOnlyList<ProposalAsset> assets, Dictionary<int, double> targets,
        bool fractional = false, double bps = 0, decimal minFee = 0, double drift = 0) =>
        new(cash, assets, targets, fractional, bps, minFee, drift);

    [Fact]
    public void WholeShares_AreFlooredAndCashIsConsistent()
    {
        var assets = new[]
        {
            new ProposalAsset(1, "AAPL", null, 0, Px(1, "AAPL", 190m)),
            new ProposalAsset(2, "MSFT", null, 0, Px(2, "MSFT", 420m)),
        };
        var r = TradeProposalCalculator.Compute(Params(10_000m, assets, new() { [1] = 0.5, [2] = 0.5 }));

        var aapl = r.Lines.Single(l => l.Symbol == "AAPL");
        var msft = r.Lines.Single(l => l.Symbol == "MSFT");
        Assert.Equal(26m, aapl.OrderQuantity);   // floor(5000 / 190) = 26
        Assert.Equal(11m, msft.OrderQuantity);   // floor(5000 / 420) = 11
        Assert.Equal(10_000m - 26 * 190m - 11 * 420m, r.CashAfter);
        Assert.Equal(ProposalAction.Buy, aapl.Action);
    }

    [Fact]
    public void Fees_NeverCauseNegativeCash()
    {
        var assets = new[] { new ProposalAsset(1, "X", null, 0, Px(1, "X", 100m)) };
        // 100% target: 100 shares cost exactly all cash; a 10 bps fee forces a reduction.
        var r = TradeProposalCalculator.Compute(Params(10_000m, assets, new() { [1] = 1.0 }, bps: 10, minFee: 1));
        var line = r.Lines.Single();
        Assert.Equal(99m, line.OrderQuantity);
        Assert.True(r.CashAfter >= 0);
        Assert.Equal(10_000m - 9_900m - 9.90m, r.CashAfter);
        Assert.Contains("Reduced", line.Note);
    }

    [Fact]
    public void Sells_FundBuys_AndExitedPositionsAreSold()
    {
        var assets = new[]
        {
            new ProposalAsset(1, "OLD", null, 50, Px(1, "OLD", 100m)),
            new ProposalAsset(2, "NEW", null, 0, Px(2, "NEW", 50m)),
        };
        // Value = 1,000 cash + 5,000 = 6,000; target 100% NEW → 120 shares.
        var r = TradeProposalCalculator.Compute(Params(1_000m, assets, new() { [2] = 1.0 }));
        Assert.Equal(ProposalAction.Sell, r.Lines.Single(l => l.Symbol == "OLD").Action);
        Assert.Equal(50m, r.Lines.Single(l => l.Symbol == "OLD").OrderQuantity);
        Assert.Equal(120m, r.Lines.Single(l => l.Symbol == "NEW").OrderQuantity);
        Assert.Equal(0m, r.CashAfter);
    }

    [Fact]
    public void FractionalShares_UseFourDecimals()
    {
        var assets = new[] { new ProposalAsset(1, "X", null, 0, Px(1, "X", 300m)) };
        var r = TradeProposalCalculator.Compute(Params(1_000m, assets, new() { [1] = 0.5 }, fractional: true));
        Assert.Equal(1.6666m, r.Lines.Single().OrderQuantity);
    }

    [Fact]
    public void MissingPrice_IsReportedAndNotTraded()
    {
        var assets = new[]
        {
            new ProposalAsset(1, "A", null, 0, Px(1, "A", 10m)),
            new ProposalAsset(2, "B", null, 0, null),
        };
        var r = TradeProposalCalculator.Compute(Params(1_000m, assets, new() { [1] = 0.5, [2] = 0.5 }));
        Assert.Equal(ProposalAction.NoPrice, r.Lines.Single(l => l.Symbol == "B").Action);
        Assert.Contains(r.Notes, n => n.Contains("B: no price"));
        Assert.Equal(500m, r.CashAfter);
    }

    [Fact]
    public void StalePrice_IsFlagged()
    {
        var assets = new[] { new ProposalAsset(1, "A", null, 0, Px(1, "A", 10m, stale: true)) };
        var r = TradeProposalCalculator.Compute(Params(1_000m, assets, new() { [1] = 1.0 }));
        Assert.True(r.Lines.Single().PriceIsStale);
        Assert.Contains(r.Notes, n => n.Contains("stale"));
    }

    [Fact]
    public void DriftThreshold_SkipsSmallRebalances()
    {
        var assets = new[]
        {
            new ProposalAsset(1, "A", null, 50, Px(1, "A", 100m)),   // 5,000 = 50%
            new ProposalAsset(2, "B", null, 49, Px(2, "B", 100m)),   // 4,900 = 49%
        };
        var r = TradeProposalCalculator.Compute(Params(100m, assets, new() { [1] = 0.495, [2] = 0.505 }, drift: 0.02));
        Assert.All(r.Lines, l => Assert.Equal(ProposalAction.Hold, l.Action));
    }

    [Fact]
    public void MinimumFee_Applies()
    {
        Assert.Equal(1m, TransactionCostModel.Estimate(100m, 5, 1m));
        Assert.Equal(5m, TransactionCostModel.Estimate(10_000m, 5, 1m));
        Assert.Equal(0m, TransactionCostModel.Estimate(0m, 5, 1m));
    }
}

public class PortfolioAccountingTests
{
    private static readonly Asset Aapl = new() { Id = 1, Symbol = "AAPL" };
    private static readonly DateOnly D = new(2026, 1, 2);

    private static Portfolio NewPortfolio(decimal cash)
    {
        var p = new Portfolio { Id = Guid.NewGuid(), Name = "Test" };
        p.Deposit(cash, DateTime.UtcNow, "init");
        return p;
    }

    [Fact]
    public void Buy_CapitalizesFeesIntoAverageCost()
    {
        var p = NewPortfolio(10_000m);
        p.Buy(Aapl, 10, 100m, D, 5m, Guid.NewGuid(), DateTime.UtcNow);
        p.Buy(Aapl, 10, 120m, D, 5m, Guid.NewGuid(), DateTime.UtcNow);

        var h = p.Holdings.Single();
        Assert.Equal(20m, h.Quantity);
        Assert.Equal((1000m + 5 + 1200m + 5) / 20m, h.AverageCost);
        Assert.Equal(10_000m - 2210m, p.CashBalance);
        Assert.Equal(5, p.CashEntries.Count); // deposit + 2×(settlement + fee)
        Assert.Equal(p.CashBalance, p.CashEntries[^1].BalanceAfter);
    }

    [Fact]
    public void PartialSell_RealizesPnlAndKeepsAverageCost()
    {
        var p = NewPortfolio(10_000m);
        p.Buy(Aapl, 10, 100m, D, 10m, Guid.NewGuid(), DateTime.UtcNow); // avg 101
        var e = p.Sell(Aapl, 4, 150m, D, 2m, Guid.NewGuid(), DateTime.UtcNow);

        var h = p.Holdings.Single();
        Assert.Equal(6m, h.Quantity);
        Assert.Equal(101m, h.AverageCost);
        Assert.Equal(4 * 150m - 2m - 4 * 101m, e.RealizedPnl);
        Assert.Equal(e.RealizedPnl, h.RealizedPnl);
        Assert.Equal(10_000m - 1010m + 598m, p.CashBalance);
    }

    [Fact]
    public void Buy_WithInsufficientCash_Throws()
    {
        var p = NewPortfolio(1_000m);
        Assert.Throws<DomainException>(() => p.Buy(Aapl, 10, 100m, D, 1m, Guid.NewGuid(), DateTime.UtcNow));
        Assert.Empty(p.Holdings);
        Assert.Equal(1_000m, p.CashBalance);
    }

    [Fact]
    public void Sell_MoreThanHeld_Throws()
    {
        var p = NewPortfolio(10_000m);
        p.Buy(Aapl, 5, 100m, D, 0m, Guid.NewGuid(), DateTime.UtcNow);
        Assert.Throws<DomainException>(() => p.Sell(Aapl, 6, 100m, D, 0m, Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void FullSell_ResetsAverageCost_AndIncrementsVersion()
    {
        var p = NewPortfolio(10_000m);
        var v0 = p.Version;
        p.Buy(Aapl, 5, 100m, D, 0m, Guid.NewGuid(), DateTime.UtcNow);
        p.Sell(Aapl, 5, 90m, D, 0m, Guid.NewGuid(), DateTime.UtcNow);
        var h = p.Holdings.Single();
        Assert.Equal(0m, h.Quantity);
        Assert.Equal(0m, h.AverageCost);
        Assert.Equal(-50m, h.RealizedPnl);
        Assert.Equal(v0 + 2, p.Version);
    }
}

public class PriceNormalizerTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly End = new(2026, 12, 31);
    private static readonly DateOnly Today = new(2026, 6, 30);

    [Fact]
    public void RejectsInvalidRows_AndCollapsesDuplicates()
    {
        var raw = new[]
        {
            new RawBar(new(2026, 1, 5), 10, 11, 9, 10.5, 10.4, 100),
            new RawBar(new(2026, 1, 5), 10, 11, 9, 10.6, 10.5, 100),   // duplicate date, last wins
            new RawBar(new(2026, 1, 6), 10, 11, 9, null, null, 100),   // missing close
            new RawBar(new(2026, 1, 7), 10, 11, 9, -1, null, 100),     // negative
            new RawBar(new(2026, 1, 8), 10, 9, 11, 10, 10, 100),       // high < low
            new RawBar(new(2026, 1, 9), 10, 11, 9, double.NaN, 1, 100),
            new RawBar(new(2026, 7, 1), 10, 11, 9, 10, 10, 100),       // future
            new RawBar(new(2025, 12, 31), 10, 11, 9, 10, 10, 100),     // out of range (ignored, not rejected)
        };
        var r = PriceNormalizer.Normalize(raw, Start, End, Today);
        Assert.Single(r.Bars);
        Assert.Equal(10.6m, r.Bars[0].Close);
        Assert.Equal(1, r.Duplicates);
        Assert.Equal(5, r.Rejected);
    }

    [Fact]
    public void RoundsRawPricesToFourDecimals()
    {
        var r = PriceNormalizer.Normalize([new RawBar(new(2026, 2, 2), null, null, null, 263.730011, 262.1234567, null)], Start, End, Today);
        Assert.Equal(263.73m, r.Bars[0].Close);
        Assert.Equal(262.123457m, r.Bars[0].AdjustedClose);
    }
}
