namespace PortfolioOptimizer.Domain;

/// <summary>Configuration used for optimization, trade proposals and rebalancing of a portfolio.</summary>
public sealed record PortfolioSettings
{
    public List<string> Universe { get; init; } = [];
    /// <summary>Estimation window in trading days.</summary>
    public int LookbackDays { get; init; } = 756;
    public ObjectiveKind Objective { get; init; } = ObjectiveKind.MaximumSharpe;
    public CovarianceKind CovarianceMethod { get; init; } = CovarianceKind.LedoitWolf;
    public double RiskFreeRate { get; init; } = 0.04;
    public string BenchmarkSymbol { get; init; } = "SPY";
    public double MinWeight { get; init; }
    public double MaxWeight { get; init; } = 0.35;
    public int? MaxHoldings { get; init; }
    public double? MinPositionWeight { get; init; }
    public double CashReserve { get; init; } = 0.01;
    public double? MaxTurnover { get; init; }
    public Dictionary<string, double> SectorLimits { get; init; } = [];
    public List<string> ExcludedSymbols { get; init; } = [];
    public double? TargetReturn { get; init; }
    public double? MaxVolatility { get; init; }
    public double TransactionCostBps { get; init; } = 5;
    public decimal MinimumFee { get; init; } = 1m;
    public bool AllowFractionalShares { get; init; }
    public RebalanceKind RebalanceFrequency { get; init; } = RebalanceKind.Quarterly;
    /// <summary>Rebalance only positions whose weight drift exceeds this threshold (absolute weight).</summary>
    public double RebalanceDriftThreshold { get; init; } = 0.02;
}

