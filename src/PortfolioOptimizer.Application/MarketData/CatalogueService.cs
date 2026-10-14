using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>
/// Maintains the shared instrument catalogue from CSV universe files (symbol, name, sector, industry, exchange, currency,
/// type). Metadata is only ever taken from the file and recorded with its source; nothing is inferred or generated.
/// </summary>
public sealed partial class CatalogueService(IApplicationDbContext db, IAuditLog audit, TimeProvider clock)
{
    private static readonly string[] RequiredColumns = ["symbol", "name", "sector"];

    public async Task<CatalogueImportResultDto> ImportAsync(ImportCatalogueRequest request, CancellationToken ct, bool recordAudit = true)
    {
        var (rows, errors) = Parse(request.Csv);
        if (rows.Count == 0 && errors.Count > 0) throw new ValidationException("csv", string.Join(" ", errors.Take(5)));

        var symbols = rows.Select(r => r.Symbol).ToList();
        var existing = await db.Assets.Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        int inserted = 0, updated = 0, unchanged = 0;
        foreach (var r in rows)
        {
            if (!existing.TryGetValue(r.Symbol, out var asset))
            {
                db.Assets.Add(new Asset
                {
                    Symbol = r.Symbol,
                    Name = r.Name,
                    Sector = r.Sector,
                    Industry = r.Industry,
                    Exchange = r.Exchange,
                    Currency = r.Currency,
                    AssetType = r.Type,
                    IsEnabled = request.Track,
                    MetadataSource = request.Source,
                    MetadataUpdatedAtUtc = now,
                    CreatedAtUtc = now,
                });
                inserted++;
                continue;
            }

            var changed = false;
            void Set(Func<Asset, string?> get, Action<Asset, string?> set, string? value)
            {
                if (value is null) return;
                var current = get(asset);
                if (current == value || (current is not null && !request.OverwriteMetadata)) return;
                set(asset, value);
                changed = true;
            }
            Set(a => a.Name, (a, v) => a.Name = v, r.Name);
            Set(a => a.Sector, (a, v) => a.Sector = v, r.Sector);
            Set(a => a.Industry, (a, v) => a.Industry = v, r.Industry);
            Set(a => a.Exchange, (a, v) => a.Exchange = v, r.Exchange);
            if (request.Track && !asset.IsEnabled) { asset.IsEnabled = true; changed = true; }
            if (!string.Equals(asset.Currency, r.Currency, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{r.Symbol}: catalogue currency {r.Currency} differs from the stored {asset.Currency}; currency not changed.");
            if (changed)
            {
                asset.MetadataSource = request.Source;
                asset.MetadataUpdatedAtUtc = now;
                updated++;
            }
            else unchanged++;
        }
        await db.SaveChangesAsync(ct);
        if (recordAudit)
            await audit.RecordAsync(AuditActions.CatalogueImported, "Catalogue", null,
                $"{request.Source}: {rows.Count} rows, {inserted} inserted, {updated} updated, {errors.Count} issue(s).", ct: ct);
        return new CatalogueImportResultDto(rows.Count, inserted, updated, unchanged, errors.Take(100).ToList());
    }

    internal sealed record CatalogueRow(string Symbol, string Name, string? Sector, string? Industry, string? Exchange, string Currency, AssetType Type);

    internal static (List<CatalogueRow> Rows, List<string> Errors) Parse(string csv)
    {
        var rows = new List<CatalogueRow>();
        var errors = new List<string>();
        var lines = csv.Replace("\r\n", "\n").Split('\n').Select((l, i) => (Line: l, Number: i + 1))
            .Where(x => !string.IsNullOrWhiteSpace(x.Line) && !x.Line.TrimStart().StartsWith('#')).ToList();
        if (lines.Count == 0) return (rows, ["The file contains no data rows."]);

        var header = SplitCsvLine(lines[0].Line).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var missing = RequiredColumns.Except(header).ToList();
        if (missing.Count > 0) return (rows, [$"Missing column(s): {string.Join(", ", missing)}. Expected: symbol,name,sector,industry,exchange,currency,type."]);
        int Col(string name) => header.IndexOf(name);

        var seen = new HashSet<string>();
        foreach (var (line, number) in lines.Skip(1))
        {
            var cells = SplitCsvLine(line);
            string? Cell(string name)
            {
                var i = Col(name);
                var v = i >= 0 && i < cells.Count ? cells[i].Trim() : null;
                return string.IsNullOrEmpty(v) ? null : v;
            }

            var symbol = Cell("symbol") is { } s ? Asset.NormalizeSymbol(s) : null;
            if (symbol is null || !SymbolPattern().IsMatch(symbol)) { errors.Add($"Line {number}: invalid symbol '{Cell("symbol")}'."); continue; }
            if (!seen.Add(symbol)) { errors.Add($"Line {number}: duplicate symbol {symbol} ignored."); continue; }
            var name = Cell("name");
            if (name is null) { errors.Add($"Line {number}: {symbol} has no name."); continue; }
            var currency = (Cell("currency") ?? "USD").ToUpperInvariant();
            if (!CurrencyPattern().IsMatch(currency)) { errors.Add($"Line {number}: {symbol} has an invalid currency '{currency}'."); continue; }
            var type = Cell("type") is { } t && Enum.TryParse<AssetType>(t, ignoreCase: true, out var parsed) ? parsed : AssetType.Equity;
            rows.Add(new CatalogueRow(symbol, Limit(name, 200)!, Limit(Cell("sector"), 100), Limit(Cell("industry"), 150), Limit(Cell("exchange"), 50), currency, type));
        }
        return (rows, errors);
    }

    /// <summary>RFC 4180 field splitting (quoted fields, doubled quotes).</summary>
    public static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static string? Limit(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"^[A-Z0-9.\-^=]{1,15}$")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}
