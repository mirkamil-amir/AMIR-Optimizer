namespace PortfolioOptimizer.Application.Common;

/// <summary>Bound from the "MarketData" configuration section.</summary>
public sealed class MarketDataOptions
{
    public const string Section = "MarketData";

    /// <summary>Provider used on first start when no settings row exists (Yahoo, AlphaVantage or Mock).</summary>
    public string DefaultProvider { get; set; } = "Yahoo";

    /// <summary>Years of history downloaded by the scheduled refresh for assets with no data.</summary>
    public int DefaultHistoryYears { get; set; } = 5;

    public bool ScheduledRefreshEnabled { get; set; } = true;

    public double ScheduledRefreshIntervalHours { get; set; } = 12;

    /// <summary>Delay before the first scheduled refresh after start-up.</summary>
    public double ScheduledRefreshInitialDelayMinutes { get; set; } = 2;
}
