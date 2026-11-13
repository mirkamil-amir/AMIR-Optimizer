using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Research;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Pilot;

public sealed record PilotModelDto(string Provider, string Model, bool Available, bool IsTestModel);

/// <summary>A portfolio of the caller with its pilot (if any) – AI Probe's list.</summary>
public sealed record PilotPortfolioRowDto(Guid PortfolioId, string Name, string BaseCurrency, string Status, PilotStatus? PilotStatus,
    bool Running, DateTime? LastDecisionAtUtc, DateTime? NextDecisionAtUtc);

public sealed record PilotHomeDto(PilotModelDto ConfiguredModel, IReadOnlyList<PilotPortfolioRowDto> Portfolios);

public sealed record PilotDto(
    Guid Id,
    Guid PortfolioId,
    string PortfolioName,
    string BaseCurrency,
    PilotStatus Status,
    // ACTIVE, RUNNING (a cycle is queued or holds the portfolio), PAUSED, KILL SWITCH or DISABLED.
    string DisplayStatus,
    PilotCyclePhase? RunningPhase,
    PilotDecisionFrequency Frequency,
    int MandateVersion,
    PilotMandate Mandate,
    string Provider,
    string Model,
    string Effort,
    bool ModelMatchesConfiguration,
    DateTime CreatedAtUtc,
    DateTime? EnabledAtUtc,
    DateTime? LastDecisionAtUtc,
    DateTime? LastSuccessfulDecisionAtUtc,
    DateTime? NextDecisionAtUtc,
    DateTime? PausedAtUtc,
    string? PauseReason,
    int ConsecutiveFailures,
    PilotBaseline? Baseline,
    PilotEvaluationDto Evaluation,
    PilotCycleSummaryDto? LatestCycle,
    PilotCycleSummaryDto? LatestDecision);

public sealed record PilotOverviewDto(Guid PortfolioId, string PortfolioName, string BaseCurrency, bool PortfolioActive, PilotModelDto ConfiguredModel,
    PilotDto? Pilot, PilotMandate SuggestedMandate, string Label);

/// <summary>How the experiment is doing, counted from the recorded cycles (nothing selected or smoothed).</summary>
public sealed record PilotEvaluationDto(
    int Cycles,
    int Rebalanced,
    int NoTrade,
    int Skipped,
    int Blocked,
    int Failed,
    int PausedByDrawdown,
    int TradesBooked,
    decimal Fees,
    double? CostDragPct,
    double TurnoverSum,
    double? AverageTurnoverPerRebalance,
    int ToolCalls,
    int ToolFailures,
    int DeniedToolRequests,
    int InputTokens,
    int OutputTokens,
    double? AverageDaysBetweenDecisions,
    double? HitRate1M,
    int HitRateSample,
    int FitRevisions = 0,
    int DecisionsThatFittedFirstTime = 0,
    int UnconstrainedPortfolios = 0);

public sealed record PilotCycleSummaryDto(
    Guid Id,
    string CycleKey,
    string Trigger,
    DateTime AsOfUtc,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    PilotCyclePhase Phase,
    PilotCycleOutcome Outcome,
    string OutcomeLabel,
    string? OutcomeReason,
    string? Action,
    string? Approach,
    string? Confidence,
    int Targets,
    int TradesBooked,
    int ModelCalls,
    int ToolCalls,
    double TotalMs,
    string Provider,
    string Model,
    int MandateVersion);

public sealed record PilotStepDto(int Sequence, DateTime AtUtc, PilotStepKind Kind, string Category, string Name, string Status, double DurationMs,
    string Summary, string? InputJson, string? OutputJson, string? OutputHash);

public sealed record PilotCycleDetailDto(
    PilotCycleSummaryDto Summary,
    Guid CorrelationId,
    string? ModelReported,
    string Effort,
    string PromptVersion,
    string ToolsVersion,
    string SchemaVersion,
    int InputTokens,
    int OutputTokens,
    double DataMs,
    double ToolMs,
    double ModelMs,
    double ValidationMs,
    double ExecutionMs,
    PilotObservation? Observation,
    PilotDecision? Decision,
    PilotCritique? Critique,
    PilotValidationResult? Validation,
    PilotExecuted? Executed,
    Guid? ProposalId,
    Guid? TradeBatchId,
    decimal Fees,
    IReadOnlyList<InterpretationSection> Narrative,
    string? NarrativeSource,
    IReadOnlyList<PilotStepDto> Steps,
    PilotDecision? InitialDecision = null,
    int FitRevisions = 0,
    PilotShadow? Unconstrained = null,
    PilotPortfolioComparison? Comparison = null);

