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
    /// <summary>Currencies the provider quotes against EUR (EUR itself is implicit).</summary>
    IReadOnlySet<string> SupportedCurrencies { get; }
    Task<IReadOnlyList<FxObservation>> GetRatesAsync(IReadOnlyCollection<string> currencies, DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>Thrown by providers when the source cannot be reached or returns an unusable response.</summary>
public sealed class FxProviderException(string message, Exception? inner = null) : Exception(message, inner);
