namespace PortfolioOptimizer.Optimization;

/// <summary>
/// Conventions used consistently by optimization, reporting and backtesting.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Daily returns are simple returns of adjusted close prices: r[t] = P[t] / P[t-1] - 1.</item>
/// <item>Expected (annualized) return = arithmetic mean daily return × <see cref="TradingDaysPerYear"/>.</item>
/// <item>Annualized volatility = sample standard deviation (n-1) of daily returns × sqrt(<see cref="TradingDaysPerYear"/>).</item>
/// <item>Annualized (realized) return of a value series = geometric CAGR: (V_end / V_start)^(TradingDaysPerYear / n) - 1.</item>
/// <item>Sharpe = (annualized mean daily excess return) / annualized volatility, with daily rf = annual rf / TradingDaysPerYear.</item>
/// </list>
/// </remarks>
public sealed record AnalyticsConventions(int TradingDaysPerYear = 252)
{
    public static AnalyticsConventions Default { get; } = new();

    public double DailyRate(double annualRate) => annualRate / TradingDaysPerYear;
}
