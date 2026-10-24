using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Research;

/// <summary>
/// The research orchestrator. A deterministic workflow decides the next step from the session state; typed tools call the
/// application's services; the language model (optional) only proposes a brief, a refinement or a narrative, all of which
/// are validated before use. Calculations, authorization, budgets and persistence never depend on the model.
/// </summary>
public sealed class ResearchAgent(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    ILanguageModel model,
    ToolRunner tools,
    BriefInterpreter briefInterpreter,
    RefinementInterpreter refinementInterpreter,
    InterpretationService interpretation,
    SearchUniverseTool searchUniverse,
    EvaluateHypothesisTool evaluateHypothesis,
    ReviewHypothesisTool reviewHypothesis,
    CompanyContextTool companyContext,
    SaveHypothesisTool saveHypothesis,
    OptimizationService optimizations,
    TimeProvider clock)
{
    public const int MaxHypotheses = 10;
    public const int MaxLlmTokens = 150_000;
    public const int MaxSessionsPerUser = 50;

    public const string Disclaimer =
        "Research and simulation on stored historical data. Calculations come from the application's optimizer and review; explanations are " +
        "checked against them. Nothing here forecasts returns, guarantees results or recommends buying or selling. Not investment advice.";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ queries

    public async Task<IReadOnlyList<ResearchSessionSummaryDto>> ListAsync(CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var rows = await db.ResearchSessions.AsNoTracking().Where(s => s.OwnerId == owner).OrderByDescending(s => s.UpdatedAtUtc).Take(100)
            .Select(s => new { s.Id, s.Title, s.State, s.CreatedAtUtc, s.UpdatedAtUtc, s.HypothesesJson, s.PortfolioId }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var h = JsonSerializer.Deserialize<List<HypothesisDto>>(r.HypothesesJson, Json) ?? [];
            return new ResearchSessionSummaryDto(r.Id, r.Title, r.State.ToString(), r.CreatedAtUtc, r.UpdatedAtUtc, h.Count, h.Any(x => x.SavedRunId is not null), r.PortfolioId);
        }).ToList();
    }

    public async Task<ResearchSessionDto> GetAsync(Guid id, CancellationToken ct) => await ToDtoAsync(await LoadAsync(id, ct, tracking: false), ct);

    // ------------------------------------------------------------------ stage 1–3: objective → brief → universe

    public async Task<ResearchSessionDto> StartAsync(StartResearchRequest request, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        if (await db.ResearchSessions.CountAsync(s => s.OwnerId == owner, ct) >= MaxSessionsPerUser)
            throw new ConflictException($"You have {MaxSessionsPerUser} research sessions. Delete old ones before starting another.");
        var now = clock.GetUtcNow().UtcDateTime;
        var session = new ResearchSession { Id = Guid.NewGuid(), OwnerId = owner, Title = "New research", CreatedAtUtc = now, UpdatedAtUtc = now };
        if (request.PortfolioId is { } pid)
        {
            // Owner-checked: another user's portfolio is reported as not found.
            var portfolio = await db.Portfolios.AsNoTracking().Where(p => p.Id == pid && p.OwnerId == owner)
                .Select(p => new { p.Name, p.BaseCurrency }).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Portfolio", pid);
            session.PortfolioId = pid;
            Event(session, "user", "portfolio_context", "ok", $"Research for portfolio “{portfolio.Name}” (analysis currency {portfolio.BaseCurrency}).");
        }
        db.ResearchSessions.Add(session);

        var interpreted = await briefInterpreter.InterpretAsync(request, ct);
        Count(session, interpreted.InputTokens, interpreted.OutputTokens);
        var brief = interpreted.Brief;
        session.Title = Title(brief);
        Event(session, "user", "objective", "ok", string.IsNullOrWhiteSpace(request.Objective) ? "Started from the form." : $"Objective: {Short(request.Objective)}");
        Event(session, interpreted.InterpretedBy.Contains("rules +") ? "model" : "system", "research_brief", "ok",
            $"Brief built by {interpreted.InterpretedBy}: {brief.Origins.Count(o => o.Value.Source == FieldOrigin.User)} stated value(s), {brief.Origins.Count(o => o.Value.Source == FieldOrigin.Assumption)} assumption(s).");
        foreach (var r in interpreted.Rejected) Event(session, "model", "research_brief", "rejected", r);
        Save(session, brief);
        await Guarded(session, () => AdvanceAsync(session, brief, ct), ct);
        await PersistAsync(session, ct);
        return await ToDtoAsync(session, ct);
    }

    /// <summary>Runs a step; if a tool fails, the session (incl. the failure in its activity log) is saved before the error is returned.</summary>
    private async Task Guarded(ResearchSession session, Func<Task> step, CancellationToken ct)
    {
        try { await step(); }
        catch (ToolException)
        {
            await PersistAsync(session, ct);
            throw;
        }
    }

    public async Task<ResearchSessionDto> UpdateBriefAsync(Guid id, UpdateBriefRequest r, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct);
        var brief = Brief(session);
        var origins = new Dictionary<string, FieldOrigin>(brief.Origins);
        var edited = new List<string>();
        void Mark(string field) { origins[field] = new(FieldOrigin.User, "edited by you"); edited.Add(field); }
        if (r.Market is { } m)
        {
            var mu = m.ToUpperInvariant();
            if (!BriefRules.IsMarket(mu)) throw new ValidationException("market", $"Market must be one of {string.Join(", ", BriefRules.DefaultIndex.Keys)}.");
            brief = brief with { Market = mu, Index = BriefRules.DefaultIndex[mu] }; Mark("market");
        }
        if (r.Index is { } ix)
        {
            var allowed = await db.MarketIndices.AsNoTracking().Where(i => i.Code == ix.ToUpperInvariant()).Select(i => i.Country).SingleOrDefaultAsync(ct)
                          ?? throw new ValidationException("index", $"Unknown index {ix}.");
            if (allowed != brief.Market) throw new ValidationException("index", "The index must belong to the selected market.");
            brief = brief with { Index = ix.ToUpperInvariant() }; Mark("index");
        }
        if (r.ExcludeSectors is { } es)
        {
            if (es.Any(s => !BriefRules.Sectors.Contains(s))) throw new ValidationException("excludeSectors", "Unknown sector.");
            brief = brief with { ExcludeSectors = es.Distinct().ToList() }; Mark("excludeSectors");
        }
        if (r.ExcludeSymbols is { } ex) { brief = brief with { ExcludeSymbols = ex.Select(Asset.NormalizeSymbol).Distinct().ToList() }; Mark("excludeSymbols"); }
        if (r.RiskPreference is { } rp)
        {
            brief = brief with { RiskPreference = rp }; Mark("riskPreference");
            if (brief.Origins.GetValueOrDefault("objective")?.Source != FieldOrigin.User && r.Objective is null)
            {
                var (o, why) = BriefRules.ObjectiveFor(rp);
                brief = brief with { Objective = o };
                origins["objective"] = new(FieldOrigin.Assumption, why);
            }
        }
        if (r.LossCapacity is { } lc) { brief = brief with { LossCapacity = lc }; Mark("lossCapacity"); }
        if (r.HorizonYears is { } hy) { brief = brief with { HorizonYears = hy }; Mark("horizonYears"); }
        if (r.Objective is { } ob) { brief = brief with { Objective = ob }; Mark("objective"); }
        if (r.MaxWeight is { } mw) { brief = brief with { MaxWeight = mw }; Mark("maxWeight"); }
        if (r.CandidateCount is { } cc) { brief = brief with { CandidateCount = cc }; Mark("candidateCount"); }
        if (r.LookbackDays is { } lb) { brief = brief with { LookbackDays = lb }; Mark("lookbackDays"); }
        brief = BriefRules.Complete(brief with { Origins = origins });
        session.Title = Title(brief);
        Event(session, "user", "edit_brief", "ok", edited.Count == 0 ? "No change." : $"Edited: {string.Join(", ", edited)}.");
        Save(session, brief);
        // A changed brief invalidates the shortlist (prior hypotheses stay for comparison).
        session.CandidatesJson = "null";
        await Guarded(session, () => AdvanceAsync(session, brief, ct), ct);
        await PersistAsync(session, ct);
        return await ToDtoAsync(session, ct);
    }

    /// <summary>Decides the next step after the brief changed: ask for missing input, or research the universe.</summary>
    private async Task AdvanceAsync(ResearchSession session, ResearchBrief brief, CancellationToken ct)
    {
        if (!BriefRules.IsMarket(brief.Market))
        {
            session.State = ResearchState.AwaitingInput;
            Event(session, "system", "next_step", "ok", "Waiting for you: choose the market to research.");
            return;
        }
        session.State = ResearchState.BriefReady;
        var candidates = await tools.RunAsync(searchUniverse, Context(session), brief, ct);
        session.CandidatesJson = JsonSerializer.Serialize(candidates, Json);
        session.State = ResearchState.UniverseReady;
        Event(session, "system", "next_step", "ok", candidates.SelectedSymbols.Count >= 2
            ? "Review the shortlist, adjust it if you like, then calculate portfolio hypotheses."
            : "Too few stocks meet the data requirements; change the brief or download price history.");
    }

    public async Task<ResearchSessionDto> SetUniverseAsync(Guid id, SetUniverseRequest request, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct);
        var set = Candidates(session) ?? throw new ConflictException("Research the universe first.");
        var symbols = request.Symbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
        var eligible = set.Candidates.Where(c => c.Included || c.Reason.StartsWith("Meets")).Select(c => c.Symbol).ToHashSet();
        var invalid = symbols.Where(s => !eligible.Contains(s)).ToList();
        if (invalid.Count > 0)
            throw new ValidationException("symbols", $"Not eligible (not screened or not meeting the data requirements): {string.Join(", ", invalid)}.");
        session.CandidatesJson = JsonSerializer.Serialize(set with { SelectedSymbols = symbols }, Json);
        Event(session, "user", "choose_universe", "ok", $"Universe set to {symbols.Count} stocks.");
        await PersistAsync(session, ct);
        return await ToDtoAsync(session, ct);
    }

    // ------------------------------------------------------------------ stage 4–7: hypotheses, review, interpretation

    public async Task<ResearchSessionDto> EvaluateAsync(Guid id, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct);
        if (session.State is ResearchState.AwaitingInput or ResearchState.BriefReady) throw new ConflictException("Complete the brief and the universe first.");
        var brief = Brief(session);
        var set = Candidates(session) ?? throw new ConflictException("Research the universe first.");
        if (set.SelectedSymbols.Count < 2) throw new ConflictException("Choose at least two stocks for the universe.");
        // Analysed in the currency of the research's portfolio (or, without one, the user's first portfolio) so a saved run matches
        // the hypothesis; otherwise in the market's own currency.
        var mine = db.Portfolios.AsNoTracking().Where(p => p.OwnerId == session.OwnerId);
        var portfolioCurrency = session.PortfolioId is { } pid
            ? await mine.Where(p => p.Id == pid).Select(p => p.BaseCurrency).FirstOrDefaultAsync(ct)
            : await mine.OrderBy(p => p.CreatedAtUtc).Select(p => p.BaseCurrency).FirstOrDefaultAsync(ct);
        var baseConfig = new HypothesisConfig(brief.Objective, brief.MaxWeight, set.SelectedSymbols, [], new Dictionary<string, double>(),
            "LedoitWolf", brief.LookbackDays, 10, portfolioCurrency ?? "");
        var hypotheses = Hypotheses(session);
        var objectiveName = InterpretationTemplate.ObjectiveNames[brief.Objective];
        var label = $"{char.ToUpperInvariant(objectiveName[0])}{objectiveName[1..]} (from your brief)";
        await Guarded(session, async () =>
        {
            await AddHypothesisAsync(session, hypotheses, baseConfig, label, "initial", set, ct);
            if (brief.Objective != "EqualWeight")
                await AddHypothesisAsync(session, hypotheses, baseConfig with { Objective = "EqualWeight" }, "Equal weight (baseline)", "baseline", set, ct);
        }, ct);
        session.State = ResearchState.Evaluated;
        Event(session, "system", "next_step", "ok", "Review the findings, compare the alternatives, refine or save.");
        await PersistAsync(session, ct);
        return await ToDtoAsync(session, ct);
    }

    private async Task<HypothesisDto> AddHypothesisAsync(ResearchSession session, List<HypothesisDto> hypotheses, HypothesisConfig config, string label, string origin,
        CandidateSetDto set, CancellationToken ct)
    {
        var hash = Hash(config);
        if (hypotheses.FirstOrDefault(h => h.ConfigHash == hash) is { } existing)
        {
            Event(session, "system", "run_portfolio_optimization", "skipped", $"“{existing.Label}” already uses this exact configuration; reused instead of recalculating.");
            return existing;
        }
        if (hypotheses.Count >= MaxHypotheses) throw new ConflictException($"A session holds at most {MaxHypotheses} hypotheses. Start a new session to continue.");
        var run = await tools.RunAsync(evaluateHypothesis, Context(session), config, ct);
        var result = await optimizations.ToDtoAsync(run, ct);
        RealityCheckDto? review = null;
        if (run.Status == OptimizationRunStatus.Succeeded)
            review = await tools.RunAsync(reviewHypothesis, Context(session), run, ct);
        var flags = HypothesisReview.Flags(config, result, review);
        var suggestions = HypothesisReview.Suggestions(config, result, review);
        var known = set.Candidates.Select(c => c.Symbol).ToHashSet(StringComparer.Ordinal);
        var allowModel = session.LlmInputTokens + session.LlmOutputTokens < MaxLlmTokens;
        var interp = await interpretation.InterpretAsync(config, result, review, flags, suggestions, known, allowModel, ct);
        Count(session, interp.InputTokens, interp.OutputTokens);
        if (interp.ModelEventStatus is not null) Event(session, "model", "interpret_result", interp.ModelEventStatus, interp.ModelEventSummary!);
        var h = new HypothesisDto(Guid.NewGuid(), label, origin, clock.GetUtcNow().UtcDateTime, config, hash, result, review, flags, suggestions, interp.Interpretation, null);
        hypotheses.Add(h);
        session.HypothesesJson = JsonSerializer.Serialize(hypotheses, Json);
        return h;
    }

    // ------------------------------------------------------------------ stage 8: refinement

    public async Task<ResearchSessionDto> ProposeRefinementAsync(Guid id, ProposeRefinementRequest request, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct);
        var hypotheses = Hypotheses(session);
        var baseH = (request.HypothesisId is { } hid ? hypotheses.FirstOrDefault(h => h.Id == hid) : hypotheses.FirstOrDefault(h => h.Origin != "baseline") ?? hypotheses.FirstOrDefault())
                    ?? throw new ConflictException("Calculate a portfolio hypothesis first.");
        var set = Candidates(session) ?? throw new ConflictException("Research the universe first.");
        var allowModel = session.LlmInputTokens + session.LlmOutputTokens < MaxLlmTokens;
        RefinementInterpretation r;
        if (request.Changes is { Count: > 0 } explicitChanges)
        {
            var symbols = set.Candidates.Select(c => c.Symbol).ToHashSet();
            var checkedChanges = explicitChanges.Select(c => RefinementRules.Check(c, baseH.Config, symbols)).ToList();
            var rejectedChanges = checkedChanges.Where(x => x.Change is null).Select(x => x.Rejection!).ToList();
            var ok = checkedChanges.Where(x => x.Change is not null).Select(x => x.Change!).ToList();
            r = new RefinementInterpretation(ok.Count > 0 ? "change" : "answer", ok, ok.Count > 0 ? null : string.Join(" ", rejectedChanges), "suggestion", rejectedChanges, 0, 0);
        }
        else if (request.Text.Trim().Length < 2) throw new ValidationException("text", "Describe the change or question.");
        else r = await refinementInterpreter.InterpretAsync(request.Text, baseH, set.Candidates, allowModel, ct);
        Count(session, r.InputTokens, r.OutputTokens);
        var requestText = request.Text.Trim().Length > 0 ? request.Text : string.Join("; ", r.Changes.Select(c => c.Description));
        var proposal = new RefinementProposalDto(Guid.NewGuid(), Short(requestText, 500), r.Kind, baseH.Id, r.Changes, r.Answer, r.InterpretedBy, r.Rejected);
        session.PendingRefinementJson = r.Changes.Count > 0 ? JsonSerializer.Serialize(proposal, Json) : null;
        Event(session, "user", "refinement_request", "ok", Short(requestText));
        Event(session, r.InterpretedBy.StartsWith("rules") ? "system" : "model", "interpret_refinement", r.Changes.Count > 0 ? "ok" : "skipped",
            r.Changes.Count > 0 ? $"Proposed: {string.Join("; ", r.Changes.Select(c => c.Description))} – waiting for your confirmation." : $"Answered without changes ({r.Kind}).");
        foreach (var x in r.Rejected) Event(session, "model", "interpret_refinement", "rejected", x);
        await PersistAsync(session, ct);
        var dto = await ToDtoAsync(session, ct);
        return dto with { PendingRefinement = proposal };
    }

    public async Task<ResearchSessionDto> ApplyRefinementAsync(Guid id, Guid proposalId, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct);
        var proposal = session.PendingRefinementJson is { } pj ? JsonSerializer.Deserialize<RefinementProposalDto>(pj, Json) : null;
        if (proposal is null || proposal.Id != proposalId) throw new ConflictException("This proposal is no longer pending; propose the change again.");
        var hypotheses = Hypotheses(session);
        var baseH = hypotheses.FirstOrDefault(h => h.Id == proposal.BaseHypothesisId) ?? throw new ConflictException("The hypothesis to refine no longer exists.");
        var set = Candidates(session)!;
        // Re-validate against the current configuration (never trust the stored proposal blindly).
        var symbols = set.Candidates.Select(c => c.Symbol).ToHashSet();
        foreach (var c in proposal.Changes)
            if (RefinementRules.Check(c, baseH.Config, symbols).Change is null) throw new ConflictException($"The change “{c.Description}” is no longer valid.");
        var config = RefinementRules.Apply(baseH.Config, proposal.Changes);
        Event(session, "user", "confirm_refinement", "ok", $"Confirmed: {string.Join("; ", proposal.Changes.Select(c => c.Description))}.");
        session.PendingRefinementJson = null;
        await Guarded(session, () => AddHypothesisAsync(session, hypotheses, config, $"{baseH.Label} – refined",
            $"refinement of “{baseH.Label}”: {string.Join("; ", proposal.Changes.Select(c => c.Description))}", set, ct), ct);
        await PersistAsync(session, ct);
        return await ToDtoAsync(session, ct);
    }

    public async Task<ResearchSessionDto> DiscardRefinementAsync(Guid id, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct);
        if (session.PendingRefinementJson is not null) Event(session, "user", "discard_refinement", "ok", "Proposed change discarded.");
        session.PendingRefinementJson = null;
        await PersistAsync(session, ct);
        return await ToDtoAsync(session, ct);
    }

    public async Task<HypothesisComparisonDto> CompareAsync(Guid id, Guid a, Guid b, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct, tracking: false);
        var hypotheses = Hypotheses(session);
        var ha = hypotheses.FirstOrDefault(h => h.Id == a) ?? throw new NotFoundException("Hypothesis", a);
        var hb = hypotheses.FirstOrDefault(h => h.Id == b) ?? throw new NotFoundException("Hypothesis", b);
        return Compare(ha, hb);
    }

    /// <summary>Weight and configuration differences between two hypotheses (pure).</summary>
    public static HypothesisComparisonDto Compare(HypothesisDto a, HypothesisDto b)
    {
        var wa = a.Result.Allocation.ToDictionary(x => x.Symbol, x => x.Weight);
        var wb = b.Result.Allocation.ToDictionary(x => x.Symbol, x => x.Weight);
        var weights = wa.Keys.Union(wb.Keys).Select(s => new WeightDiffDto(s, wa.GetValueOrDefault(s), wb.GetValueOrDefault(s)))
            .Where(w => w.WeightA >= 0.0005 || w.WeightB >= 0.0005).OrderByDescending(w => Math.Max(w.WeightA, w.WeightB)).ToList();
        var cfg = new List<ConfigDiffDto>();
        void Diff(string f, string x, string y) { if (x != y) cfg.Add(new(f, x, y)); }
        Diff("Objective", RefinementRules.Name(a.Config.Objective), RefinementRules.Name(b.Config.Objective));
        Diff("Per-stock maximum", $"{a.Config.MaxWeight:0%}", $"{b.Config.MaxWeight:0%}");
        Diff("Excluded", string.Join(", ", a.Config.ExcludedSymbols), string.Join(", ", b.Config.ExcludedSymbols));
        Diff("Universe size", a.Config.Universe.Count.ToString(), b.Config.Universe.Count.ToString());
        Diff("Sector limits", string.Join(", ", a.Config.SectorLimits.Select(kv => $"{kv.Key} ≤ {kv.Value:0%}")), string.Join(", ", b.Config.SectorLimits.Select(kv => $"{kv.Key} ≤ {kv.Value:0%}")));
        Diff("Covariance", a.Config.CovarianceMethod, b.Config.CovarianceMethod);
        Diff("History window", $"{a.Config.LookbackDays} days", $"{b.Config.LookbackDays} days");
        var notes = new List<string>();
        var turnover = weights.Sum(w => Math.Abs(w.WeightA - w.WeightB)) / 2;
        notes.Add($"Switching from A to B would change {turnover:0%} of the portfolio (one-way turnover).");
        if (a.Result.PeriodStart == b.Result.PeriodStart && a.Result.PeriodEnd == b.Result.PeriodEnd)
            notes.Add($"Both use the same window ({a.Result.PeriodStart:yyyy-MM-dd} to {a.Result.PeriodEnd:yyyy-MM-dd}), so the figures are comparable.");
        else
            notes.Add("The two use different history windows; their estimated figures are not directly comparable.");
        if (a.Result.Volatility is { } va && b.Result.Volatility is { } vb)
            notes.Add($"Estimated volatility: A {va:0.0%}, B {vb:0.0%}" + (Math.Abs(va - vb) < 0.005 ? " – practically the same; the evidence does not distinguish them on risk." : "."));
        return new HypothesisComparisonDto(a, b, weights, cfg, notes);
    }

    // ------------------------------------------------------------------ stage 9: save

    public async Task<ResearchSessionDto> SaveHypothesisAsync(Guid id, SaveHypothesisRequest request, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct);
        var hypotheses = Hypotheses(session);
        var h = hypotheses.FirstOrDefault(x => x.Id == request.HypothesisId) ?? throw new NotFoundException("Hypothesis", request.HypothesisId);
        if (h.SavedRunId is not null)
        {
            Event(session, "system", "save_research_run", "skipped", $"“{h.Label}” was already saved; no duplicate created.");
            await PersistAsync(session, ct);
            return await ToDtoAsync(session, ct);
        }
        if (h.Result.Status != OptimizationRunStatus.Succeeded) throw new ConflictException("Only a successful calculation can be saved.");
        SaveRunResult? result = null;
        await Guarded(session, async () => result = await tools.RunAsync(saveHypothesis, Context(session, confirmed: request.Confirm),
            new SaveRunArgs(h.Config, request.PortfolioId ?? session.PortfolioId, h.Result.DataSnapshotHash), ct), ct);
        hypotheses[hypotheses.IndexOf(h)] = h with { SavedRunId = result!.RunId };
        session.HypothesesJson = JsonSerializer.Serialize(hypotheses, Json);
        await PersistAsync(session, ct);
        return await ToDtoAsync(session, ct);
    }

    public async Task<CompanyContextDto> CompanyContextAsync(Guid id, string symbol, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct);
        // Context is only available for stocks this session actually researched.
        var set = JsonSerializer.Deserialize<CandidateSetDto>(session.CandidatesJson, Json);
        var normalized = Asset.NormalizeSymbol(symbol);
        if (set is null || !set.Candidates.Any(c => c.Symbol == normalized))
            throw new NotFoundException("Stock in this research session", normalized);
        symbol = normalized;
        CompanyContextDto? context = null;
        await Guarded(session, async () => context = await tools.RunAsync(companyContext, Context(session), symbol, ct), ct);
        await PersistAsync(session, ct);
        return context!;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var session = await LoadAsync(id, ct);
        await db.ResearchEvents.Where(e => e.SessionId == session.Id).ExecuteDeleteAsync(ct);
        db.ResearchSessions.Remove(session);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ResearchSession> LoadAsync(Guid id, CancellationToken ct, bool tracking = true)
    {
        var owner = currentUser.RequireUserId();
        var q = tracking ? db.ResearchSessions : db.ResearchSessions.AsNoTracking();
        return await q.SingleOrDefaultAsync(s => s.Id == id && s.OwnerId == owner, ct) ?? throw new NotFoundException("Research session", id);
    }

    private ToolContext Context(ResearchSession s, bool confirmed = false) => new(currentUser.RequireUserId(), s, confirmed);

    private async Task PersistAsync(ResearchSession session, CancellationToken ct)
    {
        session.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        session.Version++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("This research session was changed by another request at the same time. Reload and try again.");
        }
    }

    private void Event(ResearchSession s, string kind, string name, string status, string summary) =>
        s.Events.Add(new ResearchEvent { SessionId = s.Id, AtUtc = clock.GetUtcNow().UtcDateTime, Kind = kind, Name = name, Status = status, Summary = Short(summary, 500) });

    private static void Count(ResearchSession s, int input, int output)
    {
        s.LlmInputTokens += input;
        s.LlmOutputTokens += output;
    }

    private static ResearchBrief Brief(ResearchSession s) => JsonSerializer.Deserialize<ResearchBrief>(s.BriefJson, Json) ?? new ResearchBrief();
    private static void Save(ResearchSession s, ResearchBrief b) => s.BriefJson = JsonSerializer.Serialize(b, Json);
    private static CandidateSetDto? Candidates(ResearchSession s) => JsonSerializer.Deserialize<CandidateSetDto?>(s.CandidatesJson, Json);
    private static List<HypothesisDto> Hypotheses(ResearchSession s) => JsonSerializer.Deserialize<List<HypothesisDto>>(s.HypothesesJson, Json) ?? [];

    private static string Title(ResearchBrief b) =>
        Short((b.Market is { } m ? BriefRules.MarketNames[m] : "Market to be chosen") + " · " +
              InterpretationTemplate.ObjectiveNames.GetValueOrDefault(b.Objective, b.Objective), 140);

    private static string Short(string s, int max = 160) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Stable fingerprint of a configuration (same inputs → same hypothesis; used for idempotency).</summary>
    public static string Hash(HypothesisConfig c)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            c.Objective, c.MaxWeight, Universe = c.Universe.OrderBy(x => x, StringComparer.Ordinal), Excluded = c.ExcludedSymbols.OrderBy(x => x, StringComparer.Ordinal),
            Sectors = c.SectorLimits.OrderBy(kv => kv.Key, StringComparer.Ordinal), c.CovarianceMethod, c.LookbackDays, c.TransactionCostBps, c.Currency,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    private async Task<ResearchSessionDto> ToDtoAsync(ResearchSession s, CancellationToken ct)
    {
        var events = await db.ResearchEvents.AsNoTracking().Where(e => e.SessionId == s.Id).OrderBy(e => e.Id).ToListAsync(ct);
        // Events added in this request are not in the database yet when the session is unsaved.
        var all = events.Concat(s.Events.Where(e => e.Id == 0)).OrderBy(e => e.AtUtc).ToList();
        var hypotheses = Hypotheses(s);
        var pending = s.PendingRefinementJson is { } pj ? JsonSerializer.Deserialize<RefinementProposalDto>(pj, Json) : null;
        var brief = Brief(s);
        var portfolio = s.PortfolioId is { } pid
            ? await db.Portfolios.AsNoTracking().Where(p => p.Id == pid && p.OwnerId == s.OwnerId).Select(p => new { p.Name, p.BaseCurrency }).SingleOrDefaultAsync(ct)
            : null;
        return new ResearchSessionDto(s.Id, s.Title, s.State.ToString(), s.CreatedAtUtc, s.UpdatedAtUtc, brief, brief.Question, Steps(s, hypotheses),
            Candidates(s), hypotheses, pending,
            all.TakeLast(200).Select(e => new ResearchEventDto(e.AtUtc, e.Kind, e.Name, e.Status, e.DurationMs, e.Summary)).ToList(),
            new ResearchBudgetDto(s.ToolCalls, ToolRunner.MaxToolCallsPerSession, hypotheses.Count, MaxHypotheses, s.LlmInputTokens + s.LlmOutputTokens, MaxLlmTokens),
            new AssistantStatusDto(model.Provider, model.IsAvailable ? model.Model : null, model.IsAvailable,
                model.IsAvailable ? "A language model drafts briefs, interprets refinements and writes explanations; every proposal is validated against the calculations."
                    : "No language model is configured: briefs, refinements and explanations come from deterministic rules over the calculations. All analyses work."),
            Disclaimer, portfolio is null ? null : s.PortfolioId, portfolio?.Name, portfolio?.BaseCurrency);
    }

    private static List<WorkflowStepDto> Steps(ResearchSession s, List<HypothesisDto> h)
    {
        string St(bool done, bool current) => done ? "done" : current ? "current" : "pending";
        var saved = h.Any(x => x.SavedRunId is not null);
        return
        [
            new("objective", "Objective", "done"),
            new("brief", "Research brief", s.State == ResearchState.AwaitingInput ? "waiting" : "done"),
            new("universe", "Candidate universe", St(s.State >= ResearchState.UniverseReady, s.State == ResearchState.BriefReady)),
            new("hypotheses", "Portfolio hypotheses", St(h.Count > 0, s.State == ResearchState.UniverseReady)),
            new("review", "Critical review", St(h.Any(x => x.Review is not null), false)),
            new("refine", "Refinement", St(h.Any(x => x.Origin.StartsWith("refinement")), s.State == ResearchState.Evaluated && !saved)),
            new("save", "Saved result", St(saved, false)),
        ];
    }
}
