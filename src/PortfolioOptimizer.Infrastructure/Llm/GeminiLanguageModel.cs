using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;

namespace PortfolioOptimizer.Infrastructure.Llm;

/// <summary>
/// Google Gemini via the Generative Language REST API (<c>models/{model}:generateContent</c>) with structured JSON output
/// (<c>responseJsonSchema</c>). Like the Anthropic provider it only sends what the caller built – redacted user text and
/// computed values – and maps every failure to <see cref="LanguageModelException"/> so callers fall back to their rules.
/// The key is sent in the <c>x-goog-api-key</c> header (never in the URL) and is never logged.
/// </summary>
public sealed class GeminiLanguageModel : ILanguageModel
{
    public const string HttpClientName = "gemini";
    public const string DefaultModel = "gemini-3.8-flash";
    private readonly HttpClient _http;
    private readonly LanguageModelOptions _options;
    private readonly ILogger<GeminiLanguageModel> _logger;
    private readonly string? _key;

    public GeminiLanguageModel(IHttpClientFactory httpClients, IOptions<LanguageModelOptions> options, ILogger<GeminiLanguageModel> logger)
    {
        _http = httpClients.CreateClient(HttpClientName);
        _options = options.Value;
        _logger = logger;
        _key = new[] { _options.ApiKey, Environment.GetEnvironmentVariable("GEMINI_API_KEY"), Environment.GetEnvironmentVariable("LLM_API_KEY") }
            .FirstOrDefault(k => !string.IsNullOrWhiteSpace(k))?.Trim();
        Model = string.IsNullOrWhiteSpace(_options.Model) ? DefaultModel : _options.Model.Trim();
    }

    public string Provider => "Gemini";
    public string Model { get; }
    public bool IsAvailable => _key is not null;

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        if (_key is null) throw new LanguageModelException(LlmFailure.Unavailable, "No Gemini API key is configured.");
        var body = BuildRequest(request, _options.Effort);
        var sw = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            HttpResponseMessage response;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(Model)}:generateContent")
                {
                    Content = JsonContent.Create(body),
                };
                message.Headers.Add("x-goog-api-key", _key);
                response = await _http.SendAsync(message, timeout.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new LanguageModelException(LlmFailure.Timeout, "The language model did not answer in time.", ex);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Language model call {Purpose} failed: {Type}", request.Purpose, ex.GetType().Name);
                throw new LanguageModelException(LlmFailure.Unavailable, "The language-model provider could not be reached.", ex);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                    return Parse(text, Model, sw.Elapsed.TotalMilliseconds);

                var status = (int)response.StatusCode;
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;
                if (retryable && attempt < _options.MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), ct);
                    continue;
                }
                // Status only – the body can echo request details.
                _logger.LogWarning("Language model call {Purpose} failed: HTTP {Status}", request.Purpose, status);
                throw response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => new LanguageModelException(LlmFailure.RateLimited, "The language-model provider is rate limiting requests (quota)."),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new LanguageModelException(LlmFailure.ProviderError, "The language-model provider rejected the API key."),
                    HttpStatusCode.NotFound => new LanguageModelException(LlmFailure.ProviderError, $"The model {Model} is not available for this key."),
                    _ => new LanguageModelException(LlmFailure.ProviderError, $"The language-model provider returned HTTP {status}."),
                };
            }
        }
    }

    /// <summary>The generateContent body: instructions as system instruction, fenced user content, JSON schema output.</summary>
    public static JsonObject BuildRequest(LlmRequest request, string effort)
    {
        var schema = new JsonObject();
        foreach (var (k, v) in request.Schema) schema[k] = JsonNode.Parse(v.GetRawText());
        var generation = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["responseJsonSchema"] = schema,
            ["maxOutputTokens"] = request.MaxOutputTokens,
        };
        // Structured extraction and short explanations need no extended reasoning.
        if (effort == "low") generation["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.Instructions }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.UserContent }),
            }),
            ["generationConfig"] = generation,
        };
    }

    /// <summary>Maps a generateContent response to <see cref="LlmResponse"/>; blocked, truncated or non-JSON answers are failures.</summary>
    public static LlmResponse Parse(string responseBody, string model, double latencyMs)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(responseBody).RootElement; }
        catch (JsonException ex) { throw new LanguageModelException(LlmFailure.InvalidOutput, "The provider's response was not valid JSON.", ex); }

        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _))
            throw new LanguageModelException(LlmFailure.Refused, "The provider blocked the request.");
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new LanguageModelException(LlmFailure.InvalidOutput, "The provider returned no answer.");
        var candidate = candidates[0];
        var finish = candidate.TryGetProperty("finishReason", out var f) ? f.GetString() : null;
        switch (finish)
        {
            case "MAX_TOKENS":
                throw new LanguageModelException(LlmFailure.InvalidOutput, "The model's answer was cut off.");
            case "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "LANGUAGE":
                throw new LanguageModelException(LlmFailure.Refused, $"The model declined the request ({finish}).");
        }

        var text = "";
        if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            foreach (var part in parts.EnumerateArray())
                if (!(part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) && part.TryGetProperty("text", out var t))
                    text += t.GetString();
        JsonElement output;
        try { output = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException ex) { throw new LanguageModelException(LlmFailure.InvalidOutput, "The model's answer was not valid JSON.", ex); }
        if (output.ValueKind != JsonValueKind.Object)
            throw new LanguageModelException(LlmFailure.InvalidOutput, "The model's answer was not a JSON object.");

        int Count(string name) => root.TryGetProperty("usageMetadata", out var u) && u.TryGetProperty(name, out var n) && n.TryGetInt32(out var i) ? i : 0;
        var version = root.TryGetProperty("modelVersion", out var mv) && mv.GetString() is { Length: > 0 } v ? v : model;
        return new LlmResponse(output, version, Count("promptTokenCount"), Count("candidatesTokenCount") + Count("thoughtsTokenCount"), latencyMs);
    }
}
