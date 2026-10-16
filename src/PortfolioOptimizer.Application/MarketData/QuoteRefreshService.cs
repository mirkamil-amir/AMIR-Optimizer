using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

public sealed class QuoteRefreshOptions
{
    public const string Section = "MarketData:Quotes";

    /// <summary>Start a refresh cycle when an approved user signs in (disable only for deterministic tests).</summary>
    public bool RefreshOnSignIn { get; set; } = true;

    /// <summary>A stored quote retrieved within this window is reused instead of asking the provider again.</summary>
    public int FreshForMinutes { get; set; } = 15;
    /// <summary>A sign-in starts a new refresh cycle only if the last cycle was requested longer ago than this.</summary>
    public int CycleMinIntervalMinutes { get; set; } = 15;
    /// <summary>A forced single-stock refresh within this many seconds of the last retrieval returns the stored quote.</summary>
    public int SingleRefreshCooldownSeconds { get; set; } = 30;
    /// <summary>Maximum symbols per synchronous page refresh.</summary>
    public int PageRefreshMaxSymbols { get; set; } = 100;
    /// <summary>For providers with a daily quota, the share of the remaining budget a sign-in cycle may use.</summary>
    public double CycleQuotaShare { get; set; } = 0.5;
}

/// <summary>What happened to one symbol in a refresh.</summary>
public enum QuoteRefreshOutcome { Updated, Fresh, Failed, Unsupported, NotFound }

public sealed record QuoteRefreshItem(string Symbol, QuoteRefreshOutcome Outcome, string? Message);