public sealed record PilotEventDto(long Id, DateTime AtUtc, PilotActor Actor, string Action, PilotStatus? PreviousStatus, PilotStatus? NewStatus,
    string? Reason, Guid? CycleId, Guid? CorrelationId);

public sealed record PilotMandateVersionDto(int Version, DateTime CreatedAtUtc, string? Reason, IReadOnlyList<PilotMandateChange> Changes, PilotMandate Mandate);

// ------------------------------------------------------------------------------------------------ requests

public sealed record CreatePilotRequest
{
    public PilotMandate Mandate { get; init; } = new();
    public PilotDecisionFrequency Frequency { get; init; } = PilotDecisionFrequency.Weekly;
    public string Effort { get; init; } = "high";
}

/// <summary>Switching Matrix Pilot on: the owner confirms autonomous management of this portfolio with this model.</summary>
public sealed record EnablePilotRequest
{
    public bool Confirm { get; init; }
    /// <summary>The model the owner saw in the confirmation (must equal the configured one).</summary>
    public string? Provider { get; init; }
    public string? Model { get; init; }
}

public sealed record PilotReasonRequest
{
    public string? Reason { get; init; }
}

public sealed record UpdatePilotMandateRequest
{
    public PilotMandate Mandate { get; init; } = new();
    public PilotDecisionFrequency Frequency { get; init; }
    public string Effort { get; init; } = "high";
    public string? Reason { get; init; }
    public bool Confirm { get; init; }
    /// <summary>Adopt the configured model (when it differs from the pilot's).</summary>
    public bool AdoptConfiguredModel { get; init; }
}

public sealed record RunPilotNowRequest
{
    public bool Confirm { get; init; }
}

// ------------------------------------------------------------------------------------------------ live record comparison

public sealed record PilotComparisonSeriesDto(
    string Key,
    string Label,
    Guid? PortfolioId,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<PilotComparisonPointDto> Points,
    MetricDto TotalReturn,
    MetricDto AnnualizedReturn,
    MetricDto Volatility,
    MetricDto Sharpe,
    MetricDto Sortino,
    MetricDto MaxDrawdown,
    int Trades,
    decimal Fees,
    double? CashWeight,
    int Holdings,
    double? LargestWeight,
    IReadOnlyList<string> Warnings);

public sealed record PilotComparisonPointDto(DateOnly Date, double CumulativeReturn, double Drawdown);

public sealed record PilotComparisonDto(
    Guid PilotPortfolioId,
    string Currency,
    DateTime? BaselineAtUtc,
    DateOnly? Start,
    PilotComparisonSeriesDto Pilot,
    PilotComparisonSeriesDto? Other,
    PilotComparisonSeriesDto? Benchmark,
    IReadOnlyList<PilotMonthlyDto> Monthly,
    PilotEvaluationDto Evaluation,
    IReadOnlyList<PilotTimelineEntryDto> Timeline,
    string Methodology,
    IReadOnlyList<string> Warnings);

public sealed record PilotMonthlyDto(string Month, double? Pilot, double? Other, double? Benchmark);

public sealed record PilotTimelineEntryDto(DateTime AtUtc, string Kind, string Text, Guid? CycleId);

// ------------------------------------------------------------------------------------------------ scorecard

public sealed record PilotScoreRowDto(
    Guid CycleId,
    DateTime DecidedAtUtc,
    string Action,
    string Outcome,
    string Approach,
    string Confidence,
    int ExpectedHoldingDays,
    IReadOnlyList<PilotScoreTargetDto> Targets,
    double? PortfolioAfter1D,
    double? PortfolioAfter1W,
    double? PortfolioAfter1M,
    double? BenchmarkAfter1M,
    int FitRevisions = 0,
    double? MandateTargetsAfter1W = null,
    double? MandateTargetsAfter1M = null,
    double? UnconstrainedAfter1W = null,
    double? UnconstrainedAfter1M = null);

public sealed record PilotScoreTargetDto(string Symbol, double Weight, string Thesis, decimal? EntryPrice, double? After1D, double? After1W, double? After1M);

public sealed record PilotScorecardDto(IReadOnlyList<PilotScoreRowDto> Rows, string Methodology);

/// <summary>
/// Result of the model check (one tiny call without portfolio data). Status: ok, not_configured, key_rejected,
/// model_not_found, rate_limited, timeout, unavailable or provider_error.
/// </summary>
public sealed record PilotModelCheckDto(string Provider, string Model, bool Ok, string Status, string Message, double? LatencyMs, string? ModelReported,
    DateTime CheckedAtUtc);
