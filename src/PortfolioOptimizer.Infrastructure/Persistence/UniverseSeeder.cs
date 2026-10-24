using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.Persistence;

public sealed class UniverseOptions
{
    public const string Section = "Universes";

    /// <summary>
    /// Optional directory with <c>indices.csv</c> and <c>&lt;CODE&gt;.csv</c> files that override the bundled universe files
    /// (relative to the content root or absolute). Lets operators update index constituents without rebuilding.
    /// </summary>
    public string? Directory { get; set; }
}

/// <summary>
/// Loads the index universes (S&amp;P 500, DAX 40, DAX 30 historical, CAC 40, FTSE 100, Nikkei 225, SMI, IBEX 35, FTSE MIB,
/// OMX Stockholm 30, OMX Copenhagen 25, S&amp;P/TSX 60, S&amp;P/ASX 50, Hang Seng, NIFTY 50, Straits Times and one list of
/// national index ETFs per new market) from the versioned CSV
/// files in <c>Seed/universes</c> (built by tools/universe-builder from pinned Wikipedia revisions, CC BY-SA 4.0) and
/// ensures the benchmark instruments exist. An index is re-imported only when its file content changed (SHA-256), so
/// starting the API stays fast.
/// </summary>
public sealed class UniverseSeeder(UniverseService universes, AppDbContext db, IOptions<UniverseOptions> options, IHostEnvironment environment,
    ILogger<UniverseSeeder> logger)
{
    private const string ResourcePrefix = "universes/";

    public static readonly (string Symbol, string Name, string Currency, AssetType Type)[] Benchmarks =
    [
        ("SPY", "SPDR S&P 500 ETF Trust", "USD", AssetType.Etf),
        ("^GDAXI", "DAX index (performance)", "EUR", AssetType.Index),
        ("^FCHI", "CAC 40 index", "EUR", AssetType.Index),
        ("^FTSE", "FTSE 100 index", "GBP", AssetType.Index),
        ("^N225", "Nikkei 225 index", "JPY", AssetType.Index),
        ("^SSMI", "SMI index (price)", "CHF", AssetType.Index),
        ("^IBEX", "IBEX 35 index", "EUR", AssetType.Index),
        ("FTSEMIB.MI", "FTSE MIB index", "EUR", AssetType.Index),
        ("^OMX", "OMX Stockholm 30 index", "SEK", AssetType.Index),
        ("^OMXC25", "OMX Copenhagen 25 index", "DKK", AssetType.Index),
        ("^GSPTSE", "S&P/TSX Composite index", "CAD", AssetType.Index),
        ("^AXJO", "S&P/ASX 200 index", "AUD", AssetType.Index),
        ("^HSI", "Hang Seng Index", "HKD", AssetType.Index),
        ("^NSEI", "NIFTY 50 index", "INR", AssetType.Index),
        ("^STI", "Straits Times Index", "SGD", AssetType.Index),
    ];

    public async Task SeedAsync(CancellationToken ct)
    {
        foreach (var b in Benchmarks) await universes.EnsureBenchmarkAsync(b.Symbol, b.Name, b.Currency, b.Type, ct);

        var indicesCsv = Read("indices.csv") ?? throw new InvalidOperationException("Universe definition file indices.csv is missing.");
        var header = CatalogueService.SplitCsvLine(indicesCsv.Split('\n')[0]).Select(h => h.Trim()).ToList();
        foreach (var line in indicesCsv.Replace("\r\n", "\n").Split('\n').Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var cells = CatalogueService.SplitCsvLine(line);
            string? Cell(string name) => header.IndexOf(name) is var i and >= 0 && i < cells.Count && cells[i].Trim() is { Length: > 0 } v ? v : null;
            var code = Cell("code")!;
            var definition = new IndexDefinition(code, Cell("name")!, Cell("country")!, Cell("currency")!, Cell("benchmark"),
                string.Equals(Cell("is_historical"), "true", StringComparison.OrdinalIgnoreCase), Cell("description"), Cell("source")!,
                Cell("source_url"), Cell("revision"), Cell("licence"),
                DateOnly.TryParse(Cell("verified_on"), CultureInfo.InvariantCulture, out var verified) ? verified : null);

            var csv = Read($"{code}.csv");
            if (csv is null)
            {
                logger.LogWarning("Universe file {Code}.csv is missing; index {Code} not loaded", code, code);
                continue;
            }
            var hash = UniverseService.ContentHash(csv);
            var existing = await db.MarketIndices.AsNoTracking().Where(i => i.Code == code).Select(i => new { i.ContentHash }).SingleOrDefaultAsync(ct);
            if (existing?.ContentHash == hash) continue; // unchanged since the last import
            var result = await universes.ImportAsync(definition, csv, ct, recordAudit: false);
            logger.LogInformation("Universe {Code}: {Members} members ({Added} added, {Removed} removed, {New} new instruments, {Issues} issues) from {Source}",
                code, result.Members, result.Added, result.Removed, result.NewInstruments, result.Errors.Count, definition.Source);
            foreach (var error in result.Errors.Take(10)) logger.LogWarning("Universe {Code}: {Issue}", code, error);
        }
    }

    /// <summary>Reads a universe file from the override directory (if configured and present) or the embedded resources.</summary>
    private string? Read(string file)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.Directory))
        {
            var dir = Path.IsPathRooted(options.Value.Directory) ? options.Value.Directory : Path.Combine(environment.ContentRootPath, options.Value.Directory);
            var path = Path.Combine(dir, file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        using var stream = typeof(UniverseSeeder).Assembly.GetManifestResourceStream(ResourcePrefix + file);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
