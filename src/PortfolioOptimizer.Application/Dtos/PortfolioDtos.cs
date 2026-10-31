using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Dtos;

/// <summary>Portfolio configuration. Weights and rates are fractions (0.35 = 35%).</summary>
public sealed record PortfolioSettingsDto : IValidatableObject
{
    /// <example>["AAPL","MSFT","NVDA","AMZN","GOOGL"]</example>
    [Required, MinLength(1)]
    public List<string> Universe { get; init; } = [];

    /// <summary>Estimation window in trading days (252 ≈ 1 year).</summary>
    [Range(60, 5040)] public int LookbackDays { get; init; } = 756;
    public ObjectiveKind Objective { get; init; } = ObjectiveKind.MaximumSharpe;
    public CovarianceKind CovarianceMethod { get; init; } = CovarianceKind.LedoitWolf;
    /// <summary>Annual risk-free rate (assumption).</summary>
    [Range(-0.05, 0.25)] public double RiskFreeRate { get; init; } = 0.04;
    [Required, StringLength(15)] public string BenchmarkSymbol { get; init; } = "SPY";
    [Range(0, 1)] public double MinWeight { get; init; }
    [Range(0.01, 1)] public double MaxWeight { get; init; } = 0.35;
    [Range(1, 500)] public int? MaxHoldings { get; init; }
    [Range(0, 1)] public double? MinPositionWeight { get; init; }
    [Range(0, 0.99)] public double CashReserve { get; init; } = 0.01;
    [Range(0, 1)] public double? MaxTurnover { get; init; }
    public Dictionary<string, double> SectorLimits { get; init; } = [];
    public List<string> ExcludedSymbols { get; init; } = [];
    [Range(-1, 5)] public double? TargetReturn { get; init; }
    [Range(0.001, 5)] public double? MaxVolatility { get; init; }
    [Range(0, 500)] public double TransactionCostBps { get; init; } = 5;
    [Range(0, 1000)] public decimal MinimumFee { get; init; } = 1m;
    public bool AllowFractionalShares { get; init; }
    public RebalanceKind RebalanceFrequency { get; init; } = RebalanceKind.Quarterly;
    [Range(0, 1)] public double RebalanceDriftThreshold { get; init; } = 0.02;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinWeight > MaxWeight)
            yield return new ValidationResult("MinWeight cannot exceed MaxWeight.", [nameof(MinWeight), nameof(MaxWeight)]);
        if (Objective == ObjectiveKind.TargetReturn && TargetReturn is null)
            yield return new ValidationResult("TargetReturn is required for the TargetReturn objective.", [nameof(TargetReturn)]);
        if (Objective == ObjectiveKind.MaximumReturnForRisk && MaxVolatility is null)
            yield return new ValidationResult("MaxVolatility is required for the MaximumReturnForRisk objective.", [nameof(MaxVolatility)]);
        if (MinPositionWeight is { } mp && mp > MaxWeight)
            yield return new ValidationResult("MinPositionWeight cannot exceed MaxWeight.", [nameof(MinPositionWeight)]);
        foreach (var (sector, cap) in SectorLimits)
        {
            if (cap is < 0 or > 1)
                yield return new ValidationResult($"Sector limit for '{sector}' must be between 0 and 1.", [nameof(SectorLimits)]);
        }
    }
}

public sealed record CreatePortfolioRequest
{
    /// <example>Core US Equity</example>
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; init; } = "";
    /// <example>Long-term diversified core holdings</example>
    [StringLength(500)] public string? Description { get; init; }
    /// <example>100000</example>
    [Range(typeof(decimal), "100", "1000000000")] public decimal InitialInvestment { get; init; } = 100_000m;
    [Required, RegularExpression("^[A-Z]{3}$")] public string BaseCurrency { get; init; } = "USD";
    /// <summary>Optional settings; defaults are used when omitted.</summary>
    public PortfolioSettingsDto? Settings { get; init; }
    /// <summary>
    /// Working universe chosen in New portfolio (used with default settings when <see cref="Settings"/> is omitted).
    /// It is the set the optimizer may use – not holdings: nothing is bought until a proposal is confirmed.
    /// </summary>
    [MaxLength(500)] public List<string>? Universe { get; init; }
    /// <summary>Optional maximum weight per stock (0–1) with default settings.</summary>
    [Range(0.01, 1.0)] public double? MaxWeight { get; init; }
}

/// <summary>Renames a portfolio or changes its description (no effect on holdings, settings or history).</summary>
public sealed record UpdatePortfolioDetailsRequest
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; init; } = "";
    [StringLength(500)] public string? Description { get; init; }
}

