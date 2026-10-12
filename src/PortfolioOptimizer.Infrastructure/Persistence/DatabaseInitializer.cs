using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.Persistence;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>Insert the default asset universe when the database has no assets.</summary>
    public bool Assets { get; set; } = true;

    /// <summary>Create a demo portfolio (simulated cash) when no portfolio exists.</summary>
    public bool DemoPortfolio { get; set; } = true;

    /// <summary>Enqueue an initial history download when the active provider has no prices yet.</summary>
    public bool SyncOnEmptyDatabase { get; set; } = true;

    public int InitialHistoryYears { get; set; } = 5;
}

/// <summary>Applies EF Core migrations and inserts idempotent seed data. Safe to run on every start.</summary>
public sealed class DatabaseInitializer(
    AppDbContext db,
    PortfolioService portfolios,
    MarketDataService marketData,
    IOptions<MarketDataOptions> marketOptions,
    IOptions<SeedOptions> seedOptions,
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

    public async Task InitializeAsync(CancellationToken ct)
    {
        var dataSource = db.Database.GetDbConnection().DataSource;
        if (!string.IsNullOrEmpty(dataSource) && db.Database.IsSqlite())
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        if (db.Database.IsRelational()) await db.Database.MigrateAsync(ct);
        else await db.Database.EnsureCreatedAsync(ct);

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

        if (seed.DemoPortfolio && !await db.Portfolios.AnyAsync(ct) && await db.Assets.AnyAsync(ct))
        {
            await portfolios.CreateAsync(new CreatePortfolioRequest { Name = "Demo Portfolio", InitialInvestment = 100_000m, BaseCurrency = "USD" }, ct);
            logger.LogInformation("Created demo portfolio with 100,000 USD simulated cash");
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
