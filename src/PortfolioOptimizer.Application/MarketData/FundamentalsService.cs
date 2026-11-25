using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>One metric of a stock: an amount in <see cref="Currency"/> or a fraction, for the period ending <see cref="AsOf"/>; null with the reason otherwise.</summary>
public sealed record FundamentalMetricDto(double? Value, string? Currency, DateOnly? AsOf, string Basis, string? Limitation, int? SpanYears = null);

/// <summary>
/// Reported-figure metrics of one stock for the stock list. <see cref="Status"/>: Available, Pending (being fetched – ask
/// again shortly), NotFound (the source has no figures), Error (the source failed and nothing is stored), NotApplicable
/// (ETFs and indices), NotConfigured (no source enabled). Amounts are in the currency the company reports in, not converted.
/// <see cref="UsesFinancialMeasures"/>: a Financials-sector company (banks, insurers, exchanges), for which the stock list
/// shows EBT/PPOP instead of EBIT/EBITDA and ROE instead of ROIC.
/// </summary>
public sealed record StockFundamentalsDto(
    string Symbol,
    string Status,
    FundamentalMetricDto? MarketCap,
    FundamentalMetricDto? FreeCashFlow,
    FundamentalMetricDto? Roic,
    FundamentalMetricDto? RevenueGrowth,
    FundamentalMetricDto? Ebit,
    FundamentalMetricDto? Ebitda,
    FundamentalMetricDto? PeRatio,
    FundamentalMetricDto? Revenue,
    FundamentalMetricDto? Ebt,
    FundamentalMetricDto? Ppop,
    FundamentalMetricDto? Roe,
    bool UsesFinancialMeasures,
    DateTime? RetrievedAtUtc,
    bool IsSimulated,
    string? Message);

/// <summary>
/// The accounting card of one stock (<c>GET /stocks/{symbol}/financials</c>, AMIR-41): fiscal-year and quarterly statement
/// tables (newest first) and the latest valuation figures, in the company's reporting currency. <see cref="Status"/> as in
/// <see cref="StockFundamentalsDto"/>; the tables are null unless Available.
/// </summary>
public sealed record StockFinancialsDto(
    string Symbol,
    string Status,
    FinancialTable? Annual,
    FinancialTable? Quarterly,
    IReadOnlyList<ValuationFigure> Valuation,
    bool UsesFinancialMeasures,
    DateTime? RetrievedAtUtc,
    bool IsSimulated,
    string? Message);

