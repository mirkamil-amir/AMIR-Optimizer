namespace PortfolioOptimizer.Optimization.Statistics;

/// <summary>
/// Risk and performance metrics on daily simple-return series. All functions return
/// <see cref="MetricValue.Unavailable"/> rather than NaN when a metric is undefined.
/// </summary>
public sealed class PerformanceMetrics(AnalyticsConventions conventions)
{
    public const int MinimumObservations = 2;
    private const double ZeroTolerance = 1e-14;

    public PerformanceMetrics() : this(AnalyticsConventions.Default) { }

    public AnalyticsConventions Conventions { get; } = conventions;

    public MetricValue CumulativeReturn(IReadOnlyList<double> returns) =>
        returns.Count == 0
            ? MetricValue.Unavailable("No returns available.")
            : MetricValue.Of(ReturnCalculator.CumulativeReturn(returns));

    /// <summary>Geometric annualized return (CAGR) from daily returns.</summary>
    public MetricValue AnnualizedReturn(IReadOnlyList<double> returns)
    {
        if (returns.Count < MinimumObservations) return Insufficient(returns.Count);
        var growth = 1d + ReturnCalculator.CumulativeReturn(returns);
        if (growth <= 0) return MetricValue.Unavailable("Series lost 100% of its value.");
        return MetricValue.Of(Math.Pow(growth, (double)Conventions.TradingDaysPerYear / returns.Count) - 1d);
    }

    /// <summary>Arithmetic annualized mean return (mean daily × trading days). Used as the optimizer's expected return.</summary>
    public MetricValue AnnualizedMeanReturn(IReadOnlyList<double> returns) =>
        returns.Count < MinimumObservations
            ? Insufficient(returns.Count)
            : MetricValue.Of(Descriptive.Mean(returns) * Conventions.TradingDaysPerYear);

    public MetricValue AnnualizedVolatility(IReadOnlyList<double> returns) =>
        returns.Count < MinimumObservations
            ? Insufficient(returns.Count)
            : MetricValue.Of(Math.Sqrt(Descriptive.SampleVariance(returns) * Conventions.TradingDaysPerYear));

    public MetricValue SharpeRatio(IReadOnlyList<double> returns, double annualRiskFreeRate)
    {
        if (returns.Count < MinimumObservations) return Insufficient(returns.Count);
        var sd = Math.Sqrt(Descriptive.SampleVariance(returns));
        if (sd < ZeroTolerance) return MetricValue.Unavailable("Volatility is zero; Sharpe ratio is undefined.");
        var excess = Descriptive.Mean(returns) - Conventions.DailyRate(annualRiskFreeRate);
        return MetricValue.Of(excess / sd * Math.Sqrt(Conventions.TradingDaysPerYear));
    }

    /// <summary>Sortino ratio with the daily risk-free rate as minimum acceptable return; downside deviation uses all observations.</summary>
    public MetricValue SortinoRatio(IReadOnlyList<double> returns, double annualRiskFreeRate)
    {
        if (returns.Count < MinimumObservations) return Insufficient(returns.Count);
        var rf = Conventions.DailyRate(annualRiskFreeRate);
        var downside = 0d;
        foreach (var r in returns)
        {
            var d = Math.Min(0d, r - rf);
            downside += d * d;
        }
        var dd = Math.Sqrt(downside / returns.Count);
        if (dd < ZeroTolerance) return MetricValue.Unavailable("No downside deviation; Sortino ratio is undefined.");
        return MetricValue.Of((Descriptive.Mean(returns) - rf) / dd * Math.Sqrt(Conventions.TradingDaysPerYear));
    }

    /// <summary>Maximum drawdown (as a negative fraction, e.g. -0.25) of a value series.</summary>
    public static MetricValue MaxDrawdown(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return Insufficient(values.Count);
        return MetricValue.Of(DrawdownSeries(values).Min());
    }

