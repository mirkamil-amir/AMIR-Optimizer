using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Pilot;

public static class PilotJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string? json) => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>Versions recorded on every cycle, so a later change never makes an old decision look like the new setup's.</summary>
public static class PilotVersions
{
    public const string Prompt = "pilot-prompt-2";
    public const string Tools = "pilot-tools-2";
    public const string Schema = "pilot-decision-1";
}

/// <summary>
/// The boundaries Matrix Pilot works within. Set and changed only by the owner (each change is a new version); enforced by
/// <see cref="PilotDecisionValidator"/> outside the model. Long-only, no leverage: the platform supports nothing else.
/// </summary>
public sealed record PilotMandate
{
    /// <summary>Individually allowed symbols (in addition to <see cref="Indexes"/>).</summary>
    public List<string> Universe { get; init; } = [];
    /// <summary>Index codes whose current members are investable.</summary>
    public List<string> Indexes { get; init; } = [];
    public List<string> ExcludedSymbols { get; init; } = [];
    public double MaxPositionWeight { get; init; } = 0.10;
    public double MinPositionWeight { get; init; } = 0.02;
    public int MinHoldings { get; init; } = 5;
    public int MaxHoldings { get; init; } = 20;
    public double MinCashWeight { get; init; } = 0.01;
    public double MaxCashWeight { get; init; } = 0.30;
    /// <summary>One-way turnover ½Σ|w − w₀| per cycle; applies once the portfolio holds positions.</summary>
    public double MaxTurnover { get; init; } = 0.50;
    /// <summary>Smallest order the pilot may place (portfolio currency).</summary>
    public decimal MinTradeValue { get; init; } = 100m;
    public Dictionary<string, double> SectorLimits { get; init; } = [];
    public Dictionary<string, double> CurrencyLimits { get; init; } = [];
    public Dictionary<string, double> CountryLimits { get; init; } = [];
    /// <summary>Ceiling for the proposal's ex-ante annualized volatility (null = none).</summary>
    public double? MaxVolatility { get; init; }
    /// <summary>The pilot pauses itself when the live record's drawdown from its peak exceeds this.</summary>
    public double DrawdownPauseThreshold { get; init; } = 0.20;
    /// <summary>Benchmark of the live record (null = the portfolio's benchmark).</summary>
    public string? Benchmark { get; init; }

    [JsonIgnore] public bool ShortSellingAllowed => false;
    [JsonIgnore] public bool LeverageAllowed => false;