/// <summary>
/// Serves reported figures (<see cref="FundamentalFact"/>) and the metrics computed from them for a page of the stock list.
/// Reads never wait for the provider: missing or due listings are queued for <see cref="IFundamentalsRefreshScheduler"/>,
/// which calls <see cref="RefreshAsync"/> one listing at a time. Figures are refreshed after a day, "not found" after 7 days,
/// errors after 15 minutes. A refresh never removes figures (history is kept, <see cref="FundamentalsHistory"/>), and a
/// failed one never yields invented ones.
/// </summary>
public sealed class FundamentalsService(
    IApplicationDbContext db,
    IEnumerable<IFundamentalsProvider> providers,
    IFundamentalsRefreshScheduler scheduler,
    TimeProvider clock,
    ILogger<FundamentalsService> logger)
{
    public const int MaxSymbols = 200;
    public static readonly TimeSpan AvailableTtl = TimeSpan.FromDays(1);
    public static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan ErrorRetry = TimeSpan.FromMinutes(15);
    /// <summary>One listing needs several provider requests (one per statement plus valuation).</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(90);

    private IFundamentalsProvider? Provider => providers.FirstOrDefault(p => p.IsConfigured);

    public async Task<IReadOnlyList<StockFundamentalsDto>> GetAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var list = symbols.Select(Asset.NormalizeSymbol).Distinct().ToList();
        if (list.Count > MaxSymbols) throw new ValidationException("symbols", $"At most {MaxSymbols} symbols per request.");
        if (list.Count == 0) return [];
        var assets = await db.Assets.AsNoTracking().Where(a => list.Contains(a.Symbol))
            .Select(a => new { a.Id, a.Symbol, a.AssetType, a.Sector }).ToDictionaryAsync(a => a.Symbol, ct);
        var provider = Provider;
        if (provider is null)
            return list.Where(assets.ContainsKey).Select(s => Empty(s, "NotConfigured", "Company figures are unavailable: no source is enabled.")).ToList();

        var ids = assets.Values.Where(a => a.AssetType == AssetType.Equity).Select(a => a.Id).ToList();
        var states = await db.FundamentalsFetchStates.AsNoTracking().Where(s => ids.Contains(s.AssetId)).ToDictionaryAsync(s => s.AssetId, ct);
        // Only what the list computes from: current values of its concepts, years and trailing values (a listing stores
        // hundreds of concepts, quarters and superseded values).
        var concepts = FundamentalMetrics.Concepts.All;
        var facts = (await db.FundamentalFacts.AsNoTracking()
                .Where(f => ids.Contains(f.AssetId) && f.Source == provider.Name && f.SupersededAtUtc == null
                    && concepts.Contains(f.Concept) && (f.PeriodType == FundamentalMetrics.FiscalYear || f.PeriodType == FundamentalMetrics.Trailing))
                .Select(f => new { f.AssetId, f.Concept, f.PeriodType, f.PeriodEnd, f.Value, f.Currency }).ToListAsync(ct))
            .GroupBy(f => f.AssetId)
            .ToDictionary(g => g.Key, g => g.Select(f => new FactPoint(f.Concept, f.PeriodType, f.PeriodEnd, (double)f.Value, f.Currency)).ToList());

        var due = ids.Where(id => !states.TryGetValue(id, out var st) || st.Source != provider.Name || IsDue(st, provider)).ToList();
        if (due.Count > 0) scheduler.Schedule(due);

        return list.Where(assets.ContainsKey).Select(symbol =>
        {
            var asset = assets[symbol];
            if (asset.AssetType != AssetType.Equity)
                return Empty(symbol, "NotApplicable", "Company figures exist only for individual companies, not for ETFs or indices.");
            states.TryGetValue(asset.Id, out var state);
            if (state is not null && state.Source != provider.Name) state = null; // another source's state: fetch again
            if (facts.TryGetValue(asset.Id, out var own) && own.Count > 0)
            {
                var m = FundamentalMetrics.Compute(own);
                var message = state?.LastError is not { } err ? null
                    : state.Status == FundamentalsFetchStatus.NotFound
                        ? $"Showing figures retrieved on {state.RetrievedAtUtc:yyyy-MM-dd}; the source no longer reports figures for this listing ({err})."
                        : $"Showing figures retrieved on {state.RetrievedAtUtc:yyyy-MM-dd}; the latest refresh failed ({err}).";
                return new StockFundamentalsDto(symbol, "Available", Dto(m.MarketCap), Dto(m.FreeCashFlow), Dto(m.Roic), Dto(m.RevenueGrowth),
                    Dto(m.Ebit), Dto(m.Ebitda), Dto(m.PeRatio), Dto(m.Revenue), Dto(m.Ebt), Dto(m.Ppop), Dto(m.Roe),
                    IsFinancial(asset.Sector), state?.RetrievedAtUtc, provider.IsSimulated, message);
            }
            if (state is null || scheduler.IsQueued(asset.Id) || (state.Status == FundamentalsFetchStatus.Available && IsDue(state, provider)))
                return Empty(symbol, "Pending", "Company figures are being retrieved.", provider.IsSimulated);
            return state.Status == FundamentalsFetchStatus.NotFound
                ? Empty(symbol, "NotFound", $"Company figures are unavailable: {state.LastError ?? "the source has none for this listing."}", provider.IsSimulated)
                : Empty(symbol, "Error", $"The source of company figures could not be reached ({state.LastError ?? "unknown error"}). It will be retried in a few minutes.", provider.IsSimulated);
        }).ToList();
    }

    /// <summary>
    /// The accounting card of one stock: its current figures as statement tables (<see cref="FinancialStatements"/>).
    /// Unknown symbol → <see cref="NotFoundException"/>; never waits for the source (missing or due figures are queued).
    /// </summary>
    public async Task<StockFinancialsDto> GetFinancialsAsync(string symbol, CancellationToken ct)
    {
        symbol = Asset.NormalizeSymbol(symbol);
        var asset = await db.Assets.AsNoTracking().Where(a => a.Symbol == symbol)
            .Select(a => new { a.Id, a.AssetType, a.Sector }).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Stock", symbol);
        StockFinancialsDto Empty(string status, string message, bool simulated = false) =>
            new(symbol, status, null, null, [], false, null, simulated, message);
        if (asset.AssetType != AssetType.Equity)
            return Empty("NotApplicable", "Company figures exist only for individual companies, not for ETFs or indices.");
        var provider = Provider;
        if (provider is null) return Empty("NotConfigured", "Company figures are unavailable: no source is enabled.");

        var state = await db.FundamentalsFetchStates.AsNoTracking().SingleOrDefaultAsync(s => s.AssetId == asset.Id, ct);
        if (state is null || state.Source != provider.Name || IsDue(state, provider)) scheduler.Schedule([asset.Id]);
        if (state is not null && state.Source != provider.Name) state = null;

        var facts = (await db.FundamentalFacts.AsNoTracking()
                .Where(f => f.AssetId == asset.Id && f.Source == provider.Name && f.SupersededAtUtc == null)
                .Select(f => new { f.Concept, f.PeriodType, f.PeriodEnd, f.Value, f.Currency }).ToListAsync(ct))
            .Select(f => new FactPoint(f.Concept, f.PeriodType, f.PeriodEnd, (double)f.Value, f.Currency)).ToList();
        if (facts.Count > 0)
        {
            var financial = IsFinancial(asset.Sector);
            var message = state?.LastError is not { } err ? null
                : state.Status == FundamentalsFetchStatus.NotFound
                    ? $"Showing figures retrieved on {state.RetrievedAtUtc:yyyy-MM-dd}; the source no longer reports figures for this listing ({err})."
                    : $"Showing figures retrieved on {state.RetrievedAtUtc:yyyy-MM-dd}; the latest refresh failed ({err}).";
            return new StockFinancialsDto(symbol, "Available",
                FinancialStatements.Table(facts, FundamentalMetrics.FiscalYear, financial),
                FinancialStatements.Table(facts, FundamentalMetrics.Quarter, financial),
                FinancialStatements.LatestValuation(facts), financial, state?.RetrievedAtUtc, provider.IsSimulated, message);
        }
        if (state is null || scheduler.IsQueued(asset.Id) || (state.Status == FundamentalsFetchStatus.Available && IsDue(state, provider)))
            return Empty("Pending", "Company figures are being retrieved.", provider.IsSimulated);
        return state.Status == FundamentalsFetchStatus.NotFound
            ? Empty("NotFound", $"Company figures are unavailable: {state.LastError ?? "the source has none for this listing."}", provider.IsSimulated)
            : Empty("Error", $"The source of company figures could not be reached ({state.LastError ?? "unknown error"}). It will be retried in a few minutes.", provider.IsSimulated);
    }

    /// <summary>
    /// Queues every individual company whose figures are missing or due behind page requests (the scheduled full refresh),
    /// so the stock list shows figures without waiting. Returns how many were queued.
    /// </summary>
    public async Task<int> ScheduleDueAsync(CancellationToken ct)
    {
        var provider = Provider;
        if (provider is null) return 0;
        var ids = await db.Assets.AsNoTracking().Where(a => a.AssetType == AssetType.Equity).OrderBy(a => a.Symbol).Select(a => a.Id).ToListAsync(ct);
        var states = await db.FundamentalsFetchStates.AsNoTracking().ToDictionaryAsync(s => s.AssetId, ct);
        var due = ids.Where(id => !states.TryGetValue(id, out var st) || st.Source != provider.Name || IsDue(st, provider)).ToList();
        if (due.Count > 0) scheduler.Schedule(due, background: true);
        return due.Count;
    }

    /// <summary>Fetches one listing's figures if they are missing or due (called by the background worker).</summary>
    public async Task RefreshAsync(int assetId, CancellationToken ct)
    {
        var provider = Provider;
        if (provider is null) return;
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null || asset.AssetType != AssetType.Equity) return;
        var state = await db.FundamentalsFetchStates.SingleOrDefaultAsync(s => s.AssetId == assetId, ct);
        if (state is not null && state.Source == provider.Name && !IsDue(state, provider)) return;
        if (state is null)
        {
            state = new FundamentalsFetchState { AssetId = assetId, Source = provider.Name, Status = FundamentalsFetchStatus.Error };
            db.FundamentalsFetchStates.Add(state);
        }
        else if (state.Source != provider.Name)
        {
            state.Source = provider.Name;
            state.Status = FundamentalsFetchStatus.Error;
            state.RetrievedAtUtc = null;
        }
        var now = clock.GetUtcNow().UtcDateTime;
        state.AttemptedAtUtc = now;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);
            var result = await provider.GetAsync(asset.Symbol, timeout.Token);
            if (result.Found && result.Facts.Count > 0)
            {
                // History is kept: stored figures are confirmed, superseded or left as they are – never deleted.
                var current = await db.FundamentalFacts
                    .Where(f => f.AssetId == assetId && f.Source == provider.Name && f.SupersededAtUtc == null).ToListAsync(ct);
                var merge = FundamentalsHistory.Merge(assetId, provider.Name, current, result.Facts, now);
                if (merge.Superseded > 0)
                {
                    // Close the superseded rows first: the new current rows share their period (one current row per period).
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    await db.SaveChangesAsync(ct);
                    db.FundamentalFacts.AddRange(merge.Added);
                    SetAvailable(state, now);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    logger.LogInformation("Fundamentals for {Symbol}: {Superseded} values changed by the source.", asset.Symbol, merge.Superseded);
                    return;
                }
                db.FundamentalFacts.AddRange(merge.Added);
                SetAvailable(state, now);
            }
            else
            {
                // Stored figures stay; they are shown with a note that the source no longer reports any.
                var hasFigures = await db.FundamentalFacts.AnyAsync(f => f.AssetId == assetId && f.Source == provider.Name, ct);
                state.Status = FundamentalsFetchStatus.NotFound;
                if (!hasFigures) state.RetrievedAtUtc = null;
                state.LastError = Truncate(result.NotFoundReason ?? "the source has no figures for this listing.", 300);
            }
        }
        catch (Exception ex) when (ex is FundamentalsProviderException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var message = ex is OperationCanceledException ? "The source did not answer in time." : ex.Message;
            logger.LogWarning("Fundamentals for {Symbol} failed: {Message}", asset.Symbol, message);
            state.LastError = Truncate(message, 300);
            // Earlier figures stay (served with a note); otherwise record the error.
            if (state.Status != FundamentalsFetchStatus.Available) state.Status = FundamentalsFetchStatus.Error;
        }
        await db.SaveChangesAsync(ct);
    }

    private static void SetAvailable(FundamentalsFetchState state, DateTime now)
    {
        state.Status = FundamentalsFetchStatus.Available;
        state.RetrievedAtUtc = now;
        state.LastError = null;
    }

    /// <summary>Banks, insurers and other financials: EBIT/EBITDA and ROIC are not meaningful for them.</summary>
    public static bool IsFinancial(string? sector) => string.Equals(sector, "Financials", StringComparison.OrdinalIgnoreCase);

    private bool IsDue(FundamentalsFetchState s, IFundamentalsProvider provider)
    {
        // Fetched before the provider added figures: fetch once more (ConceptsChangedAtUtc lies in the past).
        if (s.AttemptedAtUtc < provider.ConceptsChangedAtUtc) return true;
        var age = clock.GetUtcNow().UtcDateTime - s.AttemptedAtUtc;
        if (s.LastError is not null && s.Status != FundamentalsFetchStatus.NotFound) return age >= ErrorRetry;
        return s.Status switch
        {
            FundamentalsFetchStatus.Available => age >= AvailableTtl,
            FundamentalsFetchStatus.NotFound => age >= NotFoundTtl,
            _ => age >= ErrorRetry,
        };
    }

    private static FundamentalMetricDto Dto(FundamentalMetric m) => new(m.Value, m.Currency, m.AsOf, m.Basis, m.Limitation, m.SpanYears);

    private static StockFundamentalsDto Empty(string symbol, string status, string message, bool simulated = false) =>
        new(symbol, status, null, null, null, null, null, null, null, null, null, null, null, false, null, simulated, message);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
