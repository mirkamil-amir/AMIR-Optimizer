namespace PortfolioOptimizer.Application.Dtos;

/// <summary>The FX source used with the active price source, its convention and the currencies it can convert.</summary>
/// <param name="Currencies">Currencies with current rates (EUR included).</param>
/// <param name="Discontinued">Currencies no longer published: history up to <see cref="FxDiscontinuedDto.LastRateDate"/> only.</param>
public sealed record FxInfoDto(
    string Source,
    string Description,
    bool IsSimulated,
    IReadOnlyList<string> Currencies,
    string Convention,
    int MaxCarryForwardDays,
    IReadOnlyList<FxDiscontinuedDto> Discontinued);

public sealed record FxDiscontinuedDto(string Currency, DateOnly LastRateDate, string Note);

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

/// <summary>Published daily rates of one pair (only days with an observation; nothing is carried forward).</summary>
public sealed record FxHistoryDto(
    string From,
    string To,
    string Source,
    bool IsSimulated,
    IReadOnlyList<FxPointDto> Points,
    string Note);

public sealed record FxPointDto(DateOnly Date, double Rate);

/// <summary>Latest reference rates of several currencies against one base, with recent changes.</summary>
public sealed record FxBoardDto(string Base, string Source, bool IsSimulated, IReadOnlyList<FxBoardRowDto> Rows, string Note);

/// <param name="Rate">Units of <paramref name="Quote"/> per one unit of the base.</param>
/// <param name="Change1D">Relative change since the previous published rate (null when not available).</param>
/// <param name="Change1M">Relative change since the last rate at least one month earlier.</param>
public sealed record FxBoardRowDto(string Quote, double? Rate, DateOnly? ObservationDate, double? Change1D, double? Change1M, string? Problem);
