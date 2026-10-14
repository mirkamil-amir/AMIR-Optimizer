using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace PortfolioOptimizer.DataMigration;

public sealed record ImportOptions(string SqlitePath, string PostgresConnectionString, string OwnerEmail, bool DryRun);

public sealed record TableReport(string Table, long SourceRows, long TargetRows);

public sealed record ImportReport(Guid OwnerId, IReadOnlyList<TableReport> Tables, IReadOnlyList<string> Notes, bool Committed);

/// <summary>
/// Copies the data of a legacy SQLite database (schema migration <c>20261012203642_InitialCreate</c>) into a migrated,
/// otherwise unused PostgreSQL database, in one transaction:
/// <list type="bullet">
/// <item>Column values are converted using the PostgreSQL column types (TEXT GUIDs → uuid, TEXT timestamps → timestamptz
/// (UTC), JSON string lists → text[], INTEGER booleans → boolean, TEXT decimals → numeric).</item>
/// <item>Ownership did not exist in SQLite: portfolios, optimization runs and backtests are assigned to
/// <see cref="ImportOptions.OwnerEmail"/>, which must be an existing account (e.g. the bootstrap administrator).</item>
/// <item>Seed rows the API inserted on first start (assets, settings) are replaced by the imported rows; identity
/// sequences are advanced past the imported keys.</item>
/// <item>Row counts and key balances are verified before committing. The SQLite file is opened read-only.</item>
/// </list>
/// </summary>
public sealed class SqliteImporter
{
    public const string SupportedSourceMigration = "20261012203642_InitialCreate";

    /// <summary>Tables in foreign-key order. Owner columns are filled for the tables listed in <see cref="OwnedTables"/>.</summary>
    public static readonly string[] Tables =
    [
        "Assets", "Settings", "HistoricalPrices", "LatestQuotes", "SyncJobs", "SyncLogs",
        "Portfolios", "Holdings", "CashLedger", "Executions",
        "OptimizationRuns", "OptimizationRunWeight", "TradeProposals", "TradeProposalLine",
        "PaperTradeBatches", "Orders", "Backtests",
    ];

    private static readonly HashSet<string> OwnedTables = ["Portfolios", "OptimizationRuns", "Backtests"];

    /// <summary>User data that must not exist yet in the target (the import is not a merge).</summary>
    private static readonly string[] MustBeEmpty =
        ["HistoricalPrices", "Portfolios", "OptimizationRuns", "Backtests", "TradeProposals", "Executions", "CashLedger"];

    /// <summary>Rows the API seeds on first start; replaced by the imported data.</summary>
    private static readonly string[] SeedTables = ["LatestQuotes", "SyncLogs", "SyncJobs", "Settings", "Assets"];

    public async Task<ImportReport> ImportAsync(ImportOptions options, TextWriter log, CancellationToken ct = default)
    {
        if (!File.Exists(options.SqlitePath)) throw new InvalidOperationException($"SQLite database not found: {options.SqlitePath}");
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.SqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await source.OpenAsync(ct);
        await CheckSourceAsync(source, ct);

        await using var target = new NpgsqlConnection(options.PostgresConnectionString);
        await target.OpenAsync(ct);
        await using var tx = await target.BeginTransactionAsync(ct);
        var notes = new List<string>();

        var ownerId = await ScalarAsync<Guid?>(target, tx, "SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = @e",
                          ct, ("e", options.OwnerEmail.Trim().ToUpperInvariant()))
                      ?? throw new InvalidOperationException(
                          $"No account with e-mail {options.OwnerEmail} exists in the target database. Start the API once with Admin__Email/Admin__Password " +
                          "(or register and approve the user) before importing.");

        foreach (var table in MustBeEmpty)
        {
            var n = await ScalarAsync<long>(target, tx, $"SELECT COUNT(*) FROM \"{table}\"", ct);
            if (n > 0) throw new InvalidOperationException($"Target table {table} already contains {n} row(s). Import into a freshly migrated database (start the API once with Seed__DemoPortfolio=false and Seed__SyncOnEmptyDatabase=false before importing).");
        }
        foreach (var table in SeedTables)
        {
            var removed = await ExecuteAsync(target, tx, $"DELETE FROM \"{table}\"", ct);
            if (removed > 0) notes.Add($"Removed {removed} seed row(s) from {table} (replaced by imported data).");
        }

        var reports = new List<TableReport>();
        foreach (var table in Tables)
        {
            ct.ThrowIfCancellationRequested();
            var columns = await TargetColumnsAsync(target, tx, table, ct);
            var sourceColumns = await SourceColumnsAsync(source, table, ct);
            var copied = await CopyTableAsync(source, target, table, columns, sourceColumns, ownerId, ct);
            var targetCount = await ScalarAsync<long>(target, tx, $"SELECT COUNT(*) FROM \"{table}\"", ct);
            var sourceCount = await SourceScalarAsync(source, $"SELECT COUNT(*) FROM \"{table}\"", ct);
            if (copied != sourceCount || targetCount != sourceCount)
                throw new InvalidOperationException($"{table}: copied {copied}, source {sourceCount}, target {targetCount} rows – aborting.");
            reports.Add(new TableReport(table, sourceCount, targetCount));
            await log.WriteLineAsync($"  {table,-24} {sourceCount,8} rows");

            var identity = columns.FirstOrDefault(c => c.IsIdentity);
            if (identity is not null && targetCount > 0)
                await ExecuteAsync(target, tx,
                    $"SELECT setval(pg_get_serial_sequence('\"{table}\"', '{identity.Name}'), (SELECT MAX(\"{identity.Name}\") FROM \"{table}\"))", ct);
        }

        await VerifyBalancesAsync(source, target, tx, ct);
        notes.Add($"Portfolios, optimization runs and backtests are owned by {options.OwnerEmail} ({ownerId}).");

        if (options.DryRun)
        {
            await tx.RollbackAsync(ct);
            notes.Add("Dry run: all checks passed; the transaction was rolled back.");
            return new ImportReport(ownerId, reports, notes, Committed: false);
        }
        await tx.CommitAsync(ct);
        return new ImportReport(ownerId, reports, notes, Committed: true);
    }

