using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.Infrastructure.Identity;

/// <summary>What a purge found (dry run) or removed (applied).</summary>
public sealed record TestDataPurgeReport(
    string Environment,
    string Database,
    bool Applied,
    IReadOnlyList<string> Accounts,
    int Portfolios,
    int OptimizationRuns,
    int Backtests,
    int MlExperiments,
    int StrategyComparisons,
    int AuditEvents)
{
    public bool IsEmpty => Accounts.Count == 0 && AuditEvents == 0;
}

/// <summary>
/// Removes automated-test artifacts: accounts under the reserved <c>.test</c> domain (<see cref="TestDataPolicy"/>),
/// everything they own, and audit events they performed or that target them. Genuine accounts and their data are never
/// touched. Runs in one transaction; refuses to run against a Production environment unless explicitly allowed.
/// </summary>
public sealed class TestDataPurger(AppDbContext db, IModelArtifactStore artifacts, IHostEnvironment environment, IAuditLog audit,
    ILogger<TestDataPurger> logger)
{
    public async Task<TestDataPurgeReport> PurgeAsync(bool apply, bool allowProduction, CancellationToken ct)
    {
        if (environment.IsProduction() && !allowProduction)
            throw new InvalidOperationException(
                "Refusing to purge test data in the Production environment. Re-run with --allow-production after verifying the target database.");

        var database = db.Database.GetDbConnection().Database;
        // Load candidates and classify in memory with the single policy (no SQL re-implementation of the rule).
        var candidates = await db.Users.AsNoTracking().Where(u => u.Email != null && u.Email.Contains(".test"))
            .Select(u => new { u.Id, u.Email }).ToListAsync(ct);
        var users = candidates.Where(u => TestDataPolicy.IsTestEmail(u.Email)).ToList();
        var ids = users.Select(u => u.Id).ToList();
        var idStrings = ids.Select(i => i.ToString()).ToList();
        var emails = users.Select(u => u.Email!).ToList();

        var portfolioIds = await db.Portfolios.Where(p => ids.Contains(p.OwnerId)).Select(p => p.Id).ToListAsync(ct);
        var experimentIds = await db.MlExperiments.Where(e => ids.Contains(e.OwnerId)).Select(e => e.Id).ToListAsync(ct);
        var auditQuery = db.AuditEvents.Where(a =>
            (a.ActorUserId != null && ids.Contains(a.ActorUserId.Value)) ||
            (a.TargetId != null && idStrings.Contains(a.TargetId)) ||
            (a.ActorEmail != null && emails.Contains(a.ActorEmail)) ||
            (a.ActorEmail != null && a.ActorEmail.EndsWith(TestDataPolicy.ReservedTestDomain)));

        var report = new TestDataPurgeReport(
            environment.EnvironmentName,
            database,
            apply,
            emails.OrderBy(e => e).ToList(),
            portfolioIds.Count,
            await db.OptimizationRuns.CountAsync(r => ids.Contains(r.OwnerId), ct),
            await db.Backtests.CountAsync(b => ids.Contains(b.OwnerId), ct),
            experimentIds.Count,
            await db.StrategyComparisons.CountAsync(c => ids.Contains(c.OwnerId), ct),
            await auditQuery.CountAsync(ct));
        if (!apply || report.IsEmpty) return report;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Children before parents where foreign keys restrict deletes.
            await db.StrategyComparisons.Where(c => ids.Contains(c.OwnerId)).ExecuteDeleteAsync(ct);
            await db.MlPredictionSets.Where(p => experimentIds.Contains(p.ExperimentId)).ExecuteDeleteAsync(ct);
            await db.MlExperiments.Where(e => experimentIds.Contains(e.Id)).ExecuteDeleteAsync(ct);
            await db.Backtests.Where(b => ids.Contains(b.OwnerId)).ExecuteDeleteAsync(ct);
            await db.PaperTradeBatches.Where(b => portfolioIds.Contains(b.PortfolioId)).ExecuteDeleteAsync(ct);
            await db.TradeProposals.Where(p => portfolioIds.Contains(p.PortfolioId)).ExecuteDeleteAsync(ct);
            await db.OptimizationRuns.Where(r => ids.Contains(r.OwnerId)).ExecuteDeleteAsync(ct);
            await db.Portfolios.Where(p => portfolioIds.Contains(p.Id)).ExecuteDeleteAsync(ct);
            await auditQuery.ExecuteDeleteAsync(ct);
            await db.Users.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync(ct); // roles, claims, logins, tokens cascade
            await tx.CommitAsync(ct);
        }
        foreach (var id in experimentIds) await artifacts.DeleteExperimentAsync(id, ct);

        var summary = $"Removed {report.Accounts.Count} test account(s), {report.Portfolios} portfolio(s), {report.OptimizationRuns} optimization run(s), " +
                      $"{report.Backtests} backtest(s), {report.MlExperiments} ML experiment(s), {report.StrategyComparisons} comparison(s), " +
                      $"{report.AuditEvents} audit event(s) from database '{database}' ({environment.EnvironmentName}).";
        logger.LogWarning("Test data purge: {Summary}", summary);
        await audit.RecordForAsync(null, "system:purge-test-data", AuditActions.TestDataPurged, "Database", database, summary, ct: ct);
        return report;
    }
}
