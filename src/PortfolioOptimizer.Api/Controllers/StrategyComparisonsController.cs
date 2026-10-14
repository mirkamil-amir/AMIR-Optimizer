using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Comparisons;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>
/// Side-by-side evaluation of a mathematical optimization strategy and an ML strategy (plus equal weight and the
/// benchmark) on one shared simulator. Saved comparisons are immutable and private to their owner.
/// </summary>
[ApiController]
[Route("api/v1/strategy-comparisons")]
public sealed class StrategyComparisonsController(StrategyComparisonService comparisons) : ControllerBase
{
    /// <summary>Queues a comparison of a successful optimization run and a completed ML experiment.</summary>
    /// <response code="202">Queued; poll the returned comparison.</response>
    /// <response code="409">A source has not completed successfully, or too many comparisons are active.</response>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    [ProducesResponseType<StrategyComparisonDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StrategyComparisonDto>> Create(CreateStrategyComparisonRequest request, CancellationToken ct)
    {
        var c = await comparisons.CreateAsync(request, ct);
        return AcceptedAtAction(nameof(Get), new { id = c.Summary.Id }, c);
    }

    /// <summary>Saved comparisons of the caller, newest first.</summary>
    [HttpGet]
    public Task<PagedResult<StrategyComparisonSummaryDto>> List(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        comparisons.ListAsync(page, pageSize, ct);

    /// <summary>Configuration (frozen copies of both strategies' settings), fairness assessment, series and metrics.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<StrategyComparisonDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<StrategyComparisonDto> Get(Guid id, CancellationToken ct) => comparisons.GetAsync(id, ct);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await comparisons.DeleteAsync(id, ct);
        return NoContent();
    }
}
