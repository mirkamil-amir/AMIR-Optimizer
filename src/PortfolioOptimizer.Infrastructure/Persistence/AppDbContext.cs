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

    public bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>All timestamps are UTC (PostgreSQL <c>timestamptz</c>); values are normalized on write and tagged as UTC on read.</summary>
    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
