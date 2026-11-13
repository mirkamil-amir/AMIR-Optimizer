using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Pilot;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>
/// Matrix Pilot – an AI-managed experimental (paper) portfolio. The owner sets it up, switches it on (confirmed), stops it
/// (kill switch), resumes it (confirmed) and changes its mandate (new version); everything else – deciding, validating,
/// booking – happens in background cycles. Every route resolves the portfolio owner-scoped: another user's id is 404.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class MatrixPilotController(PilotService pilots) : ControllerBase
{
    /// <summary>The caller's portfolios with their pilot state and the configured language model (AI Probe › Matrix Pilot).</summary>
    [HttpGet("pilots")]
    public Task<PilotHomeDto> Home(CancellationToken ct) => pilots.HomeAsync(ct);

    /// <summary>The portfolio's pilot (null when none exists yet) and a suggested mandate.</summary>
    [HttpGet("portfolios/{id:guid}/pilot")]
    [ProducesResponseType<PilotOverviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PilotOverviewDto> Get(Guid id, CancellationToken ct) => pilots.OverviewAsync(id, ct);

    /// <summary>Sets up Matrix Pilot for the portfolio (switched off; enabling is a separate, confirmed step).</summary>
    [HttpPost("portfolios/{id:guid}/pilot")]
    [ProducesResponseType<PilotDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PilotDto>> Create(Guid id, CreatePilotRequest request, CancellationToken ct)
    {
        var p = await pilots.CreateAsync(id, request, ct);
        return CreatedAtAction(nameof(Get), new { id }, p);
    }

    /// <summary>Switches autonomous management on. Requires <c>confirm = true</c> and the model shown in the confirmation.</summary>
    [HttpPost("portfolios/{id:guid}/pilot/enable")]
    public Task<PilotDto> Enable(Guid id, EnablePilotRequest request, CancellationToken ct) => pilots.EnableAsync(id, request, ct);

    /// <summary>Kill switch: no further decisions or trades until the owner resumes. Booked trades stay in the ledger.</summary>
    [HttpPost("portfolios/{id:guid}/pilot/kill")]
    public Task<PilotDto> Kill(Guid id, PilotReasonRequest request, CancellationToken ct) => pilots.KillAsync(id, request, ct);

    [HttpPost("portfolios/{id:guid}/pilot/resume")]
    public Task<PilotDto> Resume(Guid id, EnablePilotRequest request, CancellationToken ct) => pilots.ResumeAsync(id, request, ct);

    [HttpPost("portfolios/{id:guid}/pilot/disable")]
    public Task<PilotDto> Disable(Guid id, PilotReasonRequest request, CancellationToken ct) => pilots.DisableAsync(id, request, ct);

    /// <summary>A new mandate version (the previous one is kept with the changed fields).</summary>
    [HttpPut("portfolios/{id:guid}/pilot/mandate")]
    public Task<PilotDto> UpdateMandate(Guid id, UpdatePilotMandateRequest request, CancellationToken ct) => pilots.UpdateMandateAsync(id, request, ct);

    [HttpGet("portfolios/{id:guid}/pilot/mandates")]
    public Task<IReadOnlyList<PilotMandateVersionDto>> Mandates(Guid id, CancellationToken ct) => pilots.MandatesAsync(id, ct);

    /// <summary>Starts this period's decision cycle now (one decision per period). Model calls cost money: rate limited.</summary>
    [HttpPost("portfolios/{id:guid}/pilot/run")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    [ProducesResponseType<PilotCycleSummaryDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<PilotCycleSummaryDto>> Run(Guid id, RunPilotNowRequest request, CancellationToken ct) =>
        Accepted(await pilots.RunNowAsync(id, request, ct));

    [HttpGet("portfolios/{id:guid}/pilot/cycles")]
    public Task<PagedResult<PilotCycleSummaryDto>> Cycles(Guid id, CancellationToken ct, int page = 1, int pageSize = 25, PilotCycleOutcome? outcome = null) =>
        pilots.CyclesAsync(id, page, pageSize, outcome, ct);

    /// <summary>One cycle with its journal: observation, model calls, tool calls, calculations, validation, execution, outcome.</summary>
    [HttpGet("portfolios/{id:guid}/pilot/cycles/{cycleId:guid}")]
    public Task<PilotCycleDetailDto> Cycle(Guid id, Guid cycleId, CancellationToken ct) => pilots.CycleAsync(id, cycleId, ct);

    [HttpGet("portfolios/{id:guid}/pilot/events")]
    public Task<PagedResult<PilotEventDto>> Events(Guid id, CancellationToken ct, int page = 1, int pageSize = 50) => pilots.EventsAsync(id, page, pageSize, ct);

    /// <summary>Forward-only live record against another of the caller's portfolios and the benchmark.</summary>
    [HttpGet("portfolios/{id:guid}/pilot/comparison")]
    public Task<PilotComparisonDto> Comparison(Guid id, CancellationToken ct, Guid? other = null) => pilots.CompareAsync(id, other, ct);

    [HttpGet("portfolios/{id:guid}/pilot/scorecard")]
    public Task<PilotScorecardDto> Scorecard(Guid id, CancellationToken ct) => pilots.ScorecardAsync(id, ct);
}
