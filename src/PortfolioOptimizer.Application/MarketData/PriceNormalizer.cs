using PortfolioOptimizer.Application.Abstractions;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>Raw bar as parsed from a provider response, before validation.</summary>
public sealed record RawBar(DateOnly Date, double? Open, double? High, double? Low, double? Close, double? AdjustedClose, long? Volume);

public sealed record NormalizationResult(IReadOnlyList<ProviderBar> Bars, int Rejected, int Duplicates, IReadOnlyList<string> Warnings);

/// <summary>
/// Validates and normalizes provider bars: rejects missing/non-finite/non-positive closes, inconsistent high/low,
/// future dates and out-of-range dates; removes duplicate dates (last observation wins); sorts by date and rounds
/// raw prices to 4 and adjusted closes to 6 decimal places.
/// </summary>
public static class PriceNormalizer
{
    public static NormalizationResult Normalize(IEnumerable<RawBar> raw, DateOnly start, DateOnly end, DateOnly today)
    {
        var warnings = new List<string>();
        var rejected = 0;
        var byDate = new Dictionary<DateOnly, ProviderBar>();
        var duplicates = 0;

        foreach (var r in raw)
        {
            if (r.Date < start || r.Date > end) continue;
            if (r.Date > today)
            {
                rejected++;
                warnings.Add($"{r.Date:yyyy-MM-dd}: date is in the future.");
                continue;
            }
            if (r.Close is not { } close || !double.IsFinite(close) || close <= 0)
            {
                rejected++;
                continue; // missing observations (null rows on holidays/halts) are common and silently counted
            }
            if (r.AdjustedClose is { } adj && (!double.IsFinite(adj) || adj <= 0))
            {
                rejected++;
                warnings.Add($"{r.Date:yyyy-MM-dd}: invalid adjusted close {adj}.");
                continue;
            }
            if (r.High is { } h && r.Low is { } l && double.IsFinite(h) && double.IsFinite(l) && h < l)
            {
                rejected++;
                warnings.Add($"{r.Date:yyyy-MM-dd}: high {h} below low {l}.");
                continue;
            }

            var bar = new ProviderBar(r.Date, Dec(r.Open, 4), Dec(r.High, 4), Dec(r.Low, 4), Dec(close, 4)!.Value, Dec(r.AdjustedClose, 6),
                r.Volume is >= 0 ? r.Volume : null);
            if (byDate.ContainsKey(r.Date)) duplicates++;
            byDate[r.Date] = bar;
        }

        if (duplicates > 0) warnings.Add($"{duplicates} duplicate date(s) collapsed (last observation kept).");
        return new NormalizationResult(byDate.Values.OrderBy(b => b.Date).ToList(), rejected, duplicates, warnings.Take(20).ToList());
    }

    /// <summary>Raw prices are rounded to 4 decimals (removes float artifacts such as 263.730011); adjusted closes keep 6.</summary>
    private static decimal? Dec(double? v, int decimals) =>
        v is { } d && double.IsFinite(d) && d > 0 && d < 1e12 ? Math.Round((decimal)d, decimals, MidpointRounding.AwayFromZero) : null;
}
