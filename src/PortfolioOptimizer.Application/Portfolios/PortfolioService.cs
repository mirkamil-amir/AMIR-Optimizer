using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Portfolios;

/// <summary>Portfolios are private: every query is restricted to the current user's portfolios (others → 404).</summary>
public sealed class PortfolioService(IApplicationDbContext db, PriceQueryService prices, Fx.FxService fx, PortfolioValuationService valuations,
    IModelArtifactStore artifacts, ICurrentUser currentUser, IAuditLog audit, TimeProvider clock)
{
    public const string OverviewMethodology =
        "Each portfolio is valued in its own currency at the latest stored prices (positions in other currencies at the reference rate " +
        "of the price date) plus cash. Profit/loss = total value − net contributions (deposits − withdrawals), so cash moved in or out " +
        "is never counted as a gain or loss. In another currency, today's value is converted at the latest reference rates and every " +
        "deposit and withdrawal at the rate of its own date, so profit/loss includes the currency effect since the money came in. " +
        "The total adds the active portfolios in that currency; it is not a combined return, and it is shown only when every active " +
        "portfolio can be valued and converted.";

    public async Task<IReadOnlyList<PortfolioSummaryDto>> ListAsync(CancellationToken ct) =>
        (await SummariesAsync(currentUser.RequireUserId(), ct)).Select(s => s.Summary).ToList();

    /// <summary>All of the user's portfolios with a total over the active ones in <paramref name="currency"/> (default: the application currency).</summary>
    public async Task<PortfolioOverviewDto> OverviewAsync(string? currency, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var app = await prices.SettingsAsync(ct);
        var target = Currencies.Normalize(currency ?? app.DefaultCurrency);
        if (!(await fx.SupportedCurrenciesAsync(ct)).Contains(target))
            throw new ValidationException("currency", $"{target} is not supported: no exchange rates are published for it.");
        var rows = await SummariesAsync(owner, ct);
        var simulatedRates = await fx.ActiveSourceIsSimulatedAsync(ct);
        var ids = rows.Select(r => r.Portfolio.Id).ToList();
        var flows = (await db.CashLedger.AsNoTracking()
                .Where(c => ids.Contains(c.PortfolioId) && (c.Type == CashEntryType.Deposit || c.Type == CashEntryType.Withdrawal))
                .Select(c => new { c.PortfolioId, c.Amount, c.TimestampUtc }).ToListAsync(ct))
            .ToLookup(c => c.PortfolioId, c => (Date: DateOnly.FromDateTime(c.TimestampUtc), c.Amount));
        var converted = new List<PortfolioSummaryDto>(rows.Count);
        foreach (var (s, p) in rows)
            converted.Add(s with { InCurrency = await InCurrencyAsync(s, p, target, [.. flows[p.Id]], simulatedRates, ct) });

        var active = converted.Where(s => s.Status == "Active").ToList();
        var problems = new List<string>();
        decimal? total = 0, contributions = 0;
        foreach (var s in active)
        {
            var c = s.InCurrency!;
            if (c.TotalValue is null || c.NetContributions is null)
            {
                problems.Add($"“{s.Name}” cannot be shown in {target}: {string.Join(" ", c.Problems)}".Trim());
                total = null;
                continue;
            }
            total += c.TotalValue;
            contributions += c.NetContributions;
        }
        if (total is null) contributions = null;
        return new PortfolioOverviewDto(converted, target, total is null ? null : Math.Round(total.Value, 2),
            contributions is null ? null : Math.Round(contributions.Value, 2), active.Count, rows.Count - active.Count, problems, OverviewMethodology);
    }

    /// <summary>
    /// One portfolio's figures in <paramref name="target"/>: today's value through <see cref="PortfolioValuationService"/> (the
    /// Holdings reporting conversion), each deposit/withdrawal at the reference rate of its own date (carry-forward policy of
    /// <see cref="Fx.FxTable"/>). Missing rates leave the affected amounts null with the reason.
    /// </summary>
    private async Task<PortfolioInCurrencyDto> InCurrencyAsync(PortfolioSummaryDto s, Portfolio p, string target,
        IReadOnlyList<(DateOnly Date, decimal Amount)> flows, bool simulatedRates, CancellationToken ct)
    {
        if (s.BaseCurrency == target)
            return new PortfolioInCurrencyDto(target, s.TotalValue, s.MarketValue, s.CashBalance, s.NetContributions, s.ProfitLoss, s.ValuationProblems ?? []);

        var problems = new List<string>();
        var value = await valuations.ValueAsync(p, target, ct);
        problems.AddRange(value.Problems);
        decimal? cash = value.CashFx is null && p.CashBalance != 0 ? null : value.Cash;

        decimal? contributions = 0;
        if (flows.Count > 0)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            try
            {
                var table = await fx.GetTableAsync([s.BaseCurrency, target], flows.Min(f => f.Date), today, simulatedRates, ct);
                foreach (var (date, amount) in flows)
                {
                    if (table.TryRate(s.BaseCurrency, target, date, out var q, out var reason))
                        contributions += amount * Fx.FxPrecision.Round(q!.Rate);
                    else
                    {
                        problems.Add($"No {s.BaseCurrency}→{target} rate for the contribution of {date:yyyy-MM-dd}: {reason}");
                        contributions = null;
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is Fx.FxRateUnavailableException or ValidationException)
            {
                problems.Add(ex.Message);
                contributions = null;
            }
        }
        var total = value.TotalValue is { } t ? Math.Round(t, 2) : (decimal?)null;
        var net = contributions is { } n ? Math.Round(n, 2) : (decimal?)null;
        return new PortfolioInCurrencyDto(target, total, value.MarketValue is { } m ? Math.Round(m, 2) : null,
            cash is { } c ? Math.Round(c, 2) : null, net, total is { } tv && net is { } nv ? tv - nv : null, problems);
    }

    /// <summary>One valuation per portfolio (no history replay), plus one grouped query each for contributions, runs and activity.</summary>
    private async Task<List<(PortfolioSummaryDto Summary, Portfolio Portfolio)>> SummariesAsync(Guid owner, CancellationToken ct)
    {
        var portfolios = await db.Portfolios.AsNoTracking().Include(p => p.Holdings).Where(p => p.OwnerId == owner)
            .OrderBy(p => p.CreatedAtUtc).ToListAsync(ct);
        var ids = portfolios.Select(p => p.Id).ToList();
        var contributions = await db.CashLedger.AsNoTracking()
            .Where(c => ids.Contains(c.PortfolioId) && (c.Type == CashEntryType.Deposit || c.Type == CashEntryType.Withdrawal))
            .GroupBy(c => c.PortfolioId).Select(g => new { g.Key, Sum = g.Sum(c => c.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var activity = await db.CashLedger.AsNoTracking().Where(c => ids.Contains(c.PortfolioId))
            .GroupBy(c => c.PortfolioId).Select(g => new { g.Key, Last = g.Max(c => c.TimestampUtc) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        var runs = await db.OptimizationRuns.AsNoTracking().Where(r => ids.Contains(r.PortfolioId))
            .GroupBy(r => r.PortfolioId).Select(g => new { g.Key, Last = g.Max(r => r.CreatedAtUtc) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        var result = new List<(PortfolioSummaryDto, Portfolio)>();
        foreach (var p in portfolios)
        {
            var v = await valuations.ValueAsync(p, null, ct);
            var net = contributions.GetValueOrDefault(p.Id);
            var held = v.Positions.Values.Where(x => x.Quantity > 0).ToList();
            result.Add((new PortfolioSummaryDto(p.Id, p.Name, p.BaseCurrency, p.InitialInvestment, p.CashBalance, v.MarketValue, v.TotalValue,
                p.Holdings.Count(h => h.Quantity > 0), p.CreatedAtUtc, p.UpdatedAtUtc, v.Problems, p.Description,
                p.IsArchived ? "Archived" : "Active", p.ArchivedAtUtc, net, v.TotalValue is { } t ? t - net : null,
                held.Select(x => x.Price?.PriceDate).Max(), held.Count(x => x.Price is { IsStale: true }), v.UsesSimulatedData,
                runs.TryGetValue(p.Id, out var r) ? r : null, activity.TryGetValue(p.Id, out var a) ? a : null), p));
        }
        return result;
    }

    public async Task<PortfolioDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, ct));

    public Task<PortfolioDto> CreateAsync(CreatePortfolioRequest request, CancellationToken ct) =>
        CreateForOwnerAsync(currentUser.RequireUserId(), request, ct);

    /// <summary>Creates a portfolio for an explicit owner (used by seeding; HTTP requests use <see cref="CreateAsync"/>).</summary>
    public async Task<PortfolioDto> CreateForOwnerAsync(Guid ownerId, CreatePortfolioRequest request, CancellationToken ct)
    {
        var appSettings = await prices.SettingsAsync(ct);
        // Any currency with exchange rates can be the portfolio (account) currency; it holds the simulated cash and is fixed.
        var baseCurrency = request.BaseCurrency.ToUpperInvariant();
        if (!(await fx.SupportedCurrenciesAsync(ct)).Contains(baseCurrency))
            throw new ValidationException("baseCurrency", $"{baseCurrency} is not supported: no exchange rates are published for it.");

        var settings = request.Settings?.ToDomain() ?? await DefaultSettingsAsync(appSettings, ct);
        if (request.Settings is null && request.Universe is { Count: > 0 } universe)
        {
            var symbols = universe.Select(Asset.NormalizeSymbol).Distinct().ToList();
            // A small working universe needs a feasible cap: n stocks can hold at most 1/n each when fully invested.
            var maxWeight = Math.Max(request.MaxWeight ?? settings.MaxWeight, symbols.Count > 0 ? 1.0 / symbols.Count : 1);
            settings = settings with { Universe = symbols, MaxWeight = Math.Min(1, Math.Round(maxWeight, 4)) };
        }
        else if (request.Settings is null && request.MaxWeight is { } mw) settings = settings with { MaxWeight = mw };
        await ValidateUniverseAsync(settings, request.BaseCurrency, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var portfolio = new Portfolio
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            BaseCurrency = request.BaseCurrency.ToUpperInvariant(),
            InitialInvestment = request.InitialInvestment,
            Settings = settings,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        portfolio.Deposit(request.InitialInvestment, now, "Initial investment (simulated cash)");
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync(ct);
        return ToDto(portfolio);
    }

    public async Task<PortfolioDto> UpdateDetailsAsync(Guid id, UpdatePortfolioDetailsRequest request, CancellationToken ct)
    {
        var portfolio = await LoadAsync(id, ct, tracking: true);
        portfolio.Name = request.Name.Trim();
        portfolio.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        portfolio.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ToDto(portfolio);
    }

    /// <summary>Archives a portfolio: it keeps its full history and stays readable but cannot be changed until restored.</summary>
    public async Task<PortfolioDto> ArchiveAsync(Guid id, CancellationToken ct)
    {
        var portfolio = await LoadAsync(id, ct, tracking: true);
        portfolio.Archive(clock.GetUtcNow().UtcDateTime);
        // Pending proposals of an archived portfolio can no longer be executed.
        await db.TradeProposals.Where(p => p.PortfolioId == id && p.Status == ProposalStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ProposalStatus.Cancelled), ct);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.PortfolioArchived, "Portfolio", id.ToString(), portfolio.Name, ct: ct);
        return ToDto(portfolio);
    }

    public async Task<PortfolioDto> RestoreAsync(Guid id, CancellationToken ct)
    {
        var portfolio = await LoadAsync(id, ct, tracking: true);
        portfolio.Restore(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.PortfolioRestored, "Portfolio", id.ToString(), portfolio.Name, ct: ct);
        return ToDto(portfolio);
    }

    public async Task<PortfolioDto> UpdateAsync(Guid id, UpdatePortfolioRequest request, CancellationToken ct)
    {
        var portfolio = await LoadAsync(id, ct, tracking: true);
        portfolio.EnsureActive();
        var settings = request.Settings.ToDomain();
        await ValidateUniverseAsync(settings, portfolio.BaseCurrency, ct);
        portfolio.Name = request.Name.Trim();
        portfolio.Settings = settings;
        portfolio.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ToDto(portfolio);
    }

    public async Task UpdateSettingsAsync(Guid id, PortfolioSettings settings, CancellationToken ct)
    {
        var portfolio = await LoadAsync(id, ct, tracking: true);
        portfolio.EnsureActive();
        await ValidateUniverseAsync(settings, portfolio.BaseCurrency, ct);
        portfolio.Settings = settings;
        portfolio.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Permanently deletes a portfolio (active or archived) with every record of it: holdings, ledger, transactions, proposals,
    /// Math and ML Optimizations (with their model artifacts), backtests, strategy comparisons and research sessions. Shared
    /// data – instruments, prices, quotes, FX rates – is never touched. The client confirms first; refused (409) while one of
    /// its optimizations, backtests or comparisons is still queued or running.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var portfolio = await LoadAsync(id, ct, tracking: true);
        var experiments = db.MlExperiments.Where(e => e.PortfolioId == id);
        var backtests = db.Backtests.Where(r => r.PortfolioId == id);
        var comparisons = db.StrategyComparisons.Where(c => c.PortfolioId == id);
        var busy = new[] { JobStatus.Queued, JobStatus.Running };
        if (await experiments.AnyAsync(e => busy.Contains(e.Status), ct) || await backtests.AnyAsync(r => busy.Contains(r.Status), ct)
            || await comparisons.AnyAsync(c => busy.Contains(c.Status), ct))
            throw new ConflictException("An optimization, backtest or comparison of this portfolio is still running. Delete it once it has finished.");
        var experimentIds = await experiments.Select(e => e.Id).ToListAsync(ct);
        var sessions = db.ResearchSessions.Where(r => r.PortfolioId == id);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await comparisons.ExecuteDeleteAsync(ct);
        await backtests.ExecuteDeleteAsync(ct);
        // Prediction sets reference model versions (restrict); versions and training events cascade with the experiment.
        await db.MlPredictionSets.Where(p => experimentIds.Contains(p.ExperimentId)).ExecuteDeleteAsync(ct);
        await experiments.ExecuteDeleteAsync(ct);
        await db.ResearchEvents.Where(e => sessions.Select(r => r.Id).Contains(e.SessionId)).ExecuteDeleteAsync(ct);
        await sessions.ExecuteDeleteAsync(ct);
        await db.Orders.Where(o => o.PortfolioId == id).ExecuteDeleteAsync(ct);
        await db.PaperTradeBatches.Where(b => b.PortfolioId == id).ExecuteDeleteAsync(ct);
        await db.TradeProposals.Where(p => p.PortfolioId == id).ExecuteDeleteAsync(ct);
        await db.OptimizationRuns.Where(r => r.PortfolioId == id).ExecuteDeleteAsync(ct);
        db.Portfolios.Remove(portfolio);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        foreach (var experimentId in experimentIds) await artifacts.DeleteExperimentAsync(experimentId, ct);
        await audit.RecordAsync(AuditActions.PortfolioDeleted, "Portfolio", id.ToString(), portfolio.Name, ct: ct);
    }

    /// <summary>Symbols of the portfolio's open positions (owner-scoped), e.g. to refresh their quotes.</summary>
    public async Task<IReadOnlyList<string>> HeldSymbolsAsync(Guid id, CancellationToken ct)
    {
        await EnsureExistsAsync(id, ct);
        return await db.Holdings.AsNoTracking().Where(h => h.PortfolioId == id && h.Quantity > 0).OrderBy(h => h.Symbol)
            .Select(h => h.Symbol).ToListAsync(ct);
    }

    public async Task<PagedResult<TransactionDto>> GetTransactionsAsync(Guid id, int page, int pageSize, string? symbol, CancellationToken ct)
    {
        ValidatePaging(page, pageSize);
        await EnsureExistsAsync(id, ct);
        var q = db.Executions.AsNoTracking().Where(e => e.PortfolioId == id);
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var s = Asset.NormalizeSymbol(symbol);
            q = q.Where(e => e.Symbol == s);
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.ExecutedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<TransactionDto>(items.Select(e => e.ToDto()).ToList(), page, pageSize, total);
    }

    public async Task<PagedResult<CashEntryDto>> GetCashLedgerAsync(Guid id, int page, int pageSize, CancellationToken ct)
    {
        ValidatePaging(page, pageSize);
        await EnsureExistsAsync(id, ct);
        var q = db.CashLedger.AsNoTracking().Where(e => e.PortfolioId == id);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.TimestampUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<CashEntryDto>(
            items.Select(e => new CashEntryDto(e.Id, e.Type, e.Amount, e.BalanceAfter, e.TimestampUtc, e.Description)).ToList(),
            page, pageSize, total);
    }

    public async Task<PagedResult<OrderDto>> GetOrdersAsync(Guid id, int page, int pageSize, CancellationToken ct)
    {
        ValidatePaging(page, pageSize);
        await EnsureExistsAsync(id, ct);
        var q = db.Orders.AsNoTracking().Where(o => o.PortfolioId == id);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(o => o.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<OrderDto>(items.Select(o => o.ToDto()).ToList(), page, pageSize, total);
    }

    /// <summary>Loads one of the current user's portfolios; a portfolio of another user is reported as not found.</summary>
    public async Task<Portfolio> LoadAsync(Guid id, CancellationToken ct, bool tracking = false)
    {
        var owner = currentUser.RequireUserId();
        var q = db.Portfolios.Include(p => p.Holdings).AsQueryable();
        if (!tracking) q = q.AsNoTracking();
        return await q.SingleOrDefaultAsync(p => p.Id == id && p.OwnerId == owner, ct) ?? throw new NotFoundException("Portfolio", id);
    }

    /// <summary>Throws <see cref="NotFoundException"/> unless the portfolio exists and belongs to the current user.</summary>
    public async Task EnsureExistsAsync(Guid id, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        if (!await db.Portfolios.AnyAsync(p => p.Id == id && p.OwnerId == owner, ct)) throw new NotFoundException("Portfolio", id);
    }

    private async Task<PortfolioSettings> DefaultSettingsAsync(ApplicationSettings app, CancellationToken ct)
    {
        var universe = await db.Assets.Where(a => a.IsEnabled && a.Symbol != app.DefaultBenchmark)
            .OrderBy(a => a.Symbol).Select(a => a.Symbol).ToListAsync(ct);
        return new PortfolioSettings { Universe = universe, RiskFreeRate = app.DefaultRiskFreeRate, BenchmarkSymbol = app.DefaultBenchmark };
    }

    private async Task ValidateUniverseAsync(PortfolioSettings settings, string currency, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var symbols = settings.Universe.Concat([settings.BenchmarkSymbol]).Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).ToListAsync(ct);
        var unknown = symbols.Except(assets.Select(a => a.Symbol)).ToList();
        if (unknown.Count > 0) errors["settings.universe"] = [$"Unknown asset(s): {string.Join(", ", unknown)}."];
        // Stocks in other currencies are allowed (converted into the portfolio currency); only currencies without rates are not.
        var supported = await fx.SupportedCurrenciesAsync(ct);
        var unconvertible = assets.Where(a => a.Currency != currency && !supported.Contains(a.Currency)).Select(a => $"{a.Symbol} ({a.Currency})").ToList();
        if (unconvertible.Count > 0)
            errors["settings.universe"] = [.. errors.GetValueOrDefault("settings.universe", []), $"No exchange rates exist to convert these assets into {currency}: {string.Join(", ", unconvertible)}."];
        if (settings.Universe.Count == 0) errors["settings.universe"] = ["The universe must contain at least one asset."];
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private static void ValidatePaging(int page, int pageSize)
    {
        if (page < 1) throw new ValidationException("page", "Page must be ≥ 1.");
        if (pageSize is < 1 or > 500) throw new ValidationException("pageSize", "Page size must be between 1 and 500.");
    }

    private static PortfolioDto ToDto(Portfolio p) =>
        new(p.Id, p.Name, p.BaseCurrency, p.InitialInvestment, p.CashBalance, p.Settings.ToDto(), p.CreatedAtUtc, p.UpdatedAtUtc, p.Version,
            p.Description, p.IsArchived ? "Archived" : "Active", p.ArchivedAtUtc);
}
