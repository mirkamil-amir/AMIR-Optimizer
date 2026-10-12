namespace PortfolioOptimizer.Infrastructure.MarketData;

public sealed class YahooFinanceOptions
{
    public const string Section = "MarketData:Yahoo";

    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://query1.finance.yahoo.com";
    public string UserAgent { get; set; } = "Mozilla/5.0 (compatible; PortfolioOptimizer/1.0; personal research)";
    /// <summary>Minimum delay between consecutive requests (self-imposed politeness limit; Yahoo publishes no official limit).</summary>
    public int MinRequestIntervalMs { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 20;
}

public sealed class AlphaVantageOptions
{
    public const string Section = "MarketData:AlphaVantage";

    public string BaseUrl { get; set; } = "https://www.alphavantage.co";
    /// <summary>Secret. Supply via environment variable MarketData__AlphaVantage__ApiKey or user-secrets; never commit it.</summary>
    public string? ApiKey { get; set; }
    /// <summary>TIME_SERIES_DAILY_ADJUSTED is a premium endpoint; enable only with a premium key.</summary>
    public bool UseAdjustedEndpoint { get; set; }
    public int MinRequestIntervalMs { get; set; } = 1500;
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class MockProviderOptions
{
    public const string Section = "MarketData:Mock";

    public bool Enabled { get; set; } = true;
    /// <summary>Simulation start; series are deterministic for a given symbol regardless of the requested range.</summary>
    public DateOnly Origin { get; set; } = new(2010, 1, 4);
}
