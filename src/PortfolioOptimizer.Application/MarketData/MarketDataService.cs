using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

public sealed class MarketDataService(
    IApplicationDbContext db,
    IMarketDataProviderRegistry providers,
    IBackgroundJobQueue queue,
    PriceQueryService prices,
    TimeProvider clock,
    ILogger<MarketDataService> logger)
{
    /// <summary>Days re-downloaded before the last stored date to pick up late corrections.</summary>
    private const int OverlapDays = 7;
    /// <summary>Relative change in overlapping adjusted closes that indicates new split/dividend adjustments.</summary>
    private const decimal AdjustmentTolerance = 0.0005m;

    // ------------------------------------------------------------------ assets

    public async Task<IReadOnlyList<AssetDto>> GetAssetsAsync(bool? enabled, CancellationToken ct)
    {
        var q = db.Assets.AsNoTracking();
        if (enabled is { } e) q = q.Where(a => a.IsEnabled == e);
        return (await q.OrderBy(a => a.Symbol).ToListAsync(ct)).Select(a => a.ToDto()).ToList();
    }

    public async Task<AssetDto> CreateAssetAsync(CreateAssetRequest request, CancellationToken ct)
    {
        var symbol = Asset.NormalizeSymbol(request.Symbol);
        if (await db.Assets.AnyAsync(a => a.Symbol == symbol, ct))
            throw new ConflictException($"Asset {symbol} already exists.");
        var asset = new Asset
        {
            Symbol = symbol,
            Name = request.Name?.Trim(),
            Exchange = request.Exchange?.Trim(),
            Currency = request.Currency.ToUpperInvariant(),
            AssetType = request.AssetType,
            Sector = request.Sector?.Trim(),
            IsEnabled = true,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync(ct);
        return asset.ToDto();
    }

    public async Task<AssetDto> UpdateAssetAsync(string symbol, UpdateAssetRequest request, CancellationToken ct)
    {
        var asset = await FindAssetAsync(symbol, ct);
        if (request.Name is not null) asset.Name = request.Name.Trim();
        if (request.Sector is not null) asset.Sector = request.Sector.Trim();
        if (request.IsEnabled is { } e) asset.IsEnabled = e;
        await db.SaveChangesAsync(ct);
        return asset.ToDto();
    }

    public async Task<PagedResult<PriceBarDto>> GetPricesAsync(string symbol, DateOnly? from, DateOnly? to, string? source, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) throw new ValidationException("page", "Page must be ≥ 1.");
        if (pageSize is < 1 or > 5000) throw new ValidationException("pageSize", "Page size must be between 1 and 5000.");
        var asset = await FindAssetAsync(symbol, ct);
        source ??= (await prices.SettingsAsync(ct)).ActiveProvider;
        var q = db.HistoricalPrices.AsNoTracking().Where(p => p.AssetId == asset.Id && p.Source == source);
        if (from is { } f) q = q.Where(p => p.Date >= f);
        if (to is { } t) q = q.Where(p => p.Date <= t);
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(p => p.Date).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new PriceBarDto(p.Date, p.Open, p.High, p.Low, p.Close, p.AdjustedClose, p.Volume, p.Source, p.IsSimulated, p.RetrievedAtUtc))
            .ToListAsync(ct);
        return new PagedResult<PriceBarDto>(items, page, pageSize, total);
    }

    // ------------------------------------------------------------------ sync jobs

    public async Task<SyncJobDto> EnqueueSyncAsync(SyncRequest request, CancellationToken ct)
    {
        var settings = await prices.SettingsAsync(ct);
        var providerName = request.Provider ?? settings.ActiveProvider;
        var provider = providers.Get(providerName); // validates name/configuration
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var start = request.StartDate ?? today.AddYears(-5);
        var end = request.EndDate ?? today;
        if (end < start) throw new ValidationException("endDate", "EndDate must not be before StartDate.");
        if (end > today) end = today;

        List<string> symbols;
        if (request.Symbols.Count == 0)
        {
            symbols = await db.Assets.Where(a => a.IsEnabled).OrderBy(a => a.Symbol).Select(a => a.Symbol).ToListAsync(ct);
        }
        else
        {
            symbols = request.Symbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
            var known = await db.Assets.Where(a => symbols.Contains(a.Symbol)).Select(a => a.Symbol).ToListAsync(ct);
            var unknown = symbols.Except(known).ToList();
            if (unknown.Count > 0)
                throw new ValidationException("symbols", $"Unknown asset(s): {string.Join(", ", unknown)}. Add them to the universe first.");
        }
        if (symbols.Count == 0) throw new ValidationException("symbols", "No enabled assets to synchronize.");

        var job = new MarketDataSyncJob
        {
            Id = Guid.NewGuid(),
            Status = JobStatus.Queued,
            Source = provider.Descriptor.Name,
            Symbols = string.Join(',', symbols),
            StartDate = start,
            EndDate = end,
            ForceFullRefresh = request.ForceFullRefresh,
            RequestedAtUtc = clock.GetUtcNow().UtcDateTime,
            AssetsTotal = symbols.Count,
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.MarketDataSync, job.Id), ct);
        return job.ToDto();
    }

    public async Task<SyncJobDto> GetJobAsync(Guid id, CancellationToken ct)
    {
        var job = await db.SyncJobs.AsNoTracking().Include(j => j.Logs).SingleOrDefaultAsync(j => j.Id == id, ct)
                  ?? throw new NotFoundException("Sync job", id);
        return job.ToDto();
    }

    /// <summary>Executes a queued sync job (called by the background worker).</summary>
    public async Task ExecuteSyncJobAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.SyncJobs.SingleOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status is not JobStatus.Queued) return;

        job.Status = JobStatus.Running;
        job.StartedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        IMarketDataProvider provider;
        try { provider = providers.Get(job.Source); }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Message = ex.Message;
            job.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var symbol in job.Symbols.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var log = await SyncAssetAsync(provider, symbol, job.StartDate, job.EndDate, job.ForceFullRefresh, ct);
            log.JobId = job.Id;
            db.SyncLogs.Add(log);
            if (log.Success) job.AssetsCompleted++;
            else job.AssetsFailed++;
            await db.SaveChangesAsync(ct);
        }

        job.Status = job.AssetsFailed == 0 ? JobStatus.Succeeded
            : job.AssetsCompleted == 0 ? JobStatus.Failed
            : JobStatus.PartiallySucceeded;
        job.Message = $"{job.AssetsCompleted} succeeded, {job.AssetsFailed} failed.";
        job.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Market data sync {JobId} from {Source} finished: {Message}", job.Id, job.Source, job.Message);
    }

    private async Task<DataSyncLog> SyncAssetAsync(IMarketDataProvider provider, string symbol, DateOnly start, DateOnly end, bool force, CancellationToken ct)
    {
        var source = provider.Descriptor.Name;
        var log = new DataSyncLog { Symbol = symbol, Source = source };
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Symbol == symbol, ct);
        if (asset is null)
        {
            log.Message = "Asset no longer exists.";
            log.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
            return log;
        }
        log.AssetId = asset.Id;

        try
        {
            var existing = await db.HistoricalPrices.Where(p => p.AssetId == asset.Id && p.Source == source)
                .GroupBy(_ => 1)
                .Select(g => new { Min = g.Min(p => p.Date), Max = g.Max(p => p.Date) })
                .SingleOrDefaultAsync(ct);

            var ranges = new List<(DateOnly From, DateOnly To)>();
            if (force || existing is null) ranges.Add((start, end));
            else
            {
                if (start < existing.Min) ranges.Add((start, existing.Min.AddDays(-1)));
                var incrementalFrom = existing.Max.AddDays(-OverlapDays);
                if (incrementalFrom < end) ranges.Add((incrementalFrom < start ? start : incrementalFrom, end));
            }

            var messages = new List<string>();
            foreach (var (from, to) in ranges)
            {
                var history = await provider.GetHistoricalPricesAsync(symbol, from, to, ct);
                var upsert = await UpsertAsync(asset, provider, history, ct);
                Accumulate(log, history, upsert);
                messages.AddRange(history.Warnings);

                if (upsert.AdjustmentsChanged && !force && existing is not null)
                {
                    // Back-adjusted series changed (new dividend or split): refresh the full stored history.
                    var fullFrom = existing.Min < start ? existing.Min : start;
                    messages.Add($"Adjusted prices changed on overlapping dates; re-downloaded full history from {fullFrom:yyyy-MM-dd}.");
                    var full = await provider.GetHistoricalPricesAsync(symbol, fullFrom, end, ct);
                    Accumulate(log, full, await UpsertAsync(asset, provider, full, ct));
                    break;
                }
            }

            var quote = await provider.GetLatestQuoteAsync(symbol, ct);
            if (quote is not null) await UpsertQuoteAsync(asset, provider, quote, ct);

            log.Success = true;
            log.Message = ranges.Count == 0 ? "Already up to date." : string.Join(" ", messages.Distinct().Take(10));
        }
        catch (MarketDataProviderException ex)
        {
            logger.LogWarning(ex, "Provider error syncing {Symbol} from {Source}", symbol, source);
            log.Success = false;
            log.Message = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Network error syncing {Symbol} from {Source}", symbol, source);
            log.Success = false;
            log.Message = $"Network error: {ex.Message}";
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            log.Success = false;
            log.Message = $"Request timed out: {ex.Message}";
        }

        log.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        return log;
    }

    private static void Accumulate(DataSyncLog log, ProviderHistory h, UpsertResult u)
    {
        log.RowsReceived += h.RawRowCount;
        log.RowsRejected += h.RejectedRows;
        log.DuplicatesSkipped += h.DuplicateRows;
        log.RowsInserted += u.Inserted;
        log.RowsUpdated += u.Updated;
    }

    private sealed record UpsertResult(int Inserted, int Updated, bool AdjustmentsChanged);

    private async Task<UpsertResult> UpsertAsync(Asset asset, IMarketDataProvider provider, ProviderHistory history, CancellationToken ct)
    {
        if (!string.Equals(history.Currency, asset.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new MarketDataProviderException(provider.Descriptor.Name, asset.Symbol,
                $"Provider currency {history.Currency} differs from the asset currency {asset.Currency}. Currency conversion is not supported; fix the asset definition.");
        }

        if (asset.Name is null && history.CompanyName is not null) asset.Name = history.CompanyName;
        if (asset.Exchange is null && history.Exchange is not null) asset.Exchange = history.Exchange;

        if (history.Bars.Count == 0) return new UpsertResult(0, 0, false);
        var from = history.Bars[0].Date;
        var to = history.Bars[^1].Date;
        var source = provider.Descriptor.Name;
        var existing = await db.HistoricalPrices
            .Where(p => p.AssetId == asset.Id && p.Source == source && p.Date >= from && p.Date <= to)
            .ToDictionaryAsync(p => p.Date, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        int inserted = 0, updated = 0;
        var adjustmentsChanged = false;
        foreach (var bar in history.Bars)
        {
            if (existing.TryGetValue(bar.Date, out var row))
            {
                if (row.AdjustedClose is { } oldAdj && bar.AdjustedClose is { } newAdj && oldAdj > 0 &&
                    Math.Abs(newAdj / oldAdj - 1) > AdjustmentTolerance)
                    adjustmentsChanged = true;

                if (row.Close != bar.Close || row.AdjustedClose != bar.AdjustedClose || row.Volume != bar.Volume)
                {
                    row.Open = bar.Open; row.High = bar.High; row.Low = bar.Low;
                    row.Close = bar.Close; row.AdjustedClose = bar.AdjustedClose; row.Volume = bar.Volume;
                    row.RetrievedAtUtc = now;
                    updated++;
                }
                continue;
            }

            db.HistoricalPrices.Add(new HistoricalPrice
            {
                AssetId = asset.Id,
                Date = bar.Date,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                AdjustedClose = bar.AdjustedClose,
                Volume = bar.Volume,
                Source = source,
                IsSimulated = provider.Descriptor.IsSimulated,
                RetrievedAtUtc = now,
            });
            inserted++;
        }
        await db.SaveChangesAsync(ct);
        return new UpsertResult(inserted, updated, adjustmentsChanged);
    }

    private async Task UpsertQuoteAsync(Asset asset, IMarketDataProvider provider, MarketQuote quote, CancellationToken ct)
    {
        if (quote.Price <= 0 || !string.Equals(quote.Currency, asset.Currency, StringComparison.OrdinalIgnoreCase)) return;
        var source = provider.Descriptor.Name;
        var row = await db.LatestQuotes.SingleOrDefaultAsync(q => q.AssetId == asset.Id && q.Source == source, ct);
        if (row is null)
        {
            row = new LatestQuote { AssetId = asset.Id, Source = source };
            db.LatestQuotes.Add(row);
        }
        row.Price = quote.Price;
        row.Currency = quote.Currency;
        row.AsOfUtc = quote.AsOfUtc;
        row.RetrievedAtUtc = clock.GetUtcNow().UtcDateTime;
        row.IsSimulated = provider.Descriptor.IsSimulated;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ status

    public async Task<MarketDataStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var settings = await prices.SettingsAsync(ct);
        var source = settings.ActiveProvider;
        var assets = await db.Assets.AsNoTracking().OrderBy(a => a.Symbol).ToListAsync(ct);
        var stats = await db.HistoricalPrices.AsNoTracking()
            .Where(p => p.Source == source)
            .GroupBy(p => p.AssetId)
            .Select(g => new
            {
                AssetId = g.Key,
                Count = g.Count(),
                First = g.Min(p => p.Date),
                Last = g.Max(p => p.Date),
                Retrieved = g.Max(p => p.RetrievedAtUtc),
                AdjCount = g.Count(p => p.AdjustedClose != null),
            })
            .ToDictionaryAsync(x => x.AssetId, ct);

        var lastLogs = (await db.SyncLogs.AsNoTracking().Where(l => l.Source == source)
                .OrderByDescending(l => l.CompletedAtUtc).Take(500).ToListAsync(ct))
            .GroupBy(l => l.AssetId).ToDictionary(g => g.Key, g => g.First());
        var latest = await prices.GetLatestPricesAsync(assets.Select(a => a.Id).ToList(), source, settings.StalePriceDays, ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var coverage = assets.Select(a =>
        {
            stats.TryGetValue(a.Id, out var s);
            lastLogs.TryGetValue(a.Id, out var log);
            latest.TryGetValue(a.Id, out var lp);
            var freshness = s is null ? DataFreshness.Missing
                : PriceQueryService.BusinessDaysBetween(s.Last, today) > settings.StalePriceDays ? DataFreshness.Stale
                : DataFreshness.Fresh;
            return new AssetCoverageDto(a.Id, a.Symbol, a.Name, a.IsEnabled, source, providers.Providers.Any(p => p.Name == source && p.IsSimulated),
                s?.Count ?? 0, s?.First, s?.Last, s is not null && s.AdjCount == s.Count, s?.Retrieved, freshness,
                lp?.Price, lp?.AsOfUtc, log?.Message, log?.Success);
        }).ToList();

        var jobs = await db.SyncJobs.AsNoTracking().OrderByDescending(j => j.RequestedAtUtc).Take(10).Include(j => j.Logs).ToListAsync(ct);
        var active = providers.Providers.FirstOrDefault(p => p.Name == source);
        return new MarketDataStatusDto(
            source,
            active?.IsSimulated ?? false,
            providers.Providers.Select(ToDto).ToList(),
            coverage,
            jobs.Select(j => j.ToDto()).ToList(),
            settings.StalePriceDays,
            clock.GetUtcNow().UtcDateTime);
    }

    public static ProviderStatusDto ToDto(ProviderDescriptor p) =>
        new(p.Name, p.DisplayName, p.IsSimulated, p.RequiresApiKey, p.IsConfigured, p.ProvidesAdjustedClose, p.RateLimit, p.Terms);

    private async Task<Asset> FindAssetAsync(string symbol, CancellationToken ct)
    {
        var s = Asset.NormalizeSymbol(symbol);
        return await db.Assets.SingleOrDefaultAsync(a => a.Symbol == s, ct) ?? throw new NotFoundException("Asset", s);
    }
}
