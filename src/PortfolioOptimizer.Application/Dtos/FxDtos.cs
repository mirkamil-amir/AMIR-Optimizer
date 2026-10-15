namespace PortfolioOptimizer.Application.Dtos;

/// <summary>The FX source used with the active price source, its convention and the currencies it can convert.</summary>
public sealed record FxInfoDto(
    string Source,
    string Description,
    bool IsSimulated,
    IReadOnlyList<string> Currencies,
    string Convention,
    int MaxCarryForwardDays);

/// <summary>A rate in the internal convention: <paramref name="Rate"/> units of <paramref name="To"/> per one unit of <paramref name="From"/>.</summary>
public sealed record FxRateDto(
    string From,
    string To,
    double Rate,
    DateOnly ObservationDate,
    int CarriedForwardDays,
    string Source,
    bool IsSimulated,
    string Note);
