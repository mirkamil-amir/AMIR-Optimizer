namespace PortfolioOptimizer.Application.News;

public sealed class NewsOptions
{
    public const string Section = "News";

    public bool Enabled { get; set; } = true;
    /// <summary>A feed or company query is fetched again only when its last attempt is older than this (shared by all users).</summary>
    public int RefreshMinutes { get; set; } = 30;
    /// <summary>After a failed fetch, wait this long before retrying the same source.</summary>
    public int FailureBackoffMinutes { get; set; } = 10;
    /// <summary>Stored articles older than this are deleted on ingestion.</summary>
    public int RetentionDays { get; set; } = 120;
    /// <summary>
    /// Show provider thumbnails. Off by default: display rights for third-party article images were not verified (see
    /// docs/financial-news.md). With it off, a neutral placeholder is shown.
    /// </summary>
    public bool ShowImages { get; set; }
    /// <summary>At most this many watchlist companies are re-fetched per feed request (the stalest first).</summary>
    public int WatchlistRefreshLimit { get; set; } = 5;
}
