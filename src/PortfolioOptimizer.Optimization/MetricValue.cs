namespace PortfolioOptimizer.Optimization;

/// <summary>
/// A metric that is either a finite number or explicitly unavailable with a reason.
/// Used instead of returning NaN/Infinity for undefined cases (insufficient history, zero volatility, ...).
/// </summary>
public readonly record struct MetricValue(double? Value, string? UnavailableReason)
{
    public bool IsAvailable => Value.HasValue;

    public static MetricValue Of(double value) =>
        double.IsFinite(value) ? new MetricValue(value, null) : Unavailable("Result is not a finite number.");

    public static MetricValue Unavailable(string reason) => new(null, reason);

    public override string ToString() => Value?.ToString("G6") ?? $"n/a ({UnavailableReason})";
}
