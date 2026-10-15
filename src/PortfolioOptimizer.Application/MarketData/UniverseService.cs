using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>Index metadata as declared in <c>indices.csv</c> or by an administrator.</summary>
public sealed record IndexDefinition(
    string Code,
    string Name,
    string Country,
    string Currency,
    string? BenchmarkSymbol,
    bool IsHistorical,
    string? Description,
    string Source,
    string? SourceUrl,
    string? SourceRevision,
    string? Licence,
    DateOnly? VerifiedOn);

/// <summary>
/// Maintains index universes (markets → indices → constituents). An import replaces the index's membership with the
/// file's rows and upserts the constituents' metadata (name, sector, industry, venue, currency) from the file, which is
/// the source of truth. Instruments are never deleted: a stock that leaves an index stays in the catalogue (and keeps
/// its prices) without that membership. Selecting stocks never writes here.
/// </summary>
public sealed partial class UniverseService(IApplicationDbContext db, IAuditLog audit, TimeProvider clock)
{
    public static readonly string[] MemberColumns = ["symbol", "name", "country", "exchange", "mic", "currency", "sector", "industry", "isin", "wiki"];

    public sealed record MemberRow(string Symbol, string Name, string Country, string? Exchange, string? Mic, string Currency, string? Sector, string? Industry, string? Isin,
        string? ReferenceArticle = null);

    public async Task<UniverseImportResultDto> ImportAsync(IndexDefinition index, string membersCsv, CancellationToken ct, bool recordAudit = true)
    {
        ValidateDefinition(index);
        var (rows, errors) = ParseMembers(membersCsv);
        if (rows.Count == 0) throw new ValidationException("csv", errors.Count > 0 ? string.Join(" ", errors.Take(5)) : "The file contains no constituents.");

        var now = clock.GetUtcNow().UtcDateTime;
        var existingIndex = await db.MarketIndices.SingleOrDefaultAsync(i => i.Code == index.Code, ct);
        if (existingIndex is null)
        {
            existingIndex = new MarketIndex { Code = index.Code, Name = index.Name, Country = index.Country, Currency = index.Currency, Source = index.Source };
            db.MarketIndices.Add(existingIndex);
        }
        existingIndex.Name = index.Name;
        existingIndex.Country = index.Country;
        existingIndex.Currency = index.Currency;
        existingIndex.BenchmarkSymbol = index.BenchmarkSymbol;
        existingIndex.IsHistorical = index.IsHistorical;
        existingIndex.Description = index.Description;
        existingIndex.Source = index.Source;
        existingIndex.SourceUrl = index.SourceUrl;
        existingIndex.SourceRevision = index.SourceRevision;
        existingIndex.Licence = index.Licence;
        existingIndex.VerifiedOn = index.VerifiedOn;
        existingIndex.ContentHash = ContentHash(membersCsv);
        existingIndex.UpdatedAtUtc = now;

        var symbols = rows.Select(r => r.Symbol).ToList();
        var assets = await db.Assets.Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);
        var withPrices = (await db.HistoricalPrices.Where(p => symbols.Contains(p.Asset!.Symbol)).Select(p => p.Asset!.Symbol).Distinct().ToListAsync(ct)).ToHashSet();
        var provenance = $"{index.Source}{(index.SourceRevision is null ? "" : $" (revision {index.SourceRevision})")}";
        int inserted = 0, updated = 0;
        foreach (var r in rows)
        {
            if (!assets.TryGetValue(r.Symbol, out var asset))
            {
                asset = new Asset { Symbol = r.Symbol, IsEnabled = false, CreatedAtUtc = now, Currency = r.Currency, AssetType = AssetType.Equity };
                db.Assets.Add(asset);
                assets[r.Symbol] = asset;
                inserted++;
            }
            else if (!string.Equals(asset.Currency, r.Currency, StringComparison.OrdinalIgnoreCase))
            {
                if (withPrices.Contains(r.Symbol))
                {
                    errors.Add($"{r.Symbol}: listed in {r.Currency} but stored prices are in {asset.Currency}; currency not changed.");
                }
                else asset.Currency = r.Currency;
            }

            var changed = asset.Name != r.Name || asset.Sector != r.Sector || asset.Industry != r.Industry || asset.Country != r.Country ||
                          asset.Exchange != r.Exchange || asset.Mic != r.Mic || (r.Isin is not null && asset.Isin != r.Isin) ||
                          (r.ReferenceArticle is not null && asset.ReferenceArticle != r.ReferenceArticle);
            asset.Name = r.Name;
            asset.Sector = r.Sector;
            asset.Industry = r.Industry;
            asset.Country = r.Country;
            asset.Exchange = r.Exchange;
            asset.Mic = r.Mic;
            if (r.Isin is not null) asset.Isin = r.Isin;
            // Only a supplied reference replaces the stored one (a file without the column keeps it).
            if (r.ReferenceArticle is not null) asset.ReferenceArticle = r.ReferenceArticle;
            if (changed || asset.MetadataSource is null)
            {
                asset.MetadataSource = provenance.Length > 300 ? provenance[..300] : provenance;
                asset.MetadataUpdatedAtUtc = now;
                if (asset.Id != 0) updated++;
            }
        }
        await db.SaveChangesAsync(ct);

