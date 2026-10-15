using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Portfolios;

/// <summary>Portfolios are private: every query is restricted to the current user's portfolios (others → 404).</summary>
public sealed class PortfolioService(IApplicationDbContext db, PriceQueryService prices, Fx.FxService fx, PortfolioValuationService valuations,
    ICurrentUser currentUser, IAuditLog audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<PortfolioSummaryDto>> ListAsync(CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var portfolios = await db.Portfolios.AsNoTracking().Include(p => p.Holdings).Where(p => p.OwnerId == owner)
            .OrderBy(p => p.CreatedAtUtc).ToListAsync(ct);
        var result = new List<PortfolioSummaryDto>();
        foreach (var p in portfolios)
        {
            var v = await valuations.ValueAsync(p, null, ct);
            result.Add(new PortfolioSummaryDto(p.Id, p.Name, p.BaseCurrency, p.InitialInvestment, p.CashBalance, v.MarketValue, v.TotalValue,
                p.Holdings.Count(h => h.Quantity > 0), p.CreatedAtUtc, p.UpdatedAtUtc, v.Problems));
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
        await ValidateUniverseAsync(settings, request.BaseCurrency, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var portfolio = new Portfolio
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = request.Name.Trim(),
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

    public async Task<PortfolioDto> UpdateAsync(Guid id, UpdatePortfolioRequest request, CancellationToken ct)
    {
        var portfolio = await LoadAsync(id, ct, tracking: true);
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
        await ValidateUniverseAsync(settings, portfolio.BaseCurrency, ct);
        portfolio.Settings = settings;
        portfolio.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a portfolio and its entire simulated history (runs, proposals, ledger).</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var portfolio = await LoadAsync(id, ct, tracking: true);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Orders.Where(o => o.PortfolioId == id).ExecuteDeleteAsync(ct);
        await db.PaperTradeBatches.Where(b => b.PortfolioId == id).ExecuteDeleteAsync(ct);
        await db.TradeProposals.Where(p => p.PortfolioId == id).ExecuteDeleteAsync(ct);
        await db.OptimizationRuns.Where(r => r.PortfolioId == id).ExecuteDeleteAsync(ct);
        db.Portfolios.Remove(portfolio);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await audit.RecordAsync(AuditActions.PortfolioDeleted, "Portfolio", id.ToString(), portfolio.Name, ct: ct);
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
        new(p.Id, p.Name, p.BaseCurrency, p.InitialInvestment, p.CashBalance, p.Settings.ToDto(), p.CreatedAtUtc, p.UpdatedAtUtc, p.Version);
}
