namespace PortfolioOptimizer.Domain;

/// <summary>
/// Lifecycle of a Matrix Pilot. Whether a decision cycle is running right now is a separate fact
/// (<see cref="MatrixPilot.ActiveCycleId"/>), so a status never mixes "allowed to act" with "acting".
/// </summary>
public enum PilotStatus
{
    /// <summary>Configured but switched off by the owner; no cycle starts.</summary>
    Disabled,
    /// <summary>Decision cycles start on schedule (or on the owner's request) and may book trades within the mandate.</summary>
    Active,
    /// <summary>Stopped by the system (drawdown limit, repeated failures, unknown state). Only the owner can resume.</summary>
    Paused,
    /// <summary>Stopped by the owner's kill switch. Only the owner can resume.</summary>
    KillSwitched,
}

public enum PilotDecisionFrequency { Daily, Weekly, Monthly }

/// <summary>Step of a decision cycle (explicit state machine, docs/matrix-pilot.md#state-machine).</summary>
public enum PilotCyclePhase { Queued, Observe, Research, Construct, Critique, Finalize, Validate, Execute, Journal, Complete }

/// <summary>What a cycle ended with. Each value means something different and is never shown as another.</summary>
public enum PilotCycleOutcome
{
    /// <summary>Still running.</summary>
    Pending,
    /// <summary>The model decided to rebalance, the decision passed every check and trades were booked.</summary>
    Rebalanced,
    /// <summary>The model decided to hold (or its target needed no trade) – a first-class decision.</summary>
    NoTrade,
    /// <summary>Not attempted: data stale or missing, or the pilot was not active when the cycle came up.</summary>
    Skipped,
    /// <summary>The model's decision violated the mandate or a validation rule; nothing was traded.</summary>
    Blocked,
    /// <summary>A model, tool, data or system failure; nothing was traded.</summary>
    Failed,
    /// <summary>The drawdown limit was breached; the pilot paused itself before deciding.</summary>
    PausedByDrawdown,
}

/// <summary>Who caused a pilot event.</summary>
public enum PilotActor { Owner, MatrixPilot, System }

/// <summary>Category of a journal entry. The categories are never blurred (docs/matrix-pilot.md#journal).</summary>
public enum PilotStepKind
{
    /// <summary>Deterministic observation of the starting state (portfolio, freshness, drawdown).</summary>
    Observation,
    /// <summary>A model call and what it returned (structured output only – never hidden reasoning).</summary>
    AiDecision,
    /// <summary>A tool the model requested, run by deterministic code.</summary>
    ToolCall,
    /// <summary>A deterministic calculation the pipeline made on its own (risk of the proposal, trade estimate).</summary>
    Calculation,
    /// <summary>Schema and mandate checks.</summary>
    Validation,
    /// <summary>Proposal and simulated booking.</summary>
    Execution,
    /// <summary>The cycle's outcome as observed.</summary>
    Outcome,
}

/// <summary>
/// An autonomous AI portfolio manager attached to exactly one portfolio of its owner (an experiment, paper money only).
/// The model decides holdings and weights; the platform validates, books and records. See docs/matrix-pilot.md.
/// </summary>
public class MatrixPilot
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public Guid OwnerId { get; set; }
    public PilotStatus Status { get; set; }
    public PilotDecisionFrequency Frequency { get; set; }
    /// <summary>Current mandate version (rows in <see cref="MatrixPilotMandateVersion"/> are never edited).</summary>
    public int MandateVersion { get; set; }
    /// <summary>Language-model provider and model chosen by the owner (recorded again on every cycle).</summary>
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string Effort { get; set; } = "high";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? EnabledAtUtc { get; set; }
    public DateTime? LastDecisionAtUtc { get; set; }
    public DateTime? LastSuccessfulDecisionAtUtc { get; set; }
    public DateTime? NextDecisionAtUtc { get; set; }
    public DateTime? PausedAtUtc { get; set; }
    public string? PauseReason { get; set; }
    public int ConsecutiveFailures { get; set; }
    /// <summary>The cycle holding the portfolio right now (lease); at most one per pilot.</summary>
    public Guid? ActiveCycleId { get; set; }
    public DateTime? ActiveCycleStartedAtUtc { get; set; }
    /// <summary>Forward-only start of the live record, captured once at the first activation (JSON, <c>PilotBaseline</c>).</summary>
    public string? BaselineJson { get; set; }
    public DateTime? BaselineAtUtc { get; set; }
    /// <summary>Optimistic concurrency token: lifecycle changes and cycle leases never overwrite each other.</summary>
    public int Version { get; set; }

    public bool CanStartCycle => Status == PilotStatus.Active;
}

