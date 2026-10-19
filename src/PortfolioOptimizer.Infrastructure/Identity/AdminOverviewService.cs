using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.MarketData;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.Infrastructure.Identity;

/// <summary>
/// Administrator dashboard data. Analysis activity counts only genuine accounts: owners under the reserved test domain
/// (<see cref="TestDataPolicy"/>) are excluded so automated test runs can never inflate what administrators see.
/// Exposes metadata (what ran, when, by whom) but no analysis results, and never any credential or API key.
/// </summary>
public sealed class AdminOverviewService(
    AppDbContext db,
    IMarketDataProviderRegistry providers,
    AlphaVantageQuota alphaVantageQuota,
    QuoteRefreshService quoteRefresh,
    PriceQueryService prices,
    TimeProvider clock) : IAdminOverviewService
{
    public async Task<AdminOverviewDto> GetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email, u.DisplayName, u.Status }).ToListAsync(ct);
        var testIds = users.Where(u => TestDataPolicy.IsTestEmail(u.Email)).Select(u => u.Id).ToHashSet();
        var genuine = users.Where(u => !testIds.Contains(u.Id)).ToList();
        var adminRole = await db.Roles.AsNoTracking().Where(r => r.Name == Roles.Administrator).Select(r => r.Id).SingleOrDefaultAsync(ct);
        var admins = await db.UserRoles.AsNoTracking().CountAsync(r => r.RoleId == adminRole, ct);
        var accounts = new AccountCountsDto(
            genuine.Count(u => u.Status == UserAccountStatus.Pending), genuine.Count(u => u.Status == UserAccountStatus.Approved),
            genuine.Count(u => u.Status == UserAccountStatus.Rejected), genuine.Count(u => u.Status == UserAccountStatus.Suspended),
            admins, testIds.Count);

        var test = testIds.ToList();
        var since = now.AddDays(-30);
        var activity = new ActivityCountsDto(
            await db.OptimizationRuns.CountAsync(r => !test.Contains(r.OwnerId), ct),
            await db.OptimizationRuns.CountAsync(r => !test.Contains(r.OwnerId) && r.CreatedAtUtc >= since, ct),
            await db.MlExperiments.CountAsync(e => !test.Contains(e.OwnerId), ct),
            await db.MlExperiments.CountAsync(e => !test.Contains(e.OwnerId) && e.Status == JobStatus.Failed, ct),
            await db.MlExperiments.CountAsync(e => !test.Contains(e.OwnerId) && (e.Status == JobStatus.Queued || e.Status == JobStatus.Running), ct),
            await db.StrategyComparisons.CountAsync(c => !test.Contains(c.OwnerId), ct),
            await db.Backtests.CountAsync(b => !test.Contains(b.OwnerId), ct),
            await db.Portfolios.CountAsync(p => !test.Contains(p.OwnerId), ct));

        var names = genuine.ToDictionary(u => u.Id, u => (Name: u.DisplayName, Email: u.Email ?? ""));
        var recent = new List<(string Kind, Guid Id, string Name, string Status, Guid Owner, DateTime Created)>();
        recent.AddRange((await db.OptimizationRuns.AsNoTracking().Where(r => !test.Contains(r.OwnerId)).OrderByDescending(r => r.CreatedAtUtc).Take(10)
            .Select(r => new { r.Id, r.Objective, r.Status, r.OwnerId, r.CreatedAtUtc }).ToListAsync(ct))
            .Select(r => ("Optimization", r.Id, r.Objective.ToString(), r.Status.ToString(), r.OwnerId, r.CreatedAtUtc)));
        recent.AddRange((await db.MlExperiments.AsNoTracking().Where(e => !test.Contains(e.OwnerId)).OrderByDescending(e => e.CreatedAtUtc).Take(10)
            .Select(e => new { e.Id, e.Name, e.Status, e.OwnerId, e.CreatedAtUtc }).ToListAsync(ct))
            .Select(e => ("ML experiment", e.Id, e.Name, e.Status.ToString(), e.OwnerId, e.CreatedAtUtc)));
        recent.AddRange((await db.StrategyComparisons.AsNoTracking().Where(c => !test.Contains(c.OwnerId)).OrderByDescending(c => c.CreatedAtUtc).Take(10)
            .Select(c => new { c.Id, c.Name, c.Status, c.OwnerId, c.CreatedAtUtc }).ToListAsync(ct))
            .Select(c => ("Comparison", c.Id, c.Name, c.Status.ToString(), c.OwnerId, c.CreatedAtUtc)));
        var recentRuns = recent.OrderByDescending(r => r.Created).Take(15)
            .Select(r => new RecentRunDto(r.Kind, r.Id, r.Name, r.Status, names.GetValueOrDefault(r.Owner).Name ?? "?", names.GetValueOrDefault(r.Owner).Email ?? "", r.Created))
            .ToList();

        return new AdminOverviewDto(accounts, activity, recentRuns, await MarketDataAsync(now, ct), now);
    }

    private async Task<MarketDataHealthDto> MarketDataAsync(DateTime now, CancellationToken ct)
    {
        var active = (await db.Settings.AsNoTracking().SingleAsync(ct)).ActiveProvider;
        var providerHealth = providers.Providers.Select(p => new ProviderHealthDto(p.Name, p.DisplayName, p.IsConfigured, p.RequiresApiKey, p.IsSimulated, p.RateLimit,
            p.Name == AlphaVantageProvider.Name && p.IsConfigured ? alphaVantageQuota.Remaining : null)).ToList();

        var today = now.Date;
        // Counted in memory from set queries: per-member EXISTS subqueries can degrade to a table scan per member.
        var withPrices = await prices.AssetIdsWithPricesAsync(active, ct);
        var quotes = await db.LatestQuotes.AsNoTracking().Where(q => q.Source == active).Select(q => new { q.AssetId, q.RetrievedAtUtc }).ToListAsync(ct);
        var withQuotes = quotes.Select(q => q.AssetId).ToHashSet();
        var quotedToday = quotes.Where(q => q.RetrievedAtUtc >= today).Select(q => q.AssetId).ToHashSet();
        var members = (await db.IndexMemberships.AsNoTracking().Select(m => new { m.IndexCode, m.AssetId }).ToListAsync(ct)).ToLookup(m => m.IndexCode, m => m.AssetId);
        var indices = (await db.MarketIndices.AsNoTracking().OrderBy(i => i.IsHistorical).ThenBy(i => i.Name).ToListAsync(ct))
            .Select(i => new IndexHealthDto(i.Code, i.Name, members[i.Code].Count(),
                members[i.Code].Count(withQuotes.Contains), members[i.Code].Count(quotedToday.Contains), members[i.Code].Count(withPrices.Contains),
                i.VerifiedOn))
            .ToList();
        var lastSync = await db.SyncJobs.AsNoTracking().OrderByDescending(j => j.RequestedAtUtc)
            .Select(j => new SyncJobSummaryDto(j.Id, j.Status.ToString(), j.RequestedAtUtc, j.CompletedAtUtc, j.AssetsTotal, j.AssetsFailed, j.Message))
            .FirstOrDefaultAsync(ct);
        return new MarketDataHealthDto(active, providerHealth, indices, await quoteRefresh.GetRecentCyclesAsync(5, ct), lastSync);
    }
}
