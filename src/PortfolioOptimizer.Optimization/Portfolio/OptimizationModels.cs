namespace PortfolioOptimizer.Optimization.Portfolio;

public enum OptimizationObjective
{
    /// <summary>Minimize portfolio variance wᵀΣw.</summary>
    MinimumVariance,
    /// <summary>Maximize (wᵀμ − rf) / sqrt(wᵀΣw).</summary>
    MaximumSharpe,
    /// <summary>Minimize variance subject to wᵀμ ≥ target return.</summary>
    TargetReturn,
    /// <summary>Maximize wᵀμ subject to sqrt(wᵀΣw) ≤ volatility limit.</summary>
    MaximumReturnForRisk,
    /// <summary>Equal weights over eligible assets (reference portfolio, no optimization).</summary>
    EqualWeight,
}

public enum OptimizationStatus
{
    Optimal,
    Infeasible,
    InvalidInput,
    SolverFailure,
}

/// <summary>
/// Allocation constraints. Weights are fractions of total portfolio value (equities + cash).
/// Short selling is not supported in this release: all lower bounds must be ≥ 0.
/// </summary>
public sealed record PortfolioConstraints
{
    public double MinWeight { get; init; }
    public double MaxWeight { get; init; } = 1d;

    /// <summary>Per-asset (min, max) overrides keyed by symbol.</summary>
    public IReadOnlyDictionary<string, (double Min, double Max)> AssetBounds { get; init; } =
        new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum number of non-zero positions (heuristic: iterative elimination of the smallest weight).</summary>
    public int? MaxHoldings { get; init; }

    /// <summary>Any position held must be at least this weight (heuristic: positions below are removed and the problem re-solved).</summary>
    public double? MinPositionWeight { get; init; }

    /// <summary>Fraction of the portfolio held in cash; asset weights sum to 1 − CashReserve.</summary>
    public double CashReserve { get; init; }

    /// <summary>Maximum one-way turnover ½Σ|w − w₀| relative to <see cref="OptimizationInput.CurrentWeights"/>. Ignored when there are no current holdings.</summary>
    public double? MaxTurnover { get; init; }

    /// <summary>Sector of each symbol; used for sector caps.</summary>
    public IReadOnlyDictionary<string, string> Sectors { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum aggregate weight per sector.</summary>
    public IReadOnlyDictionary<string, double> SectorMaxWeights { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> ExcludedSymbols { get; init; } = [];

    /// <summary>Annualized target return for <see cref="OptimizationObjective.TargetReturn"/>.</summary>
    public double? TargetReturn { get; init; }

    /// <summary>Annualized volatility limit for <see cref="OptimizationObjective.MaximumReturnForRisk"/>.</summary>
    public double? MaxVolatility { get; init; }

    public double InvestedFraction => 1d - CashReserve;
}

public sealed record OptimizationInput(
    IReadOnlyList<string> Symbols,
    double[] ExpectedReturns,
    double[,] Covariance,
    OptimizationObjective Objective,
    PortfolioConstraints Constraints,
    double RiskFreeRate,
    double[]? CurrentWeights = null);

public sealed record PortfolioStatistics(double ExpectedReturn, double Volatility, double? SharpeRatio);

public sealed record OptimizationResult(
    OptimizationStatus Status,
    OptimizationObjective Objective,
    IReadOnlyList<string> Symbols,
    double[]? Weights,
    PortfolioStatistics? Statistics,
    double[]? RiskContributions,
    string Message,
    IReadOnlyList<string> Diagnostics,
    int SolverIterations,
    int SolverCalls)
{
    public bool Succeeded => Status == OptimizationStatus.Optimal && Weights is not null;
}

public sealed record FrontierPoint(double ExpectedReturn, double Volatility, double? SharpeRatio, double[] Weights);
