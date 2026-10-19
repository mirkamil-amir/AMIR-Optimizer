using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using Xunit.Abstractions;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Opt-in benchmark (set <c>PERF_BENCH=1</c>): real API in-process on a disposable PostgreSQL database with the deterministic
/// mock provider. For each request it reports latency (median of N) and the number of SQL commands EF Core executed, so
/// query-count regressions (N+1) and slow paths are visible. Writes a JSON report to <c>PERF_BENCH_OUT</c> when set.
/// Not a pass/fail test except for the guard rails at the end.
/// </summary>
public sealed class PerformanceBenchmarkTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("PERF_BENCH") == "1";
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        _factory = await ApiFactory.CreateAsync();
        var admin = await _factory.CreateAdminSessionAsync();
        await admin.SyncAndWaitAsync(new SyncRequest { StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3) });
        (_client, _) = await _factory.CreateApprovedUserAsync(admin);
    }

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        _client.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    /// <summary>Counts EF Core commands of this process (the API runs in-process).</summary>
    private sealed class CommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _subscription;
        private readonly ConcurrentBag<IDisposable> _inner = [];
        private int _count;
        public CommandCounter() => _subscription = DiagnosticListener.AllListeners.Subscribe(this);
        public int Count => Volatile.Read(ref _count);
        public void Reset() { Interlocked.Exchange(ref _count, 0); Texts.Clear(); }
        public void OnNext(DiagnosticListener value) { if (value.Name == DbLoggerCategory.Name) _inner.Add(value.Subscribe(this)); }
        public ConcurrentQueue<string> Texts { get; } = new();
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key != RelationalEventId.CommandExecuted.Name) return;
            Interlocked.Increment(ref _count);
            if (value.Value is CommandExecutedEventData e) Texts.Enqueue(e.Command.CommandText);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { _subscription.Dispose(); foreach (var d in _inner) d.Dispose(); }
    }

    private sealed record Row(string Name, double FirstMs, double MedianMs, int Queries, int Bytes);

    [Fact]
    public async Task Benchmark()
    {
        if (!Enabled) return;
        using var counter = new CommandCounter();
        var rows = new List<Row>();

        // Two portfolios with holdings, so valuation paths do real work.
        var universe = new[] { "AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "JPM", "XOM", "JNJ", "PG", "META" };
        var ids = new List<Guid>();
        foreach (var name in new[] { "Bench A", "Bench B" })
        {
            var p = await (await _client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest { Name = name, Universe = [.. universe] })).ReadAsync<PortfolioDto>();
            ids.Add(p.Id);
        }
        var settings = new PortfolioSettingsDto { Universe = [.. universe], Objective = ObjectiveKind.MinimumVariance, MaxWeight = 0.3, LookbackDays = 504 };

        async Task<Row> Measure(string name, Func<Task<HttpResponseMessage>> call, int n = 15)
        {
            counter.Reset();
            var sw = Stopwatch.StartNew();
            var first = await call();
            var firstMs = sw.Elapsed.TotalMilliseconds;
            var queries = counter.Count;
            if (Environment.GetEnvironmentVariable("PERF_BENCH_SQL") is { Length: > 0 } show && name.StartsWith(show))
                foreach (var g in counter.Texts.GroupBy(t => System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ")[..Math.Min(160, System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Length)]).OrderByDescending(g => g.Count()))
                    output.WriteLine($"   {g.Count(),3} × {g.Key}");
            var bytes = (await first.Content.ReadAsByteArrayAsync()).Length;
            Assert.True(first.IsSuccessStatusCode, $"{name}: HTTP {(int)first.StatusCode}");
            var times = new List<double>();
            for (var i = 0; i < n; i++)
            {
                sw.Restart();
                (await call()).EnsureSuccessStatusCode();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            var row = new Row(name, Math.Round(firstMs, 1), Math.Round(times[times.Count / 2], 1), queries, bytes);
            rows.Add(row);
            output.WriteLine($"{name,-38} first {row.FirstMs,8:0.0} ms  median {row.MedianMs,8:0.0} ms  queries {row.Queries,4}  {row.Bytes,9:N0} B");
            return row;
        }

        // Optimization: each call creates and stores a run (POST), measured on the disposable database only.
        var run = await Measure("optimization run (POST, 10 stocks)", () => _client.PostJsonAsync($"/api/v1/portfolios/{ids[0]}/optimization-runs",
            new RunOptimizationRequest { Settings = settings }), n: 10);
        var runs = await (await _client.GetAsync($"/api/v1/portfolios/{ids[0]}/optimization-runs")).ReadAsync<PagedResult<OptimizationRunSummaryDto>>();
        var runId = runs.Items[0].Id;
        var proposal = await (await _client.PostJsonAsync($"/api/v1/optimization-runs/{runId}/trade-proposals", new { })).ReadAsync<TradeProposalDto>();
        var exec = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/portfolios/{ids[0]}/paper-trades")
        { Content = new StringContent(JsonSerializer.Serialize(new ExecutePaperTradesRequest { ProposalId = proposal.Id, Confirm = true }, ApiFactory.Json), System.Text.Encoding.UTF8, "application/json") };
        exec.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await _client.SendAsync(exec)).EnsureSuccessStatusCode();

        await Measure("market-data status", () => _client.GetAsync("/api/v1/market-data/status"));
        await Measure("markets", () => _client.GetAsync("/api/v1/markets"));
        await Measure("sectors", () => _client.GetAsync("/api/v1/sectors"));
        await Measure("stocks with price history", () => _client.GetAsync("/api/v1/stocks?page=1&pageSize=50&withPrices=true"));
        await Measure("stocks page", () => _client.GetAsync("/api/v1/stocks?page=1&pageSize=50&sort=name"));
        await Measure("portfolio list", () => _client.GetAsync("/api/v1/portfolios"));
        await Measure("portfolio overview", () => _client.GetAsync("/api/v1/portfolios/overview"));
        await Measure("holdings", () => _client.GetAsync($"/api/v1/portfolios/{ids[0]}/holdings"));
        await Measure("performance", () => _client.GetAsync($"/api/v1/portfolios/{ids[0]}/performance"));
        await Measure("risk", () => _client.GetAsync($"/api/v1/portfolios/{ids[0]}/risk"));
        await Measure("optimization run (GET)", () => _client.GetAsync($"/api/v1/optimization-runs/{runId}"));
        await Measure("reality check", () => _client.GetAsync($"/api/v1/optimization-runs/{runId}/reality-check"), n: 5);
        await Measure("news feed", () => _client.GetAsync("/api/v1/news?page=1&pageSize=20"));
        await Measure("watchlist", () => _client.GetAsync("/api/v1/watchlist?range=1M"));
        await Measure("stock history 1Y", () => _client.GetAsync("/api/v1/stocks/AAPL/history?range=1Y"));

        if (Environment.GetEnvironmentVariable("PERF_BENCH_OUT") is { Length: > 0 } path)
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(OptimizationRunStatus.Succeeded, runs.Items[0].Status);
        _ = run;
    }
}
