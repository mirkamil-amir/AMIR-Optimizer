using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Fx;

/// <summary>
/// A part of a currency's history that is not stored yet. <paramref name="Backward"/>: older history before the stored range
/// (downloaded newest-first, so every stored chunk joins the stored range); otherwise the days after it (oldest-first).
/// </summary>
public sealed record FxGap(string Currency, DateOnly From, DateOnly To, bool Backward);

/// <summary>What one download did. <see cref="Problems"/> are user-facing sentences (shown with the conversion that waited).</summary>
public sealed record FxDownloadReport(int Requests, int Received, int Stored, int Rejected, IReadOnlyList<string> Problems);

/// <summary>Pure planning rules of the FX download (unit tested).</summary>
internal static class FxDownloadPlan
{
    /// <summary>
    /// Two years per provider request. The ECB answered an 8-year request for three currencies in 17 s and single years in
    /// 2.6–20 s (measured 2026-10-23), so one request for a long history exceeded the attempt timeout and nothing was stored.
    /// </summary>
    public const int ChunkDays = 731;

    /// <summary>
    /// Largest pause between two published rates that still counts as one continuous series (the ECB publishes on TARGET
    /// days; the longest closure, Good Friday–Easter Monday, is a 5-day step). Larger pauses are a missing stretch.
    /// </summary>
    public const int ContiguityToleranceDays = 7;

    /// <summary>[from, to] in chunks of at most <paramref name="chunkDays"/> days, newest first when <paramref name="backward"/>.</summary>
    public static IEnumerable<(DateOnly From, DateOnly To)> Chunks(DateOnly from, DateOnly to, bool backward, int chunkDays = ChunkDays)
    {
        if (to < from) yield break;
        if (backward)
        {
            for (var end = to; end >= from; end = end.AddDays(-chunkDays))
            {
                var start = end.AddDays(-(chunkDays - 1));
                yield return (start < from ? from : start, end);
                if (start <= from) yield break;
            }
        }
        else
        {
            for (var start = from; start <= to; start = start.AddDays(chunkDays))
            {
                var end = start.AddDays(chunkDays - 1);
                yield return (start, end > to ? to : end);
                if (end >= to) yield break;
            }
        }
    }

    /// <summary>
    /// The observations of one currency that may be stored without leaving a hole: those inside the stored range
    /// [<paramref name="storedMin"/>, <paramref name="storedMax"/>] plus the runs that continue it outward with steps of at most
    /// <paramref name="tolerance"/> days. Without stored data, the run ending at the newest observation. Everything beyond a
    /// larger pause is left out, because coverage is judged by the first and last stored date (FxService.GapsAsync) and a
    /// hidden hole would otherwise count as stored.
    /// </summary>
    public static List<FxObservation> Connected(IEnumerable<FxObservation> observations, DateOnly? storedMin, DateOnly? storedMax,
        int tolerance = ContiguityToleranceDays)
    {
        var sorted = observations.GroupBy(o => o.Date).Select(g => g.Last()).OrderBy(o => o.Date).ToList();
        if (sorted.Count == 0) return [];
        var lo = storedMin ?? sorted[^1].Date;
        var hi = storedMax ?? sorted[^1].Date;
        var accepted = sorted.Where(o => o.Date >= lo && o.Date <= hi).ToList();
        var edge = hi;
        foreach (var o in sorted.Where(o => o.Date > hi))
        {
            if (o.Date.DayNumber - edge.DayNumber > tolerance) break;
            accepted.Add(o);
            edge = o.Date;
        }
        edge = lo;
        foreach (var o in sorted.Where(o => o.Date < lo).Reverse())
        {
            if (edge.DayNumber - o.Date.DayNumber > tolerance) break;
            accepted.Add(o);
            edge = o.Date;
        }
        return accepted;
    }
}

