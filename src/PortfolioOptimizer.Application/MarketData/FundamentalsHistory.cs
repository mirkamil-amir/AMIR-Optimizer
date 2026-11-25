using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>
/// Merges a listing's freshly retrieved figures into its stored ones without losing history (AMIR-41). Pure: changes
/// only the rows it is given and returns the new rows to add.
/// <list type="bullet">
/// <item>same value and currency as the current row → the row is confirmed (<see cref="FundamentalFact.RetrievedAtUtc"/> = now);</item>
/// <item>a different value or currency → the current row is superseded (<see cref="FundamentalFact.SupersededAtUtc"/> = now)
/// and a new current row is added (a restatement or correction by the source);</item>
/// <item>a period not stored yet → a new row;</item>
/// <item>a stored period the source no longer returns → left unchanged (the source keeps only about four years).</item>
/// </list>
/// Values are rounded to the stored precision first, so an unchanged figure is never mistaken for a restatement.
/// </summary>
public static class FundamentalsHistory
{
    /// <summary>Decimal places stored for <see cref="FundamentalFact.Value"/> (numeric(28, 8)).</summary>
    public const int StoredDecimals = 8;

    public sealed record MergeResult(IReadOnlyList<FundamentalFact> Added, int Confirmed, int Superseded);

    /// <param name="current">The listing's current rows of this source (no <see cref="FundamentalFact.SupersededAtUtc"/>).</param>
    public static MergeResult Merge(int assetId, string source, IReadOnlyList<FundamentalFact> current,
        IEnumerable<ProviderFundamentalFact> incoming, DateTime now)
    {
        var stored = current.Where(f => f.SupersededAtUtc is null)
            .ToDictionary(f => (f.Concept, f.PeriodType, f.PeriodEnd));
        var added = new List<FundamentalFact>();
        int confirmed = 0, superseded = 0;
        // The source may repeat a period within one response: its last value counts.
        foreach (var f in incoming.GroupBy(f => (f.Concept, f.PeriodType, f.PeriodEnd)).Select(g => g.Last()))
        {
            var value = Math.Round(f.Value, StoredDecimals, MidpointRounding.AwayFromZero);
            if (stored.TryGetValue((f.Concept, f.PeriodType, f.PeriodEnd), out var row))
            {
                if (row.Value == value && row.Currency == f.Currency)
                {
                    row.RetrievedAtUtc = now;
                    confirmed++;
                    continue;
                }
                row.SupersededAtUtc = now;
                superseded++;
            }
            added.Add(new FundamentalFact
            {
                AssetId = assetId, Source = source, Concept = f.Concept, PeriodType = f.PeriodType, PeriodEnd = f.PeriodEnd,
                Value = value, Currency = f.Currency, RetrievedAtUtc = now, FirstSeenAtUtc = now,
            });
        }
        return new MergeResult(added, confirmed, superseded);
    }
}
