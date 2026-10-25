using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Analytics;
using PortfolioOptimizer.Application.Backtesting;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Application.Trading;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>Stored optimization runs.</summary>
[ApiController]
[Route("api/v1/optimization-runs")]
public sealed class OptimizationRunsController(OptimizationService optimization, TradingService trading, RealityCheckService realityCheck) : ControllerBase
{
    /// <summary>Optimization history of the current user across portfolios, newest first.</summary>
    /// <param name="portfolioId">Optional portfolio filter.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Rows per page (max 200).</param>
    [HttpGet]
    public Task<PagedResult<OptimizationRunSummaryDto>> List([FromQuery] Guid? portfolioId, [FromQuery] OptimizationRunStatus? status,
        CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 25) =>
        optimization.ListForUserAsync(portfolioId, status, page, pageSize, ct);

    /// <summary>Full run: inputs, data window, weights, frontier, correlations, reference portfolios and diagnostics.</summary>
    [HttpGet("{runId:guid}")]
    [ProducesResponseType<OptimizationRunDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<OptimizationRunDto> Get(Guid runId, CancellationToken ct) => optimization.GetAsync(runId, ct);

    /// <summary>Deletes a Math Optimization; proposals made from it stay, backtests and comparisons keep their frozen settings.</summary>
    [HttpDelete("{runId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(Guid runId, CancellationToken ct)
    {
        await optimization.DeleteAsync(runId, ct);
        return NoContent();
    }

    /// <summary>
    /// Portfolio Reality Check of a successful run: concentration, risk contributions, correlation on stress days, in-sample
    /// and out-of-sample behaviour, sensitivity of the weights to the assumptions and implementation costs, with labelled findings.
    /// </summary>
    [HttpGet("{runId:guid}/reality-check")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    [ProducesResponseType<RealityCheckDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<RealityCheckDto> RealityCheck(Guid runId, CancellationToken ct) => realityCheck.GetAsync(runId, ct);

    /// <summary>Recommended target weights of a run.</summary>
    [HttpGet("{runId:guid}/allocation")]
    public Task<IReadOnlyList<AllocationDto>> Allocation(Guid runId, CancellationToken ct) => optimization.GetAllocationAsync(runId, ct);

    /// <summary>The latest trade proposal for this run; one is calculated from current prices and holdings if none exists yet.</summary>
    [HttpGet("{runId:guid}/trade-proposal")]
    [ProducesResponseType<TradeProposalDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<TradeProposalDto> TradeProposal(Guid runId, CancellationToken ct) => trading.GetOrCreateForRunAsync(runId, ct);

    /// <summary>Recalculates a fresh trade proposal for this run from the latest prices and holdings.</summary>
    [HttpPost("{runId:guid}/trade-proposals")]
    [ProducesResponseType<TradeProposalDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TradeProposalDto>> CreateProposal(Guid runId, CancellationToken ct)
    {
        var p = await trading.CreateFromRunAsync(runId, ct);
        return CreatedAtAction(nameof(TradeProposalsController.Get), "TradeProposals", new { id = p.Id }, p);
    }
}

/// <summary>Trade proposals.</summary>
[ApiController]
[Route("api/v1/trade-proposals")]
public sealed class TradeProposalsController(TradingService trading) : ControllerBase
{
    /// <summary>A trade proposal with all order lines.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<TradeProposalDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<TradeProposalDto> Get(Guid id, CancellationToken ct) => trading.GetProposalAsync(id, ct);
}

/// <summary>Historical strategy backtests (run asynchronously).</summary>
[ApiController]
[Route("api/v1/backtests")]
public sealed class BacktestsController(BacktestService backtests) : ControllerBase
{
    /// <summary>Queues a backtest. Poll GET /api/v1/backtests/{id} until status is Succeeded or Failed.</summary>
    /// <response code="202">Queued.</response>
    [HttpPost]
    [EnableRateLimiting("expensive")]
    [ProducesResponseType<BacktestDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BacktestDto>> Create(CreateBacktestRequest request, CancellationToken ct)
    {
        var b = await backtests.CreateAsync(request, ct);
        return AcceptedAtAction(nameof(Get), new { id = b.Id }, b);
    }

    /// <summary>
    /// Queues a backtest of a stored optimization of the portfolio – a mathematical run or an ML experiment
    /// (GET /api/v1/portfolios/{id}/strategies) – re-simulated walk-forward with its frozen settings.
    /// </summary>
    /// <response code="202">Queued.</response>
    [HttpPost("from-strategy")]
    [EnableRateLimiting("expensive")]
    [ProducesResponseType<BacktestDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BacktestDto>> CreateFromStrategy(CreateStrategyBacktestRequest request, CancellationToken ct)
    {
        var b = await backtests.CreateFromStrategyAsync(request, ct);
        return AcceptedAtAction(nameof(Get), new { id = b.Id }, b);
    }

    /// <summary>Backtests, newest first (optionally of one portfolio).</summary>
    [HttpGet]
    public Task<PagedResult<BacktestSummaryDto>> List(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? portfolioId = null) =>
        backtests.ListAsync(page, pageSize, ct, portfolioId);

    /// <summary>Backtest configuration, status and results.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<BacktestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<BacktestDto> Get(Guid id, CancellationToken ct) => backtests.GetAsync(id, ct);
}
