using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Fx;
using PortfolioOptimizer.Infrastructure.Persistence;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Long exchange-rate histories (regression for 2026-10-23): an 8-year ECB request took longer than the attempt timeout and
/// the 25 s request bound, nothing older than the stored five years was ever downloaded, and an ML experiment in USD excluded
/// every EUR/GBP/JPY stock with eight years of prices – also for dates its benchmark did not even cover.
/// </summary>
public sealed class FxHistoryTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly ScriptedProvider _provider = new();

    /// <summary>Simulated rates with a configurable delay, a failing call and a first available date.</summary>
    private sealed class ScriptedProvider : IFxRateProvider
    {
        private MockFxProvider _inner = null!;
        private int _calls;
        public void Init(TimeProvider clock) => _inner ??= new MockFxProvider(clock);
        public int Calls => Volatile.Read(ref _calls);
        public TimeSpan Delay { get; set; }
        public int? FailOnCall { get; set; }
        public DateOnly? FirstAvailable { get; set; }
        public string Name => _inner.Name;
        public bool IsSimulated => true;
        public string Description => "test: scripted simulated rates";
        public IReadOnlySet<string> SupportedCurrencies => _inner.SupportedCurrencies;
        public IReadOnlyDictionary<string, DateOnly> DiscontinuedCurrencies => _inner.DiscontinuedCurrencies;

        public async Task<IReadOnlyList<FxObservation>> GetRatesAsync(IReadOnlyCollection<string> currencies, DateOnly from, DateOnly to, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (call == FailOnCall) throw new FxProviderException("test: provider error");
            var all = await _inner.GetRatesAsync(currencies, from, to, ct);
            return FirstAvailable is { } first ? all.Where(o => o.Date >= first).ToList() : all;
        }
    }

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync(testServices: s =>
        {
            s.RemoveAll<IFxRateProvider>();
            s.AddSingleton<IFxRateProvider>(sp => { _provider.Init(sp.GetRequiredService<TimeProvider>()); return _provider; });
            s.RemoveAll<FxFetchThrottle>();
            s.AddSingleton(new FxFetchThrottle { ProviderWait = TimeSpan.FromSeconds(1), BackgroundProviderWait = TimeSpan.FromMinutes(2), RetryInterval = TimeSpan.Zero });
        });
        _client = await _factory.CreateAdminSessionAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<(DateOnly Min, DateOnly Max, int Count)?> StoredAsync(string currency)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dates = await db.FxRates.Where(r => r.Currency == currency).Select(r => r.Date).ToListAsync();
        return dates.Count == 0 ? null : (dates.Min(), dates.Max(), dates.Count);
    }

    private static void AssertContinuous(IReadOnlyList<FxPointDto> points)
    {
        for (var i = 1; i < points.Count; i++)
            Assert.True(points[i].Date.DayNumber - points[i - 1].Date.DayNumber <= 5, $"hole after {points[i - 1].Date}");
    }

    [Fact]
    public async Task LongHistory_IsDownloadedInChunks_KeptWhenAChunkFails_AndResumedNextTime()
    {
        _provider.FailOnCall = 2;
        var first = await (await _client.GetAsync("/api/v1/fx/history?from=EUR&to=ZAR&days=3000")).ReadAsync<FxHistoryDto>();
        Assert.Equal(2, _provider.Calls);
        // The newest two years arrived before the failure and are kept (not discarded with the failed chunk).
        Assert.InRange(first.Points.Count, 450, 560);
        AssertContinuous(first.Points);

        var second = await (await _client.GetAsync("/api/v1/fx/history?from=EUR&to=ZAR&days=3000")).ReadAsync<FxHistoryDto>();
        Assert.InRange(_provider.Calls - 2, 1, 4);                       // only the missing older part, in chunks
        Assert.True(second.Points.Count > 2100, $"{second.Points.Count} points");
        // The first published rate can be up to two days after the window start when that falls on a weekend (no ECB rates).
        Assert.True(second.Points[0].Date <= DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2997), $"first point {second.Points[0].Date}");
        AssertContinuous(second.Points);
    }

    [Fact]
    public async Task ARequestThatStopsWaiting_LeavesTheDownloadRunning_AndTheStoredRatesAreUsedAtOnce()
    {
        _provider.Delay = TimeSpan.FromSeconds(2);
        var watch = Stopwatch.StartNew();
        var first = await (await _client.GetAsync("/api/v1/fx/history?from=EUR&to=SEK&days=1500")).ReadAsync<FxHistoryDto>();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waited {watch.Elapsed}");
        Assert.True(first.Points.Count < 600);

        // Three 2-second chunks: the download finishes in the background without another request.
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1507);
        for (var i = 0; i < 100 && (await StoredAsync("SEK") is not { } s || s.Min > start.AddDays(4)); i++) await Task.Delay(200);
        var calls = _provider.Calls;
        var second = await (await _client.GetAsync("/api/v1/fx/history?from=EUR&to=SEK&days=1500")).ReadAsync<FxHistoryDto>();
        Assert.Equal(calls, _provider.Calls);                            // served from PostgreSQL, nothing cached in between
        Assert.True(second.Points.Count > 1000, $"{second.Points.Count} points");
        AssertContinuous(second.Points);
    }

    [Fact]
    public async Task MlExperiment_WaitsForRates_AndChecksThemOnlyWhereItsBenchmarkHasPrices()
    {
        // Stocks with seven years of prices, the benchmark with three; the rate source starts four years ago and is slow.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _provider.FirstAvailable = today.AddYears(-4);
        _provider.Delay = TimeSpan.FromSeconds(1.5);
        string[] stocks = ["AAPL", "MSFT", "NVDA", "SAP.DE", "SIE.DE", "7203.T"];
        await _client.SyncAndWaitAsync(new SyncRequest { Symbols = [.. stocks], StartDate = today.AddYears(-7) });
        await _client.SyncAndWaitAsync(new SyncRequest { Symbols = ["SPY"], StartDate = today.AddYears(-3) });

        var created = await (await _client.PostJsonAsync("/api/v1/ml/experiments", new CreateMlExperimentRequest
        {
            Universe = [.. stocks], BenchmarkSymbol = "SPY", ModelFamily = ModelFamily.RidgeRegression, Target = PredictionTarget.ForwardExcessReturn,
            HorizonDays = 21, HistoryYears = 6, TopK = 3, MaxWeight = 0.5, Currency = "USD",
        })).ReadAsync<MlExperimentDto>();
        MlExperimentDto done = created;
        for (var i = 0; i < 900 && done.Summary.Status is JobStatus.Queued or JobStatus.Running; i++)
        {
            await Task.Delay(200);
            done = await (await _client.GetAsync($"/api/v1/ml/experiments/{created.Summary.Id}")).ReadAsync<MlExperimentDto>();
        }
        Assert.True(done.Summary.Status == JobStatus.Succeeded, done.Summary.Error);
        Assert.DoesNotContain(done.Warnings, w => w.Contains("excluded"));
        Assert.Contains(done.Warnings, w => w.Contains("The benchmark SPY has stored prices from") && w.Contains("6 stock(s)"));

        var funnel = done.Results!.Eligibility!.ToDictionary(a => a.Symbol);
        Assert.Equal(stocks.Order(), funnel.Keys.Order());
        foreach (var symbol in new[] { "SAP.DE", "SIE.DE", "7203.T" })
        {
            var a = funnel[symbol];
            Assert.True(a.StoredPrices > a.PricesInBenchmarkSpan, symbol);         // older prices are outside the experiment calendar
            Assert.Equal(0, a.PricesWithoutFx);
            Assert.Equal(a.PricesInBenchmarkSpan, a.UsablePrices);
            Assert.True(a.Samples > 0, symbol);
        }
    }
}
