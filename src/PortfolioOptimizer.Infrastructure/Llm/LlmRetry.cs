using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace PortfolioOptimizer.Infrastructure.Llm;

/// <summary>
/// When to retry a failed model call over HTTP: only overload (5xx) and rate limits (429); the wait is the provider's own
/// (the <c>Retry-After</c> header, or Gemini's <c>retryDelay</c> in the error body), otherwise base × 3ⁿ. A wait longer than
/// <see cref="LanguageModelOptions.MaxRetryWaitSeconds"/> (a used-up daily quota) is not waited for.
/// </summary>
public static partial class LlmRetry
{
    public static bool IsRetryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>The wait before the next attempt, or null when the call must not be retried.</summary>
    public static TimeSpan? Delay(HttpStatusCode status, RetryConditionHeaderValue? retryAfter, string? body, int attempt,
        LanguageModelOptions options, DateTimeOffset now)
    {
        if (!IsRetryable(status) || attempt >= options.MaxRetries) return null;
        var asked = ProviderDelay(retryAfter, body, now);
        var wait = asked ?? TimeSpan.FromSeconds(options.RetryBaseSeconds * Math.Pow(3, attempt));
        return wait.TotalSeconds > options.MaxRetryWaitSeconds ? null : wait;
    }

    /// <summary>The wait the provider asked for, if any.</summary>
    public static TimeSpan? ProviderDelay(RetryConditionHeaderValue? retryAfter, string? body, DateTimeOffset now)
    {
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } date) return date > now ? date - now : TimeSpan.Zero;
        // Gemini: {"error": {"details": [{"@type": "...RetryInfo", "retryDelay": "37s"}]}}
        if (body is not null && RetryDelay().Match(body) is { Success: true } m &&
            double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    [GeneratedRegex("\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"")]
    private static partial Regex RetryDelay();
}
