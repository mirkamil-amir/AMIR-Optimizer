using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Performance changes must not change results: batched price queries equal the per-asset reference queries, the compact
/// data summary agrees with the full status, coverage counts are unchanged, and the news feed serves stored articles while due
/// sources refresh once in the background (failures are recorded, never served as articles).
/// </summary>
public sealed class DataAccessEfficiencyTests : IAsyncLifetime
{
    private readonly ControlledFeed _feed = new();
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;
    private static readonly string[] Symbols = ["AAPL", "MSFT", "SPY"];

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync(testServices: s => s.AddSingleton<INewsFeedProvider>(_feed));
        _admin = await _factory.CreateAdminSessionAsync();
        await _admin.SyncAndWaitAsync(new SyncRequest { Symbols = [.. Symbols], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1) });
        (_user, _) = await _factory.CreateApprovedUserAsync(_admin);
    }

    public async Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<T> WithScope<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _factory.Services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    [Fact]
    public async Task BatchedPriceQueries_EqualThePerAssetReference()
    {
        await WithScope(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var prices = sp.GetRequiredService<PriceQueryService>();
            var source = (await db.Settings.SingleAsync()).ActiveProvider;
            var assets = await db.Assets.Where(a => Symbols.Contains(a.Symbol)).ToListAsync();
            var from = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-6);

            // Series: full range, a date window, and the last N per asset (incl. a symbol without data).
            foreach (var (window, lastN) in new (DateOnly? From, int? LastN)[] { (null, null), (from, null), (null, 30), (from, 5) })
            {
                var batched = await prices.GetSeriesAsync([.. Symbols, "NOPE"], source, window, null, CancellationToken.None, lastN);
                Assert.Equal(Symbols.Length, batched.Count);
                foreach (var a in assets)
                {
                    var q = db.HistoricalPrices.Where(p => p.AssetId == a.Id && p.Source == source);
                    if (window is { } f) q = q.Where(p => p.Date >= f);
                    if (lastN is { } n) q = q.OrderByDescending(p => p.Date).Take(n);
                    var reference = (await q.ToListAsync()).OrderBy(p => p.Date).ToList();
                    var adjusted = reference.All(p => p.AdjustedClose.HasValue);
                    Assert.Equal(reference.Select(p => (p.Date, (double)(adjusted ? p.AdjustedClose!.Value : p.Close))),
                        batched[a.Symbol].Points.Select(p => (p.Date, p.Price)));
                    Assert.Equal(adjusted, batched[a.Symbol].UsesAdjustedClose);
                }
            }

            // Latest price per asset: the last stored close (or a newer quote).
            var latest = await prices.GetLatestPricesAsync(assets.Select(a => a.Id).ToList(), source, 5, CancellationToken.None);
            foreach (var a in assets)
            {
                var lastClose = await db.HistoricalPrices.Where(p => p.AssetId == a.Id && p.Source == source).OrderByDescending(p => p.Date).FirstAsync();
                Assert.True(latest[a.Id].PriceDate >= lastClose.Date);
                if (latest[a.Id].Origin == "Daily close") Assert.Equal(lastClose.Close, latest[a.Id].Price);
            }

            // The instruments with stored prices.
            var withPrices = await prices.AssetIdsWithPricesAsync(source, CancellationToken.None);
            var reference2 = (await db.HistoricalPrices.Where(p => p.Source == source).Select(p => p.AssetId).Distinct().ToListAsync()).ToHashSet();
            Assert.True(reference2.SetEquals(withPrices));
            Assert.Empty(await prices.AssetIdsWithPricesAsync("NoSuchSource", CancellationToken.None));
            return 0;
        });
    }

    [Fact]
    public async Task Summary_AgreesWithTheFullStatus_AndCoverageCountsAreUnchanged()
    {
        var status = await (await _user.GetAsync("/api/v1/market-data/status")).ReadAsync<MarketDataStatusDto>();
        var summary = await (await _user.GetAsync("/api/v1/market-data/summary")).ReadAsync<MarketDataSummaryDto>();
        var enabled = status.Coverage.Where(c => c.IsEnabled).ToList();
        Assert.Equal(enabled.Count(c => c.Freshness == DataFreshness.Fresh), summary.Fresh);
        Assert.Equal(enabled.Count(c => c.Freshness == DataFreshness.Stale), summary.Stale);
        Assert.Equal(enabled.Count(c => c.Freshness == DataFreshness.Missing), summary.Missing);
        Assert.Equal(enabled.Max(c => c.LastRetrievedAtUtc), summary.LastRetrievedAtUtc);
        Assert.Equal(status.ActiveProvider, summary.ActiveProvider);
        Assert.False(summary.SyncRunning);

        // Reference counts computed directly from the tables.
        var (withPrices, members, stocks) = await WithScope(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var source = (await db.Settings.SingleAsync()).ActiveProvider;
            var ids = (await db.HistoricalPrices.Where(p => p.Source == source).Select(p => p.AssetId).Distinct().ToListAsync()).ToHashSet();
            var m = await db.IndexMemberships.Select(x => new { x.IndexCode, x.AssetId }).ToListAsync();
            var nonIndex = await db.Assets.Where(a => a.AssetType != AssetType.Index).Select(a => a.Id).ToListAsync();
            return (ids, m.ToLookup(x => x.IndexCode, x => x.AssetId), nonIndex);
        });
        Assert.NotEmpty(withPrices);
        var markets = await (await _user.GetAsync("/api/v1/markets")).ReadAsync<List<MarketDto>>();
        foreach (var index in markets.SelectMany(m => m.Indices))
        {
            Assert.Equal(members[index.Code].Count(), index.Members);
            Assert.Equal(members[index.Code].Count(withPrices.Contains), index.MembersWithPriceHistory);
        }
        var sectors = await (await _user.GetAsync("/api/v1/sectors")).ReadAsync<List<SectorDto>>();
        Assert.Equal(stocks.Count, sectors.Sum(s => s.StockCount));
        Assert.Equal(stocks.Count(withPrices.Contains), sectors.Sum(s => s.WithPriceData));
        var filtered = await (await _user.GetAsync("/api/v1/stocks?withPrices=true&pageSize=200")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Equal(stocks.Count(withPrices.Contains), filtered.TotalCount);
    }

    [Fact]
    public async Task News_DueSourcesRefreshInTheBackground_OnceForConcurrentViewers()
    {
        // First visit: never fetched – fetched in the request (there is nothing stored to show yet).
        _feed.Delay = TimeSpan.FromSeconds(1);
        var first = await (await _user.GetAsync("/api/v1/news?scope=Macro")).ReadAsync<NewsFeedDto>();
        Assert.Equal(1, _feed.Calls);
        Assert.Contains(first.Items, i => i.Headline.StartsWith("Controlled feed article 1"));

        // Make the source due; a slow provider must no longer delay the page.
        _feed.Delay = TimeSpan.FromSeconds(4);
        await MakeDueAsync();
        var sw = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _user.GetAsync("/api/v1/news?scope=Macro")));
        var elapsed = sw.Elapsed;
        var feeds = await Task.WhenAll(responses.Select(r => r.ReadAsync<NewsFeedDto>()));
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"5 concurrent views took {elapsed.TotalMilliseconds:0} ms (provider delay 4 s)");
        Assert.All(feeds, f => Assert.Contains(f.Items, i => i.Headline.StartsWith("Controlled feed article 1")));
        Assert.Contains(feeds, f => f.Refreshing);

        // Exactly one background fetch for all viewers; afterwards the new article is served and the flag clears.
        await WaitUntilAsync(async () => !(await (await _user.GetAsync("/api/v1/news?scope=Macro")).ReadAsync<NewsFeedDto>()).Refreshing);
        Assert.Equal(2, _feed.Calls);
        var after = await (await _user.GetAsync("/api/v1/news?scope=Macro")).ReadAsync<NewsFeedDto>();
        Assert.Contains(after.Items, i => i.Headline.StartsWith("Controlled feed article 2"));
    }

    [Fact]
    public async Task News_ABackgroundFailure_IsRecorded_AndNeverServedAsArticles()
    {
        _feed.Delay = TimeSpan.Zero;
        await _user.GetAsync("/api/v1/news?scope=Macro");
        var before = await (await _user.GetAsync("/api/v1/news?scope=Macro")).ReadAsync<NewsFeedDto>();
        _feed.Fail = true;
        await MakeDueAsync();
        await _user.GetAsync("/api/v1/news?scope=Macro");
        await WaitUntilAsync(async () =>
        {
            var f = await (await _user.GetAsync("/api/v1/news?scope=Macro")).ReadAsync<NewsFeedDto>();
            return !f.Refreshing && f.Sources.Any(s => s.Provider == ControlledFeed.Name && s.LastError is not null);
        });
        var after = await (await _user.GetAsync("/api/v1/news?scope=Macro")).ReadAsync<NewsFeedDto>();
        Assert.Equal(before.TotalCount, after.TotalCount);
        Assert.Contains("controlled failure", after.Sources.Single(s => s.Provider == ControlledFeed.Name).LastError);
    }

    private Task MakeDueAsync() => WithScope(async sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        return await db.NewsFetchStates.Where(s => s.Provider == ControlledFeed.Name)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastAttemptUtc, DateTime.UtcNow.AddHours(-2)));
    });

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Background news refresh did not finish.");
            await Task.Delay(200);
        }
    }

    /// <summary>A feed whose latency and failure are controlled by the test; counts its calls.</summary>
    private sealed class ControlledFeed : INewsFeedProvider
    {
        public const string Name = "ControlledFeed";
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TimeSpan Delay { get; set; }
        public bool Fail { get; set; }

        public NewsProviderDescriptor Descriptor { get; } = new(Name, "Controlled test feed", true, true, "Test double.", "Synthetic data – not real news.");

        public IReadOnlyList<NewsFeed> Feeds { get; } =
            [new("controlled", "Controlled Bank", "https://example.invalid/controlled", "US", NewsCategory.CentralBank, true, "Synthetic data – not real news.")];

        public async Task<IReadOnlyList<ProviderArticle>> GetFeedAsync(NewsFeed feed, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            await Task.Delay(Delay, ct);
            if (Fail) throw new NewsProviderException(Name, "controlled failure");
            var now = DateTime.UtcNow;
            return Enumerable.Range(1, n).Select(i => new ProviderArticle(Name, $"controlled-{i}", $"https://example.invalid/controlled/{i}",
                $"Controlled feed article {i} on interest rates", "Controlled Bank", now.AddMinutes(-i), "Synthetic summary.", null, [],
                IsOfficial: true, Region: "US", SourceCategory: NewsCategory.CentralBank, IsSimulated: true)).ToList();
        }
    }
}
