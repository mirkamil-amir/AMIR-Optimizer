using System.Net;
using PortfolioOptimizer.Infrastructure.MarketData;

namespace PortfolioOptimizer.UnitTests.MarketData;

public class ThrottlingHandlerTests
{
    /// <summary>Answers after a fixed delay and records how many requests were in flight at once.</summary>
    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        private int _inFlight;
        public int MaxInFlight;
        public readonly List<DateTimeOffset> Starts = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (Starts) { Starts.Add(DateTimeOffset.UtcNow); MaxInFlight = Math.Max(MaxInFlight, now); }
            await Task.Delay(delay, ct);
            Interlocked.Decrement(ref _inFlight);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static HttpClient Client(SlowHandler inner, TimeSpan interval, int maxConcurrent) =>
        new(new ThrottlingHandler(interval, TimeProvider.System, maxConcurrent) { InnerHandler = inner }) { BaseAddress = new Uri("https://example.test") };

    [Fact]
    public async Task SeveralInFlight_StartsAreSpaced_AndTheSlowAnswersOverlap()
    {
        var inner = new SlowHandler(TimeSpan.FromMilliseconds(300));
        using var client = Client(inner, TimeSpan.FromMilliseconds(50), maxConcurrent: 3);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GetAsync("/x")));
        watch.Stop();

        Assert.Equal(3, inner.MaxInFlight);                                   // never more than allowed
        var starts = inner.Starts.Order().ToList();
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second - p.First >= TimeSpan.FromMilliseconds(40), "starts spaced"));
        // One at a time would take 6 × 300 ms; overlapping answers finish in about two rounds.
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1300), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task OneAtATime_PausesAfterEachAnswer()
    {
        var inner = new SlowHandler(TimeSpan.FromMilliseconds(100));
        using var client = Client(inner, TimeSpan.FromMilliseconds(100), maxConcurrent: 1);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.GetAsync("/x")));

        Assert.Equal(1, inner.MaxInFlight);
        var starts = inner.Starts.Order().ToList();
        // Answer (100 ms) plus pause (100 ms) between consecutive starts – the behaviour of quota-limited providers.
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second - p.First >= TimeSpan.FromMilliseconds(180), "pause after answer"));
    }
}
