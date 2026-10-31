using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Watchlists;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>
/// The caller's personal watchlist. Entries are private (another user's entries are invisible); quotes and prices are
/// the shared catalogue data. A watchlist is not a portfolio and never triggers trades or optimizations.
/// </summary>
[ApiController]
[Route("api/v1/watchlist")]
public sealed class WatchlistController(WatchlistService watchlist) : ControllerBase
{
    /// <summary>Watched stocks with latest quote (provenance included) and the change over <c>range</c> (1M, 3M, 6M, 1Y, 5Y).</summary>
    [HttpGet]
    [ProducesResponseType<WatchlistDto>(StatusCodes.Status200OK)]
    public Task<WatchlistDto> Get([FromQuery] string? range, CancellationToken ct) => watchlist.GetAsync(range, ct);

    /// <summary>Adds a stock by canonical symbol. 409 if it is already watched or the list is full.</summary>
    [HttpPost]
    [ProducesResponseType<WatchlistAddResultDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WatchlistAddResultDto>> Add(AddWatchlistEntryRequest request, CancellationToken ct)
    {
        var result = await watchlist.AddAsync(request, ct);
        return Created($"/api/v1/watchlist/{Uri.EscapeDataString(result.Item.Stock.Symbol)}", result);
    }

    /// <summary>Removes a stock from the caller's watchlist (shared prices and news are kept).</summary>
    [HttpDelete("{symbol}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(string symbol, CancellationToken ct)
    {
        await watchlist.RemoveAsync(symbol, ct);
        return NoContent();
    }

    /// <summary>Saves the caller's order of the watchlist (every watched symbol once). 409 if the list changed meanwhile.</summary>
    [HttpPut("order")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reorder(ReorderWatchlistRequest request, CancellationToken ct)
    {
        await watchlist.ReorderAsync(request, ct);
        return NoContent();
    }

    /// <summary>Refreshes the quotes of every watched stock; quotes inside the cache window are reused, not re-requested.</summary>
    [HttpPost("quotes/refresh")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<QuoteRefreshResultDto>(StatusCodes.Status200OK)]
    public Task<QuoteRefreshResultDto> Refresh(CancellationToken ct) => watchlist.RefreshAsync(ct);
}
