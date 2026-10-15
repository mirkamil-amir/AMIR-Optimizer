using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Research;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>
/// AI research workspace: objective → brief → candidate universe → portfolio hypotheses (real optimizer) → critical
/// review → refinement → saved run. Sessions are private to their owner (another user's id is 404). Every calculation
/// comes from the application's services; the optional language model only proposes, and its proposals are validated.
/// </summary>
[ApiController]
[Route("api/v1/research")]
public sealed class ResearchController(ResearchAgent agent) : ControllerBase
{
    /// <summary>The caller's research sessions, most recent first.</summary>
    [HttpGet]
    public Task<IReadOnlyList<ResearchSessionSummaryDto>> List(CancellationToken ct) => agent.ListAsync(ct);

    /// <summary>Starts a session from an objective: builds the brief and, when the market is known, shortlists candidates.</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    [ProducesResponseType<ResearchSessionDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ResearchSessionDto>> Start(StartResearchRequest request, CancellationToken ct)
    {
        var s = await agent.StartAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = s.Id }, s);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ResearchSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ResearchSessionDto> Get(Guid id, CancellationToken ct) => agent.GetAsync(id, ct);

    /// <summary>Edits the brief (validated) and re-runs the universe research when the market is known.</summary>
    [HttpPut("{id:guid}/brief")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    public Task<ResearchSessionDto> UpdateBrief(Guid id, UpdateBriefRequest request, CancellationToken ct) => agent.UpdateBriefAsync(id, request, ct);

    /// <summary>Chooses the stocks for the portfolio hypotheses (only screened stocks that meet the data requirements).</summary>
    [HttpPut("{id:guid}/universe")]
    public Task<ResearchSessionDto> SetUniverse(Guid id, SetUniverseRequest request, CancellationToken ct) => agent.SetUniverseAsync(id, request, ct);

    /// <summary>Calculates the brief's portfolio and an equal-weight baseline, reviews both and explains them.</summary>
    [HttpPost("{id:guid}/evaluate")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    public Task<ResearchSessionDto> Evaluate(Guid id, CancellationToken ct) => agent.EvaluateAsync(id, ct);

    /// <summary>Interprets a refinement request: answers questions from the results or proposes changes (nothing runs yet).</summary>
    [HttpPost("{id:guid}/refinements")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    public Task<ResearchSessionDto> ProposeRefinement(Guid id, ProposeRefinementRequest request, CancellationToken ct) => agent.ProposeRefinementAsync(id, request, ct);

    /// <summary>Applies a proposed refinement after the user confirmed it: a new hypothesis is calculated; earlier ones are kept.</summary>
    [HttpPost("{id:guid}/refinements/{proposalId:guid}/apply")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    public Task<ResearchSessionDto> ApplyRefinement(Guid id, Guid proposalId, CancellationToken ct) => agent.ApplyRefinementAsync(id, proposalId, ct);

    [HttpDelete("{id:guid}/refinements")]
    public Task<ResearchSessionDto> DiscardRefinement(Guid id, CancellationToken ct) => agent.DiscardRefinementAsync(id, ct);

    /// <summary>Weight and configuration differences between two hypotheses of the session.</summary>
    [HttpGet("{id:guid}/compare")]
    public Task<HypothesisComparisonDto> Compare(Guid id, [FromQuery] Guid a, [FromQuery] Guid b, CancellationToken ct) => agent.CompareAsync(id, a, b, ct);

    /// <summary>Saves a hypothesis as an optimization run in one of your portfolios. Requires <c>confirm: true</c>; saving twice does not duplicate.</summary>
    [HttpPost("{id:guid}/save")]
    [ProducesResponseType<ResearchSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ResearchSessionDto> Save(Guid id, SaveHypothesisRequest request, CancellationToken ct) => agent.SaveHypothesisAsync(id, request, ct);

    /// <summary>Company description and recent tagged news for one candidate (third-party text, shown as data).</summary>
    [HttpGet("{id:guid}/companies/{symbol}")]
    public Task<CompanyContextDto> Company(Guid id, string symbol, CancellationToken ct) => agent.CompanyContextAsync(id, symbol, ct);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await agent.DeleteAsync(id, ct);
        return NoContent();
    }
}