/// <summary>
/// A simulated (paper) portfolio. Cash and holdings change only through <see cref="Buy"/> / <see cref="Sell"/> /
/// <see cref="Deposit"/>, which also append immutable ledger records.
/// </summary>
public class Portfolio
{
    public Guid Id { get; set; }
    /// <summary>The user who owns this portfolio; only the owner can read or change it.</summary>
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string BaseCurrency { get; set; } = "USD";
    public decimal InitialInvestment { get; set; }
    public decimal CashBalance { get; private set; }
    public PortfolioSettings Settings { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    /// <summary>Optimistic concurrency token, incremented on every cash/holding mutation.</summary>
    public int Version { get; set; }

    public List<Holding> Holdings { get; set; } = [];
    public List<CashLedgerEntry> CashEntries { get; set; } = [];
    public List<TradeExecution> Executions { get; set; } = [];

    public CashLedgerEntry Deposit(decimal amount, DateTime timestampUtc, string description)
    {
        if (amount <= 0) throw new DomainException("Deposit amount must be positive.");
        CashBalance += amount;
        return AddCash(CashEntryType.Deposit, amount, timestampUtc, null, description);
    }

    /// <summary>Buy in the portfolio's own currency (the asset must trade in <see cref="BaseCurrency"/>).</summary>
    public TradeExecution Buy(Asset asset, decimal quantity, decimal price, DateOnly priceDate, decimal fee, Guid orderId, DateTime timestampUtc) =>
        Buy(asset, quantity, price, priceDate, TradeFx.SameCurrency, fee, orderId, timestampUtc);

    /// <summary>
    /// Buys <paramref name="quantity"/> at <paramref name="price"/> in the asset's own currency. For a foreign asset the
    /// portfolio's cash is exchanged at <paramref name="fx"/> (a SIMULATED exchange, recorded on the execution); cost basis,
    /// cash flows and <paramref name="fee"/> are in the portfolio currency.
    /// </summary>
    public TradeExecution Buy(Asset asset, decimal quantity, decimal price, DateOnly priceDate, TradeFx fx, decimal fee, Guid orderId, DateTime timestampUtc)
    {
        if (quantity <= 0) throw new DomainException("Buy quantity must be positive.");
        if (price <= 0) throw new DomainException($"Invalid price {price} for {asset.Symbol}.");
        if (fee < 0) throw new DomainException("Fees cannot be negative.");
        var basePrice = BasePrice(asset, price, fx);
        var gross = quantity * basePrice;
        if (gross + fee > CashBalance)
            throw new DomainException($"Insufficient cash to buy {quantity} {asset.Symbol}: requires {gross + fee:N2}, available {CashBalance:N2}.");

        var holding = Holdings.SingleOrDefault(h => h.AssetId == asset.Id);
        if (holding is null)
        {
            holding = new Holding { Id = Guid.NewGuid(), PortfolioId = Id, AssetId = asset.Id, Symbol = asset.Symbol, OpenedAtUtc = timestampUtc };
            Holdings.Add(holding);
        }
        holding.ApplyBuy(quantity, basePrice, fee, timestampUtc);

        CashBalance -= gross + fee;
        var execution = NewExecution(asset, OrderSide.Buy, quantity, price, priceDate, fx, gross, fee, -(gross + fee), null, orderId, timestampUtc);
        AddCash(CashEntryType.BuySettlement, -gross, timestampUtc, execution.Id, $"Buy {quantity} {asset.Symbol} @ {price} {asset.Currency}{FxText(asset, fx)}");
        if (fee > 0) AddCash(CashEntryType.Fee, -fee, timestampUtc, execution.Id, $"Commission {asset.Symbol}");
        Touch(timestampUtc);
        return execution;
    }

    /// <summary>Sell in the portfolio's own currency (the asset must trade in <see cref="BaseCurrency"/>).</summary>
    public TradeExecution Sell(Asset asset, decimal quantity, decimal price, DateOnly priceDate, decimal fee, Guid orderId, DateTime timestampUtc) =>
        Sell(asset, quantity, price, priceDate, TradeFx.SameCurrency, fee, orderId, timestampUtc);

    /// <summary>Sells at <paramref name="price"/> in the asset's own currency; proceeds are exchanged at <paramref name="fx"/> (simulated).</summary>
    public TradeExecution Sell(Asset asset, decimal quantity, decimal price, DateOnly priceDate, TradeFx fx, decimal fee, Guid orderId, DateTime timestampUtc)
    {
        if (quantity <= 0) throw new DomainException("Sell quantity must be positive.");
        if (price <= 0) throw new DomainException($"Invalid price {price} for {asset.Symbol}.");
        if (fee < 0) throw new DomainException("Fees cannot be negative.");
        var holding = Holdings.SingleOrDefault(h => h.AssetId == asset.Id && h.Quantity > 0)
            ?? throw new DomainException($"No position in {asset.Symbol} to sell.");
        var basePrice = BasePrice(asset, price, fx);
        var gross = quantity * basePrice;
        if (gross - fee + CashBalance < 0) throw new DomainException("Fees exceed available cash and sale proceeds.");

        var realized = holding.ApplySell(quantity, basePrice, fee, timestampUtc);
        CashBalance += gross - fee;
        var execution = NewExecution(asset, OrderSide.Sell, quantity, price, priceDate, fx, gross, fee, gross - fee, realized, orderId, timestampUtc);
        AddCash(CashEntryType.SellSettlement, gross, timestampUtc, execution.Id, $"Sell {quantity} {asset.Symbol} @ {price} {asset.Currency}{FxText(asset, fx)}");
        if (fee > 0) AddCash(CashEntryType.Fee, -fee, timestampUtc, execution.Id, $"Commission {asset.Symbol}");
        Touch(timestampUtc);
        return execution;
    }

    /// <summary>Price in the portfolio currency. A foreign asset needs an explicit rate; the same currency must not carry one.</summary>
    private decimal BasePrice(Asset asset, decimal nativePrice, TradeFx fx)
    {
        if (fx.Rate <= 0) throw new DomainException($"Invalid exchange rate {fx.Rate} for {asset.Symbol}.");
        if (asset.Currency == BaseCurrency)
        {
            if (fx.Rate != 1) throw new DomainException($"{asset.Symbol} trades in {BaseCurrency}, the portfolio currency; no exchange rate applies.");
            return nativePrice;
        }
        if (fx.Source is null)
            throw new DomainException($"{asset.Symbol} trades in {asset.Currency} but the portfolio is in {BaseCurrency}: an exchange rate with its source is required.");
        return nativePrice * fx.Rate;
    }

    private string FxText(Asset asset, TradeFx fx) =>
        asset.Currency == BaseCurrency ? "" : $" × {fx.Rate:0.######} {BaseCurrency}/{asset.Currency} ({fx.Source} {fx.RateDate:yyyy-MM-dd}, simulated exchange)";

    private TradeExecution NewExecution(Asset asset, OrderSide side, decimal qty, decimal price, DateOnly priceDate, TradeFx fx, decimal gross, decimal fee,
        decimal netCash, decimal? realized, Guid orderId, DateTime ts)
    {
        var e = new TradeExecution
        {
            Id = Guid.NewGuid(),
            PortfolioId = Id,
            OrderId = orderId,
            AssetId = asset.Id,
            Symbol = asset.Symbol,
            Side = side,
            Quantity = qty,
            Price = price,
            PriceDate = priceDate,
            Currency = asset.Currency,
            FxRate = asset.Currency == BaseCurrency ? 1 : fx.Rate,
            FxRateDate = asset.Currency == BaseCurrency ? null : fx.RateDate,
            FxSource = asset.Currency == BaseCurrency ? null : fx.Source,
            GrossAmount = gross,
            Fees = fee,
            NetCashFlow = netCash,
            RealizedPnl = realized,
            ExecutedAtUtc = ts,
        };
        Executions.Add(e);
        return e;
    }

    private CashLedgerEntry AddCash(CashEntryType type, decimal amount, DateTime ts, Guid? reference, string description)
    {
        var entry = new CashLedgerEntry
        {
            Id = Guid.NewGuid(),
            PortfolioId = Id,
            Type = type,
            Amount = amount,
            BalanceAfter = CashBalance,
            TimestampUtc = ts,
            ReferenceId = reference,
            Description = description,
        };
        CashEntries.Add(entry);
        return entry;
    }

    private void Touch(DateTime ts)
    {
        UpdatedAtUtc = ts;
        Version++;
    }
}

/// <summary>
/// Current position. Cost basis uses the weighted-average cost method: buy commissions are capitalized into the
/// average cost; partial sales do not change the average cost and realize (proceeds − sell fee − avg cost × qty).
/// </summary>
public class Holding
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    public required string Symbol { get; set; }
    public decimal Quantity { get; private set; }
    public decimal AverageCost { get; private set; }
    public decimal RealizedPnl { get; private set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public decimal CostBasis => Quantity * AverageCost;

    public void ApplyBuy(decimal quantity, decimal price, decimal fee, DateTime ts)
    {
        var totalCost = CostBasis + quantity * price + fee;
        Quantity += quantity;
        AverageCost = totalCost / Quantity;
        UpdatedAtUtc = ts;
    }

    /// <returns>Realized P&amp;L of this sale.</returns>
    public decimal ApplySell(decimal quantity, decimal price, decimal fee, DateTime ts)
    {
        if (quantity > Quantity) throw new DomainException($"Cannot sell {quantity} {Symbol}; only {Quantity} held.");
        var realized = quantity * price - fee - quantity * AverageCost;
        Quantity -= quantity;
        RealizedPnl += realized;
        if (Quantity == 0) AverageCost = 0;
        UpdatedAtUtc = ts;
        return realized;
    }
}

/// <summary>
/// Exchange rate applied to a simulated trade: portfolio-currency units per unit of the asset's currency, with the date
/// and source of the observation. This models a currency exchange in the paper portfolio; no real money is converted.
/// </summary>
public readonly record struct TradeFx(decimal Rate, DateOnly? RateDate, string? Source)
{
    public static TradeFx SameCurrency => new(1, null, null);
}

/// <summary>Immutable cash movement. Amount is signed (+ inflow, − outflow).</summary>
public class CashLedgerEntry
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public CashEntryType Type { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public DateTime TimestampUtc { get; set; }
    public Guid? ReferenceId { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>Immutable simulated fill.</summary>
public class TradeExecution
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public Guid OrderId { get; set; }
    public int AssetId { get; set; }
    public required string Symbol { get; set; }
    public OrderSide Side { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Execution price in the instrument's own currency (<see cref="Currency"/>).</summary>
    public decimal Price { get; set; }
    public DateOnly PriceDate { get; set; }
    /// <summary>Trading currency of the instrument.</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>
    /// Portfolio-currency units per unit of <see cref="Currency"/> of the SIMULATED currency exchange that funded the trade
    /// (1 when the currencies match). Gross amount, fees, cash flow and realized P&amp;L are in the portfolio currency.
    /// </summary>
    public decimal FxRate { get; set; } = 1;
    public DateOnly? FxRateDate { get; set; }
    public string? FxSource { get; set; }
    /// <summary>Quantity × price × FX rate, in the portfolio currency.</summary>
    public decimal GrossAmount { get; set; }
    public decimal Fees { get; set; }
    public decimal NetCashFlow { get; set; }
    public decimal? RealizedPnl { get; set; }
    public DateTime ExecutedAtUtc { get; set; }
}

/// <summary>Simple proportional commission with a minimum ticket fee.</summary>
public static class TransactionCostModel
{
    public static decimal Estimate(decimal tradeValue, double bps, decimal minimumFee)
    {
        if (tradeValue <= 0) return 0;
        var proportional = Math.Round(Math.Abs(tradeValue) * (decimal)bps / 10_000m, 2, MidpointRounding.AwayFromZero);
        return Math.Max(proportional, minimumFee);
    }
}
