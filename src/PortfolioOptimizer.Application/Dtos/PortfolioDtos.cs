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
    /// <example>100000</example>
    [Range(typeof(decimal), "100", "1000000000")] public decimal InitialInvestment { get; init; } = 100_000m;
    [Required, RegularExpression("^[A-Z]{3}$")] public string BaseCurrency { get; init; } = "USD";
    /// <summary>Optional settings; defaults are used when omitted.</summary>
    public PortfolioSettingsDto? Settings { get; init; }
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
    decimal MarketValue,
    decimal TotalValue,
    int Holdings,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record PortfolioDto(
    Guid Id,
    string Name,
    string BaseCurrency,
    decimal InitialInvestment,
    decimal CashBalance,
    PortfolioSettingsDto Settings,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int Version);

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
    DateTime UpdatedAtUtc);

public sealed record PortfolioValuationDto(
    decimal MarketValue,
    decimal CashBalance,
    decimal TotalValue,
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
    IReadOnlyList<string> Warnings);

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
    DateTime ExecutedAtUtc);

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
    string? Message);
