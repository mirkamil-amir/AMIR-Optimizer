using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.UnitTests.Trading;

/// <summary>Cash events, corrections and the archive lifecycle of a portfolio (domain rules, no database).</summary>
public class PortfolioCashTests
{
    private static readonly Asset Aapl = new() { Id = 1, Symbol = "AAPL" };
    private static readonly Asset Msft = new() { Id = 2, Symbol = "MSFT" };
    private static readonly DateOnly D = new(2026, 1, 2);
    private static readonly DateTime T = new(2026, 1, 2, 20, 0, 0, DateTimeKind.Utc);

    private static Portfolio NewPortfolio(decimal cash)
    {
        var p = new Portfolio { Id = Guid.NewGuid(), Name = "Test" };
        p.Deposit(cash, T, "init");
        return p;
    }

    private static decimal LedgerSum(Portfolio p) => p.CashEntries.Sum(e => e.Amount);

    [Fact]
    public void Withdrawal_ReducesCash_AndIsRecordedAsNegativeEntry()
    {
        var p = NewPortfolio(1_000m);
        var entry = p.Withdraw(250m, T, "out");
        Assert.Equal(750m, p.CashBalance);
        Assert.Equal(CashEntryType.Withdrawal, entry.Type);
        Assert.Equal(-250m, entry.Amount);
        Assert.Equal(750m, entry.BalanceAfter);
        Assert.Equal(p.CashBalance, LedgerSum(p));
    }

    [Fact]
    public void Withdrawal_CannotOverdraw_AndLeavesStateUnchanged()
    {
        var p = NewPortfolio(100m);
        var version = p.Version;
        Assert.Throws<DomainException>(() => p.Withdraw(100.01m, T, "too much"));
        Assert.Equal(100m, p.CashBalance);
        Assert.Equal(version, p.Version);
        Assert.Single(p.CashEntries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CashEvents_RequirePositiveAmounts(decimal amount)
    {
        var p = NewPortfolio(100m);
        Assert.Throws<DomainException>(() => p.Deposit(amount, T, "x"));
        Assert.Throws<DomainException>(() => p.Withdraw(amount, T, "x"));
        Assert.Throws<DomainException>(() => p.ChargeFee(amount, T, "x"));
    }

    [Fact]
    public void Dividend_RequiresAHoldingEverHeld_AndDoesNotChangeQuantityOrCost()
    {
        var p = NewPortfolio(10_000m);
        Assert.Throws<DomainException>(() => p.ReceiveDividend(Aapl, 10m, T, "never held"));

        p.Buy(Aapl, 10, 100m, D, 1m, Guid.NewGuid(), T);
        var holding = p.Holdings.Single();
        var (qty, avg) = (holding.Quantity, holding.AverageCost);
        var entry = p.ReceiveDividend(Aapl, 12.5m, T, "div");
        Assert.Equal(CashEntryType.Dividend, entry.Type);
        Assert.Equal(qty, holding.Quantity);
        Assert.Equal(avg, holding.AverageCost);
        Assert.Equal(10_000m - 1_001m + 12.5m, p.CashBalance);

        // A position sold completely has still been held: a later dividend payment is allowed.
        p.Sell(Aapl, 10, 100m, D, 0m, Guid.NewGuid(), T);
        p.ReceiveDividend(Aapl, 3m, T, "late div");
        Assert.Throws<DomainException>(() => p.ReceiveDividend(Msft, 1m, T, "other stock"));
        Assert.Equal(p.CashBalance, LedgerSum(p));
    }

    [Fact]
    public void Fee_ReducesCash_AndCannotExceedIt()
    {
        var p = NewPortfolio(50m);
        p.ChargeFee(20m, T, "custody");
        Assert.Equal(30m, p.CashBalance);
        Assert.Throws<DomainException>(() => p.ChargeFee(30.01m, T, "too much"));
        Assert.Equal(p.CashBalance, LedgerSum(p));
    }

    [Fact]
    public void Reversal_AppendsOppositeEntry_AndKeepsOriginal()
    {
        var p = NewPortfolio(1_000m);
        var deposit = p.Deposit(500m, T, "mistake");
        var reversal = p.ReverseCashEntry(deposit, T, "fix");
        Assert.Equal(CashEntryType.Deposit, reversal.Type);
        Assert.Equal(-500m, reversal.Amount);
        Assert.Equal(1_000m, p.CashBalance);
        Assert.Equal(3, p.CashEntries.Count);
        Assert.Equal(p.CashBalance, LedgerSum(p));
    }

    [Fact]
    public void Reversal_OfDeposit_NeedsTheCash_AndTradeEntriesCannotBeReversed()
    {
        var p = NewPortfolio(1_000m);
        var deposit = p.Deposit(500m, T, "deposit");
        p.Buy(Aapl, 10, 120m, D, 0m, Guid.NewGuid(), T);
        Assert.Throws<DomainException>(() => p.ReverseCashEntry(deposit, T, "cash already invested"));
        var settlement = p.CashEntries.Single(e => e.Type == CashEntryType.BuySettlement);
        Assert.Throws<DomainException>(() => p.ReverseCashEntry(settlement, T, "trade"));
    }

    [Fact]
    public void ArchivedPortfolio_RefusesChanges_UntilRestored()
    {
        var p = NewPortfolio(100m);
        p.Archive(T);
        Assert.True(p.IsArchived);
        Assert.Throws<DomainException>(p.EnsureActive);
        Assert.Throws<DomainException>(() => p.Archive(T));
        p.Restore(T);
        Assert.False(p.IsArchived);
        p.EnsureActive();
        Assert.Throws<DomainException>(() => p.Restore(T));
    }

    [Fact]
    public void Amounts_AreRoundedToStoredPrecision_SoTheLedgerReconciles()
    {
        var p = NewPortfolio(100m);
        p.Deposit(0.1234567891m, T, "precise");
        p.ChargeFee(0.0000004m, T, "tiny");
        Assert.Equal(p.CashBalance, LedgerSum(p));
        Assert.Equal(100.123457m, p.CashBalance);
    }
}
