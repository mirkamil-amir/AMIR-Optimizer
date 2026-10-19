using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Infrastructure.Fx;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// How exchange rates are fetched: a missing currency is loaded together with all others in one provider request, concurrent
/// requests share one fetch, an ended series (BGN) is never requested again, and a failing or hanging provider costs a bounded
/// wait with an explicit "no rate" instead of an error page. Provider calls are counted by a wrapper around the simulated rates.
/// </summary>
public sealed class FxFetchTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly CountingProvider _provider = new();

    private sealed class CountingProvider : IFxRateProvider
    {
        private MockFxProvider _inner = null!;
        private int _calls;
        public void Init(TimeProvider clock) => _inner ??= new MockFxProvider(clock);
        public int Calls => Volatile.Read(ref _calls);
        public List<IReadOnlyCollection<string>> Requested { get; } = [];
        /// <summary>When set, every call waits this long (cancellable) – a slow provider.</summary>
        public TimeSpan Delay { get; set; }
        public bool Fail { get; set; }
        public string Name => _inner.Name;
        public bool IsSimulated => true;
        public string Description => "test: counted simulated rates";
        public IReadOnlySet<string> SupportedCurrencies => _inner.SupportedCurrencies;
        public IReadOnlyDictionary<string, DateOnly> DiscontinuedCurrencies => _inner.DiscontinuedCurrencies;
        public async Task<IReadOnlyList<FxObservation>> GetRatesAsync(IReadOnlyCollection<string> currencies, DateOnly from, DateOnly to, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            lock (Requested) Requested.Add(currencies.ToList());
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Fail) throw new FxProviderException("test: provider down");
            return await _inner.GetRatesAsync(currencies, from, to, ct);
        }
    }

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync(testServices: s =>
        {
            s.RemoveAll<IFxRateProvider>();
            s.AddSingleton<IFxRateProvider>(sp => { _provider.Init(sp.GetRequiredService<TimeProvider>()); return _provider; });
            s.RemoveAll<FxFetchThrottle>();
            s.AddSingleton(new FxFetchThrottle { ProviderWait = TimeSpan.FromSeconds(3) });
        });
        _client = await _factory.CreateAdminSessionAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<FxInfoDto> InfoAsync() => await (await _client.GetAsync("/api/v1/fx/currencies")).ReadAsync<FxInfoDto>();

    private async Task<int> StoredCurrenciesSinceAsync(DateOnly from)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FxRates.Where(r => r.Date >= from).Select(r => r.Currency).Distinct().CountAsync();
    }

    [Fact]
    public async Task Board_LoadsEveryCurrencyInOneRequest_SoAddingCurrenciesNeedsNoProviderCall()
    {
        var info = await InfoAsync();
        var first = await (await _client.GetAsync("/api/v1/fx/board?base=EUR&quotes=USD,GBP")).ReadAsync<FxBoardDto>();
        Assert.All(first.Rows, r => Assert.NotNull(r.Rate));
        // The one fetch stored all published currencies for the board window …
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(info.Currencies.Count - 1, await StoredCurrenciesSinceAsync(today.AddDays(-30)));
        // … so the full board (every currency) is served from the database.
        var calls = _provider.Calls;
        var all = await (await _client.GetAsync($"/api/v1/fx/board?base=EUR&quotes={string.Join(',', info.Currencies)}")).ReadAsync<FxBoardDto>();
        Assert.Equal(info.Currencies.Count - 1, all.Rows.Count);
        Assert.All(all.Rows, r => Assert.NotNull(r.Rate));
        Assert.Equal(calls, _provider.Calls);
    }

    [Fact]
    public async Task ARangeStartingOnAWeekend_CountsAsStored_SoOtherCurrenciesAreNotRefetched()
    {
        // History requests start (days + 7) days back; pick a length whose start is a Saturday – nothing is published then,
        // so the first stored rate is the Monday. That must still count as covering the range.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = Enumerable.Range(300, 7).First(d => today.AddDays(-(d + 7)).DayOfWeek == DayOfWeek.Saturday);
        await _client.GetAsync($"/api/v1/fx/history?from=EUR&to=USD&days={days}");     // fetches every currency for the range
        var calls = _provider.Calls;
        foreach (var c in new[] { "ZAR", "SGD", "NZD" })
        {
            var h = await (await _client.GetAsync($"/api/v1/fx/history?from=EUR&to={c}&days={days}")).ReadAsync<FxHistoryDto>();
            Assert.True(h.Points.Count > 150);
        }
        Assert.Equal(calls, _provider.Calls);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameMissingRates_ShareOneProviderCall()
    {
        var calls = _provider.Calls;
        // Five-year chart (beyond the fetch-everything window): five parallel requests, one fetch of ZAR.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _client.GetAsync("/api/v1/fx/history?from=EUR&to=ZAR&days=1830")));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(calls + 1, _provider.Calls);
        var h = await responses[0].ReadAsync<FxHistoryDto>();
        Assert.True(h.Points.Count > 1000);
    }

    [Fact]
    public async Task Bgn_IsHistoryOnly_ExplainedOnTheBoard_AndNeverRefetched()
    {
        var info = await InfoAsync();
        Assert.DoesNotContain("BGN", info.Currencies);
        var bgn = Assert.Single(info.Discontinued);
        Assert.Equal(("BGN", new DateOnly(2025, 12, 31)), (bgn.Currency, bgn.LastRateDate));
        Assert.Contains("euro", bgn.Note);

        // Board: an explanation instead of a rate – and no provider call, however often it is asked.
        await _client.GetAsync("/api/v1/fx/board?base=EUR&quotes=USD");
        var calls = _provider.Calls;
        for (var i = 0; i < 3; i++)
        {
            var board = await (await _client.GetAsync($"/api/v1/fx/board?base=EUR&quotes=USD,BGN,{(i == 0 ? "GBP" : i == 1 ? "DKK" : "CHF")}")).ReadAsync<FxBoardDto>();
            var row = board.Rows.Single(r => r.Quote == "BGN");
            Assert.Null(row.Rate);
            Assert.Contains("replaced by the euro", row.Problem);
        }
        Assert.Equal(calls, _provider.Calls);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/fx/board?base=BGN&quotes=USD")).StatusCode);

        // History: the published rates up to 2025-12-31, nothing after; asked twice, fetched once.
        var h1 = await (await _client.GetAsync("/api/v1/fx/history?from=EUR&to=BGN&days=1830")).ReadAsync<FxHistoryDto>();
        calls = _provider.Calls;
        var h2 = await (await _client.GetAsync("/api/v1/fx/history?from=EUR&to=BGN&days=1830")).ReadAsync<FxHistoryDto>();
        Assert.Equal(calls, _provider.Calls);
        Assert.True(h1.Points.Count > 200);
        Assert.Equal(new DateOnly(2025, 12, 31), h1.Points[^1].Date);
        Assert.Equal(h1.Points.Count, h2.Points.Count);

        // Current conversions and new portfolios in BGN are refused with the reason, never converted at a guessed rate.
        var rate = await _client.GetAsync("/api/v1/fx/rate?from=BGN&to=EUR");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rate.StatusCode);
        var create = await _client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest { Name = "Lev", BaseCurrency = "BGN" });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task FailingOrHangingProvider_CostsABoundedWait_AndSaysThereIsNoRate()
    {
        try
        {
            // Other tests may already have stored the board window (~52 days); five years are missing either way.
            _provider.Fail = true;
            var calls = _provider.Calls;
            var failed = await (await _client.GetAsync("/api/v1/fx/history?from=EUR&to=MXN&days=1830")).ReadAsync<FxHistoryDto>();
            Assert.Equal(calls + 1, _provider.Calls);
            Assert.True(failed.Points.Count < 100);                          // only what was stored – nothing filled in

            _provider.Fail = false;
            _provider.Delay = TimeSpan.FromMinutes(5);
            var watch = Stopwatch.StartNew();
            var slow = await (await _client.GetAsync("/api/v1/fx/history?from=EUR&to=KRW&days=1830")).ReadAsync<FxHistoryDto>();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"waited {watch.Elapsed}");   // bounded by ProviderWait (3 s here)
            Assert.True(slow.Points.Count < 100);
        }
        finally
        {
            _provider.Fail = false;
            _provider.Delay = TimeSpan.Zero;
        }
    }
}
