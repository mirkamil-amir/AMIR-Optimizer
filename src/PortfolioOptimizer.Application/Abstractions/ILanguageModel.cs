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
    int MaxOutputTokens = 4000,
    string? Effort = null,
    TimeSpan? Timeout = null);

public sealed record LlmResponse(JsonElement Output, string Model, int InputTokens, int OutputTokens, double LatencyMs);

/// <summary>Why a model call failed. <c>Unauthorized</c>: the key was rejected; <c>ModelNotFound</c>: the model id is not available to this key.</summary>
public enum LlmFailure { Unavailable, RateLimited, Timeout, Refused, InvalidOutput, ProviderError, Unauthorized, ModelNotFound }

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

/// <summary>
/// The language models Matrix Pilot decides with: the primary (its own provider and model when <c>MatrixPilot:Provider</c>
/// is set, otherwise the shared <see cref="ILanguageModel"/>) and, in order, the fallbacks of <c>MatrixPilot:Fallbacks</c>.
/// A cycle moves to the next model only while no model has answered, so one model makes each decision (docs/matrix-pilot.md).
/// <see cref="ILanguageModel.Provider"/> and <see cref="ILanguageModel.Model"/> name the primary; calling this instance
/// directly calls the primary only.
/// </summary>
public interface IPilotLanguageModel : ILanguageModel
{
    /// <summary>The models tried after the primary, in order (empty when none is configured).</summary>
    IReadOnlyList<ILanguageModel> Fallbacks { get; }
}
