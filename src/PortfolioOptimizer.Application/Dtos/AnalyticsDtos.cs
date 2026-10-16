using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization;

namespace PortfolioOptimizer.Application.Dtos;

/// <summary>A metric value or the reason it is unavailable (never NaN).</summary>
public sealed record MetricDto(double? Value, string? UnavailableReason)
{
    public static MetricDto From(MetricValue m) => new(m.Value, m.UnavailableReason);
}

public sealed record PerformancePointDto(DateOnly Date, double Value, double CumulativeReturn, double? BenchmarkCumulativeReturn, double Drawdown);

public sealed record PerformanceDto(
    Guid PortfolioId,
    string BenchmarkSymbol,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<PerformancePointDto> Series,
    MetricDto TotalReturn,
    MetricDto AnnualizedReturn,
    MetricDto AnnualizedVolatility,
    MetricDto SharpeRatio,
    MetricDto SortinoRatio,
    MetricDto MaxDrawdown,
    MetricDto BenchmarkReturn,
    IReadOnlyList<PeriodReturnDto> MonthlyReturns,
    string Methodology,
    string PriceSource,
    bool UsesSimulatedData,
    IReadOnlyList<string> Warnings,
    /// <summary>Currency of the values and returns (stocks converted with the FX rate of each date).</summary>
    string Currency = "USD");

public sealed record PeriodReturnDto(int Year, int Month, double? Portfolio, double? Benchmark);

public sealed record RiskContributionDto(string Symbol, double Weight, double? RiskContribution, double Volatility);

public sealed record RiskDto(
    Guid PortfolioId,
    int LookbackDays,
    DateOnly? From,
    DateOnly? To,
    int Observations,
    double ConfidenceLevel,
    MetricDto AnnualizedVolatility,
    MetricDto ValueAtRisk,
    MetricDto ConditionalValueAtRisk,
    MetricDto Beta,
    MetricDto TrackingError,
    MetricDto InformationRatio,
    MetricDto SharpeRatio,
    MetricDto MaxDrawdown,
    IReadOnlyList<RiskContributionDto> Contributions,
    IReadOnlyList<string> CorrelationSymbols,
    double[][] Correlation,
    string Methodology,
    IReadOnlyList<string> Warnings,
    string Currency = "USD");

public sealed record CreateBacktestRequest : IValidatableObject
{
    [StringLength(100)] public string? Name { get; init; }
    [Required, MinLength(1)] public List<string> Universe { get; init; } = [];
    [Required] public DateOnly StartDate { get; init; }
    [Required] public DateOnly EndDate { get; init; }
    [Range(100, 1_000_000_000)] public double InitialCapital { get; init; } = 100_000;
    public RebalanceKind RebalanceFrequency { get; init; } = RebalanceKind.Monthly;
    public ObjectiveKind Objective { get; init; } = ObjectiveKind.MaximumSharpe;
    public CovarianceKind CovarianceMethod { get; init; } = CovarianceKind.LedoitWolf;
    [Range(60, 2520)] public int LookbackDays { get; init; } = 252;
    [Range(0, 500)] public double TransactionCostBps { get; init; } = 5;
    [Range(-0.05, 0.25)] public double RiskFreeRate { get; init; } = 0.04;
    [StringLength(15)] public string? BenchmarkSymbol { get; init; } = "SPY";
    /// <summary>Analysis currency (ISO 4217); stocks in other currencies are converted with dated FX rates. Default: the selection's own currency, or the application default when it mixes currencies.</summary>
    [RegularExpression("^[A-Z]{3}$")] public string? Currency { get; init; }
    [Range(0, 1)] public double MinWeight { get; init; }
    [Range(0.01, 1)] public double MaxWeight { get; init; } = 0.35;
    [Range(0, 0.99)] public double CashReserve { get; init; }
    [Range(1, 500)] public int? MaxHoldings { get; init; }
    [Range(-1, 5)] public double? TargetReturn { get; init; }
    [Range(0.001, 5)] public double? MaxVolatility { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EndDate <= StartDate) yield return new ValidationResult("EndDate must be after StartDate.", [nameof(EndDate)]);
        if (MinWeight > MaxWeight) yield return new ValidationResult("MinWeight cannot exceed MaxWeight.", [nameof(MinWeight)]);
        if (Objective == ObjectiveKind.TargetReturn && TargetReturn is null)
            yield return new ValidationResult("TargetReturn is required for the TargetReturn objective.", [nameof(TargetReturn)]);
        if (Objective == ObjectiveKind.MaximumReturnForRisk && MaxVolatility is null)
            yield return new ValidationResult("MaxVolatility is required for the MaximumReturnForRisk objective.", [nameof(MaxVolatility)]);
    }
}

public sealed record BacktestSummaryDto(
    Guid Id,
    string Name,
    JobStatus Status,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    string PriceSource,
    bool UsesSimulatedData,
    string? Error);

public sealed record BacktestSeriesMetricsDto(
    string Name,
    double TotalReturn,
    MetricDto AnnualizedReturn,
    MetricDto AnnualizedVolatility,
    MetricDto SharpeRatio,
    MetricDto SortinoRatio,
    MetricDto MaxDrawdown,
    double TotalTurnover,
    double TotalTransactionCosts);

public sealed record BacktestRebalanceDto(
    DateOnly SignalDate,
    DateOnly ExecutionDate,
    IReadOnlyDictionary<string, double> Weights,
    IReadOnlyList<string> EligibleSymbols,
    double Turnover,
    double TransactionCosts,
    double PortfolioValue,
    bool OptimizationSucceeded,
    string Message);

public sealed record BacktestResultDto(
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Values,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Drawdowns,
    IReadOnlyList<BacktestSeriesMetricsDto> Metrics,
    IReadOnlyList<BacktestRebalanceDto> Rebalances,
    IReadOnlyList<PeriodReturnDto> MonthlyReturns,
    MetricDto Beta,
    MetricDto TrackingError,
    MetricDto InformationRatio,
    IReadOnlyList<string> Warnings,
    string Methodology);

public sealed record BacktestDto(
    Guid Id,
    string Name,
    JobStatus Status,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string PriceSource,
    bool UsesSimulatedData,
    string? Error,
    CreateBacktestRequest Config,
    BacktestResultDto? Result);
