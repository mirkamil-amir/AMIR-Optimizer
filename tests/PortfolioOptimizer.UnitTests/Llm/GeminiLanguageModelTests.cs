using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Llm;
using PortfolioOptimizer.UnitTests.MarketData;

namespace PortfolioOptimizer.UnitTests.Llm;

public sealed class GeminiLanguageModelTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Schema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new { market = new { type = new[] { "string", "null" } } }),
    };

    private static LlmRequest Request => new("pilot.test", "Follow the schema.", "<user_objective>German stocks</user_objective>", Schema, 500);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
    }

    private static GeminiLanguageModel Model(StubHandler handler, string? key = "test-key", string? model = null) =>
        new(new Factory(handler), Options.Create(new LanguageModelOptions { Provider = "Gemini", ApiKey = key, Model = model, MaxRetries = 1 }),
            NullLogger<GeminiLanguageModel>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Shape recorded from a real gemini-3.8-flash response (2026-10-15), signature shortened.
    private const string Ok = """
        {"candidates":[{"content":{"parts":[{"text":"{\"market\": \"DE\", \"market_quote\": \"German\"}","thoughtSignature":"EmkK"}],"role":"model"},"finishReason":"STOP","index":0}],
         "usageMetadata":{"promptTokenCount":37,"candidatesTokenCount":14,"totalTokenCount":51},"modelVersion":"gemini-3.8-flash","responseId":"x"}
        """;

    [Fact]
    public async Task SendsKeyInHeader_SchemaAsJsonOutput_AndParsesTheAnswer()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Ok));
        var r = await Model(handler).GenerateAsync(Request, CancellationToken.None);
        Assert.Equal("DE", r.Output.GetProperty("market").GetString());
        Assert.Equal(("gemini-3.8-flash", 37, 14), (r.Model, r.InputTokens, r.OutputTokens));
        var sent = handler.Requests.Single();
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent", sent.RequestUri!.ToString());
        Assert.Equal("test-key", sent.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("test-key", sent.RequestUri.ToString());                       // never in the URL
        var body = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        Assert.Equal("application/json", body.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
        Assert.Equal("object", body.GetProperty("generationConfig").GetProperty("responseJsonSchema").GetProperty("type").GetString());
        Assert.Equal("Follow the schema.", body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, LlmFailure.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, LlmFailure.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, LlmFailure.ModelNotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LlmFailure.ProviderError)]
    public async Task HttpErrors_BecomeTypedFailures_AfterRetryingTransientOnes(HttpStatusCode status, LlmFailure expected)
    {
        var handler = new StubHandler(_ => Json(status, """{"error":{"message":"echo of the request"}}"""));
        var ex = await Assert.ThrowsAsync<LanguageModelException>(() => Model(handler).GenerateAsync(Request, CancellationToken.None));
        Assert.Equal(expected, ex.Failure);
        Assert.DoesNotContain("echo", ex.Message);
        var transient = status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
        Assert.Equal(transient ? 2 : 1, handler.Requests.Count);
    }

    [Fact]
    public async Task InvalidKey_AnsweredWith400_IsUnauthorized_NotRetried_AndOtherBadRequestsStayProviderErrors()
    {
        // Shape of Gemini's answer to an invalid key (reason API_KEY_INVALID), message shortened.
        var invalid = new StubHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT","details":[{"reason":"API_KEY_INVALID"}]}}"""));
        var ex = await Assert.ThrowsAsync<LanguageModelException>(() => Model(invalid).GenerateAsync(Request, CancellationToken.None));
        Assert.Equal(LlmFailure.Unauthorized, ex.Failure);
        Assert.Single(invalid.Requests);
        var other = new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":{"code":400,"status":"INVALID_ARGUMENT"}}"""));
        Assert.Equal(LlmFailure.ProviderError, (await Assert.ThrowsAsync<LanguageModelException>(() => Model(other).GenerateAsync(Request, CancellationToken.None))).Failure);
    }

    [Theory]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"{\"a\":"}]},"finishReason":"MAX_TOKENS"}]}""", LlmFailure.InvalidOutput)]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""", LlmFailure.Refused)]
    [InlineData("""{"promptFeedback":{"blockReason":"OTHER"}}""", LlmFailure.Refused)]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"not json"}]},"finishReason":"STOP"}]}""", LlmFailure.InvalidOutput)]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"[1,2]"}]},"finishReason":"STOP"}]}""", LlmFailure.InvalidOutput)]
    public void BlockedTruncatedOrMalformedAnswers_AreFailures(string body, LlmFailure expected) =>
        Assert.Equal(expected, Assert.Throws<LanguageModelException>(() => GeminiLanguageModel.Parse(body, "m", 1)).Failure);

    [Fact]
    public void ThoughtParts_AreIgnored()
    {
        const string body = """{"candidates":[{"content":{"parts":[{"text":"thinking…","thought":true},{"text":"{\"ok\":true}"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":3,"thoughtsTokenCount":7}}""";
        var r = GeminiLanguageModel.Parse(body, "m", 1);
        Assert.True(r.Output.GetProperty("ok").GetBoolean());
        Assert.Equal(10, r.OutputTokens);
    }

    [Fact]
    public async Task WithoutKey_ItIsUnavailable_AndSendsNothing()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Ok));
        var saved = (Environment.GetEnvironmentVariable("GEMINI_API_KEY"), Environment.GetEnvironmentVariable("LLM_API_KEY"));
        if (saved.Item1 is not null || saved.Item2 is not null) return;   // a developer key in the environment would make the model available
        var m = Model(handler, key: null);
        Assert.False(m.IsAvailable);
        Assert.Equal(LlmFailure.Unavailable, (await Assert.ThrowsAsync<LanguageModelException>(() => m.GenerateAsync(Request, CancellationToken.None))).Failure);
        Assert.Empty(handler.Requests);
    }
}

/// <summary>
/// Opt-in (PO_LIVE_SMOKE=1 and GEMINI_API_KEY or LLM_API_KEY): a structured call against the real
/// Gemini API (the provider Matrix Pilot can use). Costs a few hundred tokens.
/// </summary>
public sealed class LiveGeminiSmokeTests
{
    private static GeminiLanguageModel Live() => new(new LiveFactory(), Options.Create(new LanguageModelOptions { Provider = "Gemini" }),
        NullLogger<GeminiLanguageModel>.Instance);

    private sealed class LiveFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
    }

    [LiveSmokeFact]
    public async Task StructuredAnswer_FollowsTheSchema()
    {
        var model = Live();
        Assert.True(model.IsAvailable, "Set GEMINI_API_KEY or LLM_API_KEY.");
        var schema = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new { currency = new { type = "string" } }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "currency" }),
        };
        var r = await model.GenerateAsync(new LlmRequest("smoke", "Answer with the ISO 4217 code only, following the schema.",
            "Which currency is used in Germany?", schema, 500), CancellationToken.None);
        Console.WriteLine($"model {r.Model}; tokens {r.InputTokens}+{r.OutputTokens}; {r.Output}");
        Assert.Equal("EUR", r.Output.GetProperty("currency").GetString());
    }
}
