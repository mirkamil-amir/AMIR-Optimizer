using Microsoft.Data.Sqlite;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.DataMigration;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Imports a database in the legacy SQLite schema (captured from the last SQLite release) into a migrated PostgreSQL
/// database and verifies the result through the API as the owning user.
/// </summary>
public sealed class SqliteImportTests : IAsyncLifetime
{
    private const string PortfolioId = "7F2A1B82-1199-4423-8F99-E5D603898340";
    private const string RunId = "EDDF355F-BF23-4393-B30F-1312ED6EF906";
    private const string ProposalId = "CD425C54-BAA5-4180-8A60-EEF9DD9CC941";
    private const string BacktestId = "996C91EF-3FF6-4795-8E80-939C649DA311";

    private ApiFactory _factory = null!;
    private string _sqlitePath = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        using (_factory.CreateClient()) { } // start the host: migrate, seed assets/settings, bootstrap the administrator
        _sqlitePath = Path.Combine(Path.GetTempPath(), $"po-legacy-{Guid.NewGuid():N}.db");
        await CreateLegacyDatabaseAsync(_sqlitePath);
    }

    public async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_sqlitePath);
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    [Fact]
    public async Task DryRun_ValidatesEverything_ButWritesNothing()
    {
        var report = await new SqliteImporter().ImportAsync(new ImportOptions(_sqlitePath, _factory.ConnectionString, ApiFactory.AdminEmail, DryRun: true), TextWriter.Null);
        Assert.False(report.Committed);
        Assert.Equal(2, report.Tables.Single(t => t.Table == "HistoricalPrices").TargetRows);

        using var admin = await _factory.CreateAdminSessionAsync();
        Assert.Empty(await (await admin.GetAsync("/api/v1/portfolios")).ReadAsync<List<PortfolioSummaryDto>>());
    }

    [Fact]
    public async Task Import_PreservesPortfolioHoldingsLedgerRunsAndBacktests()
    {
        var report = await new SqliteImporter().ImportAsync(new ImportOptions(_sqlitePath, _factory.ConnectionString, ApiFactory.AdminEmail, DryRun: false), TextWriter.Null);
        Assert.True(report.Committed);
        Assert.All(report.Tables, t => Assert.Equal(t.SourceRows, t.TargetRows));

        using var admin = await _factory.CreateAdminSessionAsync();
        var portfolio = await (await admin.GetAsync($"/api/v1/portfolios/{PortfolioId}")).ReadAsync<PortfolioDto>();
        Assert.Equal("Imported Portfolio", portfolio.Name);
        Assert.Equal(65_170.228548m, portfolio.CashBalance);
        Assert.Equal(1, portfolio.Version);
        Assert.Equal(["AAPL", "JNJ"], portfolio.Settings.Universe);

        var holdings = await (await admin.GetAsync($"/api/v1/portfolios/{PortfolioId}/holdings")).ReadAsync<HoldingsResponse>();
        var jnj = Assert.Single(holdings.Holdings, h => h.Quantity > 0);
        Assert.Equal("JNJ", jnj.Symbol);
        Assert.Equal(132m, jnj.Quantity);
        Assert.Equal(263.861900484848m, Math.Round(jnj.AverageCost, 12));

        var ledger = await (await admin.GetAsync($"/api/v1/portfolios/{PortfolioId}/cash-ledger")).ReadAsync<PagedResult<CashEntryDto>>();
        Assert.Equal(portfolio.CashBalance, ledger.Items.Sum(e => e.Amount));
        var tx = await (await admin.GetAsync($"/api/v1/portfolios/{PortfolioId}/transactions")).ReadAsync<PagedResult<TransactionDto>>();
        Assert.Equal(-34_829.771452m, Assert.Single(tx.Items).NetCashFlow);

        var run = await (await admin.GetAsync($"/api/v1/optimization-runs/{RunId}")).ReadAsync<OptimizationRunDto>();
        Assert.Equal(["AAPL", "JNJ"], run.Symbols);
        Assert.Equal(OptimizationRunStatus.Succeeded, run.Status);
        Assert.Equal(new DateTime(2026, 10, 12, 20, 38, 19, 508, DateTimeKind.Utc).AddTicks(5740), run.CreatedAtUtc);
        Assert.Equal(2, run.Allocation.Count);

        var proposal = await (await admin.GetAsync($"/api/v1/trade-proposals/{ProposalId}")).ReadAsync<TradeProposalDto>();
        Assert.Equal(ProposalStatus.Executed, proposal.Status);
        Assert.Single(proposal.Lines);

        var backtest = await (await admin.GetAsync($"/api/v1/backtests/{BacktestId}")).ReadAsync<BacktestDto>();
        Assert.Equal(JobStatus.Succeeded, backtest.Status);

        // Identity sequences continue after the imported keys: new rows can be inserted.
        var created = await (await admin.PostJsonAsync("/api/v1/assets", new CreateAssetRequest { Symbol = "KO", Name = "Coca-Cola" })).ReadAsync<AssetDto>();
        Assert.True(created.Id > 2);

        // Another user cannot see the imported data.
        var (other, _) = await _factory.CreateApprovedUserAsync(admin);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/portfolios/{PortfolioId}")).StatusCode);

        // A second import is refused (the target is no longer empty).
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteImporter().ImportAsync(new ImportOptions(_sqlitePath, _factory.ConnectionString, ApiFactory.AdminEmail, DryRun: false), TextWriter.Null));
        Assert.Contains("already contains", again.Message);
    }

    [Fact]
    public async Task Import_RequiresExistingOwner()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteImporter().ImportAsync(new ImportOptions(_sqlitePath, _factory.ConnectionString, "nobody@test.local", DryRun: true), TextWriter.Null));
        Assert.Contains("No account", ex.Message);
    }

    private static async Task CreateLegacyDatabaseAsync(string path)
    {
        await using var c = new SqliteConnection($"Data Source={path}");
        await c.OpenAsync();
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-sqlite-schema.sql"));
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = schema;
            await cmd.ExecuteNonQueryAsync();
        }

        // Rows in the exact formats written by EF Core 10 + SQLite (TEXT GUIDs/decimals/timestamps, JSON string lists).
        const string data = $$"""
            INSERT INTO __EFMigrationsHistory VALUES ('{{SqliteImporter.SupportedSourceMigration}}', '10.0.12');
            INSERT INTO Assets VALUES (1,'AAPL','Apple Inc.','NASDAQ','USD','Equity','Information Technology',1,'2026-10-12 20:37:40.746237');
            INSERT INTO Assets VALUES (2,'JNJ','Johnson & Johnson','NYSE','USD','Equity','Health Care',1,'2026-10-12 20:37:40.746237');
            INSERT INTO Settings VALUES (1,'Yahoo','USD',0.04,'SPY',5,'2026-10-12 20:37:40.746237');
            INSERT INTO HistoricalPrices VALUES (1,1,'2026-10-09','143.229996','143.25','141.039993','141.509995','138.02475',73035900,'Yahoo',0,'2026-10-12 20:37:41.632058');
            INSERT INTO HistoricalPrices VALUES (2,2,'2026-10-12',NULL,NULL,NULL,'263.730011',NULL,NULL,'Yahoo',0,'2026-10-12 20:37:41.632058');
            INSERT INTO LatestQuotes VALUES (1,'Yahoo','340.97','USD','2026-10-12 20:00:01','2026-10-12 21:27:45.067335',0);
            INSERT INTO SyncJobs VALUES ('47DDDFB9-ACFF-4126-859A-14CEF104EB8A','Succeeded','Yahoo','AAPL,JNJ','2021-10-12','2026-10-12',0,'2026-10-12 20:37:41.063713','2026-10-12 20:37:41.234361','2026-10-12 20:37:54.446304',2,2,0,'2 succeeded, 0 failed.');
            INSERT INTO SyncLogs VALUES (1,'47DDDFB9-ACFF-4126-859A-14CEF104EB8A',1,'AAPL','Yahoo',1,1255,1255,0,0,0,NULL,'2026-10-12 20:37:42.194031');
            INSERT INTO Portfolios VALUES ('{{PortfolioId}}','Imported Portfolio','USD','100000.0','65170.228548','{"universe":["AAPL","JNJ"],"lookbackDays":756,"objective":0,"covarianceMethod":1,"riskFreeRate":0.04,"benchmarkSymbol":"SPY","minWeight":0,"maxWeight":0.35}','2026-10-12 20:37:40.978527','2026-10-12 20:38:35.47045',1);
            INSERT INTO Holdings VALUES ('05911CE3-3D26-4AA8-A398-1543EE12C6D4','{{PortfolioId}}',2,'JNJ','132.0','263.86190048484848484848484848','0','2026-10-12 20:38:35.47045','2026-10-12 20:38:35.47045');
            INSERT INTO CashLedger VALUES ('7AF81505-3B30-44B2-B1ED-CB28AB96FED2','{{PortfolioId}}','Deposit','100000.0','100000.0','2026-10-12 20:37:40.978527',NULL,'Initial investment (simulated cash)');
            INSERT INTO CashLedger VALUES ('8AF81505-3B30-44B2-B1ED-CB28AB96FED2','{{PortfolioId}}','BuySettlement','-34812.361452','65187.638548','2026-10-12 20:38:35.47045','76C24BED-1029-4984-8321-6EDC98D5102F','Buy 132 JNJ');
            INSERT INTO CashLedger VALUES ('9AF81505-3B30-44B2-B1ED-CB28AB96FED2','{{PortfolioId}}','Fee','-17.41','65170.228548','2026-10-12 20:38:35.47045','76C24BED-1029-4984-8321-6EDC98D5102F','Commission JNJ');
            INSERT INTO Executions VALUES ('76C24BED-1029-4984-8321-6EDC98D5102F','{{PortfolioId}}','04CA5D52-BCED-4DE5-89C8-2984F0150675',2,'JNJ','Buy','132.0','263.730011','2026-10-12','34812.361452','17.41','-34829.771452',NULL,'2026-10-12 20:38:35.47045');
            INSERT INTO OptimizationRuns VALUES ('{{RunId}}','{{PortfolioId}}','2026-10-12 20:38:19.508574','MinimumVariance','Succeeded','OK','["AAPL","JNJ"]','2023-10-12','2026-10-12',755,'Yahoo',0,'abcdef0123456789','{"universe":["AAPL","JNJ"]}',0.12,0.15,0.53,120,3,45.5,'[""]','{}');
            INSERT INTO OptimizationRunWeight VALUES (1,'{{RunId}}','AAPL',1,0.0,0.26,0.27,0.0);
            INSERT INTO OptimizationRunWeight VALUES (2,'{{RunId}}','JNJ',2,0.99,0.08,0.17,1.0);
            INSERT INTO TradeProposals VALUES ('{{ProposalId}}','{{PortfolioId}}','{{RunId}}','2026-10-12 20:38:35.293464','Executed',0,'100000.0','100000.0','65170.228548','17.41','0.01000000000000085',0,'["Estimated transaction costs: 17.41."]');
            INSERT INTO TradeProposalLine VALUES (1,'{{ProposalId}}',2,'JNJ','Johnson & Johnson','263.730011','2026-10-12',0,0.35,0.0,'0.0','35000.0','132.0','132.0','Buy','34812.361452','17.41',0.34812361452,'Whole-share rounding.');
            INSERT INTO PaperTradeBatches VALUES ('3DEAB5E9-8278-47DB-903A-D6F3786ED547','{{PortfolioId}}','{{ProposalId}}','25942CCD-4968-4377-903D-962FF688B342','2026-10-12 20:38:35.47045','17.41','65170.228548','[]');
            INSERT INTO Orders VALUES ('04CA5D52-BCED-4DE5-89C8-2984F0150675','3DEAB5E9-8278-47DB-903A-D6F3786ED547','{{PortfolioId}}','{{ProposalId}}',2,'JNJ','Buy','132.0','132.0','Filled','263.730011','2026-10-12 20:38:35.47045','Simulated fill.');
            INSERT INTO Backtests VALUES ('{{BacktestId}}','MinimumVariance Monthly 2023-2026','Succeeded','2026-10-12 20:38:53.646781','2026-10-12 20:38:53.669271','2026-10-12 20:38:54.142871','{"universe":["AAPL","JNJ"],"startDate":"2023-01-01","endDate":"2026-01-01"}',NULL,NULL,'Yahoo',0);
            """;
        await using var insert = c.CreateCommand();
        insert.CommandText = data;
        await insert.ExecuteNonQueryAsync();
    }
}
