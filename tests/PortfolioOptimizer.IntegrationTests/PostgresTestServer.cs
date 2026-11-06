using Npgsql;
using Testcontainers.PostgreSql;
using PortfolioOptimizer.Application.Common;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Real PostgreSQL for integration tests. Uses the server in <c>PO_TEST_POSTGRES</c> (an admin connection string, e.g.
/// the compose database) when set; otherwise starts one disposable PostgreSQL container via Testcontainers, which needs
/// a Docker-compatible endpoint (<c>DOCKER_HOST</c>; with Podman see <c>scripts/test.sh</c>).
/// Every test factory gets its own database, dropped afterwards. It is a copy (<c>CREATE DATABASE … TEMPLATE</c>, ~20 ms)
/// of one template database that the real start-up migrated and seeded once per test run (~2 s, mostly the index
/// catalogue); building every database from scratch cost ~1.7 s per test. The API's start-up still runs on each copy and
/// finds the schema current and the catalogue unchanged.
/// </summary>
public static class PostgresTestServer
{
    public const string ConnectionStringVariable = "PO_TEST_POSTGRES";

    // Resolved once per test run (thread-safe): the configured server, or one container started for all tests.
    private static readonly Lazy<Task<string>> AdminConnectionString = new(ResolveAdminConnectionStringAsync);

    // Migrated and seeded once per test run; every test database is a copy of it.
    private static readonly Lazy<Task<string>> TemplateDatabase = new(BuildTemplateAsync);

    // Copies run one at a time (~20 ms each), clear of PostgreSQL's "source database is being accessed by other users" check.
    private static readonly SemaphoreSlim CopyLock = new(1, 1);

    public static async Task<string> CreateDatabaseAsync()
    {
        var admin = await AdminConnectionString.Value;
        var template = await TemplateDatabase.Value;
        var name = $"po_it_{Guid.NewGuid():N}"[..24];
        await CopyLock.WaitAsync();
        try
        {
            await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"");
        }
        finally
        {
            CopyLock.Release();
        }
        return ConnectionStringFor(admin, name);
    }

    /// <summary>
    /// Creates an empty database, lets the real API start-up migrate and seed it (the same settings as every test, see
    /// <see cref="ApiFactory"/>), stops the host and closes every connection, so the database can serve as a template.
    /// </summary>
    private static async Task<string> BuildTemplateAsync()
    {
        var admin = await AdminConnectionString.Value;
        var name = $"po_it_tpl_{Guid.NewGuid():N}"[..24];
        await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\"");
        var connectionString = ConnectionStringFor(admin, name);
        await using (var factory = new ApiFactory(connectionString))
        {
            _ = factory.Services; // starts the host: migrations and seed data
        }
        NpgsqlConnection.ClearAllPools();
        // A copy fails while anyone is connected to the template; end any connection a background service left behind.
        await ExecuteAsync(admin, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{name}' AND pid <> pg_backend_pid()");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DropTemplateAtExit(connectionString);
        return name;
    }

    // Best effort: on a configured server (PO_TEST_POSTGRES) the template must not pile up; a Testcontainers container may
    // already be gone at this point, and with it the template.
    private static void DropTemplateAtExit(string connectionString)
    {
        try
        {
            DropDatabaseAsync(connectionString).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            // the server is no longer reachable; nothing left to clean up
        }
    }

    private static string ConnectionStringFor(string admin, string database) =>
        new NpgsqlConnectionStringBuilder(admin) { Database = database, MaxPoolSize = 20 }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(await AdminConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ResolveAdminConnectionStringAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return new NpgsqlConnectionStringBuilder(configured) { Database = "postgres" }.ConnectionString;

        var container = new PostgreSqlBuilder("docker.io/library/postgres:17-alpine").Build();
        try
        {
            await container.StartAsync();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            await container.DisposeAsync();
            throw new InvalidOperationException(
                $"Integration tests need PostgreSQL. Set {ConnectionStringVariable} to an admin connection string, or make a " +
                "Docker-compatible engine reachable for Testcontainers (DOCKER_HOST; run scripts/test.sh with Podman).", ex);
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "postgres" }.ConnectionString;
    }
}
