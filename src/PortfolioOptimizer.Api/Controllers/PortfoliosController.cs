using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Application.Analytics;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Application.Trading;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>Simulated portfolios, holdings, paper trading and analytics.</summary>
[ApiController]
[Route("api/v1/portfolios")]
public sealed class PortfoliosController(
    PortfolioService portfolios,
    OptimizationService optimization,
    TradingService trading,
    PortfolioAnalyticsService analytics) : ControllerBase
{
    /// <summary>Lists portfolios with current valuation.</summary>
    [HttpGet]
    public Task<IReadOnlyList<PortfolioSummaryDto>> List(CancellationToken ct) => portfolios.ListAsync(ct);

    /// <summary>Creates a portfolio funded with simulated cash.</summary>
    /// <response code="201">Created.</response>
    /// <response code="400">Invalid settings (unknown assets, currency mismatch, inconsistent constraints).</response>
    [HttpPost]
    [ProducesResponseType<PortfolioDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PortfolioDto>> Create(CreatePortfolioRequest request, CancellationToken ct)
    {
        var p = await portfolios.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = p.Id }, p);
    }

    /// <summary>Portfolio details and configuration.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PortfolioDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PortfolioDto> Get(Guid id, CancellationToken ct) => portfolios.GetAsync(id, ct);

    /// <summary>Renames a portfolio and/or replaces its configuration. Does not trade.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<PortfolioDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PortfolioDto> Update(Guid id, UpdatePortfolioRequest request, CancellationToken ct) => portfolios.UpdateAsync(id, request, ct);

    /// <summary>Deletes a portfolio and its entire simulated history.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await portfolios.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Current holdings with valuation, P&amp;L and target-weight drift, plus a portfolio summary.</summary>
    [HttpGet("{id:guid}/holdings")]
    public Task<HoldingsResponse> Holdings(Guid id, CancellationToken ct) => analytics.GetHoldingsAsync(id, ct);

    /// <summary>Simulated trade executions (immutable ledger), newest first.</summary>
    [HttpGet("{id:guid}/transactions")]
    public Task<PagedResult<TransactionDto>> Transactions(Guid id, [FromQuery] string? symbol, CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => portfolios.GetTransactionsAsync(id, page, pageSize, symbol, ct);

    /// <summary>Cash ledger (deposits, settlements, fees), newest first.</summary>
    [HttpGet("{id:guid}/cash-ledger")]
    public Task<PagedResult<CashEntryDto>> CashLedger(Guid id, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        portfolios.GetCashLedgerAsync(id, page, pageSize, ct);

    /// <summary>Simulated orders, newest first.</summary>
    [HttpGet("{id:guid}/orders")]
    public Task<PagedResult<OrderDto>> Orders(Guid id, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        portfolios.GetOrdersAsync(id, page, pageSize, ct);

    /// <summary>Trade proposals (pending, executed and superseded), newest first.</summary>
    [HttpGet("{id:guid}/trade-proposals")]
    public Task<PagedResult<TradeProposalDto>> Proposals(Guid id, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        trading.ListProposalsAsync(id, page, pageSize, ct);

    /// <summary>Runs an optimization and stores it (inputs, data snapshot hash, outputs, diagnostics).</summary>
    /// <remarks>
    /// Returns 201 with the stored run. A run whose constraints are infeasible is also stored and returned with
    /// status "Infeasible" and an explanation; it has no allocation.
    /// </remarks>
    [HttpPost("{id:guid}/optimization-runs")]
    [EnableRateLimiting("expensive")]
    [ProducesResponseType<OptimizationRunDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OptimizationRunDto>> Optimize(Guid id, RunOptimizationRequest request, CancellationToken ct)
    {
        var run = await optimization.RunAsync(id, request, ct);
        return CreatedAtAction(nameof(OptimizationRunsController.Get), "OptimizationRuns", new { runId = run.Id }, run);
    }

    /// <summary>Optimization runs of a portfolio, newest first.</summary>
    [HttpGet("{id:guid}/optimization-runs")]
    public Task<PagedResult<OptimizationRunSummaryDto>> Runs(Guid id, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        optimization.ListAsync(id, page, pageSize, ct);

    /// <summary>Creates a pending trade proposal that rebalances current holdings to target weights.</summary>
    /// <remarks>Targets come from explicit weights, a given run, or the latest successful run. Supersedes earlier pending proposals.</remarks>
    [HttpPost("{id:guid}/rebalance-preview")]
    [ProducesResponseType<TradeProposalDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TradeProposalDto>> RebalancePreview(Guid id, RebalancePreviewRequest request, CancellationToken ct)
    {
        var p = await trading.RebalancePreviewAsync(id, request, ct);
        return CreatedAtAction(nameof(TradeProposalsController.Get), "TradeProposals", new { id = p.Id }, p);
    }

    /// <summary>Executes a pending trade proposal in the SIMULATED portfolio (paper trading – no real orders).</summary>
    /// <remarks>
    /// Requires <c>confirm: true</c> and an <c>Idempotency-Key</c> header. Repeating a request with the same key returns
    /// the original result (<c>idempotentReplay: true</c>) without trading again. Fills use the latest stored price.
    /// </remarks>
    /// <param name="id">Portfolio id.</param>
    /// <param name="request">Proposal to execute and confirmation flag.</param>
    /// <param name="idempotencyKey">Client-generated unique key (e.g. a UUID).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Executed (or idempotent replay).</response>
    /// <response code="409">Proposal no longer pending, holdings changed, or key reused for another proposal.</response>
    [HttpPost("{id:guid}/paper-trades")]
    [ProducesResponseType<PaperTradeResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<PaperTradeResultDto> PaperTrades(Guid id, ExecutePaperTradesRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
        trading.ExecuteAsync(id, request, idempotencyKey ?? "", ct);

    /// <summary>Realized performance of the simulated portfolio vs. its benchmark.</summary>
    [HttpGet("{id:guid}/performance")]
    public Task<PerformanceDto> Performance(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        analytics.GetPerformanceAsync(id, from, to, ct);

    /// <summary>Ex-ante risk of current holdings (historical simulation): volatility, VaR, CVaR, beta, risk contributions.</summary>
    /// <param name="id">Portfolio id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="lookbackDays">Trailing trading days (20–2520).</param>
    /// <param name="confidence">VaR confidence level, e.g. 0.95.</param>
    [HttpGet("{id:guid}/risk")]
    public Task<RiskDto> Risk(Guid id, CancellationToken ct, [FromQuery] int lookbackDays = 252, [FromQuery] double confidence = 0.95) =>
        analytics.GetRiskAsync(id, lookbackDays, confidence, ct);
}