/// <summary>
/// Downloads missing exchange-rate history outside the request that needs it (singleton). One download per provider at a
/// time – concurrent callers join it. The download runs in chunks (<see cref="FxDownloadPlan.ChunkDays"/>) and stores each
/// chunk as soon as it arrives, so an interrupted download keeps what it got and the next one resumes from there. A caller
/// waits only for its budget (<see cref="FxFetchThrottle"/>); the download itself continues, so a long history that does not
/// fit into one request is complete for the next. Failures are reported, never stored as an empty success.
/// </summary>
public sealed class FxDownloader(IServiceScopeFactory scopes, FxFetchThrottle throttle, TimeProvider clock, ILogger<FxDownloader> logger) : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Task<FxDownloadReport>> _running = new();

    /// <summary>
    /// Starts a download of <paramref name="gaps"/>, or returns the provider's running one (<c>Joined</c>: it was started for
    /// another caller and may cover other gaps – check again when it ends).
    /// </summary>
    public (Task<FxDownloadReport> Download, bool Joined) Start(string provider, IReadOnlyList<FxGap> gaps)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(provider, out var running) && !running.IsCompleted) return (running, true);
            var token = _stopping.Token;
            var task = Task.Run(() => RunAsync(provider, gaps, token), CancellationToken.None);
            _running[provider] = task;
            return (task, false);
        }
    }

    /// <summary>True while a download of <paramref name="provider"/> is running.</summary>
    public bool IsRunning(string provider)
    {
        lock (_lock) return _running.TryGetValue(provider, out var running) && !running.IsCompleted;
    }

    private async Task<FxDownloadReport> RunAsync(string providerName, IReadOnlyList<FxGap> gaps, CancellationToken ct)
    {
        int requests = 0, received = 0, stored = 0, rejected = 0;
        var problems = new List<string>();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var provider = scope.ServiceProvider.GetServices<IFxRateProvider>().First(p => p.Name == providerName);
            // Recent days first (current valuations need them), then older history.
            foreach (var group in gaps.GroupBy(g => (g.From, g.To, g.Backward)).OrderBy(g => g.Key.Backward).ThenByDescending(g => g.Key.To))
            {
                var (gapFrom, gapTo, backward) = group.Key;
                var missing = group.Select(g => g.Currency).Distinct().Order().ToList();
                var codes = gapTo.DayNumber - gapFrom.DayNumber <= FxService.FetchAllCurrenciesMaxDays
                    ? missing.Union(provider.SupportedCurrencies).Order().ToList()
                    : missing;
                foreach (var (from, to) in FxDownloadPlan.Chunks(gapFrom, gapTo, backward))
                {
                    var key = $"{provider.Name}:{string.Join(",", missing)}:{from}:{to}";
                    if (!throttle.TryBegin(key, clock.GetUtcNow().UtcDateTime))
                    {
                        if (backward) problems.Add($"{string.Join(", ", missing)} rates for {from:yyyy-MM-dd} – {to:yyyy-MM-dd} were requested from {provider.Name} less than an hour ago without result; they are not requested again yet.");
                        break;
                    }
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    IReadOnlyList<FxObservation> observations;
                    requests++;
                    try
                    {
                        observations = await provider.GetRatesAsync(codes, from, to, ct);
                    }
                    catch (FxProviderException ex)
                    {
                        logger.LogWarning("FX download from {Provider} failed for {Currencies} {From}–{To} after {Ms} ms: {Message}",
                            provider.Name, string.Join(",", missing), from, to, watch.ElapsedMilliseconds, ex.Message);
                        problems.Add($"The {provider.Name} download of {string.Join(", ", missing)} rates for {from:yyyy-MM-dd} – {to:yyyy-MM-dd} failed ({ex.Message}); it is retried after an hour.");
                        break;
                    }
                    var (rows, invalid, connected) = await StoreAsync(db, provider, codes, observations, clock.GetUtcNow().UtcDateTime, ct);
                    received += observations.Count;
                    stored += rows;
                    rejected += invalid;
                    var notContinued = missing.Where(c => !connected.Contains(c)).ToList();
                    logger.LogInformation(
                        "FX download {Provider} {From}–{To} for {Currencies}: received {Received}, stored {Stored}, rejected {Rejected} (invalid or not requested), not continuing the stored series: {Short} ({Ms} ms)",
                        provider.Name, from, to, codes.Count == missing.Count ? string.Join(",", codes) : $"{string.Join(",", missing)} (+{codes.Count - missing.Count} others)",
                        observations.Count, rows, invalid, notContinued.Count == 0 ? "none" : string.Join(",", notContinued), watch.ElapsedMilliseconds);
                    if (notContinued.Count > 0)
                    {
                        // Nothing that continues the stored series: the provider has no rates there (yet), or the answer was partial.
                        // Going further would store a chunk with a hole in between, so this range stops here.
                        if (backward) problems.Add($"{provider.Name} returned no {string.Join(", ", notContinued)} rates continuing the stored history for {from:yyyy-MM-dd} – {to:yyyy-MM-dd}; older rates are not available from it.");
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            problems.Add("The exchange-rate download was stopped because the application is shutting down.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FX download from {Provider} failed", providerName);
            problems.Add("The exchange-rate download failed unexpectedly; the details were logged on the server.");
        }
        return new FxDownloadReport(requests, received, stored, rejected, problems);
    }

    /// <summary>Upserts the valid observations that continue each currency's stored series; returns the currencies that got some.</summary>
    private static async Task<(int Stored, int Rejected, HashSet<string> Connected)> StoreAsync(IApplicationDbContext db, IFxRateProvider provider,
        IReadOnlyCollection<string> codes, IReadOnlyList<FxObservation> observations, DateTime now, CancellationToken ct)
    {
        var wanted = codes.ToHashSet();
        var valid = observations.Where(o => wanted.Contains(o.Currency) && o.UnitsPerEur > 0).ToList();
        var currencies = valid.Select(o => o.Currency).Distinct().ToList();
        var coverage = await db.FxRates.AsNoTracking()
            .Where(r => r.Source == provider.Name && currencies.Contains(r.Currency))
            .GroupBy(r => r.Currency)
            .Select(g => new { Currency = g.Key, Min = g.Min(r => r.Date), Max = g.Max(r => r.Date) })
            .ToListAsync(ct);
        var rows = new List<FxRate>();
        var connected = new HashSet<string>();
        foreach (var group in valid.GroupBy(o => o.Currency))
        {
            var cov = coverage.FirstOrDefault(c => c.Currency == group.Key);
            var keep = FxDownloadPlan.Connected(group, cov?.Min, cov?.Max);
            // A currency counts as continued only when it gained dates beyond its stored range.
            if (keep.Any(o => cov is null || o.Date < cov.Min || o.Date > cov.Max)) connected.Add(group.Key);
            rows.AddRange(keep.Select(o => new FxRate
            {
                Source = provider.Name, Currency = o.Currency, Date = o.Date, UnitsPerEur = o.UnitsPerEur,
                IsSimulated = provider.IsSimulated, RetrievedAtUtc = now,
            }));
        }
        await db.UpsertFxRatesAsync(rows, ct);
        return (rows.Count, observations.Count - valid.Count, connected);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        Task[] running;
        lock (_lock) running = [.. _running.Values];
        // RunAsync never throws; give it a moment to leave the database before the host disposes it.
        Task.WaitAll(running, TimeSpan.FromSeconds(5));
        _stopping.Dispose();
    }
}
