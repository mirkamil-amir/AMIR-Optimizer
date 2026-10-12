namespace PortfolioOptimizer.Infrastructure.MarketData;

/// <summary>
/// Serializes requests to one provider and enforces a minimum interval between them, so a sync of many symbols
/// never bursts against public endpoints. Retries (in the resilience pipeline above this handler) also pass through it.
/// </summary>
public sealed class ThrottlingHandler(TimeSpan minInterval, TimeProvider clock) : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _last + minInterval - clock.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            _last = clock.GetUtcNow();
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
        base.Dispose(disposing);
    }
}
