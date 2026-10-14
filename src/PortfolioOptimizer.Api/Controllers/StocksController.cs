using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortfolioOptimizer.Api.Security;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>Stock discovery: sectors, search, metadata and latest prices with provenance.</summary>
/// <remarks>
/// Selections made in the dashboard are not stored by these endpoints; they are submitted with an optimization run, ML
/// experiment or backtest request.
/// </remarks>
[ApiController]
[Route("api/v1")]
public sealed class StocksController(StockDiscoveryService stocks, MarketDataService marketData, QuoteRefreshService quoteRefresh) : ControllerBase
{
    /// <summary>Markets (countries) with their index universes, provenance and price coverage.</summary>
    [HttpGet("markets")]
    public Task<IReadOnlyList<MarketDto>> Markets(CancellationToken ct) => stocks.GetMarketsAsync(ct);

    /// <summary>Sectors with the number of stocks and how many have stored prices, optionally within a market or index.</summary>
    [HttpGet("sectors")]
    public Task<IReadOnlyList<SectorDto>> Sectors([FromQuery] string? country, [FromQuery] string? index, CancellationToken ct) =>
        stocks.GetSectorsAsync(country, index, ct);

    /// <summary>Searches the catalogue.</summary>
    /// <param name="country">Market (ISO country: US, DE, FR, GB, JP); includes members of that market's indices.</param>
    /// <param name="index">Index code (SP500, DAX40, DAX30, CAC40, FTSE100, NIKKEI225).</param>
    /// <param name="sector">Exact sector name, or "Unclassified".</param>
    /// <param name="search">Case-insensitive match on ticker, company name or ISIN.</param>
    /// <param name="tracked">Filter on tracked (scheduled refresh) instruments.</param>
    /// <param name="withPrices">Filter on instruments with stored daily prices from the active source.</param>
    /// <param name="symbols">Comma-separated tickers to look up (e.g. the current selection).</param>
    /// <param name="sort">symbol (default), name, sector, change (ascending) or change-desc.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Rows per page (max 200).</param>
    [HttpGet("stocks")]
    public Task<PagedResult<StockListItemDto>> Search([FromQuery] string? country, [FromQuery] string? index, [FromQuery] string? sector,
        [FromQuery] string? search, [FromQuery] bool? tracked, [FromQuery] bool? withPrices, [FromQuery] string? symbols, [FromQuery] string? sort,
        CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        stocks.SearchAsync(new StockQuery(country, index, sector, search, tracked, withPrices, sort, page, pageSize,
            symbols?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), ct);

    /// <summary>Refreshes the latest quotes of up to 100 stocks (e.g. the visible page) through the server's data provider.</summary>
    /// <remarks>Quotes retrieved within the cache window are reused unless <c>force</c> is set (a 30-second cooldown still applies).</remarks>
    [HttpPost("stocks/quotes/refresh")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<QuoteRefreshResultDto>(StatusCodes.Status200OK)]
    public Task<QuoteRefreshResultDto> RefreshQuotes(RefreshQuotesRequest request, CancellationToken ct) =>
        stocks.RefreshQuotesAsync(request.Symbols, request.Force, ct);

    /// <summary>Refreshes one stock's quote now (cooldown 30 s per stock).</summary>
    [HttpPost("stocks/{symbol}/quote/refresh")]
    [EnableRateLimiting(RateLimitPolicies.Quotes)]
    [ProducesResponseType<QuoteRefreshResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<QuoteRefreshResultDto> RefreshQuote(string symbol, CancellationToken ct)
    {
        var result = await stocks.RefreshQuotesAsync([symbol], force: true, ct);
        if (result.Items is [{ Outcome: nameof(QuoteRefreshOutcome.NotFound) }]) throw new NotFoundException("Stock", symbol);
        return result;
    }

    /// <summary>The latest background quote refresh cycle (started at sign-in or by the scheduler), or 204 if none ran yet.</summary>
    [HttpGet("market-data/quote-refresh/latest")]
    [ProducesResponseType<QuoteRefreshRunDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<QuoteRefreshRunDto>> LatestCycle(CancellationToken ct) =>
        await quoteRefresh.GetLatestCycleAsync(ct) is { } run ? run : NoContent();

    /// <summary>Stock details: metadata with its source, data coverage and latest price.</summary>
    [HttpGet("stocks/{symbol}")]
    [ProducesResponseType<StockDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<StockDetailDto> Get(string symbol, CancellationToken ct) => stocks.GetAsync(symbol, ct);

    /// <summary>Latest available price: a stored provider quote (possibly delayed) or the last stored daily close.</summary>
    [HttpGet("stocks/{symbol}/quote")]
    [ProducesResponseType<StockQuoteDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<StockQuoteDto> Quote(string symbol, CancellationToken ct) => stocks.GetQuoteAsync(symbol, ct);

    /// <summary>Stored daily prices (raw and adjusted close) from the active source, ascending by date.</summary>
    [HttpGet("stocks/{symbol}/prices")]
    [ProducesResponseType<PagedResult<PriceBarDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PagedResult<PriceBarDto>> Prices(string symbol, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 1000) =>
        marketData.GetPricesAsync(symbol, from, to, null, page, pageSize, ct);
}

/// <summary>Instrument catalogue maintenance (administrators).</summary>
[ApiController]
[Route("api/v1/admin/catalogue")]
[Authorize(Policy = Policies.Administrator)]
public sealed class AdminCatalogueController(CatalogueService catalogue, UniverseService universes, QuoteRefreshService quoteRefresh) : ControllerBase
{
    /// <summary>
    /// Replaces an index's constituent list from a CSV file (symbol,name,country,exchange,mic,currency,sector,industry,isin)
    /// without code changes. Creates the index when <c>code</c> is new (name, country and currency are then required).
    /// </summary>
    [HttpPost("universes/{code}")]
    [RequestSizeLimit(3_000_000)]
    [ProducesResponseType<UniverseImportResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<UniverseImportResultDto> ImportUniverse(string code, ImportUniverseRequest request, CancellationToken ct)
    {
        var existing = (await universes.GetMarketsAsync("", ct)).SelectMany(m => m.Indices).FirstOrDefault(i => i.Code == code.ToUpperInvariant());
        var definition = new IndexDefinition(
            code.ToUpperInvariant(),
            request.Name ?? existing?.Name ?? throw new ValidationException("name", "Name is required for a new index."),
            (request.Country ?? existing?.Country ?? throw new ValidationException("country", "Country is required for a new index.")).ToUpperInvariant(),
            (request.Currency ?? existing?.Currency ?? throw new ValidationException("currency", "Currency is required for a new index.")).ToUpperInvariant(),
            request.BenchmarkSymbol ?? existing?.BenchmarkSymbol,
            request.IsHistorical ?? existing?.IsHistorical ?? false,
            request.Description ?? existing?.Description,
            request.Source, request.SourceUrl, request.SourceRevision, request.Licence,
            request.VerifiedOn ?? DateOnly.FromDateTime(DateTime.UtcNow));
        return await universes.ImportAsync(definition, request.Csv, ct);
    }

    /// <summary>Starts a quote refresh cycle for the whole catalogue now (normally started at sign-in and by the scheduler).</summary>
    [HttpPost("quote-refresh")]
    [ProducesResponseType<QuoteRefreshRunDto>(StatusCodes.Status200OK)]
    public Task<QuoteRefreshRunDto> StartQuoteRefresh(CancellationToken ct) => quoteRefresh.RequestCycleAsync(QuoteRefreshService.TriggerManual, ct);

    /// <summary>Recent quote refresh cycles (market-data health).</summary>
    [HttpGet("quote-refresh")]
    public Task<IReadOnlyList<QuoteRefreshRunDto>> QuoteRefreshHistory(CancellationToken ct) => quoteRefresh.GetRecentCyclesAsync(20, ct);

    /// <summary>Imports or enriches instruments from a CSV universe file (symbol,name,sector,industry,exchange,currency,type).</summary>
    /// <remarks>Only use data you are licensed to use. Metadata is recorded with the given source and never generated.</remarks>
    [HttpPost("import")]
    [RequestSizeLimit(3_000_000)]
    [ProducesResponseType<CatalogueImportResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<CatalogueImportResultDto> Import(ImportCatalogueRequest request, CancellationToken ct) => catalogue.ImportAsync(request, ct);
}
