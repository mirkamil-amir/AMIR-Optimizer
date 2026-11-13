using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Analytics;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Application.Research;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Pilot;

/// <summary>Investable symbols of a mandate: members of its indexes plus its explicit symbols, minus exclusions (enabled listings only).</summary>
public static class PilotUniverse
{
    public const int MaxSymbols = 1500;

    public static async Task<List<Asset>> ResolveAsync(IApplicationDbContext db, PilotMandate m, CancellationToken ct)
    {
        var indexIds = m.Indexes.Count == 0 ? [] : await db.IndexMemberships.AsNoTracking().Where(x => m.Indexes.Contains(x.IndexCode)).Select(x => x.AssetId).ToListAsync(ct);
        var assets = await db.Assets.AsNoTracking()
            .Where(a => a.IsEnabled && (indexIds.Contains(a.Id) || m.Universe.Contains(a.Symbol)) && !m.ExcludedSymbols.Contains(a.Symbol))
            .OrderBy(a => a.Symbol).Take(MaxSymbols).ToListAsync(ct);
        return assets;
    }
}

/// <summary>
/// The owner's control of Matrix Pilot: create, enable (confirmed), kill switch, resume (confirmed), switch off, change the
/// mandate (new version), run a cycle now, and read the journal, live record and scorecard. Every call resolves the
/// portfolio owner-scoped (another user's portfolio → 404); nothing here is reachable by the model.
/// </summary>
public sealed class PilotService(
    IApplicationDbContext db,
    PortfolioService portfolios,
    PortfolioValuationService valuations,
    PortfolioAnalyticsService performance,
    PriceQueryService prices,
    FxService fx,
    IPilotLanguageModel model,
    IPilotCycleQueue queue,
    ICurrentUser currentUser,
    IAuditLog audit,
    TimeProvider clock,
    IOptions<PilotOptions> options)
{
    public const string Label = "Matrix Pilot – AI-managed experimental portfolio. Paper money only; results are measured, not promised.";
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    public PilotModelDto ConfiguredModel => new(model.Provider, model.Model, model.IsAvailable, model.Provider == "Mock");

    // ==================================================================================================== reads

    public async Task<PilotHomeDto> HomeAsync(CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var mine = await db.Portfolios.AsNoTracking().Where(p => p.OwnerId == owner).OrderBy(p => p.CreatedAtUtc)
            .Select(p => new { p.Id, p.Name, p.BaseCurrency, p.ArchivedAtUtc }).ToListAsync(ct);
        var pilots = await db.MatrixPilots.AsNoTracking().Where(p => p.OwnerId == owner).ToDictionaryAsync(p => p.PortfolioId, ct);
        return new PilotHomeDto(ConfiguredModel, mine.Select(p =>
        {
            pilots.TryGetValue(p.Id, out var pilot);
            return new PilotPortfolioRowDto(p.Id, p.Name, p.BaseCurrency, p.ArchivedAtUtc is null ? "Active" : "Archived", pilot?.Status,
                pilot?.ActiveCycleId is not null, pilot?.LastDecisionAtUtc, pilot?.NextDecisionAtUtc);
        }).ToList());
    }

    public async Task<PilotOverviewDto> OverviewAsync(Guid portfolioId, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var pilot = await FindAsync(portfolioId, ct);
        return new PilotOverviewDto(portfolio.Id, portfolio.Name, portfolio.BaseCurrency, !portfolio.IsArchived, ConfiguredModel,
            pilot is null ? null : await ToDtoAsync(pilot, portfolio, ct), Suggested(portfolio), Label);
    }

    public async Task<PagedResult<PilotCycleSummaryDto>> CyclesAsync(Guid portfolioId, int page, int pageSize, PilotCycleOutcome? outcome, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 100).");
        var pilot = await RequireAsync(portfolioId, ct);
        var q = db.MatrixPilotCycles.AsNoTracking().Where(c => c.PilotId == pilot.Id);
        if (outcome is { } o) q = q.Where(c => c.Outcome == o);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(c => c.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<PilotCycleSummaryDto>(rows.Select(Summary).ToList(), page, pageSize, total);
    }

    public async Task<PilotCycleDetailDto> CycleAsync(Guid portfolioId, Guid cycleId, CancellationToken ct)
    {
        var pilot = await RequireAsync(portfolioId, ct);
        var c = await db.MatrixPilotCycles.AsNoTracking().Include(x => x.Steps)
                    .SingleOrDefaultAsync(x => x.Id == cycleId && x.PilotId == pilot.Id, ct)
                ?? throw new NotFoundException("Matrix Pilot cycle", cycleId);
        return new PilotCycleDetailDto(Summary(c), c.CorrelationId, c.ModelReported, c.Effort, c.PromptVersion, c.ToolsVersion, c.SchemaVersion,
            c.InputTokens, c.OutputTokens, c.DataMs, c.ToolMs, c.ModelMs, c.ValidationMs, c.ExecutionMs,
            PilotJson.Deserialize<PilotObservation>(c.ContextJson is "{}" ? null : c.ContextJson),
            PilotJson.Deserialize<PilotDecision>(c.DecisionJson), PilotJson.Deserialize<PilotCritique>(c.CritiqueJson),
            PilotJson.Deserialize<PilotValidationResult>(c.ValidationJson), PilotJson.Deserialize<PilotExecuted>(c.ExecutedJson),
            c.ProposalId, c.TradeBatchId, c.Fees,
            PilotJson.Deserialize<List<InterpretationSection>>(c.NarrativeJson) ?? [], c.NarrativeSource,
            c.Steps.OrderBy(s => s.Sequence).Select(s => new PilotStepDto(s.Sequence, s.AtUtc, s.Kind, Category(s.Kind), s.Name, s.Status, s.DurationMs,
                s.Summary, s.InputJson, s.OutputJson, s.OutputHash)).ToList());
    }

    public async Task<PagedResult<PilotEventDto>> EventsAsync(Guid portfolioId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 200) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 200).");
        var pilot = await RequireAsync(portfolioId, ct);
        var q = db.MatrixPilotEvents.AsNoTracking().Where(e => e.PilotId == pilot.Id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<PilotEventDto>(rows.Select(e => new PilotEventDto(e.Id, e.AtUtc, e.Actor, e.Action, e.PreviousStatus, e.NewStatus, e.Reason, e.CycleId, e.CorrelationId)).ToList(),
            page, pageSize, total);
    }

    public async Task<IReadOnlyList<PilotMandateVersionDto>> MandatesAsync(Guid portfolioId, CancellationToken ct)
    {
        var pilot = await RequireAsync(portfolioId, ct);
        var rows = await db.MatrixPilotMandates.AsNoTracking().Where(m => m.PilotId == pilot.Id).OrderByDescending(m => m.Version).ToListAsync(ct);
        return rows.Select(m => new PilotMandateVersionDto(m.Version, m.CreatedAtUtc, m.Reason,
            PilotJson.Deserialize<List<PilotMandateChange>>(m.ChangesJson) ?? [], PilotJson.Deserialize<PilotMandate>(m.MandateJson)!)).ToList();
    }

    // ==================================================================================================== lifecycle

    public async Task<PilotDto> CreateAsync(Guid portfolioId, CreatePilotRequest request, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        portfolio.EnsureActive();
        if (await db.MatrixPilots.AnyAsync(p => p.PortfolioId == portfolioId, ct))
            throw new ConflictException("This portfolio already has a Matrix Pilot.");
        var mandate = await CheckedMandateAsync(request.Mandate, ct);
        CheckEffort(request.Effort);
        var now = clock.GetUtcNow().UtcDateTime;
        var pilot = new MatrixPilot
        {
            Id = Guid.NewGuid(), PortfolioId = portfolioId, OwnerId = owner, Status = PilotStatus.Disabled, Frequency = request.Frequency,
            MandateVersion = 1, Provider = model.Provider, Model = model.Model, Effort = request.Effort, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.MatrixPilots.Add(pilot);
        db.MatrixPilotMandates.Add(new MatrixPilotMandateVersion
        {
            PilotId = pilot.Id, Version = 1, CreatedAtUtc = now, CreatedBy = owner, Reason = "Initial mandate.", MandateJson = PilotJson.Serialize(mandate),
        });
        Event(pilot, "created", null, PilotStatus.Disabled, "Matrix Pilot set up (switched off).");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex)) { throw new ConflictException("This portfolio already has a Matrix Pilot."); }
        await audit.RecordAsync(AuditActions.PilotCreated, "MatrixPilot", pilot.Id.ToString(), $"Portfolio {portfolioId}", ct: ct);
        return await ToDtoAsync(pilot, portfolio, ct);
    }

    /// <summary>Switches autonomous management on after the owner's explicit confirmation of portfolio, model and controls.</summary>
    public async Task<PilotDto> EnableAsync(Guid portfolioId, EnablePilotRequest request, CancellationToken ct)
    {
        if (!request.Confirm) throw new ValidationException("confirm", "Switching Matrix Pilot on needs your explicit confirmation.");
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        portfolio.EnsureActive();
        var pilot = await RequireAsync(portfolioId, ct, tracking: true);
        if (pilot.Status == PilotStatus.Active) throw new ConflictException("Matrix Pilot is already active.");
        if (pilot.Status is PilotStatus.Paused or PilotStatus.KillSwitched) throw new ConflictException("Matrix Pilot is stopped; use Resume.");
        RequireModel(request.Provider, request.Model);
        var now = clock.GetUtcNow().UtcDateTime;
        var previous = pilot.Status;
        if (pilot.Provider != model.Provider || pilot.Model != model.Model)
            Event(pilot, "model.adopted", previous, previous, $"Model {pilot.Provider} {pilot.Model} → {model.Provider} {model.Model} (confirmed by the owner).");
        pilot.Provider = model.Provider;
        pilot.Model = model.Model;
        pilot.Status = PilotStatus.Active;
        pilot.EnabledAtUtc = now;
        pilot.PausedAtUtc = null;
        pilot.PauseReason = null;
        pilot.ConsecutiveFailures = 0;
        pilot.NextDecisionAtUtc = PilotSchedule.NextDecision(pilot.Frequency, now, options.Value.DecisionHourUtc);
        if (pilot.BaselineAtUtc is null)
        {
            var baseline = await BaselineAsync(portfolio, await MandateAsync(pilot, ct), ct);
            pilot.BaselineJson = PilotJson.Serialize(baseline);
            pilot.BaselineAtUtc = baseline.AtUtc;
        }
        Touch(pilot, now);
        Event(pilot, "enabled", previous, PilotStatus.Active, "Autonomous management confirmed by the owner.");
        await SaveAsync(ct);
        await audit.RecordAsync(AuditActions.PilotEnabled, "MatrixPilot", pilot.Id.ToString(), $"{pilot.Provider} {pilot.Model}, {pilot.Frequency}", ct: ct);
        return await ToDtoAsync(pilot, portfolio, ct);
    }

    /// <summary>Kill switch: no new decision or trade from now on (a running cycle checks again before booking). Bookings stay in the ledger.</summary>
    public async Task<PilotDto> KillAsync(Guid portfolioId, PilotReasonRequest request, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var pilot = await RequireAsync(portfolioId, ct, tracking: true);
        if (pilot.Status is PilotStatus.Disabled or PilotStatus.KillSwitched) throw new ConflictException($"Matrix Pilot is already {pilot.Status}.");
        var previous = pilot.Status;
        var now = clock.GetUtcNow().UtcDateTime;
        pilot.Status = PilotStatus.KillSwitched;
        pilot.PausedAtUtc = now;
        pilot.PauseReason = PilotText.Clip(string.IsNullOrWhiteSpace(request.Reason) ? "Kill switch pressed by the owner." : request.Reason, 500);
        pilot.NextDecisionAtUtc = null;
        Touch(pilot, now);
        Event(pilot, "kill_switch", previous, PilotStatus.KillSwitched, pilot.PauseReason);
        await SaveAsync(ct);
        await audit.RecordAsync(AuditActions.PilotKillSwitch, "MatrixPilot", pilot.Id.ToString(), pilot.PauseReason, ct: ct);
        return await ToDtoAsync(pilot, portfolio, ct);
    }

    /// <summary>Resumes a paused or kill-switched pilot (owner only, confirmed). The model has no way to call this.</summary>
    public async Task<PilotDto> ResumeAsync(Guid portfolioId, EnablePilotRequest request, CancellationToken ct)
    {
        if (!request.Confirm) throw new ValidationException("confirm", "Resuming Matrix Pilot needs your explicit confirmation.");
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        portfolio.EnsureActive();
        var pilot = await RequireAsync(portfolioId, ct, tracking: true);
        if (pilot.Status is not (PilotStatus.Paused or PilotStatus.KillSwitched)) throw new ConflictException($"Matrix Pilot is {pilot.Status}; only a stopped pilot can be resumed.");
        RequireModel(request.Provider, request.Model);
        if (pilot.Provider != model.Provider || pilot.Model != model.Model)
            throw new ConflictException($"The configured model ({model.Provider} {model.Model}) differs from this pilot's ({pilot.Provider} {pilot.Model}). Confirm the change in the mandate first.");
        var previous = pilot.Status;
        var now = clock.GetUtcNow().UtcDateTime;
        pilot.Status = PilotStatus.Active;
        pilot.PausedAtUtc = null;
        pilot.PauseReason = null;
        pilot.ConsecutiveFailures = 0;
        pilot.NextDecisionAtUtc = PilotSchedule.NextDecision(pilot.Frequency, now, options.Value.DecisionHourUtc);
        Touch(pilot, now);
        Event(pilot, "resumed", previous, PilotStatus.Active, "Resumed by the owner.");
        await SaveAsync(ct);
        await audit.RecordAsync(AuditActions.PilotResumed, "MatrixPilot", pilot.Id.ToString(), $"From {previous}", ct: ct);
        return await ToDtoAsync(pilot, portfolio, ct);
    }

    public async Task<PilotDto> DisableAsync(Guid portfolioId, PilotReasonRequest request, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var pilot = await RequireAsync(portfolioId, ct, tracking: true);
        if (pilot.Status == PilotStatus.Disabled) throw new ConflictException("Matrix Pilot is already switched off.");
        var previous = pilot.Status;
        var now = clock.GetUtcNow().UtcDateTime;
        pilot.Status = PilotStatus.Disabled;
        pilot.NextDecisionAtUtc = null;
        Touch(pilot, now);
        Event(pilot, "disabled", previous, PilotStatus.Disabled, PilotText.Clip(request.Reason ?? "Switched off by the owner.", 500));
        await SaveAsync(ct);
        await audit.RecordAsync(AuditActions.PilotDisabled, "MatrixPilot", pilot.Id.ToString(), request.Reason, ct: ct);
        return await ToDtoAsync(pilot, portfolio, ct);
    }

    /// <summary>A new mandate version (old one kept). Only the owner can do this, with confirmation; the model cannot.</summary>
    public async Task<PilotDto> UpdateMandateAsync(Guid portfolioId, UpdatePilotMandateRequest request, CancellationToken ct)
    {
        if (!request.Confirm) throw new ValidationException("confirm", "Changing the mandate needs your explicit confirmation.");
        var owner = currentUser.RequireUserId();
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        portfolio.EnsureActive();
        var pilot = await RequireAsync(portfolioId, ct, tracking: true);
        if (pilot.ActiveCycleId is not null) throw new ConflictException("A decision cycle is running; change the mandate after it finishes.");
        CheckEffort(request.Effort);
        var before = await MandateAsync(pilot, ct);
        var after = await CheckedMandateAsync(request.Mandate, ct);
        var changes = PilotMandate.Diff(before, after).ToList();
        if (request.Frequency != pilot.Frequency) changes.Add(new PilotMandateChange("frequency", $"\"{pilot.Frequency}\"", $"\"{request.Frequency}\""));
        if (request.Effort != pilot.Effort) changes.Add(new PilotMandateChange("effort", $"\"{pilot.Effort}\"", $"\"{request.Effort}\""));
        var adopt = request.AdoptConfiguredModel && (pilot.Provider != model.Provider || pilot.Model != model.Model);
        if (adopt)
        {
            if (!model.IsAvailable) throw new ConflictException("No language model is configured.");
            changes.Add(new PilotMandateChange("model", $"\"{pilot.Provider} {pilot.Model}\"", $"\"{model.Provider} {model.Model}\""));
        }
        if (changes.Count == 0) throw new ValidationException("mandate", "Nothing changed.");

        var now = clock.GetUtcNow().UtcDateTime;
        pilot.MandateVersion++;
        db.MatrixPilotMandates.Add(new MatrixPilotMandateVersion
        {
            PilotId = pilot.Id, Version = pilot.MandateVersion, CreatedAtUtc = now, CreatedBy = owner,
            Reason = PilotText.Clip(request.Reason, 500), ChangesJson = PilotJson.Serialize(changes), MandateJson = PilotJson.Serialize(after),
        });
        pilot.Frequency = request.Frequency;
        pilot.Effort = request.Effort;
        if (adopt)
        {
            pilot.Provider = model.Provider;
            pilot.Model = model.Model;
        }
        if (pilot.Status == PilotStatus.Active) pilot.NextDecisionAtUtc = PilotSchedule.NextDecision(pilot.Frequency, now, options.Value.DecisionHourUtc);
        Touch(pilot, now);
        Event(pilot, "mandate.changed", pilot.Status, pilot.Status, $"Version {pilot.MandateVersion}: {string.Join(", ", changes.Select(c => c.Field))}. {request.Reason}".Trim());
        await SaveAsync(ct);
        await audit.RecordAsync(AuditActions.PilotMandateChanged, "MatrixPilot", pilot.Id.ToString(),
            $"v{pilot.MandateVersion}: {string.Join(", ", changes.Select(c => c.Field))}", ct: ct);
        return await ToDtoAsync(pilot, portfolio, ct);
    }

    /// <summary>Starts the current period's decision now (instead of waiting for the schedule). One decision per period.</summary>
    public async Task<PilotCycleSummaryDto> RunNowAsync(Guid portfolioId, RunPilotNowRequest request, CancellationToken ct)
    {
        if (!request.Confirm) throw new ValidationException("confirm", "Starting a decision cycle needs your explicit confirmation.");
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        portfolio.EnsureActive();
        var pilot = await RequireAsync(portfolioId, ct);
        if (pilot.Status != PilotStatus.Active) throw new ConflictException("Matrix Pilot must be active to decide.");
        if (pilot.ActiveCycleId is not null) throw new ConflictException("A decision cycle is already running.");
        var cycle = await PilotCycles.TryCreateAsync(db, pilot, "Manual", clock.GetUtcNow().UtcDateTime, ct)
                    ?? throw new ConflictException("This period already has a decision. The next one is due at the scheduled time.");
        await queue.EnqueueAsync(cycle.Id, ct);
        return Summary(cycle);
    }

    // ==================================================================================================== live record

    public async Task<PilotComparisonDto> CompareAsync(Guid portfolioId, Guid? otherPortfolioId, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var pilot = await RequireAsync(portfolioId, ct);
        var currency = portfolio.BaseCurrency;
        var warnings = new List<string>();
        var cycles = await db.MatrixPilotCycles.AsNoTracking().Where(c => c.PilotId == pilot.Id).OrderBy(c => c.AsOfUtc).ToListAsync(ct);
        var evaluation = await EvaluateAsync(pilot, cycles, portfolio, ct);
        var timeline = Timeline(pilot, cycles);
        const string methodology =
            "Live record only: Matrix Pilot's performance starts at its baseline (the activation) and is the time-weighted return of the portfolio's " +
            "ledger from then on – deposits and withdrawals are flows, not returns. Nothing before the baseline is shown and nothing is back-filled. " +
            "The other portfolio and the benchmark are measured over the same dates in the same currency. Before the first booking there is no return series.";

        if (pilot.BaselineAtUtc is not { } baselineAt)
            return new PilotComparisonDto(portfolioId, currency, null, null, Empty("pilot", "Matrix Pilot", portfolioId, "Matrix Pilot has not been switched on yet."),
                null, null, [], evaluation, timeline, methodology, ["Switch Matrix Pilot on to start its live record."]);

        var start = DateOnly.FromDateTime(baselineAt);
        var mine = await performance.GetPerformanceAsync(portfolioId, start, null, ct, currency);
        var pilotSeries = await SeriesAsync("pilot", "Matrix Pilot", portfolioId, mine, ct);
        warnings.AddRange(mine.Warnings);
        PilotComparisonSeriesDto? other = null;
        PerformanceDto? otherPerf = null;
        if (otherPortfolioId is { } oid)
        {
            if (oid == portfolioId) throw new ValidationException("otherPortfolioId", "Choose a different portfolio to compare with.");
            var otherPortfolio = await portfolios.LoadAsync(oid, ct); // owner-scoped: another user's portfolio → 404
            otherPerf = await performance.GetPerformanceAsync(oid, mine.From ?? start, mine.To, ct, currency);
            other = await SeriesAsync("other", otherPortfolio.Name, oid, otherPerf, ct);
            if (otherPerf.From is { } of && mine.From is { } mf && of != mf)
                warnings.Add($"“{otherPortfolio.Name}” has no valued day on {mf:yyyy-MM-dd}; its series starts {of:yyyy-MM-dd}, so the two periods differ.");
            if (otherPortfolio.BaseCurrency != currency)
                warnings.Add($"“{otherPortfolio.Name}” is kept in {otherPortfolio.BaseCurrency}; it is shown in {currency} with dated exchange rates, so its returns include currency moves.");
        }

        PilotComparisonSeriesDto? benchmark = null;
        if (mine.Series.Count > 0 && mine.Series.All(p => p.BenchmarkCumulativeReturn is not null))
        {
            var pts = mine.Series.Select(p => new PilotComparisonPointDto(p.Date, p.BenchmarkCumulativeReturn!.Value, 0)).ToList();
            benchmark = new PilotComparisonSeriesDto("benchmark", mine.BenchmarkSymbol, null, mine.From, mine.To, pts, mine.BenchmarkReturn,
                new MetricDto(null, "Shown for the total return only."), new MetricDto(null, null), new MetricDto(null, null), new MetricDto(null, null), new MetricDto(null, null),
                0, 0, null, 0, null, []);
        }
        var monthly = mine.MonthlyReturns.Select(m =>
        {
            var key = $"{m.Year:0000}-{m.Month:00}";
            var o = otherPerf?.MonthlyReturns.FirstOrDefault(x => x.Year == m.Year && x.Month == m.Month)?.Portfolio;
            return new PilotMonthlyDto(key, m.Portfolio, o, m.Benchmark);
        }).ToList();
        return new PilotComparisonDto(portfolioId, currency, baselineAt, start, pilotSeries, other, benchmark, monthly, evaluation, timeline, methodology, warnings);
    }

    public async Task<PilotScorecardDto> ScorecardAsync(Guid portfolioId, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var pilot = await RequireAsync(portfolioId, ct);
        var cycles = await db.MatrixPilotCycles.AsNoTracking()
            .Where(c => c.PilotId == pilot.Id && (c.Outcome == PilotCycleOutcome.Rebalanced || c.Outcome == PilotCycleOutcome.NoTrade || c.Outcome == PilotCycleOutcome.Blocked))
            .OrderByDescending(c => c.AsOfUtc).Take(60).ToListAsync(ct);
        var app = await prices.SettingsAsync(ct);
        var decisions = cycles.Select(c => (Cycle: c, Decision: PilotJson.Deserialize<PilotDecision>(c.DecisionJson))).Where(x => x.Decision is not null).ToList();
        var symbols = decisions.SelectMany(d => d.Decision!.Targets.Select(t => t.Symbol)).Append(portfolio.Settings.BenchmarkSymbol).Distinct().ToList();
        var earliest = decisions.Count == 0 ? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) : DateOnly.FromDateTime(decisions.Min(d => d.Cycle.AsOfUtc));
        var conversion = symbols.Count == 0 ? null : await fx.GetSeriesInCurrencyAsync(symbols, app.ActiveProvider, earliest.AddDays(-7), null, portfolio.BaseCurrency, ct);
        var series = conversion?.PriceSeries() ?? [];
        PerformanceDto? live = pilot.BaselineAtUtc is { } b ? await performance.GetPerformanceAsync(portfolioId, DateOnly.FromDateTime(b), null, ct) : null;
        var rows = decisions.Select(x =>
        {
            var d = x.Decision!;
            var day = DateOnly.FromDateTime(x.Cycle.AsOfUtc);
            return new PilotScoreRowDto(x.Cycle.Id, x.Cycle.AsOfUtc, d.Action, PilotEngine.OutcomeLabel(x.Cycle.Outcome), d.Strategy.Approach, d.Confidence, d.ExpectedHoldingDays,
                d.Targets.Select(t => new PilotScoreTargetDto(t.Symbol, t.Weight, t.Thesis, Entry(series, t.Symbol, day),
                    Forward(series, t.Symbol, day, 1), Forward(series, t.Symbol, day, 5), Forward(series, t.Symbol, day, 21))).ToList(),
                LiveForward(live, day, 1), LiveForward(live, day, 5), LiveForward(live, day, 21), Forward(series, portfolio.Settings.BenchmarkSymbol, day, 1 * 21));
        }).ToList();
        return new PilotScorecardDto(rows,
            "Each decision as it was recorded at the time (never edited afterwards). Outcomes are measured afterwards from stored prices in the portfolio " +
            "currency: the change of each target's price and of the live portfolio 1, 5 and 21 trading days after the decision date. A blank means that " +
            "period has not passed yet. Blocked decisions were not executed; their figures show what the proposal's holdings did, not the portfolio. " +
            "Price moves after a decision are not proof that the decision caused them.");
    }

    // ==================================================================================================== helpers

    private static decimal? Entry(Dictionary<string, PriceSeries> series, string symbol, DateOnly day) =>
        series.TryGetValue(symbol, out var s) && s.Points.LastOrDefault(p => p.Date <= day) is { } p ? Math.Round((decimal)p.Price, 4) : null;

    /// <summary>
    /// Price change from the last close on or before <paramref name="day"/> to the close <paramref name="n"/> trading days
    /// later (F-PILOT-004); null when that period has not passed yet or there is no price on or before the day.
    /// </summary>
    public static double? Forward(Dictionary<string, PriceSeries> series, string symbol, DateOnly day, int n)
    {
        if (!series.TryGetValue(symbol, out var s)) return null;
        var i = s.Points.ToList().FindLastIndex(p => p.Date <= day);
        if (i < 0 || i + n >= s.Points.Count || s.Points[i].Price <= 0) return null;
        return Math.Round(s.Points[i + n].Price / s.Points[i].Price - 1, 4);
    }

    /// <summary>Change of the live record's time-weighted wealth index over <paramref name="n"/> valued days after <paramref name="day"/> (F-PILOT-004).</summary>
    public static double? LiveForward(PerformanceDto? live, DateOnly day, int n)
    {
        if (live is null || live.Series.Count == 0) return null;
        var pts = live.Series;
        var i = pts.ToList().FindLastIndex(p => p.Date <= day);
        if (i < 0 || i + n >= pts.Count) return null;
        return Math.Round((1 + pts[i + n].CumulativeReturn) / (1 + pts[i].CumulativeReturn) - 1, 4);
    }

    private async Task<PilotComparisonSeriesDto> SeriesAsync(string key, string label, Guid portfolioId, PerformanceDto perf, CancellationToken ct)
    {
        var from = perf.From;
        var executions = db.Executions.AsNoTracking().Where(e => e.PortfolioId == portfolioId);
        if (from is { } f) executions = executions.Where(e => e.PriceDate >= f);
        var trades = await executions.CountAsync(ct);
        var fees = await executions.SumAsync(e => (decimal?)e.Fees, ct) ?? 0;
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        var valuation = await valuations.ValueAsync(portfolio, null, ct);
        var total = valuation.TotalValue;
        var weights = valuation.Positions.Values.Where(p => p.Quantity > 0 && p.Value is not null && total is > 0)
            .Select(p => (double)(p.Value!.Value / total!.Value)).ToList();
        return new PilotComparisonSeriesDto(key, label, portfolioId, perf.From, perf.To,
            perf.Series.Select(p => new PilotComparisonPointDto(p.Date, p.CumulativeReturn, p.Drawdown)).ToList(),
            perf.TotalReturn, perf.AnnualizedReturn, perf.AnnualizedVolatility, perf.SharpeRatio, perf.SortinoRatio, perf.MaxDrawdown,
            trades, Math.Round(fees, 2), total is > 0 ? Math.Round((double)(portfolio.CashBalance / total.Value), 4) : null,
            weights.Count, weights.Count == 0 ? null : Math.Round(weights.Max(), 4), perf.Warnings);
    }

    private static PilotComparisonSeriesDto Empty(string key, string label, Guid? id, string why)
    {
        var na = new MetricDto(null, why);
        return new PilotComparisonSeriesDto(key, label, id, null, null, [], na, na, na, na, na, na, 0, 0, null, 0, null, [why]);
    }

    private async Task<PilotEvaluationDto> EvaluateAsync(MatrixPilot pilot, List<MatrixPilotCycle> cycles, Portfolio portfolio, CancellationToken ct)
    {
        var finished = cycles.Where(c => c.IsFinished).ToList();
        var ids = finished.Select(c => c.Id).ToList();
        var toolSteps = await db.MatrixPilotSteps.AsNoTracking().Where(s => ids.Contains(s.CycleId) && s.Kind == PilotStepKind.ToolCall)
            .GroupBy(s => s.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var turnovers = finished.Where(c => c.Outcome == PilotCycleOutcome.Rebalanced)
            .Select(c => PilotJson.Deserialize<PilotValidationResult>(c.ValidationJson)?.Trades?.Turnover ?? 0).ToList();
        var decided = finished.Where(c => c.Outcome is PilotCycleOutcome.Rebalanced or PilotCycleOutcome.NoTrade).OrderBy(c => c.AsOfUtc).ToList();
        double? interval = decided.Count < 2 ? null : Math.Round((decided[^1].AsOfUtc - decided[0].AsOfUtc).TotalDays / (decided.Count - 1), 1);
        var baseline = PilotJson.Deserialize<PilotBaseline>(pilot.BaselineJson);
        var fees = finished.Sum(c => c.Fees);
        var (hit, sample) = await HitRateAsync(portfolio, finished.Where(c => c.Outcome == PilotCycleOutcome.Rebalanced).ToList(), ct);
        return new PilotEvaluationDto(finished.Count,
            finished.Count(c => c.Outcome == PilotCycleOutcome.Rebalanced), finished.Count(c => c.Outcome == PilotCycleOutcome.NoTrade),
            finished.Count(c => c.Outcome == PilotCycleOutcome.Skipped), finished.Count(c => c.Outcome == PilotCycleOutcome.Blocked),
            finished.Count(c => c.Outcome == PilotCycleOutcome.Failed), finished.Count(c => c.Outcome == PilotCycleOutcome.PausedByDrawdown),
            finished.Sum(c => c.TradesBooked), fees, baseline is { PortfolioValue: > 0 } ? Math.Round((double)(fees / baseline.PortfolioValue), 6) : null,
            Math.Round(turnovers.Sum(), 4), turnovers.Count == 0 ? null : Math.Round(turnovers.Average(), 4),
            finished.Sum(c => c.ToolCalls), toolSteps.Where(s => s.Key == "error").Sum(s => s.Count), toolSteps.Where(s => s.Key == "denied").Sum(s => s.Count),
            finished.Sum(c => c.InputTokens), finished.Sum(c => c.OutputTokens), interval, hit, sample);
    }

    /// <summary>Share of target holdings of executed decisions whose price rose over the 21 trading days after the decision (only periods that have passed).</summary>
    private async Task<(double?, int)> HitRateAsync(Portfolio portfolio, List<MatrixPilotCycle> rebalanced, CancellationToken ct)
    {
        if (rebalanced.Count == 0) return (null, 0);
        var targets = rebalanced.Select(c => (Day: DateOnly.FromDateTime(c.AsOfUtc), Decision: PilotJson.Deserialize<PilotDecision>(c.DecisionJson))).Where(x => x.Decision is not null).ToList();
        var symbols = targets.SelectMany(t => t.Decision!.Targets.Select(x => x.Symbol)).Distinct().ToList();
        if (symbols.Count == 0) return (null, 0);
        var app = await prices.SettingsAsync(ct);
        var series = (await fx.GetSeriesInCurrencyAsync(symbols, app.ActiveProvider, targets.Min(t => t.Day).AddDays(-7), null, portfolio.BaseCurrency, ct)).PriceSeries();
        var moves = targets.SelectMany(t => t.Decision!.Targets.Select(x => Forward(series, x.Symbol, t.Day, 21))).Where(m => m is not null).Select(m => m!.Value).ToList();
        return moves.Count == 0 ? (null, 0) : (Math.Round(moves.Count(m => m > 0) / (double)moves.Count, 4), moves.Count);
    }

    private static List<PilotTimelineEntryDto> Timeline(MatrixPilot pilot, List<MatrixPilotCycle> cycles)
    {
        var list = new List<PilotTimelineEntryDto>();
        if (pilot.BaselineAtUtc is { } b) list.Add(new(b, "baseline", "Live record baseline captured at activation.", null));
        foreach (var c in cycles.Where(c => c.IsFinished))
        {
            var d = PilotJson.Deserialize<PilotDecision>(c.DecisionJson);
            var text = c.Outcome switch
            {
                PilotCycleOutcome.Rebalanced => $"Rebalanced: {c.TradesBooked} order(s), {d?.Targets.Count ?? 0} target holdings ({d?.Strategy.Approach}).",
                PilotCycleOutcome.NoTrade => "No trade: " + (d?.IsHold == true ? "decided to hold." : "target already matched."),
                _ => $"{PilotEngine.OutcomeLabel(c.Outcome)}: {PilotText.Clip(c.OutcomeReason, 160)}",
            };
            list.Add(new(c.AsOfUtc, c.Outcome.ToString(), text, c.Id));
        }
        return list.OrderByDescending(e => e.AtUtc).Take(200).ToList();
    }

    private async Task<PilotBaseline> BaselineAsync(Portfolio portfolio, PilotMandate mandate, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var valuation = await valuations.ValueAsync(portfolio, null, ct);
        var total = valuation.TotalValue ?? throw new ConflictException(
            $"The portfolio cannot be valued right now ({string.Join(" ", valuation.Problems)}); Matrix Pilot needs a valued starting point.");
        var benchmark = mandate.Benchmark ?? portfolio.Settings.BenchmarkSymbol;
        var app = await prices.SettingsAsync(ct);
        var benchAsset = await db.Assets.AsNoTracking().Where(a => a.Symbol == benchmark).Select(a => (int?)a.Id).SingleOrDefaultAsync(ct);
        LatestPrice? bench = benchAsset is { } id ? (await prices.GetLatestPricesAsync([id], app.ActiveProvider, app.StalePriceDays, ct)).GetValueOrDefault(id) : null;
        var positions = valuation.Positions.Values.Where(p => p.Quantity > 0).Select(p => new PilotPositionSnapshot(p.Symbol, p.Quantity,
            p.Value is { } v ? Math.Round(v, 2) : null, p.Value is { } x && total > 0 ? Math.Round((double)(x / total), 4) : null, p.Price?.PriceDate)).ToList();
        return new PilotBaseline(now, DateOnly.FromDateTime(now), portfolio.BaseCurrency, Math.Round(total, 2), Math.Round(portfolio.CashBalance, 2), positions,
            benchmark, bench is null ? null : (double)bench.Price, bench?.PriceDate,
            "Starting point of the live record. Matrix Pilot's performance is measured from here only; no earlier history is attributed to it.");
    }

    private async Task<PilotMandate> CheckedMandateAsync(PilotMandate requested, CancellationToken ct)
    {
        var m = requested.Normalized();
        m.Validate();
        var universe = await PilotUniverse.ResolveAsync(db, m, ct);
        var known = await db.Assets.AsNoTracking().Where(a => m.Universe.Contains(a.Symbol)).Select(a => a.Symbol).ToListAsync(ct);
        var unknown = m.Universe.Except(known).ToList();
        if (unknown.Count > 0) throw new ValidationException("universe", $"Unknown symbol(s): {string.Join(", ", unknown.Take(10))}.");
        var indexes = await db.MarketIndices.AsNoTracking().Where(i => m.Indexes.Contains(i.Code)).Select(i => i.Code).ToListAsync(ct);
        var unknownIdx = m.Indexes.Except(indexes).ToList();
        if (unknownIdx.Count > 0) throw new ValidationException("indexes", $"Unknown index(es): {string.Join(", ", unknownIdx)}.");
        if (universe.Count < m.MinHoldings)
            throw new ValidationException("universe", $"The universe has {universe.Count} listing(s), fewer than the minimum of {m.MinHoldings} holdings.");
        if (m.Benchmark is { } b && !await db.Assets.AnyAsync(a => a.Symbol == b, ct)) throw new ValidationException("benchmark", $"Unknown benchmark {b}.");
        return m;
    }

    private static PilotMandate Suggested(Portfolio p)
    {
        var n = Math.Max(1, p.Settings.Universe.Count);
        var max = n >= 20 ? 0.10 : Math.Min(0.40, Math.Ceiling(100.0 / Math.Max(1, Math.Min(n, 20)) * 1.5) / 100);
        return new PilotMandate
        {
            Universe = [.. p.Settings.Universe],
            ExcludedSymbols = [.. p.Settings.ExcludedSymbols],
            MaxPositionWeight = max,
            MinPositionWeight = 0.02,
            MinHoldings = Math.Min(5, n),
            MaxHoldings = Math.Min(20, n),
            SectorLimits = p.Settings.SectorLimits.ToDictionary(),
        };
    }

    private void RequireModel(string? provider, string? modelId)
    {
        if (!model.IsAvailable) throw new ConflictException("No language model is configured (Agent:Llm:Provider). Matrix Pilot needs one; it never falls back to fixed rules.");
        if (provider is not null && (provider != model.Provider || modelId != model.Model))
            throw new ConflictException($"The configured model changed to {model.Provider} {model.Model} since you opened the confirmation. Review and confirm again.");
    }

    private static void CheckEffort(string effort)
    {
        if (!Efforts.Contains(effort)) throw new ValidationException("effort", $"Effort must be one of {string.Join(", ", Efforts)}.");
    }

    private async Task<PilotMandate> MandateAsync(MatrixPilot pilot, CancellationToken ct) => PilotJson.Deserialize<PilotMandate>(
        (await db.MatrixPilotMandates.AsNoTracking().SingleAsync(m => m.PilotId == pilot.Id && m.Version == pilot.MandateVersion, ct)).MandateJson)!;

    private async Task<MatrixPilot?> FindAsync(Guid portfolioId, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        return await db.MatrixPilots.AsNoTracking().SingleOrDefaultAsync(p => p.PortfolioId == portfolioId && p.OwnerId == owner, ct);
    }

    /// <summary>The caller's pilot of an owned portfolio (the portfolio check comes first; another user's → 404).</summary>
    private async Task<MatrixPilot> RequireAsync(Guid portfolioId, CancellationToken ct, bool tracking = false)
    {
        await portfolios.EnsureExistsAsync(portfolioId, ct);
        var owner = currentUser.RequireUserId();
        var q = tracking ? db.MatrixPilots : db.MatrixPilots.AsNoTracking();
        return await q.SingleOrDefaultAsync(p => p.PortfolioId == portfolioId && p.OwnerId == owner, ct)
               ?? throw new NotFoundException("Matrix Pilot of portfolio", portfolioId);
    }

    private async Task<PilotDto> ToDtoAsync(MatrixPilot p, Portfolio portfolio, CancellationToken ct)
    {
        var mandate = await MandateAsync(p, ct);
        var cycles = await db.MatrixPilotCycles.AsNoTracking().Where(c => c.PilotId == p.Id).OrderBy(c => c.AsOfUtc).ToListAsync(ct);
        var latest = cycles.OrderByDescending(c => c.CreatedAtUtc).FirstOrDefault();
        var latestDecision = cycles.Where(c => c.DecisionJson is not null && c.IsFinished).OrderByDescending(c => c.AsOfUtc).FirstOrDefault();
        // Running = holding the lease, or queued and not finished yet (so the page keeps refreshing from the first moment).
        var running = p.ActiveCycleId is not null ? cycles.FirstOrDefault(c => c.Id == p.ActiveCycleId) : cycles.LastOrDefault(c => !c.IsFinished);
        var display = p.Status switch
        {
            PilotStatus.Active when running is not null => "RUNNING",
            PilotStatus.Active => "ACTIVE",
            PilotStatus.Paused => "PAUSED",
            PilotStatus.KillSwitched => "KILL SWITCH",
            _ => "DISABLED",
        };
        return new PilotDto(p.Id, p.PortfolioId, portfolio.Name, portfolio.BaseCurrency, p.Status, display, running?.Phase, p.Frequency, p.MandateVersion, mandate,
            p.Provider, p.Model, p.Effort, p.Provider == model.Provider && p.Model == model.Model, p.CreatedAtUtc, p.EnabledAtUtc, p.LastDecisionAtUtc,
            p.LastSuccessfulDecisionAtUtc, p.NextDecisionAtUtc, p.PausedAtUtc, p.PauseReason, p.ConsecutiveFailures, PilotJson.Deserialize<PilotBaseline>(p.BaselineJson),
            await EvaluateAsync(p, cycles, portfolio, ct), latest is null ? null : Summary(latest), latestDecision is null ? null : Summary(latestDecision));
    }

    public static PilotCycleSummaryDto Summary(MatrixPilotCycle c)
    {
        var d = PilotJson.Deserialize<PilotDecision>(c.DecisionJson);
        return new PilotCycleSummaryDto(c.Id, c.CycleKey, c.Trigger, c.AsOfUtc, c.CreatedAtUtc, c.CompletedAtUtc, c.Phase, c.Outcome, PilotEngine.OutcomeLabel(c.Outcome),
            c.OutcomeReason, d?.Action, d?.Strategy.Approach, d?.Confidence, d?.Targets.Count ?? 0, c.TradesBooked, c.ModelCalls, c.ToolCalls, Math.Round(c.TotalMs),
            c.Provider, c.Model, c.MandateVersion);
    }

    public static string Category(PilotStepKind k) => k switch
    {
        PilotStepKind.AiDecision => "AI DECISION",
        PilotStepKind.ToolCall => "DETERMINISTIC CALCULATION (tool requested by the AI)",
        PilotStepKind.Calculation => "DETERMINISTIC CALCULATION",
        PilotStepKind.Validation => "VALIDATION",
        PilotStepKind.Execution => "EXECUTION",
        PilotStepKind.Outcome => "OBSERVED OUTCOME",
        _ => "OBSERVATION",
    };

    private void Event(MatrixPilot pilot, string action, PilotStatus? previous, PilotStatus? next, string? reason) =>
        db.MatrixPilotEvents.Add(new MatrixPilotEvent
        {
            PilotId = pilot.Id, AtUtc = clock.GetUtcNow().UtcDateTime, Actor = PilotActor.Owner, ActorUserId = currentUser.UserId, Action = action,
            PreviousStatus = previous, NewStatus = next, Reason = reason,
        });

    private static void Touch(MatrixPilot p, DateTime now)
    {
        p.UpdatedAtUtc = now;
        p.Version++;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Matrix Pilot changed at the same moment (a decision cycle finished or another tab acted). Reload and try again."); }
    }
}

