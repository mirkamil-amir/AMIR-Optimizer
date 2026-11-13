using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Analytics;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Application.Research;
using PortfolioOptimizer.Application.Trading;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Pilot;

public sealed class PilotOptions
{
    public const string Section = "MatrixPilot";
    /// <summary>Whether the scheduler starts cycles (the API and manual runs work either way).</summary>
    public bool SchedulerEnabled { get; set; } = true;
    public int SchedulerIntervalMinutes { get; set; } = 15;
    /// <summary>UTC hour of a decision (after the trading day's closes are stored by the market-data refresh).</summary>
    public int DecisionHourUtc { get; set; } = 22;
    public int MaxResearchRounds { get; set; } = 6;
    public int MaxToolCallsPerRound { get; set; } = 6;
    public int CycleTimeoutMinutes { get; set; } = 20;
    public int ModelTimeoutSeconds { get; set; } = 300;
    public int MaxInputTokensPerCycle { get; set; } = 600_000;
    public int MaxConsecutiveFailures { get; set; } = 3;
    /// <summary>How often a proposal that breaks the mandate is sent back to the model before the cycle is BLOCKED.</summary>
    public int MaxFitRevisions { get; set; } = 5;
    /// <summary>After a FAILED or SKIPPED attempt (nothing decided or traded) the scheduler tries the same period again after this.</summary>
    public int RetryAfterMinutes { get; set; } = 60;
    /// <summary>Characters of research log sent back to the model; older tool results are reduced to their summary beyond it.</summary>
    public int MaxResearchContextChars { get; set; } = 160_000;
    /// <summary>A cycle lease older than this is treated as abandoned (the process died mid-cycle).</summary>
    public int StaleLeaseMinutes { get; set; } = 60;
}