public sealed record UpdatePortfolioRequest
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; init; } = "";
    [Required] public PortfolioSettingsDto Settings { get; init; } = new();
}

public sealed record PortfolioSummaryDto(
    Guid Id,
    string Name,
    string BaseCurrency,
    decimal InitialInvestment,
    decimal CashBalance,
    /// <summary>Null when a held position cannot be converted into the base currency (see <paramref name="ValuationProblems"/>).</summary>
    decimal? MarketValue,
    decimal? TotalValue,
    int Holdings,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<string>? ValuationProblems = null,
    string? Description = null,
    /// <summary>"Active" or "Archived".</summary>
    string Status = "Active",
    DateTime? ArchivedAtUtc = null,
    /// <summary>Deposits − withdrawals (external cash flows), in the base currency.</summary>
    decimal NetContributions = 0,
    /// <summary>Total value − net contributions (includes fees, dividends, realized and unrealized P&amp;L); null when not valued.</summary>
    decimal? ProfitLoss = null,
    /// <summary>Latest stored price date among the held positions (null without holdings).</summary>
    DateOnly? ValuationDate = null,
    /// <summary>Held positions whose latest price is older than the stale threshold.</summary>
    int StalePositions = 0,
    bool UsesSimulatedData = false,
    DateTime? LatestOptimizationAtUtc = null,
    /// <summary>Latest buy or sell settlement in the ledger (deposits, fees and dividends do not count).</summary>
    DateTime? LastTradeAtUtc = null,
    /// <summary>The same figures in the overview's chosen currency (overview only; null in plain lists).</summary>
    PortfolioInCurrencyDto? InCurrency = null);

/// <summary>
/// A portfolio's figures in a reporting currency, for viewing only (nothing is exchanged). Today's value is converted like the
/// Holdings reporting view (positions at the rate of their price date, cash at the latest rate); every deposit and withdrawal at
/// the rate of its own date, so <see cref="ProfitLoss"/> includes the currency effect since the money came in. Null amounts
/// with <see cref="Problems"/> when a rate is missing – never a plausible-looking number.
/// </summary>
public sealed record PortfolioInCurrencyDto(
    string Currency,
    decimal? TotalValue,
    decimal? MarketValue,
    decimal? Cash,
    decimal? NetContributions,
    decimal? ProfitLoss,
    IReadOnlyList<string> Problems);

/// <summary>
/// The user's portfolios plus totals over the active ones in <paramref name="Currency"/>. The total is null – never a partial
/// sum – when any active portfolio cannot be valued or converted (see <paramref name="TotalProblems"/>).
/// </summary>
public sealed record PortfolioOverviewDto(
    IReadOnlyList<PortfolioSummaryDto> Portfolios,
    string Currency,
    decimal? TotalValue,
    decimal? TotalNetContributions,
    int ActivePortfolios,
    int ArchivedPortfolios,
    IReadOnlyList<string> TotalProblems,
    string Methodology);

public sealed record PortfolioDto(
    Guid Id,
    string Name,
    string BaseCurrency,
    decimal InitialInvestment,
    decimal CashBalance,
    PortfolioSettingsDto Settings,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int Version,
    string? Description = null,
    string Status = "Active",
    DateTime? ArchivedAtUtc = null);

/// <summary>
/// A manual transaction. Buy/sell need <see cref="Symbol"/>, <see cref="Quantity"/> and <see cref="Price"/> (in the security's
/// trading currency); deposit, withdrawal and fee need <see cref="Amount"/> in the portfolio currency; a dividend needs
/// <see cref="Symbol"/> and the net <see cref="Amount"/> received in the portfolio currency.
/// </summary>
public sealed record RecordTransactionRequest
{
    [Required] public PortfolioTransactionType Type { get; init; }
    [StringLength(32)] public string? Symbol { get; init; }
    [Range(typeof(decimal), "0.000001", "1000000000")] public decimal? Quantity { get; init; }
    [Range(typeof(decimal), "0.000001", "1000000000")] public decimal? Price { get; init; }
    [Range(typeof(decimal), "0.01", "1000000000")] public decimal? Amount { get; init; }
    /// <summary>Commission of a trade in the portfolio currency (default 0).</summary>
    [Range(typeof(decimal), "0", "1000000")] public decimal? Fee { get; init; }
    /// <summary>Effective date (default today, UTC). Not in the future and not before the portfolio's latest booking.</summary>
    public DateOnly? Date { get; init; }
    [StringLength(500)] public string? Note { get; init; }
}

