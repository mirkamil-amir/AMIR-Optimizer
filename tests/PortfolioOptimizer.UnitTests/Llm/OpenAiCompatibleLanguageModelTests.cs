using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Pilot;
using PortfolioOptimizer.Infrastructure.Llm;

namespace PortfolioOptimizer.UnitTests.Llm;

/// <summary>Groq, OpenRouter, Mistral, Cerebras and OpenAI through the chat-completions API (synthetic payloads in the documented shape).</summary>
public sealed class OpenAiCompatibleLanguageModelTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Schema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "ok" }),
        ["properties"] = JsonSerializer.SerializeToElement(new { ok = new { type = "boolean" } }),
    };

    private static LlmRequest Request(string effort = "high") => new("pilot.test", "Follow the schema.", "ping", Schema, 1000, effort);

    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(Requests.Count);
        }
    }

    private static OpenAiCompatibleLanguageModel Model(StubHandler handler, string provider = "Groq", string? key = "groq-key") =>
        new(provider, "meta/llama:free", key, new HttpClient(handler), new LanguageModelOptions { MaxRetries = 1, RetryBaseSeconds = 0 },
            NullLogger<OpenAiCompatibleLanguageModel>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Ok = """
        {"id":"x","model":"meta/llama-reported","choices":[{"index":0,"message":{"role":"assistant","content":"{\"ok\": true}"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":40,"completion_tokens":6,"total_tokens":46}}
        """;

    [Fact]
    public async Task PostsToTheProvidersEndpoint_WithBearerKey_SchemaFormat_AndParsesTheAnswer()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Ok));
        var r = await Model(handler).GenerateAsync(Request(), CancellationToken.None);
        Assert.True(r.Output.GetProperty("ok").GetBoolean());
        Assert.Equal(("meta/llama-reported", 40, 6), (r.Model, r.InputTokens, r.OutputTokens));
        var sent = handler.Requests.Single();
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", sent.RequestUri!.ToString());
        Assert.Equal(("Bearer", "groq-key"), (sent.Headers.Authorization!.Scheme, sent.Headers.Authorization.Parameter));
        // No referrer or app-name header identifies the installation.
        Assert.DoesNotContain(sent.Headers, h => h.Key is "HTTP-Referer" or "X-Title");
        var body = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        Assert.Equal("meta/llama:free", body.GetProperty("model").GetString());
        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(1000 + 4096, body.GetProperty("max_tokens").GetInt32());
        Assert.Contains("\"required\":[\"ok\"]", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenAI_GetsMaxCompletionTokens_WhichItsReasoningModelsRequire()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Ok));
        await Model(handler, provider: "OpenAI").GenerateAsync(Request(), CancellationToken.None);
        var body = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal(1000 + 4096, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task AModelWithoutSchemaSupport_IsAskedOnceMoreForAPlainJsonObject()
    {
        var handler = new StubHandler(n => n == 1
            ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"response_format json_schema is not supported by this model"}}""")
            : Json(HttpStatusCode.OK, Ok));
        await Model(handler).GenerateAsync(Request(), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("json_object", JsonDocument.Parse(handler.Bodies[1]).RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, LlmFailure.RateLimited, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LlmFailure.ProviderError, true)]
    [InlineData(HttpStatusCode.Unauthorized, LlmFailure.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, LlmFailure.ModelNotFound, false)]
    [InlineData(HttpStatusCode.PaymentRequired, LlmFailure.ProviderError, false)]
    public async Task HttpErrors_BecomeTypedFailures_AfterRetryingTransientOnes(HttpStatusCode status, LlmFailure expected, bool transient)
    {
        var handler = new StubHandler(_ => Json(status, """{"error":{"message":"x"}}"""));
        var ex = await Assert.ThrowsAsync<LanguageModelException>(() => Model(handler).GenerateAsync(Request(), CancellationToken.None));
        Assert.Equal(expected, ex.Failure);
        Assert.Equal(transient ? 2 : 1, handler.Requests.Count);
    }

    [Fact]
    public async Task WithoutAKey_NothingIsSent()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Ok));
        var model = Model(handler, key: null);
        Assert.False(model.IsAvailable);
        Assert.Equal(LlmFailure.Unavailable, (await Assert.ThrowsAsync<LanguageModelException>(() => model.GenerateAsync(Request(), CancellationToken.None))).Failure);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"choices":[{"message":{"content":"{\"ok\":"},"finish_reason":"length"}]}""", LlmFailure.InvalidOutput)]
    [InlineData("""{"choices":[{"message":{"content":""},"finish_reason":"content_filter"}]}""", LlmFailure.Refused)]
    [InlineData("""{"choices":[{"message":{"content":"I think the answer is yes."},"finish_reason":"stop"}]}""", LlmFailure.InvalidOutput)]
    [InlineData("""{"choices":[]}""", LlmFailure.InvalidOutput)]
    public void TruncatedFilteredOrProseAnswers_AreFailures(string body, LlmFailure expected) =>
        Assert.Equal(expected, Assert.Throws<LanguageModelException>(() => OpenAiCompatibleLanguageModel.Parse(body, "m", 1)).Failure);

    [Theory]
    [InlineData("<think>weighing options</think>\n{\"ok\":true}")]
    [InlineData("```json\n{\"ok\":true}\n```")]
    [InlineData("<think>a</think>```\n{\"ok\":true}```")]
    public void AReasoningBlockOrCodeFence_AroundTheJson_IsRemoved(string content) =>
        Assert.Equal("{\"ok\":true}", OpenAiCompatibleLanguageModel.StripWrapping(content));

    [Fact]
    public void ModelList_ParsesProviderAndModel_KeepingColonsInTheModelId()
    {
        Assert.Empty(PilotModelFactory.ParseList(null));
        Assert.Equal([("Groq", "llama-3"), ("OpenRouter", "meta/llama:free"), ("Mistral", "")],
            PilotModelFactory.ParseList(" Groq:llama-3 ; OpenRouter:meta/llama:free,Mistral "));
    }

    [Fact]
    public void Keys_ArePerProvider_AndNeverBorrowedFromAnother()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MatrixPilot:Keys:Groq"] = " g " }).Build();
        Assert.Equal("g", PilotModelFactory.Key(config, "Groq"));
        Assert.Null(PilotModelFactory.Key(config, "Cerebras_unit_test_provider"));
    }

    [Fact]
    public void StoredList_RoundTrips_AndDescribesTheOrder()
    {
        var key = PilotModelChain.Key([new MockLanguageModel("a:free"), new MockLanguageModel("b")]);
        Assert.Equal("Mock:a:free;Mock:b", key);
        Assert.Equal([("Mock", "a:free"), ("Mock", "b")], PilotModelChain.Parse(key));
        Assert.Equal("Gemini g, then Mock a:free, then Mock b", PilotModelChain.Describe("Gemini", "g", key));
        Assert.Equal("Gemini g", PilotModelChain.Describe("Gemini", "g", ""));
    }
}