    /// <summary>Drawdown at each point: value / running peak - 1.</summary>
    public static double[] DrawdownSeries(IReadOnlyList<double> values)
    {
        var result = new double[values.Count];
        var peak = double.MinValue;
        for (var i = 0; i < values.Count; i++)
        {
            peak = Math.Max(peak, values[i]);
            result[i] = peak > 0 ? values[i] / peak - 1d : 0d;
        }
        return result;
    }

    /// <summary>One-day historical VaR at <paramref name="confidence"/> (e.g. 0.95), reported as a positive loss fraction.</summary>
    public static MetricValue HistoricalVaR(IReadOnlyList<double> returns, double confidence)
    {
        if (confidence is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        var required = (int)Math.Ceiling(1d / (1d - confidence));
        if (returns.Count < required)
            return MetricValue.Unavailable($"At least {required} observations are needed for {confidence:P0} VaR.");
        return MetricValue.Of(-Descriptive.Quantile(returns, 1d - confidence));
    }

    /// <summary>One-day historical expected shortfall (CVaR): mean loss on days at or beyond the VaR quantile.</summary>
    public static MetricValue HistoricalCVaR(IReadOnlyList<double> returns, double confidence)
    {
        if (confidence is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        var required = (int)Math.Ceiling(1d / (1d - confidence));
        if (returns.Count < required)
            return MetricValue.Unavailable($"At least {required} observations are needed for {confidence:P0} CVaR.");
        var q = Descriptive.Quantile(returns, 1d - confidence);
        var tail = returns.Where(r => r <= q).ToArray();
        return MetricValue.Of(-tail.Average());
    }

    public static MetricValue Beta(IReadOnlyList<double> portfolio, IReadOnlyList<double> benchmark)
    {
        if (portfolio.Count != benchmark.Count) throw new ArgumentException("Series must be aligned.");
        if (portfolio.Count < MinimumObservations) return Insufficient(portfolio.Count);
        var varB = Descriptive.SampleVariance(benchmark);
        if (varB < ZeroTolerance) return MetricValue.Unavailable("Benchmark variance is zero; beta is undefined.");
        return MetricValue.Of(Descriptive.SampleCovariance(portfolio, benchmark) / varB);
    }

    public MetricValue TrackingError(IReadOnlyList<double> portfolio, IReadOnlyList<double> benchmark)
    {
        if (portfolio.Count != benchmark.Count) throw new ArgumentException("Series must be aligned.");
        if (portfolio.Count < MinimumObservations) return Insufficient(portfolio.Count);
        var active = portfolio.Zip(benchmark, (p, b) => p - b).ToArray();
        return MetricValue.Of(Math.Sqrt(Descriptive.SampleVariance(active) * Conventions.TradingDaysPerYear));
    }

    public MetricValue InformationRatio(IReadOnlyList<double> portfolio, IReadOnlyList<double> benchmark)
    {
        var te = TrackingError(portfolio, benchmark);
        if (!te.IsAvailable) return te;
        if (te.Value < ZeroTolerance) return MetricValue.Unavailable("Tracking error is zero; information ratio is undefined.");
        var activeMean = portfolio.Zip(benchmark, (p, b) => p - b).Average();
        return MetricValue.Of(activeMean * Conventions.TradingDaysPerYear / te.Value!.Value);
    }

    /// <summary>One-way turnover: half the sum of absolute weight changes.</summary>
    public static double Turnover(IReadOnlyList<double> fromWeights, IReadOnlyList<double> toWeights)
    {
        if (fromWeights.Count != toWeights.Count) throw new ArgumentException("Weight vectors must have equal length.");
        var s = 0d;
        for (var i = 0; i < fromWeights.Count; i++) s += Math.Abs(toWeights[i] - fromWeights[i]);
        return s / 2d;
    }

    private static MetricValue Insufficient(int count) =>
        MetricValue.Unavailable($"Insufficient history: {count} observation(s), at least {MinimumObservations} required.");
}