        // Replace the membership as a whole: the file is the complete constituent list at its revision.
        var ids = rows.Select(r => assets[r.Symbol].Id).ToHashSet();
        var current = await db.IndexMemberships.Where(m => m.IndexCode == index.Code).ToListAsync(ct);
        var removed = current.Where(m => !ids.Contains(m.AssetId)).ToList();
        db.IndexMemberships.RemoveRange(removed);
        var currentIds = current.Select(m => m.AssetId).ToHashSet();
        foreach (var id in ids.Where(id => !currentIds.Contains(id)))
            db.IndexMemberships.Add(new IndexMembership { IndexCode = index.Code, AssetId = id });
        await db.SaveChangesAsync(ct);

        var added = ids.Count(id => !currentIds.Contains(id));
        if (recordAudit)
            await audit.RecordAsync(AuditActions.CatalogueImported, "Index", index.Code,
                $"{provenance}: {rows.Count} members ({added} added, {removed.Count} removed), {inserted} new instruments, {errors.Count} issue(s).", ct: ct);
        return new UniverseImportResultDto(index.Code, rows.Count, added, removed.Count, inserted, updated, errors.Take(100).ToList());
    }

    /// <summary>Creates or updates a benchmark instrument (index level or ETF) so it can be priced and used in analyses.</summary>
    public async Task EnsureBenchmarkAsync(string symbol, string name, string currency, AssetType type, CancellationToken ct)
    {
        var s = Asset.NormalizeSymbol(symbol);
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Symbol == s, ct);
        var venue = Markets.ForSymbol(s);
        if (asset is null)
        {
            db.Assets.Add(new Asset
            {
                Symbol = s, Name = name, Currency = currency, AssetType = type, Country = venue.Country, Exchange = type == AssetType.Index ? "Index" : null,
                IsEnabled = false, MetadataSource = "Benchmark definition", MetadataUpdatedAtUtc = clock.GetUtcNow().UtcDateTime,
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync(ct);
        }
        else if (asset.Country is null)
        {
            asset.Country = venue.Country;
            await db.SaveChangesAsync(ct);
        }
    }

    // ------------------------------------------------------------------ queries for the stock browser

    public async Task<IReadOnlyList<MarketDto>> GetMarketsAsync(string activeSource, CancellationToken ct)
    {
        var indices = await db.MarketIndices.AsNoTracking().OrderBy(i => i.IsHistorical).ThenBy(i => i.Name).ToListAsync(ct);
        var counts = await db.IndexMemberships.AsNoTracking()
            .GroupBy(m => m.IndexCode)
            .Select(g => new
            {
                Code = g.Key,
                Members = g.Count(),
                WithPrices = g.Count(m => db.HistoricalPrices.Any(p => p.AssetId == m.AssetId && p.Source == activeSource)),
                WithQuotes = g.Count(m => db.LatestQuotes.Any(q => q.AssetId == m.AssetId && q.Source == activeSource)),
            })
            .ToDictionaryAsync(x => x.Code, ct);
        return indices.GroupBy(i => i.Country)
            .Select(g =>
            {
                var venue = Markets.Venues.FirstOrDefault(v => v.Country == g.Key);
                return new MarketDto(g.Key, venue?.CountryName ?? g.Key, g.Select(i =>
                {
                    counts.TryGetValue(i.Code, out var c);
                    return new MarketIndexDto(i.Code, i.Name, i.Country, i.Currency, i.BenchmarkSymbol, i.IsHistorical, i.Description,
                        c?.Members ?? 0, c?.WithPrices ?? 0, c?.WithQuotes ?? 0, i.Source, i.SourceUrl, i.SourceRevision, i.Licence, i.VerifiedOn);
                }).ToList());
            })
            .OrderBy(m => Array.FindIndex(Markets.Venues.ToArray(), v => v.Country == m.Country) is var x && x < 0 ? 99 : x)
            .ToList();
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>SHA-256 (hex) of a constituent file, ignoring line-ending differences.</summary>
    public static string ContentHash(string csv) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(csv.Replace("\r\n", "\n"))));

    internal static (List<MemberRow> Rows, List<string> Errors) ParseMembers(string csv)
    {
        var rows = new List<MemberRow>();
        var errors = new List<string>();
        var lines = csv.Replace("\r\n", "\n").Split('\n').Select((l, i) => (Line: l, Number: i + 1))
            .Where(x => !string.IsNullOrWhiteSpace(x.Line) && !x.Line.TrimStart().StartsWith('#')).ToList();
        if (lines.Count == 0) return (rows, ["The file contains no data rows."]);
        var header = CatalogueService.SplitCsvLine(lines[0].Line).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var missing = new[] { "symbol", "name", "country", "currency" }.Except(header).ToList();
        if (missing.Count > 0) return (rows, [$"Missing column(s): {string.Join(", ", missing)}. Expected: {string.Join(",", MemberColumns)}."]);

        var seen = new HashSet<string>();
        foreach (var (line, number) in lines.Skip(1))
        {
            var cells = CatalogueService.SplitCsvLine(line);
            string? Cell(string name, int max)
            {
                var i = header.IndexOf(name);
                var v = i >= 0 && i < cells.Count ? cells[i].Trim() : null;
                return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
            }
            var symbol = Cell("symbol", 20) is { } s ? Asset.NormalizeSymbol(s) : null;
            if (symbol is null || !SymbolPattern().IsMatch(symbol)) { errors.Add($"Line {number}: invalid symbol '{Cell("symbol", 40)}'."); continue; }
            if (!seen.Add(symbol)) { errors.Add($"Line {number}: duplicate symbol {symbol} ignored."); continue; }
            var name = Cell("name", 200);
            if (name is null) { errors.Add($"Line {number}: {symbol} has no name."); continue; }
            var country = Cell("country", 2)?.ToUpperInvariant();
            if (country is null || !Markets.Venues.Any(v => v.Country == country)) { errors.Add($"Line {number}: {symbol} has an unsupported country '{country}'."); continue; }
            var venue = Markets.ForSymbol(symbol);
            if (venue.Country != country) { errors.Add($"Line {number}: {symbol} does not use the {country} symbol convention (suffix '{Markets.ByCountry(country).Suffix}')."); continue; }
            var currency = Cell("currency", 3)?.ToUpperInvariant();
            if (currency is null || !CurrencyPattern().IsMatch(currency)) { errors.Add($"Line {number}: {symbol} has an invalid currency."); continue; }
            var isin = Cell("isin", 12)?.ToUpperInvariant();
            if (isin is not null && !IsinPattern().IsMatch(isin)) { errors.Add($"Line {number}: {symbol} has an invalid ISIN; ignored."); isin = null; }
            var wiki = Cell("wiki", 300);
            if (wiki is not null && !ReferencePattern().IsMatch(wiki)) { errors.Add($"Line {number}: {symbol} has an invalid wiki reference (expected lang:Title); ignored."); wiki = null; }
            rows.Add(new MemberRow(symbol, name, country, Cell("exchange", 50) ?? venue.Exchange, Cell("mic", 4) ?? venue.Mic, currency,
                Cell("sector", 100), Cell("industry", 150), isin, wiki));
        }
        return (rows, errors);
    }

    private static void ValidateDefinition(IndexDefinition index)
    {
        if (!CodePattern().IsMatch(index.Code)) throw new ValidationException("code", "Index code must be 2–20 upper-case letters or digits.");
        if (string.IsNullOrWhiteSpace(index.Name)) throw new ValidationException("name", "Index name is required.");
        if (!Markets.Venues.Any(v => v.Country == index.Country)) throw new ValidationException("country", $"Unsupported market '{index.Country}'.");
        if (!CurrencyPattern().IsMatch(index.Currency)) throw new ValidationException("currency", "Currency must be an ISO code.");
        if (string.IsNullOrWhiteSpace(index.Source)) throw new ValidationException("source", "The membership source is required.");
    }

    [GeneratedRegex(@"^[A-Z0-9.\-^=]{1,20}$")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{9}[0-9]$")]
    private static partial Regex IsinPattern();

    [GeneratedRegex("^[A-Z0-9]{2,20}$")]
    private static partial Regex CodePattern();

    /// <summary>Wikipedia language code, colon, article title (no further namespaces).</summary>
    [GeneratedRegex(@"^[a-z]{2,3}:[^:\[\]{}|#<>]+$")]
    private static partial Regex ReferencePattern();
}
