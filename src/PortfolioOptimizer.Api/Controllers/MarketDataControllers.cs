using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Settings;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>Asset universe and stored historical prices.</summary>
[ApiController]
[Route("api/v1/assets")]
public sealed class AssetsController(MarketDataService marketData) : ControllerBase
{
    /// <summary>Lists assets in the universe.</summary>
    /// <param name="enabled">Optional filter on the enabled flag.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public Task<IReadOnlyList<AssetDto>> List([FromQuery] bool? enabled, CancellationToken ct) => marketData.GetAssetsAsync(enabled, ct);

    /// <summary>Adds an asset to the universe (administrators). Prices are downloaded by a subsequent market-data sync.</summary>
    /// <response code="201">Asset created.</response>
    /// <response code="400">Invalid symbol or currency.</response>
    /// <response code="409">The symbol already exists.</response>
    [HttpPost]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<AssetDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AssetDto>> Create(CreateAssetRequest request, CancellationToken ct)
    {
        var asset = await marketData.CreateAssetAsync(request, ct);
        return CreatedAtAction(nameof(Prices), new { symbol = asset.Symbol }, asset);
    }

    /// <summary>Updates name/sector or enables/disables an asset (administrators).</summary>
    [HttpPatch("{symbol}")]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<AssetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<AssetDto> Update(string symbol, UpdateAssetRequest request, CancellationToken ct) => marketData.UpdateAssetAsync(symbol, request, ct);

    /// <summary>Stored daily prices for one asset (paginated, ascending by date).</summary>
    /// <param name="symbol">Ticker, e.g. AAPL.</param>
    /// <param name="from">First date (inclusive).</param>
    /// <param name="to">Last date (inclusive).</param>
    /// <param name="source">Data source; defaults to the active provider.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page (max 5000).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{symbol}/prices")]
    [ProducesResponseType<PagedResult<PriceBarDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PagedResult<PriceBarDto>> Prices(string symbol, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? source, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 500) =>
        marketData.GetPricesAsync(symbol, from, to, source, page, pageSize, ct);
}

/// <summary>Market data ingestion and data-quality status.</summary>
[ApiController]
[Route("api/v1/market-data")]
public sealed class MarketDataController(MarketDataService marketData) : ControllerBase
{
    /// <summary>Queues a historical price download. Returns immediately; poll the job for progress.</summary>
    /// <remarks>Downloads are incremental: only missing dates (plus a 7-day overlap for corrections) are requested unless forceFullRefresh is set.</remarks>
    /// <response code="202">Job queued.</response>
    [HttpPost("sync")]
    [EnableRateLimiting("expensive")]
    [ProducesResponseType<SyncJobDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SyncJobDto>> Sync(SyncRequest request, CancellationToken ct)
    {
        var job = await marketData.EnqueueSyncAsync(request, ct);
        return AcceptedAtAction(nameof(Job), new { id = job.Id }, job);
    }

    /// <summary>Status of a sync job with per-asset data-quality logs.</summary>
    [HttpGet("jobs/{id:guid}")]
    [ProducesResponseType<SyncJobDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<SyncJobDto> Job(Guid id, CancellationToken ct) => marketData.GetJobAsync(id, ct);

    /// <summary>Data coverage and freshness per asset, provider status and recent jobs.</summary>
    [HttpGet("status")]
    public Task<MarketDataStatusDto> Status(CancellationToken ct) => marketData.GetStatusAsync(ct);

    /// <summary>Compact data status (freshness counts, latest retrieval, running download) for the navigation.</summary>
    [HttpGet("summary")]
    public Task<MarketDataSummaryDto> Summary(CancellationToken ct) => marketData.GetSummaryAsync(ct);
}

/// <summary>Global application settings (provider, defaults). API keys are configured server-side only.</summary>
[ApiController]
[Route("api/v1/settings")]
public sealed class SettingsController(SettingsService settings) : ControllerBase
{
    /// <summary>Current settings and provider configuration status (never includes secrets).</summary>
    [HttpGet]
    public Task<SettingsDto> Get(CancellationToken ct) => settings.GetAsync(ct);

    /// <summary>Updates the active provider and defaults (administrators).</summary>
    [HttpPut]
    [Authorize(Policy = Policies.Administrator)]
    [ProducesResponseType<SettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<SettingsDto> Update(UpdateSettingsRequest request, CancellationToken ct) => settings.UpdateAsync(request, ct);
}