/// <summary>Creates the cycle of a period – at most one per pilot and period (unique key), so duplicate ticks never decide twice.</summary>
public static class PilotCycles
{
    public static async Task<MatrixPilotCycle?> TryCreateAsync(IApplicationDbContext db, MatrixPilot pilot, string trigger, DateTime nowUtc, CancellationToken ct,
        string? cycleKey = null)
    {
        var cycle = new MatrixPilotCycle
        {
            Id = Guid.NewGuid(), PilotId = pilot.Id, PortfolioId = pilot.PortfolioId, OwnerId = pilot.OwnerId,
            CycleKey = cycleKey ?? PilotSchedule.PeriodKey(pilot.Frequency, nowUtc), Trigger = trigger, CorrelationId = Guid.NewGuid(),
            AsOfUtc = nowUtc, CreatedAtUtc = nowUtc, Phase = PilotCyclePhase.Queued, Outcome = PilotCycleOutcome.Pending,
            MandateVersion = pilot.MandateVersion, Provider = pilot.Provider, Model = pilot.Model, Effort = pilot.Effort,
            PromptVersion = PilotVersions.Prompt, ToolsVersion = PilotVersions.Tools, SchemaVersion = PilotVersions.Schema,
        };
        if (await db.MatrixPilotCycles.AnyAsync(c => c.PilotId == pilot.Id && c.CycleKey == cycle.CycleKey, ct)) return null;
        db.MatrixPilotCycles.Add(cycle);
        try
        {
            await db.SaveChangesAsync(ct);
            return cycle;
        }
        catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
        {
            db.MatrixPilotCycles.Entry(cycle).State = EntityState.Detached;
            return null; // another tick created it first
        }
    }
}