    private static async Task CheckSourceAsync(SqliteConnection source, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand();
        cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
        var migrations = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) migrations.Add(r.GetString(0));
        if (migrations is not [SupportedSourceMigration])
            throw new InvalidOperationException(
                $"Unsupported SQLite schema version [{string.Join(", ", migrations)}]; this tool supports exactly {SupportedSourceMigration}.");
    }

    private sealed record TargetColumn(string Name, string DataType, string UdtName, bool IsNullable, bool IsIdentity, bool HasDefault);

    private static async Task<List<TargetColumn>> TargetColumnsAsync(NpgsqlConnection target, NpgsqlTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name, data_type, udt_name, is_nullable = 'YES', is_identity = 'YES', column_default IS NOT NULL " +
            "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t ORDER BY ordinal_position", target, tx);
        cmd.Parameters.AddWithValue("t", table);
        var list = new List<TargetColumn>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new TargetColumn(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5)));
        if (list.Count == 0) throw new InvalidOperationException($"Target table {table} does not exist. Run the migrate command first.");
        return list;
    }

    private static async Task<HashSet<string>> SourceColumnsAsync(SqliteConnection source, string table, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        if (set.Count == 0) throw new InvalidOperationException($"Source table {table} does not exist.");
        return set;
    }

    private static async Task<long> CopyTableAsync(SqliteConnection source, NpgsqlConnection target, string table,
        List<TargetColumn> columns, HashSet<string> sourceColumns, Guid ownerId, CancellationToken ct)
    {
        var copyColumns = new List<TargetColumn>();
        foreach (var c in columns)
        {
            if (sourceColumns.Contains(c.Name) || (c.Name == "OwnerId" && OwnedTables.Contains(table))) copyColumns.Add(c);
            else if (!c.IsNullable && !c.HasDefault && !c.IsIdentity)
                throw new InvalidOperationException($"{table}.{c.Name} is required in PostgreSQL but has no source column and no default.");
        }
        var unmapped = sourceColumns.Except(columns.Select(c => c.Name)).ToList();
        if (unmapped.Count > 0) throw new InvalidOperationException($"{table}: source column(s) without a target: {string.Join(", ", unmapped)}.");

        var selectList = string.Join(", ", copyColumns.Select(c => c.Name == "OwnerId" && !sourceColumns.Contains("OwnerId") ? "NULL" : $"\"{c.Name}\""));
        await using var read = source.CreateCommand();
        read.CommandText = $"SELECT {selectList} FROM \"{table}\"";
        await using var reader = await read.ExecuteReaderAsync(ct);

        var copySql = $"COPY \"{table}\" ({string.Join(", ", copyColumns.Select(c => $"\"{c.Name}\""))}) FROM STDIN (FORMAT BINARY)";
        await using var importer = await target.BeginBinaryImportAsync(copySql, ct);
        long rows = 0;
        while (await reader.ReadAsync(ct))
        {
            await importer.StartRowAsync(ct);
            for (var i = 0; i < copyColumns.Count; i++)
            {
                var column = copyColumns[i];
                if (column.Name == "OwnerId" && !sourceColumns.Contains("OwnerId"))
                {
                    await importer.WriteAsync(ownerId, NpgsqlDbType.Uuid, ct);
                    continue;
                }
                var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (raw is null)
                {
                    if (!column.IsNullable) throw new InvalidOperationException($"{table}.{column.Name}: NULL in a NOT NULL column (row {rows + 1}).");
                    await importer.WriteNullAsync(ct);
                    continue;
                }
                var (value, type) = Convert(raw, column, table);
                await importer.WriteAsync(value, type, ct);
            }
            rows++;
        }
        await importer.CompleteAsync(ct);
        return rows;
    }

    /// <summary>Converts a SQLite value to the PostgreSQL column type.</summary>
    internal static (object Value, NpgsqlDbType Type) Convert(object raw, TargetColumnInfo column, string table) =>
        Convert(raw, new TargetColumn(column.Name, column.DataType, column.UdtName, true, false, false), table);

    private static (object Value, NpgsqlDbType Type) Convert(object raw, TargetColumn column, string table)
    {
        var s = raw as string;
        try
        {
            return column.DataType switch
            {
                "uuid" => (s is not null ? Guid.Parse(s) : raw is byte[] b ? new Guid(b) : throw Unexpected(raw), NpgsqlDbType.Uuid),
                "timestamp with time zone" => (ParseUtc(s ?? throw Unexpected(raw)), NpgsqlDbType.TimestampTz),
                "date" => (DateOnly.ParseExact(s ?? throw Unexpected(raw), "yyyy-MM-dd", CultureInfo.InvariantCulture), NpgsqlDbType.Date),
                "boolean" => (System.Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0, NpgsqlDbType.Boolean),
                "numeric" => (s is not null ? decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) : System.Convert.ToDecimal(raw, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric),
                "integer" => (System.Convert.ToInt32(raw, CultureInfo.InvariantCulture), NpgsqlDbType.Integer),
                "bigint" => (System.Convert.ToInt64(raw, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint),
                "double precision" => (System.Convert.ToDouble(raw, CultureInfo.InvariantCulture), NpgsqlDbType.Double),
                "jsonb" => (ValidJson(s ?? throw Unexpected(raw)), NpgsqlDbType.Jsonb),
                "ARRAY" when column.UdtName == "_text" => (JsonSerializer.Deserialize<string[]>(s ?? throw Unexpected(raw)) ?? [], NpgsqlDbType.Array | NpgsqlDbType.Text),
                "text" or "character varying" => (s ?? System.Convert.ToString(raw, CultureInfo.InvariantCulture)!, NpgsqlDbType.Text),
                _ => throw new InvalidOperationException($"Unsupported PostgreSQL type {column.DataType}/{column.UdtName}."),
            };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"{table}.{column.Name}: cannot convert '{raw}' to {column.DataType}: {ex.Message}", ex);
        }

        static Exception Unexpected(object v) => new FormatException($"unexpected SQLite value type {v.GetType().Name}");
    }

    /// <summary>SQLite timestamps written by EF Core: "yyyy-MM-dd HH:mm:ss.FFFFFFF" in UTC (no offset).</summary>
    internal static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string ValidJson(string s)
    {
        using var _ = JsonDocument.Parse(s);
        return s;
    }

    /// <summary>Cash balances, holding quantities and ledger sums must match exactly (decimal arithmetic).</summary>
    private static async Task VerifyBalancesAsync(SqliteConnection source, NpgsqlConnection target, NpgsqlTransaction tx, CancellationToken ct)
    {
        foreach (var (table, column) in new[] { ("Portfolios", "CashBalance"), ("Holdings", "Quantity"), ("CashLedger", "Amount"), ("Executions", "NetCashFlow") })
        {
            var sourceValues = new List<decimal>();
            await using (var cmd = source.CreateCommand())
            {
                cmd.CommandText = $"SELECT \"{column}\" FROM \"{table}\"";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) sourceValues.Add(decimal.Parse(r.GetString(0), NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            var sourceSum = sourceValues.Sum();
            var targetSum = await ScalarAsync<decimal?>(target, tx, $"SELECT SUM(\"{column}\") FROM \"{table}\"", ct) ?? 0m;
            if (Math.Round(sourceSum, 6) != Math.Round(targetSum, 6))
                throw new InvalidOperationException($"Balance check failed for {table}.{column}: source {sourceSum}, target {targetSum}.");
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] p)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (name, value) in p) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default! : (T)result;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> SourceScalarAsync(SqliteConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return System.Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
}

/// <summary>Column description used by <see cref="SqliteImporter.Convert(object, TargetColumnInfo, string)"/> in tests.</summary>
public sealed record TargetColumnInfo(string Name, string DataType, string UdtName);
