using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Abstractions;

/// <summary>Persistence boundary used by application services (implemented with EF Core in Infrastructure).</summary>
public interface IApplicationDbContext
{
    DbSet<Asset> Assets { get; }
    DbSet<HistoricalPrice> HistoricalPrices { get; }
    DbSet<LatestQuote> LatestQuotes { get; }
    DbSet<MarketIndex> MarketIndices { get; }
    DbSet<IndexMembership> IndexMemberships { get; }
    DbSet<QuoteRefreshRun> QuoteRefreshRuns { get; }
    DbSet<MarketDataSyncJob> SyncJobs { get; }
    DbSet<DataSyncLog> SyncLogs { get; }
    DbSet<ApplicationSettings> Settings { get; }
    DbSet<Portfolio> Portfolios { get; }
    DbSet<Holding> Holdings { get; }
    DbSet<CashLedgerEntry> CashLedger { get; }
    DbSet<TradeExecution> Executions { get; }
    DbSet<OptimizationRun> OptimizationRuns { get; }
    DbSet<TradeProposal> TradeProposals { get; }
    DbSet<PaperTradeBatch> PaperTradeBatches { get; }
    DbSet<SimulatedOrder> Orders { get; }
    DbSet<BacktestRun> Backtests { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<MlExperiment> MlExperiments { get; }
    DbSet<MlModelVersion> MlModelVersions { get; }
    DbSet<MlPredictionSet> MlPredictionSets { get; }
    DbSet<StrategyComparison> StrategyComparisons { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>True when the save failed because a unique index/constraint was violated (provider-specific error code).</summary>
    bool IsUniqueConstraintViolation(DbUpdateException exception);
}
