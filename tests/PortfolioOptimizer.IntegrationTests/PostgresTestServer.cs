using Npgsql;
using Testcontainers.PostgreSql;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Real PostgreSQL for integration tests. Uses the server in <c>PO_TEST_POSTGRES</c> (an admin connection string, e.g.
/// the compose database) when set; otherwise starts one disposable PostgreSQL container via Testcontainers, which needs
/// a Docker-compatible endpoint (<c>DOCKER_HOST</c>; with Podman see <c>scripts/test.sh</c>).
/// Every test factory gets its own freshly created database, dropped afterwards.
/// </summary>
public static class PostgresTestServer
{
    public const string ConnectionStringVariable = "PO_TEST_POSTGRES";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _adminConnectionString;
    private static PostgreSqlContainer? _container;

    public static async Task<string> CreateDatabaseAsync()
    {
        var admin = await AdminConnectionStringAsync();
        var name = $"po_it_{Guid.NewGuid():N}"[..24];
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(admin) { Database = name, MaxPoolSize = 20 }.ConnectionString;
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(await AdminConnectionStringAsync());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> AdminConnectionStringAsync()
    {
        if (_adminConnectionString is not null) return _adminConnectionString;
        await Gate.WaitAsync();
        try
        {
            if (_adminConnectionString is not null) return _adminConnectionString;
            var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                _adminConnectionString = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres" }.ConnectionString;
                return _adminConnectionString;
            }

            try
            {
                _container = new PostgreSqlBuilder("docker.io/library/postgres:17-alpine").Build();
                await _container.StartAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Integration tests need PostgreSQL. Set {ConnectionStringVariable} to an admin connection string, or make a " +
                    "Docker-compatible engine reachable for Testcontainers (DOCKER_HOST; run scripts/test.sh with Podman).", ex);
            }
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _container.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _adminConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = "postgres" }.ConnectionString;
            return _adminConnectionString;
        }
        finally
        {
            Gate.Release();
        }
    }
}
