namespace PortfolioOptimizer.Domain;

/// <summary>
/// One daily foreign-exchange observation, stored as the provider's normalized quote against the euro:
/// <see cref="UnitsPerEur"/> = units of <see cref="Currency"/> per 1 EUR (the ECB reference-rate convention, e.g. USD 1.0950).
/// Any pair is derived from two such observations (see <c>FxTable</c>); the euro itself is never stored (always 1).
/// </summary>
public class FxRate
{
    /// <summary>Provider that published the rate (e.g. ECB, MockFx); sources are never mixed in one conversion.</summary>
    public required string Source { get; set; }
    /// <summary>ISO 4217 code of the quoted currency (not EUR).</summary>
    public required string Currency { get; set; }
    /// <summary>Observation date (ECB: reference rate of that business day, published around 16:00 CET).</summary>
    public DateOnly Date { get; set; }
    public decimal UnitsPerEur { get; set; }
    public bool IsSimulated { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
}

public static class Currencies
{
    /// <summary>ISO 4217 format check (three upper-case letters). Display symbols such as "$" or "€" are never codes.</summary>
    public static bool IsIsoCode(string? code) => code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');

    public static string Normalize(string code)
    {
        var c = code.Trim().ToUpperInvariant();
        if (!IsIsoCode(c)) throw new DomainException($"\"{code}\" is not an ISO 4217 currency code.");
        return c;
    }
}
