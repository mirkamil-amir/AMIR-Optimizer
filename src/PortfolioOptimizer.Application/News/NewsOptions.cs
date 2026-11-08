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
    /// <summary>Scheduled fetching of all feeds and of the company set, independent of page views (<c>News:Schedule</c>).</summary>
    public NewsScheduleOptions Schedule { get; set; } = new();
    /// <summary>Wire feed rules (<c>News:Feeds</c>).</summary>
    public WireFeedOptions Feeds { get; set; } = new();
}

public sealed class WireFeedOptions
{
    /// <summary>
    /// Stories linked only to listings that other users hold or watch are not shown (they would reveal what others follow):
    /// a story is visible when it has no listing, or one of its listings is curated, an index or benchmark, or held or
    /// watched by the viewer. Default on (owner's open question; plan default).
    /// </summary>
    public bool HideOthersInterests { get; set; } = true;
    /// <summary>At most this many stories about one company on a page of Latest.</summary>
    public int LatestPerCompanyPerPage { get; set; } = 2;
    /// <summary>Stories shown per company in Company News.</summary>
    public int StoriesPerCompany { get; set; } = 3;
}

public sealed class NewsScheduleOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Minutes between scheduler runs; each run fetches only what is due.</summary>
    public int TickMinutes { get; set; } = 10;
    public int InitialDelayMinutes { get; set; } = 2;
    /// <summary>A company in the scheduled set is fetched again after this many minutes (owner's open question, default 2 h).</summary>
    public int CompanyRefreshMinutes { get; set; } = 120;
    /// <summary>At most this many company queries per run (bounds a run at ~1 request/s per provider).</summary>
    public int CompaniesPerRun { get; set; } = 60;
    /// <summary>Curated listings fetched on schedule besides held and watched stocks (canonical symbols, e.g. "SAP.DE"). Empty until chosen.</summary>
    public List<string> CuratedSymbols { get; set; } = [];
}
