using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>
/// Before an analysis runs: which stored prices and exchange rates are missing for its stocks, period and currency, and –
/// when the user agrees – downloads them (price sync jobs, background FX download). Nothing is filled or estimated; what
/// the data source does not have stays missing and is reported as such.
/// </summary>
public sealed class DataReadinessService(IApplicationDbContext db, PriceQueryService prices, MarketDataService marketData, FxService fx,
    ICurrentUser currentUser, TimeProvider clock)
{
    /// <summary>The largest universe any analysis accepts (a Math Optimization's) plus its benchmark.</summary>
    public const int MaxSymbols = Optimization.OptimizationService.MaxUniverse + 1;

    /// <summary>A history starting within a week after the requested date is complete (weekends, holidays, listing calendars).</summary>
    public const int StartToleranceDays = 7;

    public async Task<DataCheckDto> CheckAsync(DataCheckRequest request, CancellationToken ct)
    {
        var (symbols, from, to) = Validate(request);
        var known = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).Select(a => a.Symbol).ToListAsync(ct);
        var unknown = symbols.Except(known).ToList();
        if (unknown.Count > 0) throw new ValidationException("symbols", $"Unknown symbol(s): {string.Join(", ", unknown)}.");
        var settings = await prices.SettingsAsync(ct);
        var source = settings.ActiveProvider;
        var simulated = await fx.ActiveSourceIsSimulatedAsync(ct);
        var notes = new List<string>();

        var stored = await db.HistoricalPrices.AsNoTracking()
            .Where(p => symbols.Contains(p.Asset!.Symbol) && p.Source == source)
            .GroupBy(p => p.Asset!.Symbol)
            .Select(g => new { Symbol = g.Key, Min = g.Min(p => p.Date), Max = g.Max(p => p.Date), Count = g.Count(p => p.Date >= from && p.Date <= to) })
            .ToDictionaryAsync(x => x.Symbol, ct);
        // Older history already requested from this source without result: the source has none (younger listing).
        var limit = from.AddDays(StartToleranceDays);
        var askedOlder = await db.SyncJobs.AsNoTracking()
            .Where(j => j.Source == source && j.StartDate <= limit)
            .SelectMany(j => j.Logs).Where(l => l.Success && symbols.Contains(l.Symbol))
            .Select(l => l.Symbol).Distinct().ToListAsync(ct);
        var running = await RunningSyncSymbolsAsync(source, symbols, ct);

        var staleBefore = to.AddDays(-Math.Max(settings.StalePriceDays, 4));
        var missingPrices = new List<MissingPriceDto>();
        foreach (var symbol in symbols)
        {
            stored.TryGetValue(symbol, out var s);
            var needsOlder = s is null || s.Min > limit;
            var needsRecent = s is not null && s.Max < staleBefore;
            if (!needsOlder && !needsRecent) continue;
            var unavailable = needsOlder && s is not null && askedOlder.Contains(symbol);
            var canDownload = (needsOlder && !unavailable) || needsRecent;
            var explanation = s is null
                ? "No stored prices."
                : string.Join(" ", new[]
                {
                    needsOlder ? unavailable
                        ? $"Prices start {s.Min:yyyy-MM-dd}; the data source has no older prices (already requested)."
                        : $"Prices start {s.Min:yyyy-MM-dd}; the analysis needs them from {from:yyyy-MM-dd}." : null,
                    needsRecent ? $"Latest price {s.Max:yyyy-MM-dd}." : null,
                }.Where(x => x is not null));
            missingPrices.Add(new MissingPriceDto(symbol, s?.Min, s?.Max, s?.Count ?? 0, needsOlder, needsRecent, unavailable, canDownload, explanation));
        }

        // Exchange rates: the currencies the conversion will need (stocks' currencies and the analysis currency).
        var currency = await AnalysisCurrencyAsync(request, symbols, ct);
        var currencies = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).Select(a => a.Currency).Distinct().ToListAsync(ct);
        var missingRates = new List<MissingRateDto>();
        var foreign = currencies.Where(c => c != currency).ToList();
        if (foreign.Count > 0)
        {
            var supported = fx.SupportedCurrencies(simulated);
            var provider = fx.ProviderFor(simulated);
            foreach (var c in foreign.Append(currency).Distinct().Where(c => c != FxTable.Pivot && !supported.Contains(c) && !provider.DiscontinuedCurrencies.ContainsKey(c)))
                notes.Add($"No {c} reference rates are published; stocks in {c} cannot be converted to {currency}.");
            var gaps = await fx.MissingRatesAsync(foreign.Append(currency), from, to, simulated, ct);
            var coverage = await db.FxRates.AsNoTracking()
                .Where(r => r.Source == provider.Name && gaps.Select(g => g.Currency).Contains(r.Currency))
                .GroupBy(r => r.Currency).Select(g => new { Currency = g.Key, Min = g.Min(r => r.Date), Max = g.Max(r => r.Date) })
                .ToDictionaryAsync(x => x.Currency, ct);
            foreach (var g in gaps.GroupBy(g => g.Currency))
            {
                coverage.TryGetValue(g.Key, out var cov);
                var missingFrom = g.Min(x => x.From);
                var missingTo = g.Max(x => x.To);
                missingRates.Add(new MissingRateDto(g.Key, cov?.Min, cov?.Max, missingFrom, missingTo, true,
                    cov is null ? $"No stored {g.Key} rates." : $"{g.Key} rates stored {cov.Min:yyyy-MM-dd} – {cov.Max:yyyy-MM-dd}; missing {missingFrom:yyyy-MM-dd} – {missingTo:yyyy-MM-dd}."));
            }
        }

        var downloading = running.Count > 0 || fx.IsDownloading(simulated);
        var downloadable = missingPrices.Any(p => p.Downloadable) || missingRates.Count > 0;
        return new DataCheckDto(currency, from, to, missingPrices.Count == 0 && missingRates.Count == 0, downloadable, downloading,
            missingPrices, missingRates, notes);
    }

    /// <summary>Queues price downloads (jobs of at most <see cref="MarketDataService.UserSyncSymbolLimit"/>) and starts the FX download.</summary>
    public async Task<DataDownloadDto> DownloadAsync(DataCheckRequest request, CancellationToken ct)
    {
        var check = await CheckAsync(request, ct);
        var (_, from, to) = Validate(request);
        var settings = await prices.SettingsAsync(ct);
        var running = await RunningSyncSymbolsAsync(settings.ActiveProvider, check.Prices.Select(p => p.Symbol).ToList(), ct);
        var symbols = check.Prices.Where(p => p.Downloadable && !running.Contains(p.Symbol)).Select(p => p.Symbol).ToList();
        var jobs = new List<Guid>();
        // The sync is incremental: it adds older history before the stored range and recent days after it.
        foreach (var chunk in symbols.Chunk(MarketDataService.UserSyncSymbolLimit))
            jobs.Add((await marketData.EnqueueSyncAsync(new SyncRequest { Symbols = [.. chunk], StartDate = from, EndDate = to }, ct)).Id);
        var currencies = check.Rates.Select(r => r.Currency).ToList();
        if (currencies.Count > 0)
            await fx.StartDownloadAsync(currencies, from, to, await fx.ActiveSourceIsSimulatedAsync(ct), ct);
        return new DataDownloadDto(jobs, symbols, currencies);
    }

    private (List<string> Symbols, DateOnly From, DateOnly To) Validate(DataCheckRequest request)
    {
        currentUser.RequireUserId();
        var symbols = request.Symbols.Append(request.BenchmarkSymbol ?? "").Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(Asset.NormalizeSymbol).Distinct().ToList();
        if (symbols.Count == 0) throw new ValidationException("symbols", "Name at least one stock.");
        if (symbols.Count > MaxSymbols) throw new ValidationException("symbols", $"At most {MaxSymbols} symbols per check.");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var to = request.To is { } t && t < today ? t : today;
        if (request.From > to) throw new ValidationException("from", "The start must not be after the end.");
        if (request.From < new DateOnly(1990, 1, 1)) throw new ValidationException("from", "Choose a start from 1990.");
        return (symbols, request.From, to);
    }

    private async Task<string> AnalysisCurrencyAsync(DataCheckRequest request, List<string> symbols, CancellationToken ct)
    {
        var universe = request.Symbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
        var context = await UniverseCurrency.ResolveAsync(db, universe.Count > 0 ? universe : symbols, request.BenchmarkSymbol, request.Currency, ct);
        return context.Currency;
    }

    private async Task<HashSet<string>> RunningSyncSymbolsAsync(string source, List<string> symbols, CancellationToken ct)
    {
        var jobs = await db.SyncJobs.AsNoTracking().Where(j => j.Source == source && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
            .Select(j => j.Symbols).ToListAsync(ct);
        return jobs.SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries)).Where(symbols.Contains).ToHashSet();
    }
}
