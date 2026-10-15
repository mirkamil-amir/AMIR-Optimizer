using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Application.Dtos;

namespace PortfolioOptimizer.Application.Research;

/// <summary>Where a brief value came from: stated by the user, or assumed by the system (with the reason shown to the user).</summary>
public sealed record FieldOrigin(string Source, string Reason)
{
    public const string User = "user";
    public const string Assumption = "assumption";
}

/// <summary>
/// The structured research brief. Every field has an entry in <see cref="Origins"/> so the user sees what they said versus
/// what the system assumed. Values are validated by <see cref="BriefRules"/>; the language model can only propose them.
/// </summary>
public sealed record ResearchBrief
{
    public string Question { get; init; } = "";
    /// <summary>build-portfolio (the implemented end-to-end path).</summary>
    public string Path { get; init; } = "build-portfolio";
    /// <summary>ISO country of the market (US, DE, FR, GB, JP) or null when unknown.</summary>
    public string? Market { get; init; }
    /// <summary>Index code (SP500, DAX40, CAC40, FTSE100, NIKKEI225).</summary>
    public string? Index { get; init; }
    public List<string> ExcludeSectors { get; init; } = [];
    public List<string> ExcludeSymbols { get; init; } = [];
    /// <summary>Willingness to accept fluctuations: low, medium, high.</summary>
    public string RiskPreference { get; init; } = "medium";
    /// <summary>Financial ability to bear losses (distinct from willingness): low, medium, high, or null when not stated.</summary>
    public string? LossCapacity { get; init; }
    public int? HorizonYears { get; init; }
    /// <summary>MinimumVariance, MaximumSharpe or EqualWeight (implemented strategies only).</summary>
    public string Objective { get; init; } = "MinimumVariance";
    public double MaxWeight { get; init; } = 0.25;
    public int CandidateCount { get; init; } = 10;
    public int LookbackDays { get; init; } = 504;
    public Dictionary<string, FieldOrigin> Origins { get; init; } = [];
    public List<string> MissingInformation { get; init; } = [];
    public List<string> DataRequirements { get; init; } = [];
    public string IntendedOutput { get; init; } = "";
}

/// <summary>What the user may edit on a brief (validated; unknown values are rejected, not guessed).</summary>
public sealed record UpdateBriefRequest
{
    [StringLength(2, MinimumLength = 2)] public string? Market { get; init; }
    [StringLength(20)] public string? Index { get; init; }
    public List<string>? ExcludeSectors { get; init; }
    public List<string>? ExcludeSymbols { get; init; }
    [RegularExpression("^(low|medium|high)$")] public string? RiskPreference { get; init; }
    [RegularExpression("^(low|medium|high)$")] public string? LossCapacity { get; init; }
    [Range(1, 50)] public int? HorizonYears { get; init; }
    [RegularExpression("^(MinimumVariance|MaximumSharpe|EqualWeight)$")] public string? Objective { get; init; }
    [Range(0.05, 1.0)] public double? MaxWeight { get; init; }
    [Range(4, 25)] public int? CandidateCount { get; init; }
    [Range(126, 1260)] public int? LookbackDays { get; init; }
}

public sealed record StartResearchRequest
{
    /// <summary>The user's objective in their own words (optional when the structured fields are used).</summary>
    [StringLength(2000)] public string? Objective { get; init; }
    [StringLength(2, MinimumLength = 2)] public string? Market { get; init; }
    [RegularExpression("^(low|medium|high)$")] public string? RiskPreference { get; init; }
    [Range(1, 50)] public int? HorizonYears { get; init; }
}

/// <summary>A security considered for the research universe with the evidence for including or excluding it.</summary>
public sealed record CandidateDto(
    string Symbol,
    string? Name,
    string? Sector,
    string Currency,
    bool Included,
    string Reason,
    int Observations,
    DateOnly? LastPriceDate,
    bool StalePrice,
    double? Volatility1Y,
    double? Return1Y,
    double? MaxDrawdown1Y,
    bool IsSimulated);

public sealed record CandidateSetDto(
    string Market,
    string? Index,
    string Criterion,
    DateTime RetrievedAtUtc,
    string PriceSource,
    IReadOnlyList<CandidateDto> Candidates,
    IReadOnlyList<string> SelectedSymbols,
    IReadOnlyList<string> DataWarnings);

public sealed record HypothesisConfig(
    string Objective,
    double MaxWeight,
    IReadOnlyList<string> Universe,
    IReadOnlyList<string> ExcludedSymbols,
    IReadOnlyDictionary<string, double> SectorLimits,
    string CovarianceMethod,
    int LookbackDays,
    double TransactionCostBps);

