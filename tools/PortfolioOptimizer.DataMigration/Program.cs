namespace PortfolioOptimizer.DataMigration;

/// <summary>
/// Imports a legacy SQLite database into PostgreSQL. Usage:
/// <code>
/// PO_IMPORT_POSTGRES='Host=...;Username=portfolio_owner;Password=...' \
///   dotnet run --project tools/PortfolioOptimizer.DataMigration -- \
///   --sqlite src/PortfolioOptimizer.Api/data/portfolio.db --owner-email admin@example.com [--dry-run]
/// </code>
/// The connection string is read from an environment variable so it never appears in shell history or process lists.
/// </summary>
public static class ImportCommand
{
    public static async Task<int> Main(string[] args)
    {
        var arguments = args.ToList();
        string? Arg(string name)
        {
            var i = arguments.IndexOf(name);
            return i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null;
        }

        var sqlite = Arg("--sqlite");
        var owner = Arg("--owner-email");
        var postgres = Environment.GetEnvironmentVariable("PO_IMPORT_POSTGRES");
        if (sqlite is null || owner is null || string.IsNullOrWhiteSpace(postgres))
        {
            await Console.Error.WriteLineAsync(
                "Usage: PO_IMPORT_POSTGRES='<connection string>' PortfolioOptimizer.DataMigration --sqlite <file.db> --owner-email <email> [--dry-run]");
            return 2;
        }

        try
        {
            var report = await new SqliteImporter().ImportAsync(new ImportOptions(sqlite, postgres, owner, arguments.Contains("--dry-run")), Console.Out);
            foreach (var note in report.Notes) Console.WriteLine($"- {note}");
            Console.WriteLine(report.Committed ? "Import committed." : "Nothing was written.");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Import failed, nothing was written: {ex.Message}");
            return 1;
        }
    }
}
