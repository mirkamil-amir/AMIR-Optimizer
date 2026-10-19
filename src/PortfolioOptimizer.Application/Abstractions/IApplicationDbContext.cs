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
    DbSet<FxRate> FxRates { get; }
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
    DbSet<PortfolioTransaction> PortfolioTransactions { get; }
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
    DbSet<WatchlistEntry> WatchlistEntries { get; }
    DbSet<CompanyProfile> CompanyProfiles { get; }
    DbSet<ResearchSession> ResearchSessions { get; }
    DbSet<ResearchEvent> ResearchEvents { get; }
    DbSet<NewsArticle> NewsArticles { get; }
    DbSet<NewsArticleAsset> NewsArticleAssets { get; }
    DbSet<NewsFetchState> NewsFetchStates { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>True when the save failed because a unique index/constraint was violated (provider-specific error code).</summary>
    bool IsUniqueConstraintViolation(DbUpdateException exception);

    /// <summary>
    /// The latest <paramref name="count"/> daily prices of each asset from <paramref name="source"/> within [from, to], newest first,
    /// in one statement that uses the (AssetId, Source, Date) index per asset. (A LINQ "take N per group" becomes a window function
    /// over every price of the source – 25.8 ms instead of 0.4 ms on 55k prices.)
    /// </summary>
    IQueryable<HistoricalPrice> LatestPricesPerAsset(IReadOnlyCollection<int> assetIds, string source, int count, DateOnly from, DateOnly to);

    /// <summary>Inserts or updates FX observations (keyed by source, currency, date) without using the change tracker.</summary>
    Task UpsertFxRatesAsync(IReadOnlyCollection<FxRate> rates, CancellationToken ct);
}
