using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IApplicationDbContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<HistoricalPrice> HistoricalPrices => Set<HistoricalPrice>();
    public DbSet<LatestQuote> LatestQuotes => Set<LatestQuote>();
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

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(18, 6);
        builder.Properties<decimal?>().HavePrecision(18, 6);
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Asset>(e =>
        {
            e.HasIndex(x => x.Symbol).IsUnique();
            e.Property(x => x.Symbol).HasMaxLength(20);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.AssetType).HasConversion<string>().HasMaxLength(20);
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
            e.Property(x => x.Settings).HasConversion(JsonConverter<PortfolioSettings>(), JsonComparer<PortfolioSettings>());
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
            e.Property(x => x.AverageCost);
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
            e.Property(x => x.SettingsSnapshot).HasConversion(JsonConverter<PortfolioSettings>(), JsonComparer<PortfolioSettings>());
            e.HasIndex(x => new { x.PortfolioId, x.CreatedAtUtc });
            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
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
            e.HasIndex(x => x.CreatedAtUtc);
        });
    }

    private static ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, Json), v => JsonSerializer.Deserialize<T>(v, Json) ?? new T());

    private static ValueComparer<T> JsonComparer<T>() => new(
        (a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
        v => JsonSerializer.Serialize(v, Json).GetHashCode(),
        v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!);

    /// <summary>SQLite has no time-zone-aware type; all timestamps are stored and returned as UTC.</summary>
    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
