namespace PortfolioOptimizer.Domain;

/// <summary>
/// A persisted optimization run with everything needed to reproduce it: inputs (settings snapshot, universe, window,
/// price source and a hash of the exact price observations used), outputs and solver diagnostics.
/// </summary>
public class OptimizationRun
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    /// <summary>Owner of the portfolio when the run was created (denormalized for per-user history queries).</summary>
    public Guid OwnerId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public ObjectiveKind Objective { get; set; }
    public OptimizationRunStatus Status { get; set; }
    public string Message { get; set; } = "";
    public List<string> Symbols { get; set; } = [];
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    public int Observations { get; set; }
    public required string PriceSource { get; set; }
    public bool UsesSimulatedData { get; set; }
    /// <summary>ISO currency of all values (analysis/base currency); prices in other currencies were converted into it.</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>FX source used for the conversion (null when every input was already in <see cref="Currency"/>).</summary>
    public string? FxSource { get; set; }
    public string DataSnapshotHash { get; set; } = "";
    public PortfolioSettings SettingsSnapshot { get; set; } = new();
    public double? ExpectedReturn { get; set; }
    public double? Volatility { get; set; }
    public double? SharpeRatio { get; set; }
    public int SolverIterations { get; set; }
    public int SolverCalls { get; set; }
    public double DurationMs { get; set; }
    public List<string> Diagnostics { get; set; } = [];
    /// <summary>JSON payload with frontier, correlation matrix, per-asset statistics and reference portfolios.</summary>
    public string AnalyticsJson { get; set; } = "{}";
    public List<OptimizationRunWeight> Weights { get; set; } = [];
}

public class OptimizationRunWeight
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public required string Symbol { get; set; }
    public int AssetId { get; set; }
    public double Weight { get; set; }
    public double ExpectedReturn { get; set; }
    public double Volatility { get; set; }
    public double? RiskContribution { get; set; }
}

/// <summary>A proposed set of trades. Never changes holdings by itself; execution is a separate, explicit action.</summary>
public class TradeProposal
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public Guid? OptimizationRunId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public ProposalStatus Status { get; set; }
    public bool AllowFractionalShares { get; set; }
    public decimal PortfolioValue { get; set; }
    public decimal CashBefore { get; set; }
    public decimal EstimatedCashAfter { get; set; }
    public decimal EstimatedCosts { get; set; }
    public decimal TargetCashWeight { get; set; }
    /// <summary>Portfolio version when the proposal was created; execution is refused if holdings changed since.</summary>
    public int PortfolioVersion { get; set; }
    public List<string> Notes { get; set; } = [];
    public List<TradeProposalLine> Lines { get; set; } = [];
}

public class TradeProposalLine
{
    public long Id { get; set; }
    public Guid ProposalId { get; set; }
    public int AssetId { get; set; }
    public required string Symbol { get; set; }
    public string? CompanyName { get; set; }
    /// <summary>Latest price in the instrument's own currency (<see cref="Currency"/>).</summary>
    public decimal? Price { get; set; }
    public DateOnly? PriceDate { get; set; }
    public bool PriceIsStale { get; set; }
    /// <summary>Trading currency of the instrument.</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>Portfolio-currency units per unit of <see cref="Currency"/> used for the estimate (1 for the same currency).</summary>
    public decimal? FxRate { get; set; }
    public DateOnly? FxRateDate { get; set; }
    public double TargetWeight { get; set; }
    public double CurrentWeight { get; set; }
    public decimal CurrentQuantity { get; set; }
    public decimal TargetValue { get; set; }
    public decimal TargetQuantity { get; set; }
    public decimal OrderQuantity { get; set; }
    public ProposalAction Action { get; set; }
    public decimal EstimatedTradeValue { get; set; }
    public decimal EstimatedCost { get; set; }
    public double ResultingWeight { get; set; }
    public string? Note { get; set; }
}

/// <summary>One confirmed paper-trading execution of a proposal, keyed by a client idempotency key.</summary>
public class PaperTradeBatch
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public Guid ProposalId { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTime ExecutedAtUtc { get; set; }
    public decimal TotalFees { get; set; }
    public decimal CashAfter { get; set; }
    public List<string> Notes { get; set; } = [];
    public List<SimulatedOrder> Orders { get; set; } = [];
}

public class SimulatedOrder
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid PortfolioId { get; set; }
    public Guid ProposalId { get; set; }
    public int AssetId { get; set; }
    public required string Symbol { get; set; }
    public OrderSide Side { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public OrderStatus Status { get; set; }
    /// <summary>Execution price in the instrument's own currency (<see cref="Currency"/>).</summary>
    public decimal? ExecutionPrice { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>Portfolio-currency units per unit of <see cref="Currency"/> of the simulated currency exchange.</summary>
    public decimal? FxRate { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? Message { get; set; }
}

public class BacktestRun
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    /// <summary>Portfolio whose optimization is backtested, or whose settings a custom backtest started from (optional).</summary>
    public Guid? PortfolioId { get; set; }
    public string Name { get; set; } = "";
    public JobStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public required string PriceSource { get; set; }
    public bool UsesSimulatedData { get; set; }
    /// <summary>ISO currency of all values (analysis/base currency); prices in other currencies were converted into it.</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>FX source used for the conversion (null when every input was already in <see cref="Currency"/>).</summary>
    public string? FxSource { get; set; }
}
