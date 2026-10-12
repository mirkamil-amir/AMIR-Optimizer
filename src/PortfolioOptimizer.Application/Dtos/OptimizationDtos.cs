using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Dtos;

public sealed record RunOptimizationRequest
{
    /// <summary>Settings to use for this run. When omitted, the portfolio's saved settings are used.</summary>
    public PortfolioSettingsDto? Settings { get; init; }

    /// <summary>Save <see cref="Settings"/> as the portfolio's configuration.</summary>
    public bool PersistSettings { get; init; } = true;

    /// <summary>Number of efficient frontier points.</summary>
    [Range(2, 100)] public int FrontierPoints { get; init; } = 30;
}

public sealed record AllocationDto(
    string Symbol,
    string? Name,
    string? Sector,
    double Weight,
    double ExpectedReturn,
    double Volatility,
    double? RiskContribution);

public sealed record OptimizationRunSummaryDto(
    Guid Id,
    Guid PortfolioId,
    DateTime CreatedAtUtc,
    ObjectiveKind Objective,
    OptimizationRunStatus Status,
    string Message,
    double? ExpectedReturn,
    double? Volatility,
    double? SharpeRatio,
    int AssetCount,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    string PriceSource,
    bool UsesSimulatedData);

public sealed record FrontierPointDto(double ExpectedReturn, double Volatility, double? SharpeRatio, IReadOnlyDictionary<string, double> Weights);

public sealed record ReferencePortfolioDto(string Name, double? ExpectedReturn, double? Volatility, double? SharpeRatio, string? Note);

public sealed record AssetStatisticsDto(
    string Symbol,
    double ExpectedReturn,
    double Volatility,
    double? SharpeRatio,
    int DroppedObservations,
    bool UsesAdjustedClose);

public sealed record InSampleSeriesDto(IReadOnlyList<DateOnly> Dates, IReadOnlyDictionary<string, IReadOnlyList<double>> CumulativeReturns);

/// <summary>Supplementary analytics persisted with each run.</summary>
public sealed record RunAnalyticsDto(
    IReadOnlyList<FrontierPointDto> Frontier,
    IReadOnlyList<string> CorrelationSymbols,
    double[][] Correlation,
    IReadOnlyList<AssetStatisticsDto> AssetStatistics,
    IReadOnlyList<ReferencePortfolioDto> References,
    InSampleSeriesDto? InSample,
    double CovarianceShrinkage,
    IReadOnlyList<string> ExcludedForData);

public sealed record OptimizationRunDto(
    Guid Id,
    Guid PortfolioId,
    DateTime CreatedAtUtc,
    ObjectiveKind Objective,
    OptimizationRunStatus Status,
    string Message,
    IReadOnlyList<string> Symbols,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    int Observations,
    string PriceSource,
    bool UsesSimulatedData,
    string DataSnapshotHash,
    PortfolioSettingsDto Settings,
    double? ExpectedReturn,
    double? Volatility,
    double? SharpeRatio,
    int SolverIterations,
    int SolverCalls,
    double DurationMs,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<AllocationDto> Allocation,
    RunAnalyticsDto Analytics,
    string Methodology);

public sealed record TradeProposalLineDto(
    string Symbol,
    string? CompanyName,
    decimal? Price,
    DateOnly? PriceDate,
    bool PriceIsStale,
    double TargetWeight,
    double CurrentWeight,
    double ResultingWeight,
    decimal CurrentQuantity,
    decimal TargetValue,
    decimal TargetQuantity,
    decimal OrderQuantity,
    ProposalAction Action,
    decimal EstimatedTradeValue,
    decimal EstimatedCost,
    string? Note);

public sealed record TradeProposalDto(
    Guid Id,
    Guid PortfolioId,
    Guid? OptimizationRunId,
    DateTime CreatedAtUtc,
    ProposalStatus Status,
    bool AllowFractionalShares,
    decimal PortfolioValue,
    decimal CashBefore,
    decimal EstimatedCashAfter,
    decimal EstimatedCosts,
    decimal TotalBuys,
    decimal TotalSells,
    double TargetCashWeight,
    double ResultingCashWeight,
    IReadOnlyList<string> Notes,
    IReadOnlyList<TradeProposalLineDto> Lines);

public sealed record RebalancePreviewRequest
{
    /// <summary>Optimization run whose weights are the target. Defaults to the most recent successful run.</summary>
    public Guid? OptimizationRunId { get; init; }

    /// <summary>Explicit target weights by symbol (fractions). Overrides the run when supplied.</summary>
    public Dictionary<string, double>? TargetWeights { get; init; }
}

public sealed record ExecutePaperTradesRequest
{
    [Required] public Guid ProposalId { get; init; }

    /// <summary>Must be true: the client confirms the simulated execution.</summary>
    public bool Confirm { get; init; }
}

public sealed record PaperTradeResultDto(
    Guid BatchId,
    Guid ProposalId,
    DateTime ExecutedAtUtc,
    bool IdempotentReplay,
    decimal TotalFees,
    decimal CashAfter,
    IReadOnlyList<OrderDto> Orders,
    IReadOnlyList<string> Notes);
