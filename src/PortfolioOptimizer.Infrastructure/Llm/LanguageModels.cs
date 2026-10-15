using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;

namespace PortfolioOptimizer.Infrastructure.Llm;

public sealed class LanguageModelOptions
{
    public const string Section = "Agent:Llm";

    /// <summary>None (default – deterministic only), Anthropic, or Mock (tests and the e2e stack).</summary>
    public string Provider { get; set; } = "None";
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>Secret. Supply via environment variable Agent__Llm__ApiKey (or ANTHROPIC_API_KEY); never commit it, never send it to the browser.</summary>
    public string? ApiKey { get; set; }
    /// <summary>low, medium or high. Structured extraction and short explanations need little reasoning.</summary>
    public string Effort { get; set; } = "low";
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
}

/// <summary>No language model configured: every caller uses its deterministic path.</summary>
public sealed class DisabledLanguageModel : ILanguageModel
{
    public string Provider => "None";
    public string Model => "none";
    public bool IsAvailable => false;
    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct) =>
        throw new LanguageModelException(LlmFailure.Unavailable, "No language model is configured.");
}

/// <summary>
/// Claude via the official Anthropic SDK (Messages API, structured JSON output, server-side refusal fallback). Only the
/// prompt built by the caller is sent: redacted user text and computed values – never credentials, account data or
/// other users' data. Errors are mapped to <see cref="LanguageModelException"/>; callers fall back to deterministic rules.
/// </summary>
public sealed class AnthropicLanguageModel : ILanguageModel
{
    private readonly AnthropicClient _client;
    private readonly LanguageModelOptions _options;
    private readonly ILogger<AnthropicLanguageModel> _logger;

    public AnthropicLanguageModel(IOptions<LanguageModelOptions> options, ILogger<AnthropicLanguageModel> logger)
    {
        _options = options.Value;
        _logger = logger;
        var key = string.IsNullOrWhiteSpace(_options.ApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : _options.ApiKey;
        _client = new AnthropicClient
        {
            ApiKey = key ?? "",
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
            MaxRetries = _options.MaxRetries,
        };
        IsAvailable = !string.IsNullOrWhiteSpace(key);
    }

    public string Provider => "Anthropic";
    public string Model => _options.Model;
    public bool IsAvailable { get; }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        if (!IsAvailable) throw new LanguageModelException(LlmFailure.Unavailable, "No Anthropic API key is configured.");
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = request.MaxOutputTokens,
                System = request.Instructions,
                Messages = [new BetaMessageParam { Role = Role.User, Content = request.UserContent }],
                OutputConfig = new BetaOutputConfig
                {
                    Format = new BetaJsonOutputFormat { Schema = request.Schema.ToDictionary(kv => kv.Key, kv => kv.Value) },
                    Effort = _options.Effort switch { "high" => Effort.High, "medium" => Effort.Medium, _ => Effort.Low },
                },
                // A policy decline is re-served by a fallback model inside the same call (enabled by default for this model family).
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
            }, cancellationToken: ct);

            if (response.StopReason == "refusal")
                throw new LanguageModelException(LlmFailure.Refused, "The model declined the request.");
            if (response.StopReason == "max_tokens")
                throw new LanguageModelException(LlmFailure.InvalidOutput, "The model's answer was cut off.");
            var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            JsonElement json;
            try { json = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException ex) { throw new LanguageModelException(LlmFailure.InvalidOutput, "The model's answer was not valid JSON.", ex); }
            return new LlmResponse(json, response.Model.ToString(), (int)response.Usage.InputTokens, (int)response.Usage.OutputTokens, sw.Elapsed.TotalMilliseconds);
        }
        catch (LanguageModelException) { throw; }
        catch (AnthropicRateLimitException ex) { Log(request, ex); throw new LanguageModelException(LlmFailure.RateLimited, "The language-model provider is rate limiting requests.", ex); }
        catch (AnthropicApiException ex) { Log(request, ex); throw new LanguageModelException(LlmFailure.ProviderError, $"The language-model provider returned an error.", ex); }
        catch (AnthropicIOException ex) { Log(request, ex); throw new LanguageModelException(LlmFailure.Unavailable, "The language-model provider could not be reached.", ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new LanguageModelException(LlmFailure.Timeout, "The language model did not answer in time.", ex); }
    }

    // Logs the failure type only – never prompts, user text or keys.
    private void Log(LlmRequest request, Exception ex) => _logger.LogWarning("Language model call {Purpose} failed: {Type}", request.Purpose, ex.GetType().Name);
}

/// <summary>
/// Deterministic, scriptable model for automated tests and the e2e stack. By default it echoes nothing useful (empty
/// proposals) so the rule-based paths are exercised; tests enqueue specific outputs or failures, e.g. a narrative with a
/// fabricated number to prove the validator rejects it.
/// </summary>
public sealed class MockLanguageModel : ILanguageModel
{
    private readonly Queue<Func<LlmRequest, LlmResponse>> _scripted = new();
    private readonly Lock _gate = new();

    public string Provider => "Mock";
    public string Model => "mock-model";
    public bool IsAvailable { get; set; } = true;
    public List<LlmRequest> Requests { get; } = [];

    public void Enqueue(Func<LlmRequest, LlmResponse> next) { lock (_gate) _scripted.Enqueue(next); }

    public void EnqueueJson(string purpose, object output) =>
        Enqueue(r => r.Purpose == purpose ? Ok(JsonSerializer.SerializeToElement(output)) : throw new LanguageModelException(LlmFailure.InvalidOutput, $"Mock expected {purpose}, got {r.Purpose}."));

    public void EnqueueFailure(LlmFailure failure) => Enqueue(_ => throw new LanguageModelException(failure, $"Simulated {failure}."));

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        Func<LlmRequest, LlmResponse>? next = null;
        lock (_gate)
        {
            Requests.Add(request);
            if (_scripted.Count > 0) next = _scripted.Dequeue();
        }
        if (!IsAvailable) throw new LanguageModelException(LlmFailure.Unavailable, "Mock model disabled.");
        if (next is not null) return Task.FromResult(next(request));
        // Default: an empty proposal (forces validation paths; never fabricates content).
        return Task.FromResult(Ok(JsonSerializer.SerializeToElement(new { })));
    }

    private LlmResponse Ok(JsonElement json) => new(json, Model, 100, 50, 1);
}
