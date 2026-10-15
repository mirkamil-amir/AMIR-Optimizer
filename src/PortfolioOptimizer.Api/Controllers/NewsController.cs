using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.News;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>
/// Financial news from the configured providers with source, timestamps, rule-based categories and a transparent
/// relevance indicator. Articles are shared; the watchlist filter uses the caller's own watchlist.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class NewsController(NewsService news) : ControllerBase
{
    /// <summary>
    /// News feed, newest first, one row per story. Stale sources are re-fetched (at most once per refresh window, shared by
    /// all users) before the stored articles are returned.
    /// </summary>
    /// <param name="category">General, Earnings, CorporateTransaction, RegulatoryLegal, CentralBank, Macroeconomic, Geopolitical, CommoditiesEnergy, CurrenciesBonds, MarketIndex.</param>
    /// <param name="scope">Company, Market or Macro.</param>
    /// <param name="region">ISO country (US, DE, FR, GB, JP) or EU.</param>
    /// <param name="symbol">Only articles the provider tagged with this listing.</param>
    /// <param name="watchlist">Only articles about stocks on the caller's watchlist.</param>
    /// <param name="from">Published on or after (UTC date).</param>
    /// <param name="to">Published on or before (UTC date).</param>
    /// <param name="publisher">Exact publisher name.</param>
    /// <param name="search">Headline text, ticker or company name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Stories per page (max 100).</param>
    [HttpGet("news")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<NewsFeedDto>(StatusCodes.Status200OK)]
    public Task<NewsFeedDto> Feed([FromQuery] string? category, [FromQuery] string? scope, [FromQuery] string? region, [FromQuery] string? symbol,
        [FromQuery] bool? watchlist, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? publisher, [FromQuery] string? search,
        CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 30) =>
        news.GetFeedAsync(new NewsQuery(category, scope, region, symbol, watchlist ?? false, from, to, publisher, search, page, pageSize), ct);

    /// <summary>Configured news sources with their last fetch time and last error.</summary>
    [HttpGet("news/sources")]
    public Task<IReadOnlyList<NewsSourceStatusDto>> Sources(CancellationToken ct) => news.SourcesAsync(ct);

    /// <summary>
    /// Event and price-movement comparison for one article and one of its tagged stocks: last close before publication,
    /// changes after 1, 5 and 20 trading days and the benchmark over the same dates. Not evidence of causation.
    /// </summary>
    [HttpGet("news/{id:guid}/price-movement")]
    [ProducesResponseType<EventComparisonDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<EventComparisonDto> PriceMovement(Guid id, [FromQuery] string symbol, CancellationToken ct) =>
        news.GetEventComparisonAsync(id, symbol, ct);
}
