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
public sealed class NewsController(NewsService news, WireFeedService wire) : ControllerBase
{
    // ------------------------------------------------------------------ Wire feeds (stories; docs/financial-news.md › Wire feeds)

    /// <summary>Latest: Medium and High attention stories of every kind, newest first, at most two per company per page.</summary>
    /// <param name="hours">Time window back from now (default 48, max 2160).</param>
    /// <param name="region">ISO country (US, DE, …) or EU.</param>
    /// <param name="attention">Minimum attention: Medium (default) or High.</param>
    /// <param name="search">Headline text, ticker or company name.</param>
    /// <param name="cursor">NextCursor of the previous page.</param>
    /// <param name="pageSize">Stories per page (1–50, default 20).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("news/latest")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<WireFeedDto>(StatusCodes.Status200OK)]
    public Task<WireFeedDto> Latest([FromQuery] int? hours, [FromQuery] string? region, [FromQuery] string? attention, [FromQuery] string? search,
        [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken ct) =>
        wire.LatestAsync(new WireQuery(hours, region, attention, search, cursor, pageSize), ct);

    /// <summary>My Watchlist: stories linked to the caller's watched listings, and rate decisions of their currencies' central banks.</summary>
    /// <param name="hours">Time window (default 168).</param>
    /// <param name="region">ISO country or EU.</param>
    /// <param name="attention">Minimum attention.</param>
    /// <param name="search">Headline text, ticker or company name.</param>
    /// <param name="eventType">Event type (Results, RateDecision, …).</param>
    /// <param name="cursor">NextCursor of the previous page.</param>
    /// <param name="pageSize">Stories per page (1–50, default 20).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("news/watchlist")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<WireFeedDto>(StatusCodes.Status200OK)]
    public Task<WireFeedDto> Watchlist([FromQuery] int? hours, [FromQuery] string? region, [FromQuery] string? attention, [FromQuery] string? search,
        [FromQuery] string? eventType, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken ct) =>
        wire.WatchlistAsync(new WireQuery(hours, region, attention, search, cursor, pageSize, EventType: eventType), ct);

    /// <summary>Company News: companies (curated, held, watched) with their newest stories, newest company first.</summary>
    /// <param name="hours">Time window (default 168).</param>
    /// <param name="region">ISO country of the listing.</param>
    /// <param name="search">Ticker, company name or headline text.</param>
    /// <param name="eventType">Event type (Results, MergerOrAcquisition, …).</param>
    /// <param name="cursor">NextCursor of the previous page.</param>
    /// <param name="pageSize">Companies per page (1–50, default 10).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("news/companies")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<WireCompaniesDto>(StatusCodes.Status200OK)]
    public Task<WireCompaniesDto> Companies([FromQuery] int? hours, [FromQuery] string? region, [FromQuery] string? search, [FromQuery] string? eventType,
        [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken ct) =>
        wire.CompaniesAsync(new WireQuery(hours, region, null, search, cursor, pageSize, EventType: eventType), ct);

    /// <summary>Market-wide: market-level stories by area (Equities, Rates, Fx, Commodities, Volatility); with area one area, paged.</summary>
    /// <param name="area">One market area; omit for all areas.</param>
    /// <param name="hours">Time window (default 72).</param>
    /// <param name="region">ISO country or EU.</param>
    /// <param name="attention">Minimum attention.</param>
    /// <param name="search">Headline text.</param>
    /// <param name="cursor">NextCursor of the area's previous page (needs area).</param>
    /// <param name="pageSize">Stories per area (default 5; 20 with area).</param>
    /// <param name="entity">Only stories linked to this entity (key, e.g. ^GSPC, EURUSD, CL=F).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("news/markets")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<WireMarketsDto>(StatusCodes.Status200OK)]
    public Task<WireMarketsDto> Markets([FromQuery] string? area, [FromQuery] int? hours, [FromQuery] string? region, [FromQuery] string? attention,
        [FromQuery] string? search, [FromQuery] string? cursor, [FromQuery] int? pageSize, [FromQuery] string? entity, CancellationToken ct) =>
        wire.MarketsAsync(new WireQuery(hours, region, attention, search, cursor, pageSize, Area: area, Entity: entity), ct);

    /// <summary>Macroeconomic: central-bank and statistics releases and macro news, newest first.</summary>
    /// <param name="hours">Time window (default 720).</param>
    /// <param name="region">ISO country or EU.</param>
    /// <param name="attention">Minimum attention.</param>
    /// <param name="search">Headline text.</param>
    /// <param name="eventType">Event type (RateDecision, InflationRelease, …).</param>
    /// <param name="cursor">NextCursor of the previous page.</param>
    /// <param name="pageSize">Stories per page (1–50, default 20).</param>
    /// <param name="entity">Only stories linked to this entity (key, e.g. ECB, EUROSTAT).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("news/macro")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<WireFeedDto>(StatusCodes.Status200OK)]
    public Task<WireFeedDto> Macro([FromQuery] int? hours, [FromQuery] string? region, [FromQuery] string? attention, [FromQuery] string? search,
        [FromQuery] string? eventType, [FromQuery] string? cursor, [FromQuery] int? pageSize, [FromQuery] string? entity, CancellationToken ct) =>
        wire.MacroAsync(new WireQuery(hours, region, attention, search, cursor, pageSize, EventType: eventType, Entity: entity), ct);

    /// <summary>Classification history of a story's reports: every rules version with its result, reason and time.</summary>
    /// <param name="id">Story id from a Wire feed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="404">The story does not exist or is not visible to the caller.</response>
    [HttpGet("news/stories/{id:guid}/classifications")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<WireClassificationHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<WireClassificationHistoryDto> StoryClassifications(Guid id, CancellationToken ct) => wire.ClassificationHistoryAsync(id, ct);

    /// <summary>
    /// News about one of the caller's portfolios: stories linked to its holdings and official rate decisions for the
    /// currencies it holds, each with the holding's weight or the currency's share from the current valuation. Another
    /// user's portfolio → 404.
    /// </summary>
    /// <param name="id">Portfolio id.</param>
    /// <param name="hours">Time window (default 168).</param>
    /// <param name="cursor">NextCursor of the previous page.</param>
    /// <param name="pageSize">Stories per page (1–50, default 10).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("portfolios/{id:guid}/news")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<PortfolioNewsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PortfolioNewsDto> PortfolioNews(Guid id, [FromQuery] int? hours, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken ct) =>
        wire.PortfolioAsync(id, new WireQuery(hours, Cursor: cursor, PageSize: pageSize), ct);

    // ------------------------------------------------------------------ article feed (stock pages)

    /// <summary>
    /// News feed, newest first, one row per story. Stale sources are re-fetched (at most once per refresh window, shared by
    /// all users) before the stored articles are returned.
    /// </summary>
    /// <param name="category">General, Earnings, CorporateTransaction, RegulatoryLegal, CentralBank, Macroeconomic, Geopolitical, CommoditiesEnergy, CurrenciesBonds, MarketIndex.</param>
    /// <param name="scope">Company, Market or Macro.</param>
    /// <param name="region">ISO country of a market (US, DE, JP, …) or EU.</param>
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
