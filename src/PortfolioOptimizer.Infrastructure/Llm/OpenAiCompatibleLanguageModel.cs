using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;

namespace PortfolioOptimizer.Infrastructure.Llm;

/// <summary>
/// Providers that speak the OpenAI chat-completions API (<c>POST {base}chat/completions</c>): Groq, OpenRouter, Mistral,
/// Cerebras and OpenAI itself. Like the other providers it only sends what the caller built and maps every failure to
/// <see cref="LanguageModelException"/>. The key goes in the <c>Authorization</c> header and is never logged; no other
/// header (referrer, app name) is sent.
/// </summary>
public sealed partial class OpenAiCompatibleLanguageModel : ILanguageModel
{
    public const string HttpClientName = "openai-compatible";

    /// <summary>Known providers and their API base address (each needs its own key, <c>&lt;PROVIDER&gt;_API_KEY</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> Endpoints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Groq"] = "https://api.groq.com/openai/v1/",
        ["OpenRouter"] = "https://openrouter.ai/api/v1/",
        ["Mistral"] = "https://api.mistral.ai/v1/",
        ["Cerebras"] = "https://api.cerebras.ai/v1/",
        ["OpenAI"] = "https://api.openai.com/v1/",
    };

    private readonly HttpClient _http;
    private readonly LanguageModelOptions _options;
    private readonly ILogger<OpenAiCompatibleLanguageModel> _logger;
    private readonly Uri _endpoint;
    private readonly string? _key;

    public OpenAiCompatibleLanguageModel(string provider, string model, string? key, HttpClient http, LanguageModelOptions options,
        ILogger<OpenAiCompatibleLanguageModel> logger)
    {
        if (!Endpoints.TryGetValue(provider, out var baseUrl))
            throw new ArgumentException($"Unknown OpenAI-compatible provider {provider}.", nameof(provider));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException($"{provider} needs a model id (no default model is assumed).", nameof(model));
        Provider = provider;
        Model = model.Trim();
        _key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        _http = http;
        _options = options;
        _logger = logger;
        _endpoint = new Uri(new Uri(baseUrl), "chat/completions");
    }

    public string Provider { get; }
    public string Model { get; }
    public bool IsAvailable => _key is not null;

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        if (_key is null) throw new LanguageModelException(LlmFailure.Unavailable, $"No {Provider} API key is configured.");
        var effort = request.Effort ?? _options.Effort;
        var schemaFormat = true;
        var sw = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(request.Timeout ?? TimeSpan.FromSeconds(_options.TimeoutSeconds));
            HttpResponseMessage response;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(BuildRequest(request, Model, effort, schemaFormat, Provider)) };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                response = await _http.SendAsync(message, timeout.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new LanguageModelException(LlmFailure.Timeout, "The language model did not answer in time.", ex);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Language model call {Purpose} to {Provider} failed: {Type}", request.Purpose, Provider, ex.GetType().Name);
                throw new LanguageModelException(LlmFailure.Unavailable, "The language-model provider could not be reached.", ex);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                    return Parse(text, Model, sw.Elapsed.TotalMilliseconds);

                var status = (int)response.StatusCode;
                // Some models accept JSON output but not a schema: ask once more for a plain JSON object (the caller still
                // checks every required field).
                if (schemaFormat && response.StatusCode == HttpStatusCode.BadRequest &&
                    (text.Contains("response_format", StringComparison.OrdinalIgnoreCase) || text.Contains("json_schema", StringComparison.OrdinalIgnoreCase)))
                {
                    schemaFormat = false;
                    attempt--;
                    continue;
                }
                if (LlmRetry.Delay(response.StatusCode, response.Headers.RetryAfter, null, attempt, _options, DateTimeOffset.UtcNow) is { } wait)
                {
                    _logger.LogInformation("Language model call {Purpose} to {Provider}: HTTP {Status}, retry {Attempt} in {Seconds:0.#} s",
                        request.Purpose, Provider, status, attempt + 1, wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;
                }
                // Status only – the body can echo request details.
                _logger.LogWarning("Language model call {Purpose} to {Provider} failed: HTTP {Status}", request.Purpose, Provider, status);
                throw response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => new LanguageModelException(LlmFailure.RateLimited, "The language-model provider is rate limiting requests (quota)."),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new LanguageModelException(LlmFailure.Unauthorized, "The language-model provider rejected the API key."),
                    HttpStatusCode.NotFound => new LanguageModelException(LlmFailure.ModelNotFound, $"The model {Model} is not available for this key."),
                    _ => new LanguageModelException(LlmFailure.ProviderError, $"The language-model provider returned HTTP {status}."),
                };
            }
        }
    }

    /// <summary>
    /// The chat-completions body. The schema goes into the instructions as well, because not every model enforces
    /// <c>response_format</c>. Reasoning models count their reasoning as output, so above effort "low" the limit gets room for it.
    /// </summary>
    public static JsonObject BuildRequest(LlmRequest request, string model, string effort, bool schemaFormat = true, string provider = "")
    {
        // OpenAI's reasoning models (o-series, gpt-5 and later) reject max_tokens and count reasoning in
        // max_completion_tokens; the other providers take max_tokens.
        var limitName = provider == "OpenAI" ? "max_completion_tokens" : "max_tokens";
        var schema = new JsonObject();
        foreach (var (k, v) in request.Schema) schema[k] = JsonNode.Parse(v.GetRawText());
        var instructions = $"{request.Instructions}\n\nAnswer with exactly one JSON object (no prose, no code fence) that matches this JSON schema:\n{schema.ToJsonString()}";
        return new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = instructions },
                new JsonObject { ["role"] = "user", ["content"] = request.UserContent }),
            [limitName] = request.MaxOutputTokens + (effort == "low" ? 0 : 4096),
            ["response_format"] = schemaFormat
                ? new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "answer", ["schema"] = schema, ["strict"] = false } }
                : new JsonObject { ["type"] = "json_object" },
        };
    }

    /// <summary>Maps a chat-completions response to <see cref="LlmResponse"/>; truncated, filtered or non-JSON answers are failures.</summary>
    public static LlmResponse Parse(string responseBody, string model, double latencyMs)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(responseBody).RootElement; }
        catch (JsonException ex) { throw new LanguageModelException(LlmFailure.InvalidOutput, "The provider's response was not valid JSON.", ex); }
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new LanguageModelException(LlmFailure.InvalidOutput, "The provider returned no answer.");
        var choice = choices[0];
        switch (choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null)
        {
            case "length":
                throw new LanguageModelException(LlmFailure.InvalidOutput, "The model's answer was cut off.");
            case "content_filter":
                throw new LanguageModelException(LlmFailure.Refused, "The model declined the request (content_filter).");
        }
        var text = choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "" : "";
        JsonElement output;
        try { output = JsonDocument.Parse(StripWrapping(text)).RootElement.Clone(); }
        catch (JsonException ex) { throw new LanguageModelException(LlmFailure.InvalidOutput, "The model's answer was not valid JSON.", ex); }
        if (output.ValueKind != JsonValueKind.Object)
            throw new LanguageModelException(LlmFailure.InvalidOutput, "The model's answer was not a JSON object.");

        int Count(string name) => root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var n) && n.TryGetInt32(out var i) ? i : 0;
        var version = root.TryGetProperty("model", out var mv) && mv.ValueKind == JsonValueKind.String && mv.GetString() is { Length: > 0 } v ? v : model;
        return new LlmResponse(output, version, Count("prompt_tokens"), Count("completion_tokens"), latencyMs);
    }

    /// <summary>Removes a reasoning block (<c>&lt;think&gt;…&lt;/think&gt;</c>) and a Markdown code fence around the JSON; nothing else is changed.</summary>
    public static string StripWrapping(string text)
    {
        var t = ThinkBlock().Replace(text, "").Trim();
        var fence = CodeFence().Match(t);
        return fence.Success ? fence.Groups[1].Value.Trim() : t;
    }

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex("^```(?:json)?\\s*(.*?)\\s*```$", RegexOptions.Singleline)]
    private static partial Regex CodeFence();
}
