using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Opt-in (<c>PERF_BENCH=1</c>): the five Wire feeds over 100,000 stored articles and stories (a third each company,
/// market and macro; company stories linked to ten listings), inserted with SQL. Reports first and median latency per
/// feed; the guard rail fails only far above the 200 ms target, so a slow machine does not fail the suite.
/// </summary>
public sealed class WireBenchmarkTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("PERF_BENCH") == "1";
    private const int Articles = 100_000;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        _factory = await ApiFactory.CreateAsync();
        using var admin = await _factory.CreateAdminSessionAsync();
        (_client, _) = await _factory.CreateApprovedUserAsync(admin);
        foreach (var symbol in new[] { "AAPL", "MSFT", "NVDA" })
            (await _client.PostJsonAsync("/api/v1/watchlist", new AddWatchlistEntryRequest { Symbol = symbol })).EnsureStatus(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        // i = 1..N, one article per story, published one minute apart going back ~69 days; scope by i % 3.
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO "NewsArticles" ("Id","Provider","Url","Fingerprint","HeadlineKey","Headline","Publisher","PublishedAtUtc","RetrievedAtUtc",
                "Category","CategoryBasis","Scope","IsOfficialSource","IsSimulated","RuleVersion","EventType","MarketArea","Region")
            SELECT md5('a' || i)::uuid, 'Bench', 'https://bench.test/' || i, md5('f' || i), md5('k' || i), 'Bench headline number ' || i, 'Publisher ' || (i % 25),
                   now() - make_interval(mins => i), now(), 'General', 'bench',
                   CASE i % 3 WHEN 0 THEN 'Company' WHEN 1 THEN 'Market' ELSE 'Macro' END, i % 7 = 0, true, 2,
                   CASE i % 5 WHEN 0 THEN 'Results' ELSE 'General' END,
                   CASE WHEN i % 3 = 1 THEN 'Equities' END, CASE i % 4 WHEN 0 THEN 'US' WHEN 1 THEN 'EU' WHEN 2 THEN 'GB' ELSE 'JP' END
            FROM generate_series(1, {Articles}) i;
            INSERT INTO "NewsStories" ("Id","LeadArticleId","Headline","Scope","Category","EventType","EventTypeBasis","MarketArea","Region",
                "FirstPublishedAtUtc","LastPublishedAtUtc","ArticleCount","PublisherCount","HasOfficialSource","IsSimulated","BasePoints","RuleVersion","CreatedAtUtc","UpdatedAtUtc")
            SELECT md5('s' || a.i)::uuid, md5('a' || a.i)::uuid, 'Bench headline number ' || a.i,
                   CASE a.i % 3 WHEN 0 THEN 'Company' WHEN 1 THEN 'Market' ELSE 'Macro' END, 'General',
                   CASE a.i % 5 WHEN 0 THEN 'Results' ELSE 'General' END, 'bench', CASE WHEN a.i % 3 = 1 THEN 'Equities' END,
                   CASE a.i % 4 WHEN 0 THEN 'US' WHEN 1 THEN 'EU' WHEN 2 THEN 'GB' ELSE 'JP' END,
                   now() - make_interval(mins => a.i), now() - make_interval(mins => a.i), 1, 1, a.i % 7 = 0, true,
                   (CASE a.i % 5 WHEN 0 THEN 2 ELSE 0 END) + (CASE WHEN a.i % 7 = 0 THEN 1 ELSE 0 END), 1, now(), now()
            FROM generate_series(1, {Articles}) AS a(i);
            INSERT INTO "NewsStoryArticles" ("StoryId","ArticleId","JoinedAtUtc")
            SELECT md5('s' || i)::uuid, md5('a' || i)::uuid, now() FROM generate_series(1, {Articles}) i;
            INSERT INTO "NewsStoryAssets" ("StoryId","AssetId")
            SELECT md5('s' || g.i)::uuid, (ARRAY(SELECT "Id" FROM "Assets" WHERE "Symbol" IN ('AAPL','MSFT','NVDA','AMZN','GOOGL','JPM','XOM','JNJ','PG','META') ORDER BY "Symbol"))[1 + (g.i / 3) % 10]
            FROM generate_series(1, {Articles}) AS g(i) WHERE g.i % 3 = 0;
            ANALYZE "NewsArticles"; ANALYZE "NewsStories"; ANALYZE "NewsStoryArticles"; ANALYZE "NewsStoryAssets";
            """);
    }

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        _client.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    [Fact]
    public async Task WireFeeds_On100kStories()
    {
        if (!Enabled) return;
        foreach (var (name, url) in new[]
                 {
                     ("latest", "/api/v1/news/latest"), ("watchlist", "/api/v1/news/watchlist"), ("companies", "/api/v1/news/companies"),
                     ("markets", "/api/v1/news/markets"), ("macro", "/api/v1/news/macro"), ("latest page 5", ""),
                 })
        {
            var target = url;
            if (name == "latest page 5")
            {
                string? cursor = null;
                for (var i = 0; i < 4; i++)
                    cursor = (await (await _client.GetAsync($"/api/v1/news/latest{(cursor is null ? "" : $"?cursor={cursor}")}")).ReadAsync<WireFeedDto>()).NextCursor;
                target = $"/api/v1/news/latest?cursor={cursor}";
            }
            var sw = Stopwatch.StartNew();
            var first = await _client.GetAsync(target);
            var firstMs = sw.Elapsed.TotalMilliseconds;
            first.EnsureStatus(HttpStatusCode.OK);
            var times = new List<double>();
            for (var i = 0; i < 7; i++) // the news rate limit is 60 requests per minute per user; the whole run stays below it
            {
                sw.Restart();
                (await _client.GetAsync(target)).EnsureStatus(HttpStatusCode.OK);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            var median = times[times.Count / 2];
            output.WriteLine($"{name,-16} first {firstMs,7:0.0} ms  median {median,7:0.0} ms");
            Assert.True(median < 1000, $"{name}: median {median:0} ms (target 200 ms)");
        }
    }
}
