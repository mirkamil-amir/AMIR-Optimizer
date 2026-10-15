namespace PortfolioOptimizer.Application.Dtos;

public sealed record SectorWeightDto(string Sector, double Weight, int Holdings);

public sealed record ConcentrationDto(int Holdings, double LargestWeight, string LargestSymbol, double TopThreeWeight, double Hhi, double EffectiveHoldings,
    IReadOnlyList<SectorWeightDto> Sectors);

public sealed record RiskShareDto(string Symbol, double Weight, double RiskShare, double Volatility);

public sealed record CorrelationPairDto(string A, string B, double Correlation);

public sealed record RiskBreakdownDto(
    double Volatility,
    double DiversificationRatio,
    double? EffectiveRiskContributors,
    double? AverageCorrelation,
    CorrelationPairDto? HighestCorrelation,
    double? AverageCorrelationOnStressDays,
    int StressDays,
    IReadOnlyList<RiskShareDto> Contributions);

/// <summary>Historical behaviour of the weights. <see cref="Label"/> states whether the period was used to choose them.</summary>
public sealed record HistoricalCheckDto(
    string Label,
    DateOnly From,
    DateOnly To,
    int Days,
    double CumulativeReturn,
    MetricDto AnnualizedVolatility,
    MetricDto MaxDrawdown,
    MetricDto ValueAtRisk95,
    MetricDto ExpectedShortfall95,
    double WorstDay,
    double? Worst20DayReturn,
    string? BenchmarkSymbol,
    double? BenchmarkCumulativeReturn,
    MetricDto? BenchmarkMaxDrawdown,
    MetricDto? Beta);

public sealed record ScenarioWeightDto(string Symbol, double Weight, double SavedWeight);

/// <summary>The same objective and constraints re-solved under one changed assumption, compared with the saved weights.</summary>
public sealed record SensitivityScenarioDto(
    string Key,
    string Name,
    string Description,
    string Status,
    string? Message,
    double? Turnover,
    double? LargestChange,
    string? LargestChangeSymbol,
    int? Holdings,
    double? ExpectedReturn,
    double? Volatility,
    IReadOnlyList<ScenarioWeightDto> Weights);

public sealed record CostCheckDto(double CostBps, double BuildCost, double? LargestSwitchTurnover, double? SwitchCost, double? SwitchCostAtTripleBps, string Note);

/// <summary>A statement in the report, labelled by how it is supported.</summary>
/// <param name="Kind">Fact (computed from stored data), Estimate (model output), Interpretation (rule applied to a number), Caution.</param>
public sealed record RealityFindingDto(string Kind, string Topic, string Text, string? Basis);

public sealed record RealityCheckDto(
    Guid RunId,
    string Objective,
    DateTime RunCreatedAtUtc,
    string Currency,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    int Observations,
    string PriceSource,
    bool UsesSimulatedData,
    bool DataChangedSinceRun,
    ConcentrationDto Concentration,
    RiskBreakdownDto Risk,
    HistoricalCheckDto InSample,
    HistoricalCheckDto? OutOfSample,
    string? OutOfSampleUnavailableReason,
    IReadOnlyList<SensitivityScenarioDto> Scenarios,
    CostCheckDto Costs,
    IReadOnlyList<RealityFindingDto> Findings,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Limitations,
    string Methodology,
    string Disclaimer);
