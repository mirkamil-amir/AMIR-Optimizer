namespace PortfolioOptimizer.Domain;

/// <summary>Kinds of activity a user can record manually in a portfolio.</summary>
public enum PortfolioTransactionType { Buy, Sell, Deposit, Withdrawal, Dividend, Fee }

/// <summary>
/// A manually recorded portfolio transaction (simulated bookkeeping – it is not evidence that a real trade happened).
/// It is the audit record of the user's input; its effect lives in the ledger it created: a <see cref="TradeExecution"/>
/// for buys and sells (plus their cash settlement), a <see cref="CashLedgerEntry"/> for cash events. Records are never
/// edited or deleted: an erroneous cash event is corrected by a reversing transaction (<see cref="ReversesId"/>).
/// </summary>
public class PortfolioTransaction
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public PortfolioTransactionType Type { get; set; }
    /// <summary>Security of a buy, sell or dividend.</summary>
    public int? AssetId { get; set; }
    public string? Symbol { get; set; }
    public decimal? Quantity { get; set; }
    /// <summary>Price per share in <see cref="Currency"/> as entered by the user (buy/sell only).</summary>
    public decimal? Price { get; set; }
    /// <summary>Currency of <see cref="Price"/> (the security's trading currency) or of the cash amount (portfolio currency).</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>Portfolio-currency units per unit of <see cref="Currency"/> applied (1 for the same currency).</summary>
    public decimal FxRate { get; set; } = 1;
    public DateOnly? FxRateDate { get; set; }
    public string? FxSource { get; set; }
    /// <summary>Commission of a trade, in the portfolio currency.</summary>
    public decimal Fees { get; set; }
    /// <summary>Signed cash effect in the portfolio currency (+ inflow, − outflow), fees included.</summary>
    public decimal CashAmount { get; set; }
    /// <summary>Day the activity took effect (the price date of a trade).</summary>
    public DateOnly EffectiveDate { get; set; }
    /// <summary>Ledger time of the booking; ledger order follows it.</summary>
    public DateTime BookedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? Note { get; set; }
    /// <summary>Client key that makes a repeated submission return the original record instead of booking twice.</summary>
    public required string IdempotencyKey { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? CashEntryId { get; set; }
    /// <summary>The transaction this one corrects (a reversal), if any.</summary>
    public Guid? ReversesId { get; set; }
    /// <summary>The reversal that cancelled this transaction, if any.</summary>
    public Guid? ReversedById { get; set; }

    public bool IsTrade => Type is PortfolioTransactionType.Buy or PortfolioTransactionType.Sell;
}
