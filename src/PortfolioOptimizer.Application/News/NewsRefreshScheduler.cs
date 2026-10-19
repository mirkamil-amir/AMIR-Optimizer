namespace PortfolioOptimizer.Application.News;

/// <summary>Sources to refresh in the background: the general feeds and/or the news of these companies.</summary>
public sealed record NewsRefreshRequest(bool Feeds, IReadOnlyList<(string Symbol, string? Name)> Companies);

/// <summary>
/// Refreshes due news sources outside the user's request, so a page view never waits for providers once articles exist.
/// Requests are coalesced (one refresh at a time; concurrent requests merge). What is due and when it was last fetched stays
/// in <c>NewsFetchStates</c> (PostgreSQL), so nothing is lost on restart – the next page view schedules it again.
/// </summary>
public interface INewsRefreshScheduler
{
    void Schedule(NewsRefreshRequest request);

    /// <summary>True while a refresh is queued or running (the feed reports "updating sources").</summary>
    bool IsBusy { get; }
}
