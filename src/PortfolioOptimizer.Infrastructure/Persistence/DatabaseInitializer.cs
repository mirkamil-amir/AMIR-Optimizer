using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Identity;

namespace PortfolioOptimizer.Infrastructure.Persistence;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>Insert the default asset universe when the database has no assets.</summary>
    public bool Assets { get; set; } = true;

    /// <summary>Create a demo portfolio (simulated cash) for the bootstrap administrator when it is first created.</summary>
    public bool DemoPortfolio { get; set; } = true;

    /// <summary>
    /// Load the bundled index universes (S&amp;P 500, DAX 40, DAX 30 historical, CAC 40, FTSE 100, Nikkei 225; Wikipedia,
    /// CC BY-SA 4.0) as browsable, untracked instruments, plus the benchmark instruments. See <see cref="UniverseSeeder"/>.
    /// </summary>
    public bool Catalogue { get; set; } = true;

    /// <summary>Enqueue an initial history download when the active provider has no prices yet.</summary>
    public bool SyncOnEmptyDatabase { get; set; } = true;

    public int InitialHistoryYears { get; set; } = 5;
}

/// <summary>Applies EF Core migrations (when allowed) and inserts idempotent seed data. Safe to run on every start.</summary>
public sealed class DatabaseInitializer(
    AppDbContext db,
    PortfolioService portfolios,
    MarketDataService marketData,
    UniverseSeeder universeSeeder,
    AdminBootstrapper adminBootstrapper,
    IOptions<MarketDataOptions> marketOptions,
    IOptions<SeedOptions> seedOptions,
    IOptions<DatabaseOptions> databaseOptions,
    TimeProvider clock,
    ILogger<DatabaseInitializer> logger)
{
    public static readonly (string Symbol, string Name, string Exchange, AssetType Type, string? Sector)[] DefaultUniverse =
    [
        ("AAPL", "Apple Inc.", "NASDAQ", AssetType.Equity, "Information Technology"),
        ("MSFT", "Microsoft Corporation", "NASDAQ", AssetType.Equity, "Information Technology"),
        ("NVDA", "NVIDIA Corporation", "NASDAQ", AssetType.Equity, "Information Technology"),
        ("AMZN", "Amazon.com, Inc.", "NASDAQ", AssetType.Equity, "Consumer Discretionary"),
        ("GOOGL", "Alphabet Inc. (Class A)", "NASDAQ", AssetType.Equity, "Communication Services"),
        ("META", "Meta Platforms, Inc.", "NASDAQ", AssetType.Equity, "Communication Services"),
        ("JPM", "JPMorgan Chase & Co.", "NYSE", AssetType.Equity, "Financials"),
        ("JNJ", "Johnson & Johnson", "NYSE", AssetType.Equity, "Health Care"),
        ("XOM", "Exxon Mobil Corporation", "NYSE", AssetType.Equity, "Energy"),
        ("PG", "The Procter & Gamble Company", "NYSE", AssetType.Equity, "Consumer Staples"),
        ("SPY", "SPDR S&P 500 ETF Trust", "NYSE Arca", AssetType.Etf, null),
    ];

    /// <summary>Applies pending migrations (used by the <c>migrate</c> command and, if enabled, on start-up).</summary>
    public async Task MigrateAsync(CancellationToken ct)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date");
            return;
        }
        logger.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// Start-up initialization: migrates when <see cref="DatabaseOptions.MigrateOnStartup"/> is set, otherwise refuses to
    /// run against an outdated schema; then inserts idempotent seed data.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (databaseOptions.Value.MigrateOnStartup)
        {
            await MigrateAsync(ct);
        }
        else
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
                throw new InvalidOperationException(
                    $"The database schema is out of date ({pending.Count} pending migration(s): {string.Join(", ", pending)}). " +
                    "Run the migration command (dotnet PortfolioOptimizer.Api.dll migrate) with the migration identity, or enable Database:MigrateOnStartup for local development.");
        }

        await SeedAsync(ct);
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.Settings.AnyAsync(ct))
        {
            db.Settings.Add(new ApplicationSettings { Id = 1, ActiveProvider = marketOptions.Value.DefaultProvider, UpdatedAtUtc = now });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Initialized settings with provider {Provider}", marketOptions.Value.DefaultProvider);
        }

        var seed = seedOptions.Value;
        if (seed.Assets && !await db.Assets.AnyAsync(ct))
        {
            foreach (var a in DefaultUniverse)
                db.Assets.Add(new Asset { Symbol = a.Symbol, Name = a.Name, Exchange = a.Exchange, Currency = "USD", AssetType = a.Type, Sector = a.Sector, CreatedAtUtc = now });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} assets", DefaultUniverse.Length);
        }

        if (seed.Catalogue) await universeSeeder.SeedAsync(ct);

        var bootstrappedAdmin = await adminBootstrapper.EnsureAsync(ct);
        if (seed.DemoPortfolio && bootstrappedAdmin is { } adminId && !await db.Portfolios.AnyAsync(p => p.OwnerId == adminId, ct) && await db.Assets.AnyAsync(ct))
        {
            await portfolios.CreateForOwnerAsync(adminId, new CreatePortfolioRequest { Name = "Demo Portfolio", InitialInvestment = 100_000m, BaseCurrency = "USD" }, ct);
            logger.LogInformation("Created demo portfolio with 100,000 USD simulated cash for the administrator");
        }

        if (seed.SyncOnEmptyDatabase)
        {
            var settings = await db.Settings.AsNoTracking().SingleAsync(ct);
            var hasPrices = await db.HistoricalPrices.AnyAsync(p => p.Source == settings.ActiveProvider, ct);
            var pending = await db.SyncJobs.AnyAsync(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running, ct);
            if (!hasPrices && !pending && await db.Assets.AnyAsync(ct))
            {
                try
                {
                    var job = await marketData.EnqueueSyncAsync(new SyncRequest { StartDate = DateOnly.FromDateTime(now).AddYears(-seed.InitialHistoryYears) }, ct);
                    logger.LogInformation("Enqueued initial {Provider} history download ({Count} assets), job {JobId}", job.Source, job.AssetsTotal, job.Id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Initial market-data sync could not be enqueued");
                }
            }
        }
    }

}
