using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Identity;

namespace PortfolioOptimizer.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IApplicationDbContext, IDataProtectionKeyContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<HistoricalPrice> HistoricalPrices => Set<HistoricalPrice>();
    public DbSet<LatestQuote> LatestQuotes => Set<LatestQuote>();
    public DbSet<FxRate> FxRates => Set<FxRate>();
    public DbSet<MarketIndex> MarketIndices => Set<MarketIndex>();
    public DbSet<IndexMembership> IndexMemberships => Set<IndexMembership>();
    public DbSet<QuoteRefreshRun> QuoteRefreshRuns => Set<QuoteRefreshRun>();
    public DbSet<MarketDataSyncJob> SyncJobs => Set<MarketDataSyncJob>();
    public DbSet<DataSyncLog> SyncLogs => Set<DataSyncLog>();
    public DbSet<ApplicationSettings> Settings => Set<ApplicationSettings>();
    public DbSet<Portfolio> Portfolios => Set<Portfolio>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<CashLedgerEntry> CashLedger => Set<CashLedgerEntry>();
    public DbSet<TradeExecution> Executions => Set<TradeExecution>();
    public DbSet<PortfolioTransaction> PortfolioTransactions => Set<PortfolioTransaction>();
    public DbSet<OptimizationRun> OptimizationRuns => Set<OptimizationRun>();
    public DbSet<TradeProposal> TradeProposals => Set<TradeProposal>();
    public DbSet<PaperTradeBatch> PaperTradeBatches => Set<PaperTradeBatch>();
    public DbSet<SimulatedOrder> Orders => Set<SimulatedOrder>();
    public DbSet<BacktestRun> Backtests => Set<BacktestRun>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<MlExperiment> MlExperiments => Set<MlExperiment>();
    public DbSet<MlModelVersion> MlModelVersions => Set<MlModelVersion>();
    public DbSet<MlPredictionSet> MlPredictionSets => Set<MlPredictionSet>();
    public DbSet<StrategyComparison> StrategyComparisons => Set<StrategyComparison>();
    public DbSet<WatchlistEntry> WatchlistEntries => Set<WatchlistEntry>();
    public DbSet<CompanyProfile> CompanyProfiles => Set<CompanyProfile>();
    public DbSet<ResearchSession> ResearchSessions => Set<ResearchSession>();
    public DbSet<ResearchEvent> ResearchEvents => Set<ResearchEvent>();
    public DbSet<NewsArticle> NewsArticles => Set<NewsArticle>();
    public DbSet<NewsArticleAsset> NewsArticleAssets => Set<NewsArticleAsset>();
    public DbSet<NewsFetchState> NewsFetchStates => Set<NewsFetchState>();

    /// <summary>ASP.NET Core data-protection keys (cookie and antiforgery encryption), shared across restarts and instances.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(18, 6);
        builder.Properties<decimal?>().HavePrecision(18, 6);
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b); // Identity tables

        b.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.StatusReason).HasMaxLength(500);
            e.HasIndex(x => x.Status);
        });

        b.Entity<MlExperiment>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProgressMessage).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.ModelFamily).HasMaxLength(40);
            e.Property(x => x.Target).HasMaxLength(40);
            e.Property(x => x.FeatureSetVersion).HasMaxLength(40);
            e.Property(x => x.BenchmarkSymbol).HasMaxLength(20);
            e.Property(x => x.PriceSource).HasMaxLength(40);
            e.Property(x => x.DataSnapshotHash).HasMaxLength(64);
            e.Property(x => x.ConfigJson).HasColumnType("jsonb");
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.Property(x => x.BacktestJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OwnerId, x.CreatedAtUtc });
            e.HasIndex(x => x.Status);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.ModelVersions).WithOne().HasForeignKey(x => x.ExperimentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MlModelVersion>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.ModelFamily).HasMaxLength(40);
            e.Property(x => x.FeatureSetVersion).HasMaxLength(40);
            e.Property(x => x.ArtifactPath).HasMaxLength(300);
            e.Property(x => x.ArtifactSha256).HasMaxLength(64);
            e.Property(x => x.HyperparametersJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ExperimentId, x.Version }).IsUnique();
        });

        b.Entity<MlPredictionSet>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.IntervalMethod).HasMaxLength(500);
            e.Property(x => x.PredictionsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ExperimentId, x.CreatedAtUtc });
            e.HasOne<MlExperiment>().WithMany().HasForeignKey(x => x.ExperimentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MlModelVersion>().WithMany().HasForeignKey(x => x.ModelVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StrategyComparison>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProgressMessage).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.BenchmarkSymbol).HasMaxLength(20);
            e.Property(x => x.PriceSource).HasMaxLength(40);
            e.Property(x => x.DataSnapshotHash).HasMaxLength(64);
            e.Property(x => x.ConfigJson).HasColumnType("jsonb");
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OwnerId, x.CreatedAtUtc });
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            // References are kept as long as the sources exist; the comparison stores its own frozen copy of their settings.
            e.HasOne<OptimizationRun>().WithMany().HasForeignKey(x => x.OptimizationRunId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<MlExperiment>().WithMany().HasForeignKey(x => x.MlExperimentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<MlModelVersion>().WithMany().HasForeignKey(x => x.MlModelVersionId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<WatchlistEntry>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            // One entry per user and listing; the index also serves the owner-scoped list query.
            e.HasIndex(x => new { x.OwnerId, x.AssetId }).IsUnique();
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            // Catalogue assets referenced by a watchlist cannot be deleted silently.
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ResearchSession>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(160);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.BriefJson).HasColumnType("jsonb");
            e.Property(x => x.CandidatesJson).HasColumnType("jsonb");
            e.Property(x => x.HypothesesJson).HasColumnType("jsonb");
            e.Property(x => x.PendingRefinementJson).HasColumnType("jsonb");
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.OwnerId, x.UpdatedAtUtc });
            // PortfolioService.DeleteAsync removes the portfolio's research first; SET NULL only guards other deletions.
            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.SetNull);
            // Private research of the user: removed with the account.
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ResearchEvent>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Summary).HasMaxLength(500);
            e.HasIndex(x => new { x.SessionId, x.Id });
        });

        b.Entity<CompanyProfile>(e =>
        {
            e.HasKey(x => x.AssetId);
            e.Property(x => x.Reference).HasMaxLength(300);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.SourceName).HasMaxLength(100);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.Licence).HasMaxLength(100);
            e.Property(x => x.Language).HasMaxLength(8);
            e.Property(x => x.LastError).HasMaxLength(300);
            // Shared reference data: removing an asset removes its cached description.
            e.HasOne(x => x.Asset).WithOne().HasForeignKey<CompanyProfile>(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NewsArticle>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.ProviderArticleId).HasMaxLength(200);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.HeadlineKey).HasMaxLength(64);
            e.Property(x => x.Headline).HasMaxLength(500);
            e.Property(x => x.Publisher).HasMaxLength(200);
            e.Property(x => x.Summary).HasMaxLength(400);
            e.Property(x => x.ImageUrl).HasMaxLength(2000);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.CategoryBasis).HasMaxLength(200);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Region).HasMaxLength(8);
            e.HasIndex(x => x.Fingerprint).IsUnique();
            e.HasIndex(x => x.HeadlineKey);
            e.HasIndex(x => x.PublishedAtUtc);
            e.HasMany(x => x.Assets).WithOne().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NewsArticleAsset>(e =>
        {
            e.HasKey(x => new { x.ArticleId, x.AssetId });
            e.Property(x => x.MatchMethod).HasMaxLength(60);
            e.HasIndex(x => x.AssetId);
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NewsFetchState>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(120);
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.LastError).HasMaxLength(300);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.ActorEmail).HasMaxLength(256);
            e.Property(x => x.TargetType).HasMaxLength(64);
            e.Property(x => x.TargetId).HasMaxLength(64);
            e.Property(x => x.Details).HasMaxLength(2000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.ActorUserId, x.TimestampUtc });
            e.HasIndex(x => x.Action);
        });
        b.Entity<Asset>(e =>
        {
            e.HasIndex(x => x.Symbol).IsUnique();
            e.Property(x => x.Symbol).HasMaxLength(20);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.AssetType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Exchange).HasMaxLength(50);
            e.Property(x => x.Sector).HasMaxLength(100);
            e.Property(x => x.Industry).HasMaxLength(150);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.ReferenceArticle).HasMaxLength(300);
            e.Property(x => x.MetadataSource).HasMaxLength(300);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.Mic).HasMaxLength(4);
            e.Property(x => x.Isin).HasMaxLength(12);
            e.HasIndex(x => x.Sector);
            e.HasIndex(x => x.Country);
        });

        b.Entity<MarketIndex>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.BenchmarkSymbol).HasMaxLength(20);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Source).HasMaxLength(300);
            e.Property(x => x.SourceUrl).HasMaxLength(500);
            e.Property(x => x.SourceRevision).HasMaxLength(50);
            e.Property(x => x.Licence).HasMaxLength(100);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.IndexCode).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IndexMembership>(e =>
        {
            e.HasKey(x => new { x.IndexCode, x.AssetId });
            e.HasOne(x => x.Asset).WithMany(a => a.Memberships).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AssetId);
        });

        b.Entity<QuoteRefreshRun>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Trigger).HasMaxLength(20);
            e.Property(x => x.Source).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.HasIndex(x => x.RequestedAtUtc);
        });

        b.Entity<HistoricalPrice>(e =>
        {
            e.HasIndex(x => new { x.AssetId, x.Source, x.Date }).IsUnique();
            e.Property(x => x.Source).HasMaxLength(40);
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FxRate>(e =>
        {
            e.HasKey(x => new { x.Source, x.Currency, x.Date });
            e.Property(x => x.Source).HasMaxLength(40);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.UnitsPerEur).HasPrecision(20, 8);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_FxRates_Positive", "\"UnitsPerEur\" > 0");
                t.HasCheckConstraint("CK_FxRates_NotEur", "\"Currency\" <> 'EUR'");
            });
        });

        // Currency columns added with multi-currency support: ISO 4217 codes; FX source names.
        b.Entity<OptimizationRun>(e => { e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.FxSource).HasMaxLength(40); });
        b.Entity<BacktestRun>(e => { e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.FxSource).HasMaxLength(40); });
        b.Entity<StrategyComparison>(e => { e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.FxSource).HasMaxLength(40); });
        b.Entity<MlExperiment>(e => { e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.FxSource).HasMaxLength(40); });
        // Optional portfolio link of ML experiments, backtests and comparisons: PortfolioService.DeleteAsync removes them with the portfolio.
        b.Entity<MlExperiment>(e =>
        {
            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.OwnerId, x.PortfolioId });
        });
        b.Entity<BacktestRun>(e =>
        {
            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.OwnerId, x.PortfolioId });
        });
        b.Entity<StrategyComparison>(e =>
        {
            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.OwnerId, x.PortfolioId });
        });
        b.Entity<TradeProposalLine>(e => { e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.FxRate).HasPrecision(20, 10); });
        b.Entity<SimulatedOrder>(e => { e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.FxRate).HasPrecision(20, 10); });
        b.Entity<PortfolioTransaction>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.FxSource).HasMaxLength(40);
            e.Property(x => x.FxRate).HasPrecision(20, 10);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.Ignore(x => x.IsTrade);
            // A repeated submission with the same key cannot book twice, even under concurrency.
            e.HasIndex(x => new { x.PortfolioId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.PortfolioId, x.BookedAtUtc });
            e.HasIndex(x => x.ReversesId).IsUnique().HasFilter("\"ReversesId\" IS NOT NULL");
            // Part of the portfolio's history: removed only when the (archived) portfolio itself is deleted.
            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_PortfolioTransactions_FxRate", "\"FxRate\" > 0");
                t.HasCheckConstraint("CK_PortfolioTransactions_Fees", "\"Fees\" >= 0");
            });
        });

        b.Entity<TradeExecution>(e =>
        {
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.FxSource).HasMaxLength(40);
            e.Property(x => x.FxRate).HasPrecision(20, 10);
            e.ToTable(t => t.HasCheckConstraint("CK_TradeExecutions_FxRate", "\"FxRate\" > 0"));
        });

        b.Entity<LatestQuote>(e =>
        {
            e.HasKey(x => new { x.AssetId, x.Source });
            e.Property(x => x.Source).HasMaxLength(40);
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MarketDataSyncJob>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.RequestedAtUtc);
            e.HasMany(x => x.Logs).WithOne().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DataSyncLog>(e =>
        {
            e.HasIndex(x => new { x.Source, x.AssetId, x.CompletedAtUtc });
        });

        b.Entity<ApplicationSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        b.Entity<Portfolio>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Ignore(x => x.IsArchived);
            e.Property(x => x.BaseCurrency).HasMaxLength(3);
            e.Property(x => x.CashBalance);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.OwnerId);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Settings).HasConversion(JsonConverter<PortfolioSettings>(), JsonComparer<PortfolioSettings>()).HasColumnType("jsonb");
            e.HasMany(x => x.Holdings).WithOne().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.CashEntries).WithOne().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Executions).WithOne().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.CashEntries).AutoInclude(false);
            e.Navigation(x => x.Executions).AutoInclude(false);
        });

        b.Entity<Holding>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Quantity);
            // Derived per-share cost (fees capitalized): keep extra precision so quantity × average cost reproduces the cost basis.
            e.Property(x => x.AverageCost).HasPrecision(28, 12);
            e.Property(x => x.RealizedPnl);
            e.Ignore(x => x.CostBasis);
            e.HasIndex(x => new { x.PortfolioId, x.AssetId }).IsUnique();
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CashLedgerEntry>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.PortfolioId, x.TimestampUtc });
        });

        b.Entity<TradeExecution>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Side).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(x => new { x.PortfolioId, x.ExecutedAtUtc });
            e.HasIndex(x => x.OrderId);
        });

        b.Entity<OptimizationRun>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Objective).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SettingsSnapshot).HasConversion(JsonConverter<PortfolioSettings>(), JsonComparer<PortfolioSettings>()).HasColumnType("jsonb");
            e.Property(x => x.AnalyticsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.PortfolioId, x.CreatedAtUtc });
            e.HasIndex(x => new { x.OwnerId, x.CreatedAtUtc });
            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Weights).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TradeProposal>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.PortfolioId, x.CreatedAtUtc });
            e.HasIndex(x => x.OptimizationRunId);
            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<OptimizationRun>().WithMany().HasForeignKey(x => x.OptimizationRunId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TradeProposalLine>(e =>
        {
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(10);
        });

        b.Entity<PaperTradeBatch>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.HasIndex(x => new { x.PortfolioId, x.IdempotencyKey }).IsUnique();
            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<TradeProposal>().WithMany().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Orders).WithOne().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SimulatedOrder>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Side).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.PortfolioId, x.CreatedAtUtc });
        });

        b.Entity<BacktestRun>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ConfigJson).HasColumnType("jsonb");
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => new { x.OwnerId, x.CreatedAtUtc });
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, Json), v => JsonSerializer.Deserialize<T>(v, Json) ?? new T());

    private static ValueComparer<T> JsonComparer<T>() => new(
        (a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
        v => JsonSerializer.Serialize(v, Json).GetHashCode(),
        v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!);

    public IQueryable<HistoricalPrice> LatestPricesPerAsset(IReadOnlyCollection<int> assetIds, string source, int count, DateOnly from, DateOnly to) =>
        // Parameterized (interpolation becomes parameters, never string concatenation); LATERAL + LIMIT = backward index scan per asset.
        HistoricalPrices.FromSql($"""
            SELECT h.* FROM unnest({assetIds.ToArray()}) AS a(id)
            CROSS JOIN LATERAL (
                SELECT * FROM "HistoricalPrices" p
                WHERE p."AssetId" = a.id AND p."Source" = {source} AND p."Date" >= {from} AND p."Date" <= {to}
                ORDER BY p."Date" DESC LIMIT {count}) h
            """).AsNoTracking();

    public bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public async Task UpsertFxRatesAsync(IReadOnlyCollection<FxRate> rates, CancellationToken ct)
    {
        // INSERT … ON CONFLICT outside the change tracker: concurrent requests storing the same observations never
        // leave failed entities behind that would break the caller's later SaveChanges.
        foreach (var chunk in rates.Chunk(500))
        {
            var values = new List<string>();
            var args = new List<object>();
            foreach (var r in chunk)
            {
                var i = args.Count;
                values.Add($"({{{i}}}, {{{i + 1}}}, {{{i + 2}}}, {{{i + 3}}}, {{{i + 4}}}, {{{i + 5}}})");
                args.AddRange([r.Source, r.Currency, r.Date, r.UnitsPerEur, r.IsSimulated, r.RetrievedAtUtc]);
            }
            var sql = "INSERT INTO \"FxRates\" (\"Source\", \"Currency\", \"Date\", \"UnitsPerEur\", \"IsSimulated\", \"RetrievedAtUtc\") VALUES " +
                      string.Join(", ", values) +
                      " ON CONFLICT (\"Source\", \"Currency\", \"Date\") DO UPDATE SET \"UnitsPerEur\" = EXCLUDED.\"UnitsPerEur\", \"RetrievedAtUtc\" = EXCLUDED.\"RetrievedAtUtc\"";
            await Database.ExecuteSqlRawAsync(sql, args, ct);
        }
    }

    /// <summary>All timestamps are UTC (PostgreSQL <c>timestamptz</c>); values are normalized on write and tagged as UTC on read.</summary>
    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
