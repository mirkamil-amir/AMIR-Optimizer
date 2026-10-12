using System.Diagnostics;

namespace PortfolioOptimizer.Api.Infrastructure;

/// <summary>Structured one-line log per request: method, path, status, duration and trace id.</summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (!context.Request.Path.StartsWithSegments("/health"))
            {
                logger.LogInformation("HTTP {Method} {Path} responded {StatusCode} in {Elapsed:0.0} ms (trace {TraceId})",
                    context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsed, context.TraceIdentifier);
            }
        }
    }
}
