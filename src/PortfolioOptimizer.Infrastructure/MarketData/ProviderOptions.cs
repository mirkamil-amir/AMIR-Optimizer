namespace PortfolioOptimizer.Infrastructure.MarketData;

public sealed class YahooFinanceOptions
{
    public const string Section = "MarketData:Yahoo";

    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://query1.finance.yahoo.com";
    public string UserAgent { get; set; } = "Mozilla/5.0 (compatible; PortfolioOptimizer/1.0; personal research)";
    /// <summary>
    /// Minimum spacing between request starts (self-imposed politeness limit; Yahoo publishes no official limit). With
    /// <see cref="MaxConcurrentRequests"/> this caps the client at 1000 / interval requests per second (125 ms: 8/s).
    /// </summary>
    public int MinRequestIntervalMs { get; set; } = 125;
    /// <summary>Requests in flight at once (history downloads of a sync job run this many symbols in parallel).</summary>
    public int MaxConcurrentRequests { get; set; } = 8;
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
    /// <summary>Requests allowed per UTC day (free tier: 25). The client stops before exceeding it.</summary>
    public int DailyRequestLimit { get; set; } = 25;
}

/// <summary>
/// Counts Alpha Vantage requests per UTC day (process-wide). Free keys allow 25 requests per day; once the quota is used,
/// further requests fail fast with a clear message instead of being sent. (Requests made with the same key elsewhere are
/// not visible here; the provider then answers with a throttling note, reported as a transient error.)
/// </summary>
public sealed class AlphaVantageQuota(Microsoft.Extensions.Options.IOptions<AlphaVantageOptions> options, TimeProvider clock)
{
    private readonly Lock _gate = new();
    private DateOnly _day;
    private int _used;

    public int? Remaining
    {
        get
        {
            lock (_gate)
            {
                Roll();
                return Math.Max(0, options.Value.DailyRequestLimit - _used);
            }
        }
    }

    /// <summary>Reserves one request; false when today's quota is used up.</summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            Roll();
            if (_used >= options.Value.DailyRequestLimit) return false;
            _used++;
            return true;
        }
    }

    private void Roll()
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (today == _day) return;
        _day = today;
        _used = 0;
    }
}

public sealed class MockProviderOptions
{
    public const string Section = "MarketData:Mock";

    public bool Enabled { get; set; } = true;
    /// <summary>Simulation start; series are deterministic for a given symbol regardless of the requested range.</summary>
    public DateOnly Origin { get; set; } = new(2010, 1, 4);
}