public sealed record ReverseTransactionRequest
{
    [StringLength(500)] public string? Note { get; init; }
}

public sealed record PortfolioTransactionDto(
    Guid Id,
    Guid PortfolioId,
    PortfolioTransactionType Type,
    string? Symbol,
    decimal? Quantity,
    decimal? Price,
    string Currency,
    decimal FxRate,
    DateOnly? FxRateDate,
    string? FxSource,
    decimal Fees,
    decimal CashAmount,
    DateOnly EffectiveDate,
    DateTime BookedAtUtc,
    string? Note,
    Guid? ExecutionId,
    Guid? ReversesId,
    Guid? ReversedById,
    decimal? RealizedPnl,
    decimal CashBalanceAfter,
    bool IdempotentReplay = false);

public sealed record HoldingDto(
    int AssetId,
    string Symbol,
    string? Name,
    string? Sector,
    decimal Quantity,
    decimal AverageCost,
    decimal CostBasis,
    decimal? LatestPrice,
    DateOnly? PriceDate,
    bool PriceIsStale,
    decimal? MarketValue,
    double? Weight,
    decimal? UnrealizedPnl,
    double? UnrealizedReturn,
    decimal RealizedPnl,
    double? TargetWeight,
    double? WeightDeviation,
    decimal? DailyChange,
    DateTime UpdatedAtUtc,
    /// <summary>Trading currency of the stock; <see cref="LatestPrice"/> is in it. Money amounts are in the valuation currency.</summary>
    string Currency = "USD",
    /// <summary>Valuation-currency units per unit of <see cref="Currency"/> at the price date (null for the same currency).</summary>
    decimal? FxRate = null,
    DateOnly? FxRateDate = null,
    /// <summary>Why the position cannot be valued (missing price or exchange rate).</summary>
    string? ValuationProblem = null);

/// <summary>
/// Valuation in <paramref name="Currency"/>. Market value and total are null when a position cannot be converted. Cost basis,
/// realized P&amp;L, fees and contributions are booked in the account currency and, for another reporting currency, shown at
/// today's account→reporting rate (<paramref name="AccountToReportingRate"/>). Returns are measured in the account currency.
/// </summary>
public sealed record PortfolioValuationDto(
    decimal? MarketValue,
    decimal CashBalance,
    decimal? TotalValue,
    double InvestedFraction,
    decimal TotalCostBasis,
    decimal UnrealizedPnl,
    decimal RealizedPnl,
    decimal TotalFees,
    decimal NetContributions,
    decimal? DailyChange,
    double? DailyChangePercent,
    double TotalReturn,
    double? BenchmarkReturn,
    double? ExcessReturnVsBenchmark,
    string BenchmarkSymbol,
    DateOnly? ValuationDate,
    string PriceSource,
    bool UsesSimulatedData,
    IReadOnlyList<string> Warnings,
    string Currency = "USD",
    string AccountCurrency = "USD",
    decimal? AccountToReportingRate = null,
    string? FxSource = null,
    IReadOnlyList<CurrencyExposureDto>? CurrencyExposure = null,
    IReadOnlyList<string>? ValuationProblems = null);

public sealed record HoldingsResponse(PortfolioValuationDto Summary, IReadOnlyList<HoldingDto> Holdings);

public sealed record TransactionDto(
    Guid Id,
    Guid OrderId,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal Price,
    DateOnly PriceDate,
    decimal GrossAmount,
    decimal Fees,
    decimal NetCashFlow,
    decimal? RealizedPnl,
    DateTime ExecutedAtUtc,
    /// <summary>Currency of <see cref="Price"/>; gross amount, fees, cash flow and P&amp;L are in the portfolio currency.</summary>
    string Currency = "USD",
    /// <summary>Portfolio-currency units per unit of <see cref="Currency"/> of the simulated exchange (1 for the same currency).</summary>
    decimal FxRate = 1,
    DateOnly? FxRateDate = null,
    string? FxSource = null);

public sealed record CashEntryDto(Guid Id, CashEntryType Type, decimal Amount, decimal BalanceAfter, DateTime TimestampUtc, string Description);

public sealed record OrderDto(
    Guid Id,
    Guid BatchId,
    Guid ProposalId,
    string Symbol,
    OrderSide Side,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    OrderStatus Status,
    decimal? ExecutionPrice,
    DateTime CreatedAtUtc,
    string? Message,
    string Currency = "USD",
    decimal? FxRate = null);