/// <summary>A deterministic review flag derived from computed metrics (never from model text).</summary>
public sealed record ReviewFlagDto(string Kind, string Topic, string Text, string? Basis);

/// <summary>A follow-up the workflow suggests, with the exact change it would make if the user confirms it.</summary>
public sealed record SuggestionDto(string Text, IReadOnlyList<RefinementChange> Changes);

public sealed record InterpretationSection(string Key, string Title, IReadOnlyList<string> Paragraphs);

/// <summary>
/// Explanation of one hypothesis. <see cref="Source"/> "deterministic" (template over computed values) or "model"
/// (language model, accepted only after <see cref="NarrativeValidator"/> found every number and ticker in the evidence).
/// </summary>
public sealed record InterpretationDto(string Source, bool Verified, string? Model, string? Note, IReadOnlyList<InterpretationSection> Sections);

public sealed record HypothesisDto(
    Guid Id,
    string Label,
    string Origin,
    DateTime CreatedAtUtc,
    HypothesisConfig Config,
    string ConfigHash,
    OptimizationRunDto Result,
    RealityCheckDto? Review,
    IReadOnlyList<ReviewFlagDto> Flags,
    IReadOnlyList<SuggestionDto> Suggestions,
    InterpretationDto Interpretation,
    Guid? SavedRunId);

/// <summary>One validated change to a hypothesis configuration.</summary>
/// <param name="Operation">setMaxWeight, excludeSymbol, includeSymbol, setObjective, setSectorLimit, setCovariance, setLookback.</param>
public sealed record RefinementChange(string Operation, string? Symbol, string? Sector, double? Number, string? Text, string Description);

/// <summary>
/// A refinement interpreted from the user's words: either explicit changes (applied only after the user confirms) or an
/// answer built from computed values (for "why"/"explain" questions).
/// </summary>
public sealed record RefinementProposalDto(
    Guid Id,
    string Request,
    string Kind,
    Guid BaseHypothesisId,
    IReadOnlyList<RefinementChange> Changes,
    string? Answer,
    string InterpretedBy,
    IReadOnlyList<string> Rejected);

public sealed record ProposeRefinementRequest
{
    /// <summary>The request in the user's words (required unless <see cref="Changes"/> is given).</summary>
    [StringLength(500)] public string Text { get; init; } = "";
    public Guid? HypothesisId { get; init; }
    /// <summary>Explicit changes (e.g. from a suggestion button); validated like interpreted ones.</summary>
    [MaxLength(5)] public List<RefinementChange>? Changes { get; init; }
}

public sealed record SaveHypothesisRequest
{
    [Required] public Guid HypothesisId { get; init; }
    /// <summary>Portfolio to attach the saved run to; defaults to the user's first portfolio in the hypothesis currency.</summary>
    public Guid? PortfolioId { get; init; }
    /// <summary>Must be true: saving is an explicit user decision.</summary>
    public bool Confirm { get; init; }
}

public sealed record SetUniverseRequest
{
    [Required, MinLength(2), MaxLength(40)] public List<string> Symbols { get; init; } = [];
}

public sealed record ResearchEventDto(DateTime AtUtc, string Kind, string Name, string Status, double DurationMs, string Summary);

public sealed record WorkflowStepDto(string Key, string Title, string Status);

public sealed record ResearchSessionSummaryDto(Guid Id, string Title, string State, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, int Hypotheses, bool HasSavedRun);

public sealed record ResearchSessionDto(
    Guid Id,
    string Title,
    string State,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    ResearchBrief Brief,
    string? Question,
    IReadOnlyList<WorkflowStepDto> Steps,
    CandidateSetDto? Candidates,
    IReadOnlyList<HypothesisDto> Hypotheses,
    RefinementProposalDto? PendingRefinement,
    IReadOnlyList<ResearchEventDto> Events,
    ResearchBudgetDto Budget,
    AssistantStatusDto Assistant,
    string Disclaimer);

public sealed record ResearchBudgetDto(int ToolCalls, int MaxToolCalls, int Hypotheses, int MaxHypotheses, int LlmTokens, int MaxLlmTokens);

/// <summary>Whether a language model is configured; everything works without one (deterministic interpretation).</summary>
public sealed record AssistantStatusDto(string Provider, string? Model, bool Available, string Note);

public sealed record HypothesisComparisonDto(
    HypothesisDto A,
    HypothesisDto B,
    IReadOnlyList<WeightDiffDto> Weights,
    IReadOnlyList<ConfigDiffDto> ConfigChanges,
    IReadOnlyList<string> Notes);

public sealed record WeightDiffDto(string Symbol, double WeightA, double WeightB);

public sealed record ConfigDiffDto(string Field, string A, string B);