/// <summary>
/// Keeps latest quotes current through one shared backend path, so browsers never call providers themselves.
/// <list type="bullet">
/// <item>Sign-in (and the scheduler) request a <em>cycle</em>: a background job that refreshes quotes for every catalogue
/// instrument in batches (Yahoo: 20 symbols per request), then checks whether tracked instruments' daily history is
/// current and queues an incremental download if not. Cycles are de-duplicated: one runs at a time and a new one starts
/// only when the previous was requested more than <see cref="QuoteRefreshOptions.CycleMinIntervalMinutes"/> ago.</item>
/// <item>Pages refresh the visible symbols synchronously; quotes retrieved within
/// <see cref="QuoteRefreshOptions.FreshForMinutes"/> are reused (cache).</item>
/// <item>A single stock can be force-refreshed, with a short cooldown against repeated clicks.</item>
/// </list>
/// Quotes are stored per provider (<see cref="LatestQuote"/>); failures are reported, never replaced by other values.
/// </summary>
public sealed class QuoteRefreshService(
    IApplicationDbContext db,
    IMarketDataProviderRegistry providers,
    IBackgroundJobQueue queue,
    PriceQueryService prices,
    MarketDataService marketData,
    ICurrentUser currentUser,
    IOptions<QuoteRefreshOptions> options,
    TimeProvider clock,
    ILogger<QuoteRefreshService> logger)
{
    public const string TriggerLogin = "Login";
    public const string TriggerScheduled = "Scheduled";
    public const string TriggerManual = "Manual";

    // ------------------------------------------------------------------ cycles (background)

    /// <summary>Requests a background refresh cycle; returns the cycle that is (or will be) running.</summary>
    public async Task<QuoteRefreshRunDto> RequestCycleAsync(string trigger, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var latest = await db.QuoteRefreshRuns.OrderByDescending(r => r.RequestedAtUtc).FirstOrDefaultAsync(ct);
        var minInterval = TimeSpan.FromMinutes(options.Value.CycleMinIntervalMinutes);
        if (latest is not null && (latest.Status is JobStatus.Queued or JobStatus.Running ||
                                   (trigger != TriggerManual && now - latest.RequestedAtUtc < minInterval)))
            return ToDto(latest, reused: true);

        var settings = await prices.SettingsAsync(ct);
        var run = new QuoteRefreshRun
        {
            Id = Guid.NewGuid(),
            Trigger = trigger,
            RequestedBy = currentUser.UserId,
            Source = settings.ActiveProvider,
            Status = JobStatus.Queued,
            RequestedAtUtc = now,
        };
        db.QuoteRefreshRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(new BackgroundJob(BackgroundJobKind.QuoteRefresh, run.Id), ct);
        return ToDto(run, reused: false);
    }

    public async Task<QuoteRefreshRunDto?> GetLatestCycleAsync(CancellationToken ct)
    {
        var latest = await db.QuoteRefreshRuns.AsNoTracking().OrderByDescending(r => r.RequestedAtUtc).FirstOrDefaultAsync(ct);
        return latest is null ? null : ToDto(latest, reused: false);
    }

    public async Task<IReadOnlyList<QuoteRefreshRunDto>> GetRecentCyclesAsync(int count, CancellationToken ct) =>
        (await db.QuoteRefreshRuns.AsNoTracking().OrderByDescending(r => r.RequestedAtUtc).Take(count).ToListAsync(ct))
        .Select(r => ToDto(r, reused: false)).ToList();

    /// <summary>Executes a queued cycle (background worker).</summary>
    public async Task ExecuteCycleAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.QuoteRefreshRuns.SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status != JobStatus.Queued) return;
        run.Status = JobStatus.Running;
        run.StartedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        try
        {
            var provider = providers.Get(run.Source);
            // Catalogue instruments (index members, benchmarks, tracked assets); stale quotes first.
            var assets = await db.Assets.AsNoTracking()
                .Where(a => a.IsEnabled || a.Memberships.Any() || a.AssetType == AssetType.Index)
                .Select(a => new
                {
                    a.Id, a.Symbol, a.IsEnabled,
                    Retrieved = db.LatestQuotes.Where(q => q.AssetId == a.Id && q.Source == run.Source).Select(q => (DateTime?)q.RetrievedAtUtc).FirstOrDefault(),
                })
                .ToListAsync(ct);
            var ordered = assets.OrderBy(a => a.Retrieved ?? DateTime.MinValue).ThenBy(a => a.Symbol).ToList();

            if (provider.RemainingDailyRequests is { } remaining)
            {
                // Quota-limited providers (Alpha Vantage free tier): only tracked instruments, within a share of today's budget.
                var budget = (int)Math.Floor(remaining * options.Value.CycleQuotaShare);
                var limited = ordered.Where(a => a.IsEnabled).Take(budget).ToList();
                run.Message = $"The data source has a daily request quota: refreshed up to {budget} tracked instruments " +
                              $"({remaining} requests left today); other quotes stay cached.";
                ordered = limited;
            }

            var result = await RefreshAsync(provider, ordered.Select(a => a.Symbol).ToList(), force: false, ct);
            run.SymbolsRequested = result.Count;
            run.Updated = result.Count(r => r.Outcome == QuoteRefreshOutcome.Updated);
            run.Unchanged = result.Count(r => r.Outcome == QuoteRefreshOutcome.Fresh);
            run.Failed = result.Count(r => r.Outcome is QuoteRefreshOutcome.Failed or QuoteRefreshOutcome.Unsupported);

            var history = await EnsureHistoryCurrentAsync(ct);
            var failures = result.Where(r => r.Outcome == QuoteRefreshOutcome.Failed).Take(5).Select(r => $"{r.Symbol}: {r.Message}");
            run.Message = string.Join(" ", new[] { run.Message, history, run.Failed > 0 ? $"Examples of failures: {string.Join("; ", failures)}" : null }
                .Where(m => !string.IsNullOrEmpty(m)));
            if (run.Message.Length > 2000) run.Message = run.Message[..2000];
            run.Status = run.Failed == 0 ? JobStatus.Succeeded : run.Updated + run.Unchanged == 0 ? JobStatus.Failed : JobStatus.PartiallySucceeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Quote refresh cycle {RunId} failed", runId);
            run.Status = JobStatus.Failed;
            run.Message = ex is ValidationException ? ex.Message : "Quote refresh failed; see server logs.";
        }
        run.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Quote refresh {RunId} ({Trigger}, {Source}): {Updated} updated, {Unchanged} fresh, {Failed} failed",
            run.Id, run.Trigger, run.Source, run.Updated, run.Unchanged, run.Failed);
    }

    /// <summary>
    /// Validates daily history of tracked instruments: if the newest stored bar is older than the last completed weekday,
    /// an incremental download is queued (unless one is already pending). Full history is never re-downloaded here.
    /// </summary>
    private async Task<string?> EnsureHistoryCurrentAsync(CancellationToken ct)
    {
        var settings = await prices.SettingsAsync(ct);
        var source = settings.ActiveProvider;
        var lastByAsset = await db.Assets.AsNoTracking().Where(a => a.IsEnabled)
            .Select(a => new { a.Symbol, Last = db.HistoricalPrices.Where(p => p.AssetId == a.Id && p.Source == source).Max(p => (DateOnly?)p.Date) })
            .ToListAsync(ct);
        var expected = LastCompletedWeekday(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        var behind = lastByAsset.Where(a => a.Last is null || a.Last < expected).Select(a => a.Symbol).ToList();
        if (behind.Count == 0) return "Daily history of tracked instruments is current.";
        if (await db.SyncJobs.AnyAsync(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running, ct))
            return $"{behind.Count} tracked instrument(s) lack the latest daily bar; a download is already in progress.";
        var job = await marketData.EnqueueSyncAsync(new SyncRequest { Symbols = behind }, ct);
        return $"Queued an incremental history download for {job.AssetsTotal} tracked instrument(s).";
    }

    private static DateOnly LastCompletedWeekday(DateOnly today)
    {
        var d = today.AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    // ------------------------------------------------------------------ synchronous refreshes

    /// <summary>Refreshes the given symbols from the active provider (page refresh or a single stock).</summary>
    public async Task<IReadOnlyList<QuoteRefreshItem>> RefreshSymbolsAsync(IReadOnlyCollection<string> symbols, bool force, CancellationToken ct)
    {
        var list = symbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
        if (list.Count == 0) return [];
        if (list.Count > options.Value.PageRefreshMaxSymbols)
            throw new ValidationException("symbols", $"At most {options.Value.PageRefreshMaxSymbols} symbols per refresh.");
        var settings = await prices.SettingsAsync(ct);
        return await RefreshAsync(providers.Get(settings.ActiveProvider), list, force, ct);
    }

    private async Task<List<QuoteRefreshItem>> RefreshAsync(IMarketDataProvider provider, IReadOnlyList<string> symbols, bool force, CancellationToken ct)
    {
        var source = provider.Descriptor.Name;
        var now = clock.GetUtcNow().UtcDateTime;
        var assets = await db.Assets.Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);
        var ids = assets.Values.Select(a => a.Id).ToList();
        var stored = await db.LatestQuotes.Where(q => ids.Contains(q.AssetId) && q.Source == source).ToDictionaryAsync(q => q.AssetId, ct);
        var lastClose = await db.HistoricalPrices.AsNoTracking().Where(p => ids.Contains(p.AssetId) && p.Source == source)
            .GroupBy(p => p.AssetId).Select(g => new { AssetId = g.Key, Close = g.OrderByDescending(p => p.Date).Select(p => p.Close).First() })
            .ToDictionaryAsync(x => x.AssetId, x => x.Close, ct);

        var results = new List<QuoteRefreshItem>();
        var toFetch = new List<string>();
        var freshFor = force ? TimeSpan.FromSeconds(options.Value.SingleRefreshCooldownSeconds) : TimeSpan.FromMinutes(options.Value.FreshForMinutes);
        foreach (var symbol in symbols)
        {
            if (!assets.TryGetValue(symbol, out var asset)) { results.Add(new(symbol, QuoteRefreshOutcome.NotFound, "Not in the catalogue.")); continue; }
            if (!provider.Supports(symbol))
            {
                results.Add(new(symbol, QuoteRefreshOutcome.Unsupported, $"The data source does not cover {Markets.ForSymbol(symbol).CountryName}."));
                continue;
            }
            if (stored.TryGetValue(asset.Id, out var q) && now - q.RetrievedAtUtc < freshFor)
            {
                results.Add(new(symbol, QuoteRefreshOutcome.Fresh, force ? "Refreshed moments ago; showing that quote." : "Cached quote is recent."));
                continue;
            }
            toFetch.Add(symbol);
        }

        foreach (var batch in toFetch.Chunk(Math.Max(1, provider.QuoteBatchSize)))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<QuoteFetch> fetched;
            try
            {
                fetched = await provider.GetLatestQuotesAsync(batch, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or MarketDataProviderException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Quote batch from {Source} failed", source);
                fetched = batch.Select(s => QuoteFetch.Fail(s, "The data provider could not be reached.", transient: true)).ToList();
            }

            var retrievedAt = clock.GetUtcNow().UtcDateTime;
            foreach (var f in fetched)
            {
                var asset = assets[f.Symbol];
                if (f.Quote is not { } quote)
                {
                    results.Add(new(f.Symbol, f.IsUnsupported ? QuoteRefreshOutcome.Unsupported : QuoteRefreshOutcome.Failed, f.Error));
                    continue;
                }
                if (!string.Equals(quote.Currency, asset.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new(f.Symbol, QuoteRefreshOutcome.Failed, $"Provider currency {quote.Currency} differs from the listing currency {asset.Currency}; quote not stored."));
                    continue;
                }
                stored.TryGetValue(asset.Id, out var row);
                var reference = row?.Price ?? (lastClose.TryGetValue(asset.Id, out var lc) ? lc : (decimal?)null);
                if (reference is > 0 && (quote.Price / reference.Value > 20m || quote.Price / reference.Value < 0.05m))
                {
                    // Guards against unit errors (pence vs pounds) or a wrong instrument behind a symbol.
                    results.Add(new(f.Symbol, QuoteRefreshOutcome.Failed, $"Implausible price {quote.Price} versus the last known {reference}; quote not stored."));
                    continue;
                }
                if (row is null)
                {
                    row = new LatestQuote { AssetId = asset.Id, Source = source };
                    db.LatestQuotes.Add(row);
                    stored[asset.Id] = row;
                }
                row.Price = quote.Price;
                row.PreviousClose = quote.PreviousClose;
                row.Currency = asset.Currency;
                row.AsOfUtc = quote.AsOfUtc;
                row.RetrievedAtUtc = retrievedAt;
                row.IsSimulated = provider.Descriptor.IsSimulated;
                results.Add(new(f.Symbol, QuoteRefreshOutcome.Updated, null));
            }
            await db.SaveChangesAsync(ct);
        }
        return results;
    }

    private static QuoteRefreshRunDto ToDto(QuoteRefreshRun r, bool reused) =>
        new(r.Id, r.Trigger, r.Source, r.Status, r.RequestedAtUtc, r.StartedAtUtc, r.CompletedAtUtc, r.SymbolsRequested, r.Updated, r.Unchanged, r.Failed,
            r.Message, reused);
}