/// <summary>
/// Opt-in (PO_LIVE_SMOKE=1 and OPENAI_API_KEY; model from PO_LIVE_OPENAI_MODEL, default gpt-5.4-mini): one structured call
/// against the real OpenAI API through the OpenAI-compatible client. Costs a few hundred tokens.
/// </summary>
public sealed class LiveOpenAiSmokeTests
{
    [PortfolioOptimizer.UnitTests.MarketData.LiveSmokeFact]
    public async Task StructuredAnswer_FollowsTheSchema()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "Set OPENAI_API_KEY.");
        var modelId = Environment.GetEnvironmentVariable("PO_LIVE_OPENAI_MODEL") is { Length: > 0 } m ? m : "gpt-5.4-mini";
        var model = new OpenAiCompatibleLanguageModel("OpenAI", modelId, key, new HttpClient(), new LanguageModelOptions { MaxRetries = 1 },
            NullLogger<OpenAiCompatibleLanguageModel>.Instance);
        var schema = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            ["properties"] = JsonSerializer.SerializeToElement(new { currency = new { type = "string" } }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "currency" }),
        };
        var r = await model.GenerateAsync(new LlmRequest("pilot.live", "Name the ISO 4217 code of the currency asked for.", "The currency of Japan.",
            schema, 1000, "high", TimeSpan.FromSeconds(120)), CancellationToken.None);
        Assert.Equal("JPY", r.Output.GetProperty("currency").GetString());
        Assert.True(r.InputTokens > 0);
    }
}
