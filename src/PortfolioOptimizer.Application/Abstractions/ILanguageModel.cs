using System.Text.Json;

namespace PortfolioOptimizer.Application.Abstractions;

/// <summary>
/// One structured-output request. <paramref name="Instructions"/> are trusted (written by the application);
/// <paramref name="UserContent"/> may contain user text and other untrusted data, already redacted and fenced by the caller.
/// The model must answer with JSON matching <paramref name="Schema"/>; the caller validates it again.
/// </summary>
public sealed record LlmRequest(
    string Purpose,
    string Instructions,
    string UserContent,
    IReadOnlyDictionary<string, JsonElement> Schema,
    int MaxOutputTokens = 4000);

public sealed record LlmResponse(JsonElement Output, string Model, int InputTokens, int OutputTokens, double LatencyMs);

public enum LlmFailure { Unavailable, RateLimited, Timeout, Refused, InvalidOutput, ProviderError }

public sealed class LanguageModelException(LlmFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public LlmFailure Failure { get; } = failure;
}

/// <summary>
/// Provider-neutral language-model access. The application never depends on it for calculations: every caller has a
/// deterministic path that is used when <see cref="IsAvailable"/> is false or a call fails.
/// </summary>
public interface ILanguageModel
{
    /// <summary>Provider name for audit records (e.g. "Anthropic", "Mock", "None").</summary>
    string Provider { get; }
    string Model { get; }
    bool IsAvailable { get; }
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct);
}
