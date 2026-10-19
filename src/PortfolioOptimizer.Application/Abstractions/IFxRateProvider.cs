namespace PortfolioOptimizer.Application.Abstractions;

/// <summary>A normalized observation: <paramref name="UnitsPerEur"/> units of <paramref name="Currency"/> per 1 EUR on <paramref name="Date"/>.</summary>
public sealed record FxObservation(string Currency, DateOnly Date, decimal UnitsPerEur);

/// <summary>
/// Source of daily exchange rates. Implementations normalize the provider's quote convention to "units per 1 EUR" before
/// returning, so no caller ever sees a provider-specific direction. Responses that cannot be normalized are an error.
/// </summary>
public interface IFxRateProvider
{
    string Name { get; }
    bool IsSimulated { get; }
    /// <summary>Human-readable description of the source, its publication time and terms (shown with converted values).</summary>
    string Description { get; }
    /// <summary>Currencies the provider currently publishes against EUR (EUR itself is implicit).</summary>
    IReadOnlySet<string> SupportedCurrencies { get; }
    /// <summary>
    /// Currencies the provider no longer publishes, with the date of their last rate (e.g. BGN, replaced by the euro on
    /// 2026-01-01). Their stored history stays usable for dates up to that day; there is no current rate.
    /// </summary>
    IReadOnlyDictionary<string, DateOnly> DiscontinuedCurrencies => FxDiscontinued.None;
    Task<IReadOnlyList<FxObservation>> GetRatesAsync(IReadOnlyCollection<string> currencies, DateOnly from, DateOnly to, CancellationToken ct);
}

public static class FxDiscontinued
{
    public static readonly IReadOnlyDictionary<string, DateOnly> None = new Dictionary<string, DateOnly>();

    /// <summary>Why a discontinued currency has no current rate (shown to users).</summary>
    public static string Reason(string currency, DateOnly lastRate) => currency switch
    {
        "BGN" => $"The Bulgarian lev was replaced by the euro on 1 Jan 2026 (fixed rate 1 EUR = 1.95583 BGN). Reference rates were published until {lastRate:yyyy-MM-dd}; there is no current BGN rate.",
        _ => $"{currency} reference rates were published until {lastRate:yyyy-MM-dd}; there is no current {currency} rate.",
    };
}

/// <summary>Thrown by providers when the source cannot be reached or returns an unusable response.</summary>
public sealed class FxProviderException(string message, Exception? inner = null) : Exception(message, inner);