/// <summary>
/// One immutable version of a pilot's mandate (the boundaries the model works within). A change by the owner adds a row;
/// the model has no way to write one.
/// </summary>
public class MatrixPilotMandateVersion
{
    public long Id { get; set; }
    public Guid PilotId { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedBy { get; set; }
    public string? Reason { get; set; }
    /// <summary>Changed fields with old and new value (empty for version 1).</summary>
    public string ChangesJson { get; set; } = "[]";
    public string MandateJson { get; set; } = "{}";
}

/// <summary>
/// One decision cycle: what the model saw, asked for, decided, what the validator said and what was booked. Unique per
/// pilot and period (<see cref="CycleKey"/>), so a duplicated schedule tick can never decide or trade twice.
/// </summary>
public class MatrixPilotCycle
{
    public Guid Id { get; set; }
    public Guid PilotId { get; set; }
    public Guid PortfolioId { get; set; }
    public Guid OwnerId { get; set; }
    /// <summary>Idempotency key: the decision period (e.g. <c>D:2026-11-12</c>, <c>W:2026-W46</c>, <c>M:2026-11</c>).</summary>
    public required string CycleKey { get; set; }
    public required string Trigger { get; set; }
    public Guid CorrelationId { get; set; }
    /// <summary>Decision timestamp: no information dated after it may enter the cycle.</summary>
    public DateTime AsOfUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public PilotCyclePhase Phase { get; set; }
    public PilotCycleOutcome Outcome { get; set; }
    public string? OutcomeReason { get; set; }
    public int MandateVersion { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    /// <summary>Model id the provider reported for the calls (may differ from the requested alias).</summary>
    public string? ModelReported { get; set; }
    public string Effort { get; set; } = "high";
    public required string PromptVersion { get; set; }
    public required string ToolsVersion { get; set; }
    public required string SchemaVersion { get; set; }
    public int ModelCalls { get; set; }
    public int ToolCalls { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double DataMs { get; set; }
    public double ToolMs { get; set; }
    public double ModelMs { get; set; }
    public double ValidationMs { get; set; }
    public double ExecutionMs { get; set; }
    public double TotalMs { get; set; }
    /// <summary>Point-in-time context: portfolio snapshot, freshness, drawdown (JSON, <c>PilotObservation</c>).</summary>
    public string ContextJson { get; set; } = "{}";
    /// <summary>The model's structured decision exactly as accepted by the schema check (JSON, <c>PilotDecision</c>); null when none.</summary>
    public string? DecisionJson { get; set; }
    /// <summary>The critic's structured review (JSON, <c>PilotCritique</c>).</summary>
    public string? CritiqueJson { get; set; }
    /// <summary>Checks run and violations found (JSON, <c>PilotValidationResult</c>).</summary>
    public string? ValidationJson { get; set; }
    /// <summary>Weights actually held after the booking (JSON); null without a booking.</summary>
    public string? ExecutedJson { get; set; }
    public Guid? ProposalId { get; set; }
    public Guid? TradeBatchId { get; set; }
    public int TradesBooked { get; set; }
    public decimal Fees { get; set; }
    /// <summary>Plain-language report (JSON sections); <see cref="NarrativeSource"/> says whether the model's text passed validation.</summary>
    public string? NarrativeJson { get; set; }
    public string? NarrativeSource { get; set; }
    public List<MatrixPilotStep> Steps { get; set; } = [];

    public bool IsFinished => Outcome != PilotCycleOutcome.Pending;
}

/// <summary>One journal entry of a cycle (tool call, model call, calculation, validation, execution, outcome).</summary>
public class MatrixPilotStep
{
    public long Id { get; set; }
    public Guid CycleId { get; set; }
    public int Sequence { get; set; }
    public DateTime AtUtc { get; set; }
    public PilotStepKind Kind { get; set; }
    public required string Name { get; set; }
    /// <summary>ok, error, denied, rejected or skipped.</summary>
    public required string Status { get; set; }
    public double DurationMs { get; set; }
    public required string Summary { get; set; }
    /// <summary>Arguments as requested (JSON, size-capped).</summary>
    public string? InputJson { get; set; }
    /// <summary>Result as given to the model (JSON, size-capped; <see cref="OutputHash"/> covers the full result).</summary>
    public string? OutputJson { get; set; }
    public string? OutputHash { get; set; }
}

/// <summary>Lifecycle audit of a pilot: who changed what, when, from which state to which, why.</summary>
public class MatrixPilotEvent
{
    public long Id { get; set; }
    public Guid PilotId { get; set; }
    public DateTime AtUtc { get; set; }
    public PilotActor Actor { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public PilotStatus? PreviousStatus { get; set; }
    public PilotStatus? NewStatus { get; set; }
    public string? Reason { get; set; }
    public Guid? CycleId { get; set; }
    public Guid? CorrelationId { get; set; }
}