/// <summary>
/// Runs one Matrix Pilot decision cycle as an explicit state machine:
/// OBSERVE → RESEARCH (model ↔ tools, bounded rounds) → CONSTRUCT (model decision) → CRITIQUE (model reviewer) →
/// FINALIZE (model) → VALIDATE (code) → EXECUTE (code) → JOURNAL → COMPLETE. The model decides; code checks, books and
/// records. Every failure ends the cycle without a trade (fail closed). See docs/matrix-pilot.md.
/// </summary>
public sealed class PilotEngine(
    IApplicationDbContext db,
    PortfolioService portfolios,
    PortfolioValuationService valuations,
    PortfolioAnalyticsService performance,
    PriceQueryService prices,
    TradingService trading,
    IPilotLanguageModel model,
    PilotToolRunner tools,
    PilotAnalytics analytics,
    PilotJournal journal,
    PilotFitChecker fitChecker,
    BackgroundActor actor,
    ICurrentUser currentUser,
    IAuditLog audit,
    TimeProvider clock,
    IOptions<PilotOptions> options,
    ILogger<PilotEngine> logger)
{
    private readonly PilotOptions _o = options.Value;

    /// <summary>Thrown inside the state machine to end a cycle with a specific, recorded outcome (no trade).</summary>
    private sealed class CycleStop(PilotCycleOutcome outcome, string reason, bool pausePilot = false) : Exception(reason)
    {
        public PilotCycleOutcome Outcome { get; } = outcome;
        public bool PausePilot { get; } = pausePilot;
    }

    public async Task ExecuteAsync(Guid cycleId, CancellationToken ct)
    {
        var cycle = await db.MatrixPilotCycles.Include(c => c.Steps).SingleOrDefaultAsync(c => c.Id == cycleId, ct);
        if (cycle is null || cycle.IsFinished) return; // idempotent: a cycle runs once
        var pilot = await db.MatrixPilots.SingleAsync(p => p.Id == cycle.PilotId, ct);

        // The job acts as the pilot's owner (from the stored record) – never as anyone the model or a request names.
        actor.ActAs(pilot.OwnerId, "Matrix Pilot", null);
        if (currentUser.RequireUserId() != pilot.OwnerId || cycle.OwnerId != pilot.OwnerId || cycle.PortfolioId != pilot.PortfolioId)
            throw new InvalidOperationException("Cycle and pilot disagree about owner or portfolio.");

        var total = Stopwatch.StartNew();
        var now = clock.GetUtcNow().UtcDateTime;
        cycle.StartedAtUtc = now;
        cycle.AsOfUtc = now;

        // Lease: only one cycle may hold a portfolio at a time (conditional update; the loser does not trade).
        var leased = await db.MatrixPilots
            .Where(p => p.Id == pilot.Id && p.Status == PilotStatus.Active && (p.ActiveCycleId == null || p.ActiveCycleId == cycle.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ActiveCycleId, cycle.Id).SetProperty(p => p.ActiveCycleStartedAtUtc, now), ct);
        await db.Entry(pilot).ReloadAsync(ct);
        if (leased == 0)
        {
            var reason = pilot.Status != PilotStatus.Active
                ? $"Matrix Pilot was {Describe(pilot.Status)} when the cycle came up; nothing was decided or traded."
                : "Another decision cycle was holding this portfolio; this one did not start.";
            await FinishAsync(cycle, pilot, PilotCycleOutcome.Skipped, reason, total, releaseLease: false, ct);
            return;
        }

        PilotCycleOutcome outcome;
        string outcomeReason;
        var pause = false;
        using var cycleLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cycleLimit.CancelAfter(TimeSpan.FromMinutes(_o.CycleTimeoutMinutes));
        try
        {
            (outcome, outcomeReason) = await RunAsync(cycle, pilot, cycleLimit.Token);
        }
        catch (CycleStop stop)
        {
            (outcome, outcomeReason, pause) = (stop.Outcome, stop.Message, stop.PausePilot);
        }
        catch (LanguageModelException ex) when (ex.Failure is LlmFailure.Unauthorized or LlmFailure.ModelNotFound)
        {
            // Retrying cannot help: stop until the owner fixes the key or model and resumes (the model check runs again then).
            (outcome, outcomeReason, pause) = (PilotCycleOutcome.Failed,
                $"Model call failed ({ex.Failure}): {ex.Message} Nothing was traded; Matrix Pilot paused itself until the key or model is fixed and you resume it.", true);
        }
        catch (LanguageModelException ex)
        {
            (outcome, outcomeReason) = (PilotCycleOutcome.Failed, $"Model call failed ({ex.Failure}): {ex.Message} Nothing was traded.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            (outcome, outcomeReason) = (PilotCycleOutcome.Failed, $"The cycle exceeded its {_o.CycleTimeoutMinutes}-minute limit. Nothing was traded.");
        }
        catch (Exception ex) when (ex is ValidationException or ConflictException or NotFoundException or ForbiddenException or DomainException or Fx.FxRateUnavailableException)
        {
            (outcome, outcomeReason) = (PilotCycleOutcome.Failed, $"{ex.Message} Nothing was traded.");
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Unknown state: stop and let the owner look (fail closed).
            logger.LogError(ex, "Matrix Pilot cycle {CycleId} failed unexpectedly", cycle.Id);
            (outcome, outcomeReason, pause) = (PilotCycleOutcome.Failed, "Unexpected error; Matrix Pilot paused itself until the owner resumes it. Nothing was traded.", true);
        }

        if (pause || outcome == PilotCycleOutcome.PausedByDrawdown)
            await PauseAsync(pilot, cycle, outcomeReason, ct);
        await FinishAsync(cycle, pilot, outcome, outcomeReason, total, releaseLease: true, ct);
    }

    private async Task<(PilotCycleOutcome, string)> RunAsync(MatrixPilotCycle cycle, MatrixPilot pilot, CancellationToken ct)
    {
        // ------------------------------------------------------------------------------------------------ OBSERVE
        await PhaseAsync(cycle, PilotCyclePhase.Observe, ct);
        if (!model.IsAvailable)
            throw new CycleStop(PilotCycleOutcome.Failed, "No language model is configured (Agent:Llm:Provider). Matrix Pilot never falls back to fixed rules; nothing was traded.");
        if (model.Provider != pilot.Provider || model.Model != pilot.Model)
            throw new CycleStop(PilotCycleOutcome.Failed,
                $"The configured model is {model.Provider} {model.Model}, but this pilot was set up with {pilot.Provider} {pilot.Model}. The owner must confirm the model change in the mandate; nothing was traded.");

        var mandate = await MandateAsync(pilot, ct);
        var ctx = await ObserveAsync(cycle, pilot, mandate, ct);

        // ------------------------------------------------------------------------------------------------ RULES (first)
        await PhaseAsync(cycle, PilotCyclePhase.Rules, ct);
        var log = new List<object>();
        var rules = await CallAsync<PilotRulesCheck>(cycle, "pilot.rules", PilotPrompts.Rules,
            Data(new { stage = "rules", mandate, portfolio = ctx.Observation, universe = UniverseMix(ctx) }), PilotPrompts.RulesSchema, 4000, "Rules first", ct);
        rules = new PilotRulesCheck
        {
            Constraints = (rules.Constraints ?? []).Where(c => c is not null).Take(30)
                .Select(c => new PilotRuleNote(PilotText.Clip(c.Rule, 120), PilotText.Clip(c.Limit, 120), PilotText.Clip(c.Implication, 400))).ToList(),
            Tensions = (rules.Tensions ?? []).Take(15).Select(t => PilotText.Clip(t, 400)).ToList(),
        };
        log.Add(new { stage = "rules", constraints = rules.Constraints, tensions = rules.Tensions });

        // ------------------------------------------------------------------------------------------------ RESEARCH (market)
        await PhaseAsync(cycle, PilotCyclePhase.Research, ct);
        for (var round = 1; round <= _o.MaxResearchRounds; round++)
        {
            if (cycle.InputTokens > _o.MaxInputTokensPerCycle) break;
            var content = Data(new
            {
                round,
                stage = "research",
                portfolio = ctx.Observation,
                mandate,
                tools = tools.Available.Select(t => new { t.Name, t.Purpose, category = t.Category.ToString(), t.Arguments }),
                research = TrimLog(log),
            });
            var turn = await CallAsync<PilotResearchTurn>(cycle, "pilot.research", PilotPrompts.Research(round, _o.MaxResearchRounds,
                PilotToolRunner.MaxToolCallsPerCycle - cycle.ToolCalls), content, PilotPrompts.ResearchSchema, 3000, $"Research round {round}", ct);
            turn = turn with { ToolCalls = (turn.ToolCalls ?? []).Where(c => c is not null).ToList(), Notes = turn.Notes ?? "" };
            log.Add(new { round, notes = PilotText.Clip(turn.Notes, 800) });
            if (turn.Ready || turn.ToolCalls.Count == 0) break;
            foreach (var request in turn.ToolCalls.Take(_o.MaxToolCallsPerRound))
            {
                var outcome = await tools.RunAsync(ctx, request, ct);
                log.Add(new { step = outcome.Step, tool = outcome.Tool, ok = outcome.Ok, args = PilotText.Clip(request.ArgsJson, 600), result = outcome.Result });
            }
            if (turn.ToolCalls.Count > _o.MaxToolCallsPerRound)
                log.Add(new { note = $"Only the first {_o.MaxToolCallsPerRound} tool calls of a round are run." });
        }
        await SaveAsync(ct);

        // ------------------------------------------------------------------------------------------------ CONSTRUCT
        await PhaseAsync(cycle, PilotCyclePhase.Construct, ct);
        var decision = await DecideAsync(cycle, "pilot.decide", PilotPrompts.Decide,
            Data(new { stage = "decide", portfolio = ctx.Observation, mandate, research = TrimLog(log) }), "Decision", ct);
        cycle.InitialDecisionJson = PilotJson.Serialize(decision);

        // ------------------------------------------------------------------------------------------------ FIT (until it fits the mandate)
        PilotFitResult fitted;
        (decision, fitted) = await FitAsync(cycle, ctx, decision, log, ct);

        // ------------------------------------------------------------------------------------------------ CRITIQUE
        await PhaseAsync(cycle, PilotCyclePhase.Critique, ct);
        var risk = fitted.Risk;
        var trades = fitted.Validation.Trades;
        var critique = await CallAsync<PilotCritique>(cycle, "pilot.critique", PilotPrompts.Critique,
            Data(new { decision, fitsMandate = fitted.Fits, violations = fitted.Validation.Violations, risk, trades, mandate, portfolio = ctx.Observation }),
            PilotPrompts.CritiqueSchema, 3000, "Risk review", ct);
        critique = critique with { Concerns = (critique.Concerns ?? []).Where(c => c is not null).ToList(), Summary = critique.Summary ?? "" };
        critique = critique with
        {
            Recommendation = critique.Recommendation is "keep" or "revise" or "hold" ? critique.Recommendation : "keep",
            Summary = PilotText.Clip(critique.Summary, 1500),
            Concerns = critique.Concerns.Take(15).Select(c => c with { Detail = PilotText.Clip(c.Detail, 600) }).ToList(),
        };
        cycle.CritiqueJson = PilotJson.Serialize(critique);

        // ------------------------------------------------------------------------------------------------ FINALIZE
        await PhaseAsync(cycle, PilotCyclePhase.Finalize, ct);
        if (critique.Recommendation != "keep")
        {
            decision = await DecideAsync(cycle, "pilot.finalize", PilotPrompts.Finalize,
                Data(new { stage = "finalize", proposed = decision, critique, risk, trades, mandate, portfolio = ctx.Observation, research = TrimLog(log) }), "Final decision", ct);
            (decision, fitted) = await FitAsync(cycle, ctx, decision, log, ct);
        }
        else journal.Add(cycle, PilotStepKind.AiDecision, "Final decision", "ok", 0, "The reviewer recommended keeping the decision; it stands as proposed.");
        cycle.DecisionJson = PilotJson.Serialize(decision);
        await SaveAsync(ct);

        // ------------------------------------------------------------------------------------------------ VALIDATE (final, independent)
        await PhaseAsync(cycle, PilotCyclePhase.Validate, ct);
        var sw = Stopwatch.StartNew();
        var final = await fitChecker.CheckAsync(ctx, decision, requireTexts: true, ct);
        var validation = final.Validation;
        trades = validation.Trades;
        cycle.ValidationMs = sw.Elapsed.TotalMilliseconds;
        cycle.ValidationJson = PilotJson.Serialize(validation);
        journal.Add(cycle, PilotStepKind.Validation, "Validation", validation.Passed ? "ok" : "rejected", sw.Elapsed.TotalMilliseconds,
            validation.Passed ? $"Passed {validation.Checks.Count} check(s) after {cycle.FitRevisions} revision(s)." : $"Blocked: {string.Join(" ", validation.Violations.Take(4).Select(v => v.Detail))}",
            null, PilotJson.Serialize(validation));
        await SaveAsync(ct);

        // ------------------------------------------------------------------------------------------------ UNCONSTRAINED + COMPARE (never traded)
        await ShadowAsync(cycle, ctx, decision, log, ct);

        if (!validation.Passed)
            return (PilotCycleOutcome.Blocked, $"After {cycle.FitRevisions} revision(s) the decision still broke {validation.Violations.Count} rule(s); nothing was traded. " +
                                               string.Join(" ", validation.Violations.Take(3).Select(v => v.Detail)));
        if (decision.IsHold)
        {
            await NarrateAsync(cycle, ctx, decision, validation, null, PilotCycleOutcome.NoTrade, ct);
            return (PilotCycleOutcome.NoTrade, "Matrix Pilot decided to hold the current portfolio, which fits the mandate.");
        }
        if (trades is { Buys: 0, Sells: 0 })
        {
            await NarrateAsync(cycle, ctx, decision, validation, null, PilotCycleOutcome.NoTrade, ct);
            return (PilotCycleOutcome.NoTrade, "The target matches the current portfolio within the drift threshold; no order was needed.");
        }

        // ------------------------------------------------------------------------------------------------ EXECUTE
        await PhaseAsync(cycle, PilotCyclePhase.Execute, ct);
        sw.Restart();
        // The owner may have pressed the kill switch while the model was thinking: check the stored status again.
        var status = await db.MatrixPilots.AsNoTracking().Where(p => p.Id == pilot.Id).Select(p => p.Status).SingleAsync(ct);
        if (status != PilotStatus.Active)
            throw new CycleStop(PilotCycleOutcome.Skipped, $"Matrix Pilot was {Describe(status)} before execution; the validated decision was not booked.");

        var weights = decision.Targets.ToDictionary(t => Asset.NormalizeSymbol(t.Symbol), t => t.Weight);
        var proposal = await trading.CreateForTargetsAsync(pilot.PortfolioId, weights, $"Matrix Pilot decision of {cycle.AsOfUtc:yyyy-MM-dd HH:mm} UTC (cycle {cycle.Id:N}).", ct);
        cycle.ProposalId = proposal.Id;
        journal.Add(cycle, PilotStepKind.Execution, "Trade proposal", "ok", sw.Elapsed.TotalMilliseconds,
            $"{proposal.Lines.Count(l => l.Action == ProposalAction.Buy)} buy(s), {proposal.Lines.Count(l => l.Action == ProposalAction.Sell)} sell(s), estimated costs {proposal.EstimatedCosts:N2}.",
            null, PilotJson.Serialize(proposal.Lines.Where(l => l.Action is ProposalAction.Buy or ProposalAction.Sell)
                .Select(l => new { l.Symbol, action = l.Action.ToString(), l.OrderQuantity, l.EstimatedTradeValue, l.TargetWeight, l.Note })));
        await SaveAsync(ct);

        var result = await trading.ExecuteAsync(pilot.PortfolioId, new ExecutePaperTradesRequest { ProposalId = proposal.Id, Confirm = true }, $"pilot:{cycle.Id:N}", ct);
        var filled = result.Orders.Count(o => o.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled);
        cycle.TradeBatchId = result.BatchId;
        cycle.TradesBooked = filled;
        cycle.Fees = result.TotalFees;
        cycle.ExecutionMs = sw.Elapsed.TotalMilliseconds;
        journal.Add(cycle, PilotStepKind.Execution, "Simulated booking", filled > 0 ? "ok" : "error", sw.Elapsed.TotalMilliseconds,
            $"{filled} order(s) filled, {result.Orders.Count - filled} rejected, fees {result.TotalFees:N2} (simulated – no real order).",
            null, PilotJson.Serialize(result.Orders.Select(o => new { o.Symbol, side = o.Side.ToString(), o.FilledQuantity, o.ExecutionPrice, status = o.Status.ToString(), o.Message })));

        // ------------------------------------------------------------------------------------------------ JOURNAL
        await PhaseAsync(cycle, PilotCyclePhase.Journal, ct);
        var executed = await ExecutedAsync(pilot.PortfolioId, decision, ct);
        cycle.ExecutedJson = PilotJson.Serialize(executed);
        if (filled == 0)
        {
            await NarrateAsync(cycle, ctx, decision, validation, executed, PilotCycleOutcome.Failed, ct);
            return (PilotCycleOutcome.Failed, "The validated decision was submitted, but every simulated order was rejected at execution (see the booking entry).");
        }
        await NarrateAsync(cycle, ctx, decision, validation, executed, PilotCycleOutcome.Rebalanced, ct);
        return (PilotCycleOutcome.Rebalanced, $"Rebalanced: {filled} simulated order(s) booked.");
    }

    // ==================================================================================================== OBSERVE

    private async Task<PilotToolContext> ObserveAsync(MatrixPilotCycle cycle, MatrixPilot pilot, PilotMandate mandate, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var portfolio = await portfolios.LoadAsync(pilot.PortfolioId, ct); // owner-scoped (acting as the owner)
        if (portfolio.IsArchived)
            throw new CycleStop(PilotCycleOutcome.Skipped, "The portfolio is archived (read-only); Matrix Pilot paused itself.", pausePilot: true);
        var app = await prices.SettingsAsync(ct);
        var asOfDate = DateOnly.FromDateTime(cycle.AsOfUtc);

        var universeAssets = await PilotUniverse.ResolveAsync(db, mandate, ct);
        var heldIds = portfolio.Holdings.Where(h => h.Quantity > 0).Select(h => h.AssetId).ToList();
        var allIds = universeAssets.Select(a => a.Id).Union(heldIds).ToList();
        var valuation = await valuations.ValueAsync(portfolio, null, ct, allIds);

        var freshness = new List<PilotFreshness>();
        var notes = new List<string>(valuation.Warnings);
        var universe = new Dictionary<string, PilotAssetInfo>(StringComparer.Ordinal);
        var future = new List<string>();
        foreach (var a in universeAssets)
        {
            var v = valuation.Positions.GetValueOrDefault(a.Id);
            string? issue = v?.Price is null ? "no stored price"
                : v.Price.PriceDate > asOfDate ? "price dated after the decision time"
                : v.Price.IsStale ? $"latest price from {v.Price.PriceDate:yyyy-MM-dd} is stale"
                : v.UnitValue is null ? v.Problem ?? "no exchange rate" : null;
            if (issue == "price dated after the decision time") future.Add(a.Symbol);
            universe[a.Symbol] = new PilotAssetInfo(a.Symbol, a.Name, a.Sector, a.Currency, a.Country, issue is null, issue);
        }
        if (future.Count > 0)
            throw new CycleStop(PilotCycleOutcome.Skipped,
                $"Stored prices are dated after the decision time ({string.Join(", ", future.Take(5))}); the server clock may be wrong. Nothing was decided (no look-ahead).");

        var holdings = new List<PilotPositionSnapshot>();
        foreach (var h in portfolio.Holdings.Where(h => h.Quantity > 0))
        {
            var v = valuation.Positions.GetValueOrDefault(h.AssetId);
            var fresh = v?.Price is { IsStale: false } && v.UnitValue is not null && v.Price.PriceDate <= asOfDate;
            freshness.Add(new PilotFreshness(h.Symbol, fresh, v?.Price?.PriceDate,
                fresh ? "fresh" : v?.Price is null ? "no price" : v.UnitValue is null ? v.Problem ?? "no exchange rate" : $"stale since {v.Price.PriceDate:yyyy-MM-dd}"));
            holdings.Add(new PilotPositionSnapshot(h.Symbol, h.Quantity, v?.Value is { } val ? Math.Round(val, 2) : null,
                v?.Value is { } x && valuation.TotalValue is > 0 ? Math.Round((double)(x / valuation.TotalValue.Value), 4) : null, v?.Price?.PriceDate));
        }
        var pricedUniverse = universe.Values.Count(a => a.Priced);
        freshness.Add(new PilotFreshness("universe prices", pricedUniverse > 0, null, $"{pricedUniverse} of {universe.Count} investable symbols have a fresh price and rate."));

        double? drawdown = null, liveReturn = null;
        if (pilot.BaselineAtUtc is { } baseline)
        {
            var perf = await performance.GetPerformanceAsync(pilot.PortfolioId, DateOnly.FromDateTime(baseline), asOfDate, ct);
            if (perf.Series.Count > 0)
            {
                drawdown = Math.Round(perf.Series[^1].Drawdown, 4);
                liveReturn = Math.Round(perf.Series[^1].CumulativeReturn, 4);
            }
        }

        var observation = new PilotObservation(cycle.AsOfUtc, asOfDate, portfolio.BaseCurrency,
            valuation.TotalValue is { } tv ? Math.Round(tv, 2) : null, Math.Round(portfolio.CashBalance, 2), holdings, universe.Count, freshness,
            drawdown, liveReturn, valuation.UsesSimulatedData, notes);
        cycle.ContextJson = PilotJson.Serialize(observation);
        cycle.DataMs = sw.Elapsed.TotalMilliseconds;

        var staleHoldings = freshness.Where(f => !f.Fresh && f.Item != "universe prices").ToList();
        var problem = valuation.TotalValue is null ? "the portfolio cannot be valued (" + string.Join(" ", valuation.Problems) + ")"
            : staleHoldings.Count > 0 ? "held positions lack fresh data: " + string.Join(", ", staleHoldings.Select(f => $"{f.Item} ({f.Detail})"))
            : pricedUniverse == 0 ? "no investable symbol has a fresh price"
            : null;
        journal.Add(cycle, PilotStepKind.Observation, "Observe", problem is null ? "ok" : "skipped", sw.Elapsed.TotalMilliseconds,
            problem is null
                ? $"Value {observation.PortfolioValue:N2} {observation.Currency}, {holdings.Count} holding(s), cash {observation.Cash:N2}; {pricedUniverse} investable symbols priced."
                : $"Data not ready: {problem}.", null, cycle.ContextJson);
        if (problem is not null)
            throw new CycleStop(PilotCycleOutcome.Skipped, $"Waiting for data – {problem}. Nothing was decided or traded.");

        if (drawdown is { } dd && Math.Abs(dd) >= mandate.DrawdownPauseThreshold)
            throw new CycleStop(PilotCycleOutcome.PausedByDrawdown,
                $"Drawdown of the live record is {Math.Abs(dd):P1}, beyond the mandate's pause threshold of {mandate.DrawdownPauseThreshold:P0}. Matrix Pilot paused itself; only the owner can resume it.");

        await SaveAsync(ct);
        return new PilotToolContext
        {
            OwnerId = pilot.OwnerId,
            PortfolioId = pilot.PortfolioId,
            PilotId = pilot.Id,
            Cycle = cycle,
            Portfolio = portfolio,
            Mandate = mandate,
            Observation = observation,
            Universe = universe,
            Currency = portfolio.BaseCurrency,
            PriceSource = app.ActiveProvider,
            Benchmark = mandate.Benchmark ?? portfolio.Settings.BenchmarkSymbol,
            RiskFreeRate = app.DefaultRiskFreeRate,
        };
    }

    // ==================================================================================================== model calls

    private async Task<PilotDecision> DecideAsync(MatrixPilotCycle cycle, string purpose, string instructions, string content, string label, CancellationToken ct)
    {
        var d = await CallAsync<PilotDecision>(cycle, purpose, instructions, content,
            PilotPrompts.DecisionSchema, 8000, label, ct);
        // Missing lists become empty (the structure check then reports what is missing); nothing is invented.
        d = d with
        {
            Targets = d.Targets ?? [], Rejected = d.Rejected ?? [], Rationale = d.Rationale ?? [], KeyRisks = d.KeyRisks ?? [],
            InvalidationConditions = d.InvalidationConditions ?? [], MandateRequests = d.MandateRequests ?? [], EvidenceSteps = d.EvidenceSteps ?? [],
            Strategy = d.Strategy is null ? new PilotStrategyChoice() : d.Strategy with { Components = d.Strategy.Components ?? [], Approach = d.Strategy.Approach ?? "", Reason = d.Strategy.Reason ?? "" },
            Confidence = d.Confidence ?? "",
        };
        // Keep the model's values; only cap text lengths for storage (a too-long text is not a reason to change a weight).
        return d with
        {
            Action = (d.Action ?? "").Trim().ToUpperInvariant(),
            Targets = d.Targets.Take(80).Select(t => t with
            {
                Symbol = Asset.NormalizeSymbol(t.Symbol ?? ""),
                Role = PilotText.Clip(t.Role, 200), Thesis = PilotText.Clip(t.Thesis, 800), WhyNow = PilotText.Clip(t.WhyNow, 500),
                Risks = PilotText.Clip(t.Risks, 500), Invalidation = PilotText.Clip(t.Invalidation, 500),
            }).ToList(),
            Strategy = d.Strategy with { Reason = PilotText.Clip(d.Strategy.Reason, 800), Components = d.Strategy.Components.Take(12).Select(c => PilotText.Clip(c, 80)).ToList() },
            Rejected = d.Rejected.Take(40).Select(r => r with { Symbol = Asset.NormalizeSymbol(r.Symbol ?? ""), Reason = PilotText.Clip(r.Reason, 300) }).ToList(),
            Rationale = d.Rationale.Take(10).Select(r => PilotText.Clip(r, 600)).ToList(),
            KeyRisks = d.KeyRisks.Take(10).Select(r => PilotText.Clip(r, 400)).ToList(),
            InvalidationConditions = d.InvalidationConditions.Take(10).Select(r => PilotText.Clip(r, 400)).ToList(),
            MandateRequests = d.MandateRequests.Take(5).Select(r => PilotText.Clip(r, 300)).ToList(),
            CritiqueResponse = string.IsNullOrWhiteSpace(d.CritiqueResponse) ? null : PilotText.Clip(d.CritiqueResponse, 1500),
        };
    }

    /// <summary>One bounded model call with schema-checked output; anything malformed fails the cycle (no guessing).</summary>
    private async Task<T> CallAsync<T>(MatrixPilotCycle cycle, string purpose, string instructions, string content,
        IReadOnlyDictionary<string, JsonElement> schema, int maxTokens, string label, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        cycle.ModelCalls++;
        LlmResponse response;
        try
        {
            response = await model.GenerateAsync(new LlmRequest(purpose, instructions, content, schema, maxTokens, cycle.Effort,
                TimeSpan.FromSeconds(_o.ModelTimeoutSeconds)), ct);
        }
        catch (LanguageModelException ex)
        {
            cycle.ModelMs += sw.Elapsed.TotalMilliseconds;
            journal.Add(cycle, PilotStepKind.AiDecision, label, "error", sw.Elapsed.TotalMilliseconds, $"{ex.Failure}: {ex.Message}");
            throw;
        }
        cycle.ModelMs += sw.Elapsed.TotalMilliseconds;
        cycle.InputTokens += response.InputTokens;
        cycle.OutputTokens += response.OutputTokens;
        cycle.ModelReported = PilotText.Clip(response.Model, 100);
        T? value;
        try { value = response.Output.Deserialize<T>(PilotJson.Options); }
        catch (JsonException) { value = default; }
        var raw = response.Output.GetRawText();
        if (value is null || !SchemaComplete(response.Output, schema))
        {
            journal.Add(cycle, PilotStepKind.AiDecision, label, "rejected", sw.Elapsed.TotalMilliseconds, "The model's answer did not match the required format.", null, raw);
            throw new CycleStop(PilotCycleOutcome.Failed, $"Malformed model answer at “{label}”: it did not match the required format. Nothing was traded.");
        }
        journal.Add(cycle, PilotStepKind.AiDecision, label, "ok", sw.Elapsed.TotalMilliseconds, Summary(value), null, raw);
        return value;
    }

    /// <summary>Every required top-level field is present (the provider enforces the schema, this re-checks it).</summary>
    private static bool SchemaComplete(JsonElement output, IReadOnlyDictionary<string, JsonElement> schema) =>
        output.ValueKind == JsonValueKind.Object &&
        (!schema.TryGetValue("required", out var req) || req.EnumerateArray().All(r => output.TryGetProperty(r.GetString()!, out _)));

    private static string Summary(object value) => value switch
    {
        PilotResearchTurn t => $"{PilotText.Clip(t.Notes, 300)} {(t.Ready ? "Ready to decide." : $"Requested: {string.Join(", ", t.ToolCalls.Select(c => c.Tool))}.")}",
        PilotDecision d => d.IsHold ? $"HOLD ({d.Strategy.Approach}, confidence {d.Confidence})."
            : $"{d.Action}: {d.Targets.Count} holdings, cash {d.CashWeight:P1} ({d.Strategy.Approach}, confidence {d.Confidence}).",
        PilotCritique c => $"Reviewer: {c.Recommendation}; {c.Concerns.Count} concern(s). {PilotText.Clip(c.Summary, 300)}",
        _ => "Answer received.",
    };

    /// <summary>
    /// The fit loop: the validator checks the proposal; every breach goes back to the model with the proposed and the allowed
    /// value until a proposal fits, at most <see cref="PilotOptions.MaxFitRevisions"/> times. Each check and each revision is
    /// journaled, and the first proposal stays on record (<see cref="MatrixPilotCycle.InitialDecisionJson"/>).
    /// </summary>
    private async Task<(PilotDecision Decision, PilotFitResult Fit)> FitAsync(MatrixPilotCycle cycle, PilotToolContext ctx, PilotDecision decision,
        List<object> log, CancellationToken ct)
    {
        await PhaseAsync(cycle, PilotCyclePhase.Fit, ct);
        for (var attempt = 1; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            var fit = await fitChecker.CheckAsync(ctx, decision, requireTexts: true, ct);
            var v = fit.Validation;
            var figures = $"{(fit.Risk?.Summary.Volatility is { } vol ? $" Ex-ante volatility {vol:P1}." : "")}{(v.Trades is { } t ? $" {t.Buys} buy(s), {t.Sells} sell(s), turnover {t.Turnover:P1}, costs {t.Costs:N2}." : "")}";
            journal.Add(cycle, PilotStepKind.Validation, $"Fit check {attempt}", fit.Fits ? "ok" : "rejected", sw.Elapsed.TotalMilliseconds,
                fit.Fits ? $"Fits every mandate rule.{figures}"
                    : $"{v.Violations.Count} breach(es): {string.Join(" ", v.Violations.Take(4).Select(x => x.Detail))}{figures}",
                null, PilotJson.Serialize(new { validation = v, risk = fit.Risk }));
            await SaveAsync(ct);
            if (fit.Fits || attempt > _o.MaxFitRevisions) return (decision, fit);

            cycle.FitRevisions++;
            decision = await DecideAsync(cycle, "pilot.revise", PilotPrompts.Revise, Data(new
            {
                stage = "fit",
                revision = attempt,
                revisionsLeft = _o.MaxFitRevisions - attempt,
                yourDecision = decision,
                breaches = v.Violations.Select(x => new { x.Rule, x.Detail, x.Symbol, x.Proposed, x.Allowed }),
                checks = v.Checks,
                mandate = ctx.Mandate,
                portfolio = ctx.Observation,
                universe = UniverseMix(ctx),
                research = TrimLog(log),
            }), $"Revision {attempt}", ct);
        }
    }

    /// <summary>Composition of the mandate universe (counts by country, sector, currency) – lets the model see limits the universe makes hard.</summary>
    private static object UniverseMix(PilotToolContext ctx)
    {
        var priced = ctx.Universe.Values.Where(a => a.Priced).ToList();
        Dictionary<string, int> By(Func<PilotAssetInfo, string?> key) =>
            priced.GroupBy(a => key(a) ?? "Unknown").OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count());
        return new { listings = ctx.Universe.Count, priced = priced.Count, countries = By(a => a.Country), sectors = By(a => a.Sector), currencies = By(a => a.Currency) };
    }

    /// <summary>
    /// The model's own portfolio without mandate limits, and the platform's comparison with the mandate portfolio. Never traded;
    /// a failure here is journaled and does not affect the mandate decision.
    /// </summary>
    private async Task ShadowAsync(MatrixPilotCycle cycle, PilotToolContext ctx, PilotDecision decision, List<object> log, CancellationToken ct)
    {
        try
        {
            await PhaseAsync(cycle, PilotCyclePhase.Unconstrained, ct);
            await PilotCatalogue.EnsureLoadedAsync(ctx, db, ct);
            var mandatePortfolio = decision.IsHold ? PilotFitChecker.CurrentAsDecision(ctx, decision) : decision;
            PilotShadow? shadow = null;
            List<string> problems = [];
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                shadow = NormalizeShadow(await CallAsync<PilotShadow>(cycle, "pilot.unconstrained", PilotPrompts.Unconstrained, Data(new
                {
                    stage = "unconstrained",
                    mandatePortfolio = mandatePortfolio.Targets.Select(t => new { t.Symbol, t.Weight }),
                    mandateCash = mandatePortfolio.CashWeight,
                    catalogue = new { listings = ctx.Catalogue!.Count, note = "Any listing of the catalogue with recent prices may be used." },
                    problemsOfYourPreviousAnswer = attempt > 1 ? problems : null,
                    research = TrimLog(log),
                }), PilotPrompts.UnconstrainedSchema, 8000, attempt == 1 ? "Unconstrained portfolio" : "Unconstrained portfolio (corrected)", ct));
                problems = ShadowProblems(ctx, shadow);
                if (problems.Count == 0) break;
            }
            shadow = shadow! with { Problems = problems };
            cycle.ShadowJson = PilotJson.Serialize(shadow);
            if (problems.Count > 0)
            {
                journal.Add(cycle, PilotStepKind.Validation, "Unconstrained portfolio check", "rejected", 0,
                    $"The unconstrained portfolio is not usable: {string.Join(" ", problems.Take(3))} It is recorded but not compared.");
                return;
            }

            await PhaseAsync(cycle, PilotCyclePhase.Compare, ct);
            var sw = Stopwatch.StartNew();
            var comparison = await CompareAsync(ctx, mandatePortfolio, shadow, ct);
            journal.Add(cycle, PilotStepKind.Calculation, "Portfolio comparison", "ok", sw.Elapsed.TotalMilliseconds,
                $"Mandate portfolio volatility {comparison.Mandate.Risk?.Volatility:P1} vs unconstrained {comparison.Unconstrained.Risk?.Volatility:P1}; " +
                $"overlap {comparison.WeightOverlap:P0}; the unconstrained portfolio would break {comparison.RulesUnconstrainedBreaks.Count} mandate rule(s).",
                null, PilotJson.Serialize(comparison));

            var evidence = ComparisonEvidence(comparison);
            var sections = DeterministicComparison(comparison);
            var source = "deterministic";
            try
            {
                var csw = Stopwatch.StartNew();
                cycle.ModelCalls++;
                var r = await model.GenerateAsync(new LlmRequest("pilot.compare", PilotPrompts.Compare, $"<evidence>{evidence}</evidence>", PilotPrompts.CompareSchema,
                    2500, "low", TimeSpan.FromSeconds(_o.ModelTimeoutSeconds)), ct);
                cycle.ModelMs += csw.Elapsed.TotalMilliseconds;
                cycle.InputTokens += r.InputTokens;
                cycle.OutputTokens += r.OutputTokens;
                var proposed = r.Output.TryGetProperty("sections", out var s) ? s.Deserialize<List<InterpretationSection>>(PilotJson.Options) ?? [] : [];
                var allowed = comparison.Mandate.Weights.Select(w => w.Symbol).Concat(comparison.Unconstrained.Weights.Select(w => w.Symbol)).ToHashSet();
                var (ok, reason) = proposed.Count == 0 ? (false, "empty") : NarrativeValidator.Validate(proposed, evidence, allowed, ctx.Catalogue.Keys.ToHashSet());
                journal.Add(cycle, PilotStepKind.AiDecision, "Comparison", ok ? "ok" : "rejected", csw.Elapsed.TotalMilliseconds,
                    ok ? "The model's comparison passed the evidence check." : $"The model's comparison was rejected ({reason}); the platform's own summary is shown.");
                if (ok) { sections = proposed; source = "model"; }
            }
            catch (LanguageModelException ex)
            {
                journal.Add(cycle, PilotStepKind.AiDecision, "Comparison", "error", 0, $"Comparison not written by the model ({ex.Failure}); the platform's own summary is shown.");
            }
            cycle.ComparisonJson = PilotJson.Serialize(comparison with { Narrative = sections, NarrativeSource = source });
        }
        catch (Exception ex) when (ex is LanguageModelException or CycleStop)
        {
            // Secondary result: the mandate decision stands; the gap is visible in the journal.
            journal.Add(cycle, PilotStepKind.AiDecision, "Unconstrained portfolio", "error", 0,
                $"No unconstrained portfolio this cycle: {ex.Message} The mandate decision is not affected.");
        }
        finally
        {
            await SaveAsync(ct);
        }
    }

    private static PilotShadow NormalizeShadow(PilotShadow s) => s with
    {
        Approach = PilotText.Clip(s.Approach, 80),
        Targets = (s.Targets ?? []).Where(t => t is not null).Take(80).Select(t => t with
        {
            Symbol = Asset.NormalizeSymbol(t.Symbol ?? ""), Role = PilotText.Clip(t.Role, 200), Thesis = PilotText.Clip(t.Thesis, 800),
            WhyNow = PilotText.Clip(t.WhyNow, 500), Risks = PilotText.Clip(t.Risks, 500), Invalidation = PilotText.Clip(t.Invalidation, 500),
        }).ToList(),
        Justification = (s.Justification ?? []).Take(10).Select(x => PilotText.Clip(x, 600)).ToList(),
        DifferencesFromMandate = (s.DifferencesFromMandate ?? []).Take(10).Select(x => PilotText.Clip(x, 600)).ToList(),
        KeyRisks = (s.KeyRisks ?? []).Take(10).Select(x => PilotText.Clip(x, 400)).ToList(),
        Confidence = s.Confidence ?? "",
        Problems = [],
    };

    /// <summary>Structure only (no mandate limits): known listings with recent prices, positive weights, no duplicates, sum with cash = 1, reasons given.</summary>
    public static List<string> ShadowProblems(PilotToolContext ctx, PilotShadow s)
    {
        var p = new List<string>();
        if (s.Targets.Count == 0) p.Add("It has no holdings.");
        foreach (var t in s.Targets)
        {
            if (ctx.Info(t.Symbol) is not { Priced: true }) p.Add($"{t.Symbol} is not a listing with recent stored prices.");
            if (!double.IsFinite(t.Weight) || t.Weight <= 0) p.Add($"{t.Symbol}: weight must be positive.");
            if (string.IsNullOrWhiteSpace(t.Thesis)) p.Add($"{t.Symbol}: no thesis.");
        }
        if (s.Targets.Select(t => t.Symbol).Distinct().Count() != s.Targets.Count) p.Add("A symbol appears more than once.");
        var sum = s.Targets.Where(t => double.IsFinite(t.Weight)).Sum(t => t.Weight) + (double.IsFinite(s.CashWeight) ? s.CashWeight : double.NaN);
        if (!double.IsFinite(sum) || s.CashWeight < 0 || Math.Abs(sum - 1) > PilotDecisionValidator.SumTolerance) p.Add($"Holdings plus cash sum to {sum:P2}, not 100 %.");
        if (s.Justification.Count == 0) p.Add("No justification given.");
        return p;
    }

    private async Task<PilotPortfolioComparison> CompareAsync(PilotToolContext ctx, PilotDecision mandatePortfolio, PilotShadow shadow, CancellationToken ct)
    {
        async Task<PilotPortfolioFigures> Figures(string label, IReadOnlyList<PilotTarget> targets, double cash)
        {
            var w = targets.Where(t => t.Weight > 0).ToDictionary(t => t.Symbol, t => t.Weight);
            var risk = w.Count == 0 ? null : await analytics.AnalyzeAsync(ctx, w, 504, ct);
            var metrics = await analytics.MetricsAsync(ctx, w.Keys.ToList(), ct);
            double? r12 = w.Count > 0 && w.Keys.All(s => metrics.GetValueOrDefault(s)?.Return12M is not null)
                ? Math.Round(w.Sum(kv => kv.Value * metrics[kv.Key].Return12M!.Value), 4) : null;
            return new PilotPortfolioFigures(label, w.Count, Math.Round(cash, 4), risk?.Summary, risk?.DiversificationRatio, risk?.AverageCorrelation,
                risk?.ExpectedShortfall95, risk?.Worst20Days, risk?.BetaToBenchmark, r12,
                w.OrderByDescending(kv => kv.Value).Select(kv => new PilotTargetWeight(kv.Key, Math.Round(kv.Value, 4), ctx.Info(kv.Key)?.Sector,
                    ctx.Info(kv.Key)?.Currency, ctx.Info(kv.Key)?.Country)).ToList());
        }

        var mandate = await Figures("Mandate portfolio", mandatePortfolio.Targets, mandatePortfolio.CashWeight);
        var free = await Figures("Unconstrained portfolio", shadow.Targets, shadow.CashWeight);
        var asDecision = new PilotDecision { Action = "REBALANCE", CashWeight = shadow.CashWeight, Targets = shadow.Targets };
        var breaks = PilotDecisionValidator.Validate(asDecision, ctx.Mandate, ctx.Catalogue!, PilotFitChecker.CurrentWeights(ctx), free.Risk, null, requireTexts: false)
            .Violations.Where(v => v.Rule != "allocation" || !v.Detail.StartsWith("Turnover", StringComparison.Ordinal)).ToList();
        var outside = shadow.Targets.Where(t => !ctx.Universe.ContainsKey(t.Symbol)).Select(t => t.Symbol).ToList();
        if (outside.Count > 0) breaks.Insert(0, new PilotViolation("universe", $"{outside.Count} holding(s) outside the mandate's universe: {string.Join(", ", outside.Take(10))}."));
        var a = mandate.Weights.ToDictionary(x => x.Symbol, x => x.Weight);
        var b = free.Weights.ToDictionary(x => x.Symbol, x => x.Weight);
        var shared = a.Keys.Intersect(b.Keys).Order(StringComparer.Ordinal).ToList();
        return new PilotPortfolioComparison(mandate, free, breaks, shared, Math.Round(shared.Sum(s => Math.Min(a[s], b[s])), 4), [], "deterministic",
            "Historical estimates over the same window and method for both (Ledoit–Wolf covariance, cash at 0 %, portfolio currency); trailing 12-month " +
            "return = weighted 12-month returns of the holdings. Backward-looking, not forecasts. The unconstrained portfolio is never traded.");
    }

    private static string ComparisonEvidence(PilotPortfolioComparison c)
    {
        static object Side(PilotPortfolioFigures f) => new
        {
            f.Label, holdings = f.Holdings, cashPct = Math.Round(f.CashWeight * 100, 1),
            volatilityPct = f.Risk?.Volatility is { } v ? Math.Round(v * 100, 1) : (double?)null,
            maxDrawdownPct = f.Risk?.MaxDrawdown is { } d ? Math.Round(Math.Abs(d) * 100, 1) : (double?)null,
            largestPct = f.Risk?.LargestWeight is { } l ? Math.Round(l * 100, 1) : (double?)null, largest = f.Risk?.LargestSymbol,
            effectiveHoldings = f.Risk?.EffectiveHoldings is { } e ? Math.Round(e, 1) : (double?)null,
            trailing12mReturnPct = f.TrailingReturn12M is { } r ? Math.Round(r * 100, 1) : (double?)null,
            countriesPct = f.Risk?.Countries.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value * 100, 1)),
            sectorsPct = f.Risk?.Sectors.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value * 100, 1)),
            symbols = f.Weights.Select(w => w.Symbol),
        };
        return JsonSerializer.Serialize(new
        {
            mandate = Side(c.Mandate), unconstrained = Side(c.Unconstrained), sharedSymbols = c.SharedSymbols,
            overlapPct = Math.Round(c.WeightOverlap * 100, 1), rulesTheUnconstrainedPortfolioBreaks = c.RulesUnconstrainedBreaks.Select(v => v.Detail),
        }, PilotJson.Options);
    }

    private static List<InterpretationSection> DeterministicComparison(PilotPortfolioComparison c) =>
    [
        new("differences", "Differences", [$"{c.Mandate.Holdings} holdings within the mandate vs {c.Unconstrained.Holdings} without limits; {c.SharedSymbols.Count} in common ({c.WeightOverlap:P0} of the weight)."]),
        new("risk", "Risk", [$"Ex-ante volatility {c.Mandate.Risk?.Volatility:P1} vs {c.Unconstrained.Risk?.Volatility:P1} (historical estimate)."]),
        new("rules", "Rules", [c.RulesUnconstrainedBreaks.Count == 0 ? "The unconstrained portfolio happens to fit the mandate too."
            : $"The unconstrained portfolio would break {c.RulesUnconstrainedBreaks.Count} mandate rule(s)."]),
    ];

    // ==================================================================================================== journal / narrative

    private async Task NarrateAsync(MatrixPilotCycle cycle, PilotToolContext ctx, PilotDecision d, PilotValidationResult v, PilotExecuted? executed,
        PilotCycleOutcome outcome, CancellationToken ct)
    {
        var evidence = new
        {
            outcome = outcome.ToString(),
            action = d.Action,
            approach = d.Strategy.Approach,
            confidence = d.Confidence,
            targets = d.Targets.Select(t => new { symbol = t.Symbol, weightPct = Math.Round(t.Weight * 100, 1), t.Role }),
            cashPct = Math.Round(d.CashWeight * 100, 1),
            reasons = d.Rationale,
            risks = d.KeyRisks,
            volatilityPct = v.Risk?.Volatility is { } vol ? Math.Round(vol * 100, 1) : (double?)null,
            turnoverPct = v.Trades is { } t ? Math.Round(t.Turnover * 100, 1) : (double?)null,
            buys = v.Trades?.Buys, sells = v.Trades?.Sells,
            checksPassed = v.Checks.Count(c => c.Passed),
            checks = v.Checks.Count,
            ordersBooked = cycle.TradesBooked,
        };
        var evidenceJson = JsonSerializer.Serialize(evidence, PilotJson.Options);
        var fallback = Deterministic(d, v, cycle, outcome);
        IReadOnlyList<InterpretationSection> sections = fallback;
        var source = "deterministic";
        if (model.IsAvailable)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                cycle.ModelCalls++;
                var r = await model.GenerateAsync(new LlmRequest("pilot.narrative", PilotPrompts.Narrative, $"<evidence>{evidenceJson}</evidence>",
                    PilotPrompts.NarrativeSchema, 2000, "low", TimeSpan.FromSeconds(_o.ModelTimeoutSeconds)), ct);
                cycle.ModelMs += sw.Elapsed.TotalMilliseconds;
                cycle.InputTokens += r.InputTokens;
                cycle.OutputTokens += r.OutputTokens;
                var proposed = r.Output.TryGetProperty("sections", out var s) ? s.Deserialize<List<InterpretationSection>>(PilotJson.Options) ?? [] : [];
                var allowed = d.Targets.Select(t => t.Symbol).Concat(ctx.Observation.Holdings.Select(h => h.Symbol)).ToHashSet();
                var (ok, reason) = proposed.Count == 0 ? (false, "empty") : NarrativeValidator.Validate(proposed, evidenceJson, allowed, ctx.Universe.Keys.ToHashSet());
                journal.Add(cycle, PilotStepKind.AiDecision, "Report", ok ? "ok" : "rejected", sw.Elapsed.TotalMilliseconds,
                    ok ? "The model's report passed the evidence check." : $"The model's report was rejected ({reason}); the platform's own summary is shown.");
                if (ok) { sections = proposed; source = "model"; }
            }
            catch (LanguageModelException ex)
            {
                journal.Add(cycle, PilotStepKind.AiDecision, "Report", "error", 0, $"Report not written by the model ({ex.Failure}); the platform's own summary is shown.");
            }
        }
        cycle.NarrativeJson = PilotJson.Serialize(sections);
        cycle.NarrativeSource = source;
    }

    private static List<InterpretationSection> Deterministic(PilotDecision d, PilotValidationResult v, MatrixPilotCycle cycle, PilotCycleOutcome outcome)
    {
        var decision = d.IsHold
            ? "Matrix Pilot decided to hold the current portfolio."
            : $"Matrix Pilot set {d.Targets.Count} target holdings with {d.CashWeight:P1} cash ({d.Strategy.Approach}); " +
              (outcome == PilotCycleOutcome.Rebalanced ? $"{cycle.TradesBooked} simulated order(s) were booked." : "no order was needed or booked.");
        return
        [
            new("decision", "Decision", [decision]),
            new("why", "Stated reasons", d.Rationale.Take(3).ToList()),
            new("risks", "Named risks", d.KeyRisks.Take(3).ToList()),
            new("checks", "Platform checks", [$"{v.Checks.Count(c => c.Passed)} of {v.Checks.Count} checks passed."]),
        ];
    }

    // ==================================================================================================== helpers

    private async Task<PilotExecuted> ExecutedAsync(Guid portfolioId, PilotDecision d, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var valuation = await valuations.ValueAsync(portfolio, null, ct);
        var total = valuation.TotalValue ?? 0;
        var proposed = d.Targets.ToDictionary(t => t.Symbol, t => t.Weight);
        var rows = valuation.Positions.Values.Where(p => p.Quantity > 0).Select(p => new PilotExecutedWeight(p.Symbol, proposed.GetValueOrDefault(p.Symbol),
                total > 0 && p.Value is { } x ? Math.Round((double)(x / total), 4) : 0, p.Quantity))
            .Concat(proposed.Keys.Where(s => valuation.Positions.Values.All(p => p.Symbol != s || p.Quantity <= 0))
                .Select(s => new PilotExecutedWeight(s, proposed[s], 0, 0)))
            .OrderByDescending(r => r.ProposedWeight).ThenBy(r => r.Symbol).ToList();
        var notes = rows.Where(r => Math.Abs(r.ExecutedWeight - r.ProposedWeight) > 0.005)
            .Select(r => $"{r.Symbol}: proposed {r.ProposedWeight:P2}, held {r.ExecutedWeight:P2} after the booking (rounding, drift threshold, cash or a rejected order).").ToList();
        return new PilotExecuted(rows, total > 0 ? Math.Round((double)(portfolio.CashBalance / total), 4) : 0, Math.Round(total, 2), notes);
    }

    private async Task<PilotMandate> MandateAsync(MatrixPilot pilot, CancellationToken ct)
    {
        var row = await db.MatrixPilotMandates.AsNoTracking().SingleAsync(m => m.PilotId == pilot.Id && m.Version == pilot.MandateVersion, ct);
        return PilotJson.Deserialize<PilotMandate>(row.MandateJson) ?? throw new InvalidOperationException("Mandate missing.");
    }

    private static string Data(object value) => $"<data>{JsonSerializer.Serialize(value, PilotJson.Options)}</data>";

    /// <summary>Keeps the research log within the context budget: the oldest tool results shrink to their first characters.</summary>
    private object TrimLog(List<object> log)
    {
        var json = log.Select(e => JsonSerializer.Serialize(e, PilotJson.Options)).ToList();
        var total = json.Sum(j => j.Length);
        for (var i = 0; i < json.Count && total > _o.MaxResearchContextChars; i++)
        {
            if (json[i].Length <= 800) continue;
            total -= json[i].Length - 800;
            json[i] = JsonSerializer.Serialize(new { trimmed = json[i][..760] + "…" }, PilotJson.Options);
        }
        return json.Select(j => JsonDocument.Parse(j).RootElement.Clone()).ToList();
    }

    private async Task PhaseAsync(MatrixPilotCycle cycle, PilotCyclePhase phase, CancellationToken ct)
    {
        cycle.Phase = phase;
        await db.SaveChangesAsync(ct);
    }

    private Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    private async Task PauseAsync(MatrixPilot pilot, MatrixPilotCycle cycle, string reason, CancellationToken ct)
    {
        await db.Entry(pilot).ReloadAsync(ct);
        if (pilot.Status != PilotStatus.Active) return;
        var previous = pilot.Status;
        pilot.Status = PilotStatus.Paused;
        pilot.PausedAtUtc = clock.GetUtcNow().UtcDateTime;
        pilot.PauseReason = PilotText.Clip(reason, 500);
        pilot.NextDecisionAtUtc = null;
        pilot.Version++;
        db.MatrixPilotEvents.Add(new MatrixPilotEvent
        {
            PilotId = pilot.Id, AtUtc = pilot.PausedAtUtc.Value, Actor = PilotActor.System, Action = "paused", PreviousStatus = previous,
            NewStatus = PilotStatus.Paused, Reason = pilot.PauseReason, CycleId = cycle.Id, CorrelationId = cycle.CorrelationId,
        });
        await db.SaveChangesAsync(ct);
        await audit.RecordForAsync(pilot.OwnerId, null, AuditActions.PilotPaused, "MatrixPilot", pilot.Id.ToString(), $"System: {pilot.PauseReason}", ct: ct);
    }

    private async Task FinishAsync(MatrixPilotCycle cycle, MatrixPilot pilot, PilotCycleOutcome outcome, string reason, Stopwatch total, bool releaseLease, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        cycle.Outcome = outcome;
        cycle.OutcomeReason = PilotText.Clip(reason, 1500);
        cycle.Phase = PilotCyclePhase.Complete;
        cycle.CompletedAtUtc = now;
        cycle.TotalMs = total.Elapsed.TotalMilliseconds;
        journal.Add(cycle, PilotStepKind.Outcome, OutcomeLabel(outcome), outcome is PilotCycleOutcome.Rebalanced or PilotCycleOutcome.NoTrade ? "ok" : "skipped", 0, cycle.OutcomeReason);

        var evt = new MatrixPilotEvent
        {
            PilotId = pilot.Id, AtUtc = now, Actor = PilotActor.MatrixPilot, Action = $"cycle.{outcome.ToString().ToLowerInvariant()}",
            Reason = PilotText.Clip(reason, 500), CycleId = cycle.Id, CorrelationId = cycle.CorrelationId,
        };
        // Nothing decided or traded: try this period again after a while (bounded by PilotCycles.MaxAttemptsPerPeriod).
        var retry = PilotCycles.AllowsRetry(outcome)
                    && (await PilotCycles.PeriodStateAsync(db, pilot.Id, PilotCycles.PeriodOf(cycle.CycleKey), ct)).Attempts < PilotCycles.MaxAttemptsPerPeriod;
        db.MatrixPilotEvents.Add(evt);
        // The owner may change the pilot at the same moment (kill switch): reload and re-apply instead of overwriting.
        for (var attempt = 0; ; attempt++)
        {
            await db.Entry(pilot).ReloadAsync(ct);
            if (releaseLease && pilot.ActiveCycleId == cycle.Id)
            {
                pilot.ActiveCycleId = null;
                pilot.ActiveCycleStartedAtUtc = null;
            }
            if (releaseLease)
            {
                pilot.LastDecisionAtUtc = now;
                if (outcome is PilotCycleOutcome.Rebalanced or PilotCycleOutcome.NoTrade or PilotCycleOutcome.Blocked)
                {
                    if (outcome != PilotCycleOutcome.Blocked) pilot.LastSuccessfulDecisionAtUtc = now;
                    pilot.ConsecutiveFailures = 0;
                }
                else if (outcome == PilotCycleOutcome.Failed) pilot.ConsecutiveFailures++;
            }
            pilot.NextDecisionAtUtc = pilot.Status != PilotStatus.Active ? null
                : retry ? now.AddMinutes(Math.Max(5, _o.RetryAfterMinutes))
                : PilotSchedule.NextDecision(pilot.Frequency, now, _o.DecisionHourUtc);
            pilot.UpdatedAtUtc = now;
            pilot.Version++;
            evt.PreviousStatus = evt.NewStatus = pilot.Status;
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3) { }
        }
        logger.LogInformation(
            "Matrix Pilot cycle {CycleId} (correlation {CorrelationId}) for portfolio {PortfolioId}: {Outcome} in {Ms:0} ms; model {Provider}/{Model}, {ModelCalls} model calls, {ToolCalls} tool calls, {Trades} trades",
            cycle.Id, cycle.CorrelationId, cycle.PortfolioId, outcome, cycle.TotalMs, cycle.Provider, cycle.Model, cycle.ModelCalls, cycle.ToolCalls, cycle.TradesBooked);
        await audit.RecordForAsync(pilot.OwnerId, null, outcome == PilotCycleOutcome.Rebalanced ? AuditActions.PilotTradesBooked : AuditActions.PilotCycleCompleted,
            "MatrixPilotCycle", cycle.Id.ToString(), $"{outcome}: {PilotText.Clip(reason, 300)}", success: outcome != PilotCycleOutcome.Failed, ct: ct);

        if (releaseLease && pilot.Status == PilotStatus.Active && pilot.ConsecutiveFailures >= _o.MaxConsecutiveFailures)
            await PauseAsync(pilot, cycle, $"{pilot.ConsecutiveFailures} decision cycles in a row failed; Matrix Pilot paused itself until the owner resumes it.", ct);
    }

    public static string OutcomeLabel(PilotCycleOutcome o) => o switch
    {
        PilotCycleOutcome.Rebalanced => "REBALANCED",
        PilotCycleOutcome.NoTrade => "NO TRADE",
        PilotCycleOutcome.Skipped => "SKIPPED",
        PilotCycleOutcome.Blocked => "BLOCKED",
        PilotCycleOutcome.Failed => "FAILED",
        PilotCycleOutcome.PausedByDrawdown => "PAUSED – drawdown limit",
        _ => "RUNNING",
    };

    private static string Describe(PilotStatus s) => s switch
    {
        PilotStatus.KillSwitched => "stopped by the kill switch",
        PilotStatus.Paused => "paused",
        PilotStatus.Disabled => "switched off",
        _ => "active",
    };
}
