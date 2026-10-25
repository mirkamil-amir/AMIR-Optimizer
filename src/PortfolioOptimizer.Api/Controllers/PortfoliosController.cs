using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Analytics;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Application.Trading;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>Simulated portfolios, holdings, paper trading and analytics.</summary>
[ApiController]
[Route("api/v1/portfolios")]
public sealed class PortfoliosController(
    PortfolioService portfolios,
    PortfolioTransactionService transactions,
    OptimizationService optimization,
    TradingService trading,
    PortfolioAnalyticsService analytics,
    PortfolioOptimizer.Application.Comparisons.PortfolioStrategyService strategies,
    StockDiscoveryService stocks) : ControllerBase
{
    /// <summary>Lists the current user's portfolios (active and archived) with current valuation.</summary>
    [HttpGet]
    public Task<IReadOnlyList<PortfolioSummaryDto>> List(CancellationToken ct) => portfolios.ListAsync(ct);

    /// <summary>
    /// The current user's portfolios plus totals over the active ones in <paramref name="currency"/> (default: application currency).
    /// The total is omitted – never partial – when a portfolio cannot be valued or converted.
    /// </summary>
    [HttpGet("overview")]
    public Task<PortfolioOverviewDto> Overview([FromQuery, RegularExpression("^[A-Z]{3}$")] string? currency, CancellationToken ct) =>
        portfolios.OverviewAsync(currency, ct);

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

    /// <summary>
    /// The portfolio's optimizations that can be backtested or compared: successful mathematical runs and successful ML
    /// experiments linked to it, newest first.
    /// </summary>
    [HttpGet("{id:guid}/strategies")]
    [ProducesResponseType<IReadOnlyList<PortfolioStrategyDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<IReadOnlyList<PortfolioStrategyDto>> Strategies(Guid id, CancellationToken ct) => strategies.ListAsync(id, ct);

    /// <summary>Renames a portfolio and/or replaces its configuration. Does not trade.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<PortfolioDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PortfolioDto> Update(Guid id, UpdatePortfolioRequest request, CancellationToken ct) => portfolios.UpdateAsync(id, request, ct);

    /// <summary>Renames a portfolio or changes its description. Changes nothing else.</summary>
    [HttpPut("{id:guid}/details")]
    [ProducesResponseType<PortfolioDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PortfolioDto> UpdateDetails(Guid id, UpdatePortfolioDetailsRequest request, CancellationToken ct) =>
        portfolios.UpdateDetailsAsync(id, request, ct);

    /// <summary>Archives a portfolio: history stays readable; transactions, runs and proposals are refused until it is restored.</summary>
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType<PortfolioDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<PortfolioDto> Archive(Guid id, CancellationToken ct) => portfolios.ArchiveAsync(id, ct);

    /// <summary>Restores an archived portfolio.</summary>
    [HttpPost("{id:guid}/restore")]
    [ProducesResponseType<PortfolioDto>(StatusCodes.Status200OK)]
    public Task<PortfolioDto> Restore(Guid id, CancellationToken ct) => portfolios.RestoreAsync(id, ct);

    /// <summary>Permanently deletes a portfolio (active or archived) and its entire simulated history.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await portfolios.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Refreshes the quotes of the portfolio's open positions; quotes inside the cache window are reused, not re-requested.</summary>
    [HttpPost("{id:guid}/quotes/refresh")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<QuoteRefreshResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<QuoteRefreshResultDto> RefreshQuotes(Guid id, CancellationToken ct)
    {
        var symbols = await portfolios.HeldSymbolsAsync(id, ct);
        return symbols.Count == 0 ? new QuoteRefreshResultDto([], []) : await stocks.RefreshQuotesAsync(symbols, force: false, ct);
    }

    /// <summary>
    /// Current holdings with valuation, P&amp;L and target-weight drift, plus a portfolio summary, in the portfolio currency or in
    /// <paramref name="currency"/> (a reporting conversion that changes nothing in the portfolio).
    /// </summary>
    [HttpGet("{id:guid}/holdings")]
    public Task<HoldingsResponse> Holdings(Guid id, [FromQuery, RegularExpression("^[A-Z]{3}$")] string? currency, CancellationToken ct) =>
        analytics.GetHoldingsAsync(id, ct, currency);

    /// <summary>Simulated trade executions (immutable ledger), newest first.</summary>
    [HttpGet("{id:guid}/transactions")]
    public Task<PagedResult<TransactionDto>> Transactions(Guid id, [FromQuery] string? symbol, CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => portfolios.GetTransactionsAsync(id, page, pageSize, symbol, ct);

    /// <summary>Manually recorded transactions (journal), newest first.</summary>
    [HttpGet("{id:guid}/journal")]
    public Task<PagedResult<PortfolioTransactionDto>> Journal(Guid id, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        transactions.ListAsync(id, page, pageSize, ct);

    /// <summary>Records a buy, sell, deposit, withdrawal, dividend or fee (simulated bookkeeping – no real order or payment).</summary>
    /// <remarks>
    /// Requires an <c>Idempotency-Key</c> header: repeating a request with the same key returns the original booking
    /// (<c>idempotentReplay: true</c>). Validation failures change nothing. Dates cannot be in the future or before the
    /// portfolio's latest booking.
    /// </remarks>
    /// <response code="201">Booked (or idempotent replay).</response>
    /// <response code="400">Invalid input (missing fields, unknown security, no exchange rate for the date).</response>
    /// <response code="409">Key reused for another transaction, or a concurrent change.</response>
    /// <response code="422">Business rule (insufficient cash or shares, archived portfolio).</response>
    [HttpPost("{id:guid}/journal")]
    [ProducesResponseType<PortfolioTransactionDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PortfolioTransactionDto>> Record(Guid id, RecordTransactionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var t = await transactions.RecordAsync(id, request, idempotencyKey ?? "", ct);
        return StatusCode(StatusCodes.Status201Created, t);
    }

    /// <summary>Reverses a recorded deposit, withdrawal, dividend or fee with an opposite booking (the original stays).</summary>
    [HttpPost("{id:guid}/journal/{transactionId:guid}/reverse")]
    [ProducesResponseType<PortfolioTransactionDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PortfolioTransactionDto>> Reverse(Guid id, Guid transactionId, ReverseTransactionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var t = await transactions.ReverseAsync(id, transactionId, request, idempotencyKey ?? "", ct);
        return StatusCode(StatusCodes.Status201Created, t);
    }

    /// <summary>Cash ledger (deposits, withdrawals, dividends, settlements, fees), newest first.</summary>
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
    public Task<PerformanceDto> Performance(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery, RegularExpression("^[A-Z]{3}$")] string? currency, CancellationToken ct) =>
        analytics.GetPerformanceAsync(id, from, to, ct, currency);

    /// <summary>Ex-ante risk of current holdings (historical simulation): volatility, VaR, CVaR, beta, risk contributions.</summary>
    /// <param name="id">Portfolio id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="lookbackDays">Trailing trading days (20–2520).</param>
    /// <param name="confidence">VaR confidence level, e.g. 0.95.</param>
    [HttpGet("{id:guid}/risk")]
    public Task<RiskDto> Risk(Guid id, CancellationToken ct, [FromQuery] int lookbackDays = 252, [FromQuery] double confidence = 0.95) =>
        analytics.GetRiskAsync(id, lookbackDays, confidence, ct);
}