    public PilotMandate Normalized() => this with
    {
        Universe = Universe.Select(Asset.NormalizeSymbol).Where(s => s.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList(),
        Indexes = Indexes.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
        ExcludedSymbols = ExcludedSymbols.Select(Asset.NormalizeSymbol).Where(s => s.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList(),
        SectorLimits = new Dictionary<string, double>(SectorLimits, StringComparer.OrdinalIgnoreCase),
        CurrencyLimits = CurrencyLimits.ToDictionary(kv => kv.Key.Trim().ToUpperInvariant(), kv => kv.Value),
        CountryLimits = new Dictionary<string, double>(CountryLimits, StringComparer.OrdinalIgnoreCase),
        Benchmark = string.IsNullOrWhiteSpace(Benchmark) ? null : Asset.NormalizeSymbol(Benchmark),
    };

    /// <summary>Rejects a mandate that is inconsistent or impossible to satisfy (never repaired silently).</summary>
    public void Validate()
    {
        var e = new Dictionary<string, string[]>();
        void Add(string field, string message) => e[field] = [.. e.GetValueOrDefault(field, []), message];
        static bool Fraction(double v) => double.IsFinite(v) && v is >= 0 and <= 1;

        if (Universe.Count == 0 && Indexes.Count == 0) Add("universe", "Name at least one index or symbol the pilot may invest in.");
        if (Universe.Count > 500) Add("universe", "At most 500 individual symbols.");
        if (Indexes.Count > 10) Add("indexes", "At most 10 indexes.");
        if (!Fraction(MaxPositionWeight) || MaxPositionWeight < 0.01) Add("maxPositionWeight", "Maximum position weight must be between 1 % and 100 %.");
        if (!Fraction(MinPositionWeight) || MinPositionWeight > MaxPositionWeight) Add("minPositionWeight", "Minimum position weight must be between 0 % and the maximum position weight.");
        if (MinHoldings < 1 || MaxHoldings > 60 || MinHoldings > MaxHoldings) Add("holdings", "Holdings must satisfy 1 ≤ minimum ≤ maximum ≤ 60.");
        if (!Fraction(MinCashWeight) || !Fraction(MaxCashWeight) || MinCashWeight > MaxCashWeight) Add("cash", "Cash limits must satisfy 0 % ≤ minimum ≤ maximum ≤ 100 %.");
        if (!Fraction(MaxTurnover) || MaxTurnover < 0.01) Add("maxTurnover", "Maximum turnover per decision must be between 1 % and 100 %.");
        if (MinTradeValue < 0 || MinTradeValue > 1_000_000m) Add("minTradeValue", "Minimum trade value must be between 0 and 1,000,000.");
        if (!Fraction(DrawdownPauseThreshold) || DrawdownPauseThreshold < 0.01) Add("drawdownPauseThreshold", "Drawdown pause must be between 1 % and 100 %.");
        if (MaxVolatility is { } mv && (!double.IsFinite(mv) || mv is < 0.01 or > 2)) Add("maxVolatility", "Maximum volatility must be between 1 % and 200 % (or empty).");
        foreach (var (name, limits) in new[] { ("sectorLimits", SectorLimits), ("currencyLimits", CurrencyLimits), ("countryLimits", CountryLimits) })
            if (limits.Values.Any(v => !Fraction(v) || v < 0.01)) Add(name, "Every limit must be between 1 % and 100 %.");
        if (Universe.Intersect(ExcludedSymbols).Any()) Add("excludedSymbols", "A symbol cannot be both allowed and excluded.");

        // Feasibility: the invested share must be reachable with the holding and weight limits.
        if (e.Count == 0)
        {
            var maxInvested = 1 - MinCashWeight;
            var minInvested = 1 - MaxCashWeight;
            if (MaxHoldings * MaxPositionWeight < minInvested - 1e-9)
                Add("maxPositionWeight", $"{MaxHoldings} holdings of at most {MaxPositionWeight:P0} cannot reach the minimum invested share of {minInvested:P0}.");
            if (MinHoldings * MinPositionWeight > maxInvested + 1e-9)
                Add("minPositionWeight", $"{MinHoldings} holdings of at least {MinPositionWeight:P0} exceed the maximum invested share of {maxInvested:P0}.");
        }
        if (e.Count > 0) throw new ValidationException(e);
    }

    /// <summary>Field-by-field differences (for the versioned change record).</summary>
    public static IReadOnlyList<PilotMandateChange> Diff(PilotMandate before, PilotMandate after)
    {
        var a = JsonSerializer.SerializeToElement(before, PilotJson.Options);
        var b = JsonSerializer.SerializeToElement(after, PilotJson.Options);
        var changes = new List<PilotMandateChange>();
        foreach (var p in b.EnumerateObject())
        {
            var old = a.TryGetProperty(p.Name, out var o) ? o.GetRawText() : "null";
            if (old != p.Value.GetRawText()) changes.Add(new PilotMandateChange(p.Name, old, p.Value.GetRawText()));
        }
        return changes;
    }
}

public sealed record PilotMandateChange(string Field, string Before, string After);

/// <summary>Decision periods. A period has one cycle at most (its key is unique per pilot).</summary>
public static class PilotSchedule
{
    public static string PeriodKey(PilotDecisionFrequency frequency, DateTime utc)
    {
        var d = DateOnly.FromDateTime(utc);
        return frequency switch
        {
            PilotDecisionFrequency.Daily => $"D:{d:yyyy-MM-dd}",
            PilotDecisionFrequency.Weekly => $"W:{ISOWeek.GetYear(utc)}-W{ISOWeek.GetWeekOfYear(utc):00}",
            _ => $"M:{d:yyyy-MM}",
        };
    }

    /// <summary>
    /// The first decision time strictly after <paramref name="afterUtc"/>: the decision hour on the next weekday (daily), the next
    /// Friday (weekly) or the last weekday of the month (monthly) – after the trading day's closes are stored.
    /// </summary>
    public static DateTime NextDecision(PilotDecisionFrequency frequency, DateTime afterUtc, int decisionHourUtc)
    {
        var day = DateOnly.FromDateTime(afterUtc);
        for (var i = 0; i < 400; i++, day = day.AddDays(1))
        {
            var at = day.ToDateTime(new TimeOnly(decisionHourUtc, 0), DateTimeKind.Utc);
            if (at <= afterUtc || day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var ok = frequency switch
            {
                PilotDecisionFrequency.Daily => true,
                PilotDecisionFrequency.Weekly => day.DayOfWeek == DayOfWeek.Friday,
                _ => LastWeekdayOfMonth(day) == day,
            };
            if (ok) return at;
        }
        throw new InvalidOperationException("No decision time found.");
    }

    private static DateOnly LastWeekdayOfMonth(DateOnly d)
    {
        var last = new DateOnly(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
        while (last.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) last = last.AddDays(-1);
        return last;
    }
}

// ---------------------------------------------------------------------------------------------- live record baseline

public sealed record PilotBaseline(
    DateTime AtUtc,
    DateOnly AsOfDate,
    string Currency,
    decimal PortfolioValue,
    decimal Cash,
    IReadOnlyList<PilotPositionSnapshot> Positions,
    string Benchmark,
    double? BenchmarkLevel,
    DateOnly? BenchmarkDate,
    string Note);

public sealed record PilotPositionSnapshot(string Symbol, decimal Quantity, decimal? Value, double? Weight, DateOnly? PriceDate);

// ---------------------------------------------------------------------------------------------- observation (deterministic)

public sealed record PilotFreshness(string Item, bool Fresh, DateOnly? AsOf, string Detail);

public sealed record PilotObservation(
    DateTime AsOfUtc,
    DateOnly AsOfDate,
    string Currency,
    decimal? PortfolioValue,
    decimal Cash,
    IReadOnlyList<PilotPositionSnapshot> Holdings,
    int InvestableCount,
    IReadOnlyList<PilotFreshness> Freshness,
    double? LiveDrawdown,
    double? LiveReturn,
    bool PricesSimulated,
    IReadOnlyList<string> Notes);

// ---------------------------------------------------------------------------------------------- model output (schema)

/// <summary>One research round: what the model noted and which tools it wants next (arguments as JSON text).</summary>
public sealed record PilotResearchTurn(string Notes, IReadOnlyList<PilotToolRequest> ToolCalls, bool Ready);

public sealed record PilotToolRequest(string Tool, string ArgsJson);

/// <summary>The model's investment decision. Validated server-side before anything happens with it.</summary>
public sealed record PilotDecision
{
    /// <summary>REBALANCE or HOLD.</summary>
    public string Action { get; init; } = "";
    public PilotStrategyChoice Strategy { get; init; } = new();
    public List<PilotTarget> Targets { get; init; } = [];
    public double CashWeight { get; init; }
    public List<PilotRejected> Rejected { get; init; } = [];
    public List<string> Rationale { get; init; } = [];
    public List<string> KeyRisks { get; init; } = [];
    public List<string> InvalidationConditions { get; init; } = [];
    public int ExpectedHoldingDays { get; init; }
    /// <summary>low, medium or high – the model's own categorical confidence (no invented number).</summary>
    public string Confidence { get; init; } = "";
    /// <summary>Sequence numbers of the journal entries (tool results) the decision relies on.</summary>
    public List<int> EvidenceSteps { get; init; } = [];
    /// <summary>How the final decision responded to the critic (finalize stage only).</summary>
    public string? CritiqueResponse { get; init; }
    /// <summary>Changes to the mandate the model would like (recorded, never applied).</summary>
    public List<string> MandateRequests { get; init; } = [];

    public bool IsHold => Action == "HOLD";
}

public sealed record PilotStrategyChoice
{
    public string Approach { get; init; } = "";
    public List<string> Components { get; init; } = [];
    public string Reason { get; init; } = "";
}

public sealed record PilotTarget
{
    public string Symbol { get; init; } = "";
    public double Weight { get; init; }
    public string Role { get; init; } = "";
    public string Thesis { get; init; } = "";
    public string WhyNow { get; init; } = "";
    public string Risks { get; init; } = "";
    public string Invalidation { get; init; } = "";
}

public sealed record PilotRejected(string Symbol, string Reason);

public sealed record PilotCritique
{
    public List<PilotConcern> Concerns { get; init; } = [];
    /// <summary>keep, revise or hold.</summary>
    public string Recommendation { get; init; } = "";
    public string Summary { get; init; } = "";
}

public sealed record PilotConcern(string Area, string Severity, string Detail);

// ---------------------------------------------------------------------------------------------- validation

public sealed record PilotCheck(string Name, bool Passed, string Detail);

/// <summary>A blocked target: what the model proposed and what the mandate allows (shown side by side, never merged).</summary>
public sealed record PilotViolation(string Rule, string Detail, string? Symbol = null, double? Proposed = null, double? Allowed = null);

public sealed record PilotValidationResult(
    bool Passed,
    IReadOnlyList<PilotCheck> Checks,
    IReadOnlyList<PilotViolation> Violations,
    PilotRiskSummary? Risk,
    PilotTradeEstimate? Trades);

public sealed record PilotRiskSummary(double? Volatility, double? EffectiveHoldings, double? LargestWeight, string? LargestSymbol,
    IReadOnlyDictionary<string, double> Sectors, IReadOnlyDictionary<string, double> Currencies, IReadOnlyDictionary<string, double> Countries,
    double? ValueAtRisk95, double? MaxDrawdown, int Observations, string Window);

public sealed record PilotTradeEstimate(int Buys, int Sells, decimal BuyValue, decimal SellValue, decimal Costs, double Turnover,
    IReadOnlyList<PilotTradeLine> Lines, IReadOnlyList<string> Notes);

public sealed record PilotTradeLine(string Symbol, string Action, decimal Quantity, decimal Value, double CurrentWeight, double TargetWeight, string? Note);

/// <summary>Weights held after a booking (from the ledger), next to the proposal.</summary>
public sealed record PilotExecuted(IReadOnlyList<PilotExecutedWeight> Weights, double CashWeight, decimal PortfolioValue, IReadOnlyList<string> Notes);

public sealed record PilotExecutedWeight(string Symbol, double ProposedWeight, double ExecutedWeight, decimal Quantity);

public static class PilotText
{
    /// <summary>Caps model-written text before it is stored (the model's text is data, shown to the owner, never executed).</summary>
    public static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s.Trim() : s[..max].Trim() + "…";
}

// ---------------------------------------------------------------------------------------------- rules first, fit, unconstrained portfolio

/// <summary>The model's restatement of the mandate before it researches (stage RULES): what each rule means for the portfolio.</summary>
public sealed record PilotRulesCheck
{
    public List<PilotRuleNote> Constraints { get; init; } = [];
    /// <summary>Where the rules pull against each other or against the current portfolio (e.g. a position above its cap).</summary>
    public List<string> Tensions { get; init; } = [];
}

public sealed record PilotRuleNote(string Rule, string Limit, string Implication);

/// <summary>
/// The model's own portfolio without any mandate limit (stage UNCONSTRAINED) – any listing of the catalogue, any weight,
/// long-only. Never traded; recorded next to the mandate portfolio for comparison.
/// </summary>
public sealed record PilotShadow
{
    public string Approach { get; init; } = "";
    public List<PilotTarget> Targets { get; init; } = [];
    public double CashWeight { get; init; }
    /// <summary>Why this portfolio, in the model's words.</summary>
    public List<string> Justification { get; init; } = [];
    /// <summary>What it does differently from the mandate portfolio, and why.</summary>
    public List<string> DifferencesFromMandate { get; init; } = [];
    public List<string> KeyRisks { get; init; } = [];
    public string Confidence { get; init; } = "";
    /// <summary>Structural problems found by the platform (symbols not in the catalogue, weights …); empty when usable.</summary>
    public List<string> Problems { get; init; } = [];
}

/// <summary>One side of the comparison: the platform's figures for a portfolio (historical estimates, same window and method).</summary>
public sealed record PilotPortfolioFigures(
    string Label,
    int Holdings,
    double CashWeight,
    PilotRiskSummary? Risk,
    double? DiversificationRatio,
    double? AverageCorrelation,
    double? ExpectedShortfall95,
    double? Worst20Days,
    double? BetaToBenchmark,
    double? TrailingReturn12M,
    IReadOnlyList<PilotTargetWeight> Weights);

public sealed record PilotTargetWeight(string Symbol, double Weight, string? Sector, string? Currency, string? Country);

/// <summary>The mandate portfolio next to the unconstrained one, computed by the platform; the model's comparison text is validated against it.</summary>
public sealed record PilotPortfolioComparison(
    PilotPortfolioFigures Mandate,
    PilotPortfolioFigures Unconstrained,
    // Mandate rules the unconstrained portfolio would break (what the limits cost or protect against).
    IReadOnlyList<PilotViolation> RulesUnconstrainedBreaks,
    IReadOnlyList<string> SharedSymbols,
    // WeightOverlap: Σ min(wᵢ, vᵢ) over all symbols – 1 = identical portfolios, 0 = nothing in common.
    double WeightOverlap,
    IReadOnlyList<Research.InterpretationSection> Narrative,
    string NarrativeSource,
    string Basis);
