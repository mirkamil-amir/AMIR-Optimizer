namespace PortfolioOptimizer.Domain;

/// <summary>Workflow state of a research session (see docs/agentic-architecture.md#workflow).</summary>
public enum ResearchState
{
    /// <summary>The objective is missing information the workflow cannot assume (e.g. which market).</summary>
    AwaitingInput,
    /// <summary>A research brief exists; the investable universe has not been researched yet.</summary>
    BriefReady,
    /// <summary>A candidate shortlist with evidence exists; the user chooses the universe.</summary>
    UniverseReady,
    /// <summary>At least one portfolio hypothesis has been calculated and reviewed.</summary>
    Evaluated,
}

/// <summary>
/// One user's research task: objective → brief → candidates → portfolio hypotheses → review → refinement → saved run.
/// Owner-scoped. Structured state is stored as JSON documents (brief, candidates, hypotheses) so that every step is
/// reproducible; hypotheses are NOT optimization runs until the user saves one (saved runs are linked by id).
/// </summary>
public class ResearchSession
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public required string Title { get; set; }
    public ResearchState State { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string BriefJson { get; set; } = "{}";
    public string CandidatesJson { get; set; } = "null";
    public string HypothesesJson { get; set; } = "[]";
    /// <summary>A refinement proposed but not yet confirmed by the user (null when none is pending).</summary>
    public string? PendingRefinementJson { get; set; }
    public int ToolCalls { get; set; }
    public int LlmInputTokens { get; set; }
    public int LlmOutputTokens { get; set; }
    /// <summary>Optimistic concurrency token: two simultaneous actions on one session cannot both apply.</summary>
    public int Version { get; set; }
    public List<ResearchEvent> Events { get; set; } = [];
}

/// <summary>
/// Audit trail of a session: every tool call, language-model call and user decision with its outcome. Holds short
/// summaries and decisions – never prompts, model reasoning or raw provider payloads.
/// </summary>
public class ResearchEvent
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public DateTime AtUtc { get; set; }
    /// <summary>tool, model, user or system.</summary>
    public required string Kind { get; set; }
    public required string Name { get; set; }
    /// <summary>ok, error, rejected, skipped or denied.</summary>
    public required string Status { get; set; }
    public double DurationMs { get; set; }
    public required string Summary { get; set; }
}
