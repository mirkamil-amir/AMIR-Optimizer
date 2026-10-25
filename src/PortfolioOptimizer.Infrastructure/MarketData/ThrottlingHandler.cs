namespace PortfolioOptimizer.Infrastructure.MarketData;

/// <summary>
/// Rate limit for one provider, so a sync of many symbols never bursts against public endpoints. With one request at a
/// time (<paramref name="maxConcurrent"/> = 1) the next request starts <paramref name="minInterval"/> after the previous
/// one finished; with more, request <em>starts</em> are spaced by <paramref name="minInterval"/> and at most
/// <paramref name="maxConcurrent"/> are in flight – a fixed ceiling of requests per second that does not depend on how
/// slowly the provider answers. Retries (in the resilience pipeline above this handler) also pass through it.
/// </summary>
public sealed class ThrottlingHandler(TimeSpan minInterval, TimeProvider clock, int maxConcurrent = 1) : DelegatingHandler
{
    private readonly int _maxConcurrent = Math.Max(1, maxConcurrent);
    private readonly SemaphoreSlim _slots = new(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent));
    private readonly SemaphoreSlim _spacing = new(1, 1);
    private DateTimeOffset _nextStart = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        try
        {
            await _spacing.WaitAsync(cancellationToken);
            try
            {
                var wait = _nextStart - clock.GetUtcNow();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, cancellationToken);
                _nextStart = clock.GetUtcNow() + minInterval;
            }
            finally
            {
                _spacing.Release();
            }
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                // One at a time: keep the pause after the answer (the behaviour slow, quota-limited providers expect).
                if (_maxConcurrent == 1) _nextStart = clock.GetUtcNow() + minInterval;
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _slots.Dispose();
            _spacing.Dispose();
        }
        base.Dispose(disposing);
    }
}
