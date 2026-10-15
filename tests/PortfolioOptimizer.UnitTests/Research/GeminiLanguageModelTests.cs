using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Research;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Llm;
using PortfolioOptimizer.UnitTests.MarketData;

namespace PortfolioOptimizer.UnitTests.Research;

public sealed class GeminiLanguageModelTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Schema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new { market = new { type = new[] { "string", "null" } } }),
    };

    private static LlmRequest Request => new("research-brief", "Follow the schema.", "<user_objective>German stocks</user_objective>", Schema, 500);

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
    [InlineData(HttpStatusCode.Forbidden, LlmFailure.ProviderError)]
    [InlineData(HttpStatusCode.NotFound, LlmFailure.ProviderError)]
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
/// Opt-in (PO_LIVE_SMOKE=1 and GEMINI_API_KEY or LLM_API_KEY): the research assistant's three model uses against the real
/// Gemini API, through the same validators as in production. Costs a few thousand tokens.
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
    public async Task Brief_FromGermanText_UsesQuotedModelProposals()
    {
        var model = Live();
        Assert.True(model.IsAvailable, "Set GEMINI_API_KEY or LLM_API_KEY.");
        // The rule parser does not know these German words; the model's proposal is accepted only with a verbatim quote.
        var r = await new BriefInterpreter(model).InterpretAsync(
            new StartResearchRequest { Objective = "Ich möchte ein risikoarmes Portfolio aus deutschen Aktien, ohne Energieunternehmen." }, CancellationToken.None);
        Console.WriteLine($"brief by: {r.InterpretedBy}; market {r.Brief.Market}; risk {r.Brief.RiskPreference}; excluded {string.Join(",", r.Brief.ExcludeSectors)}; rejected: {string.Join(" | ", r.Rejected)}; tokens {r.InputTokens}+{r.OutputTokens}");
        Assert.Contains("Gemini", r.InterpretedBy);
        Assert.Equal("DE", r.Brief.Market);
        Assert.Equal(FieldOrigin.User, r.Brief.Origins["market"].Source);
    }

    [LiveSmokeFact]
    public async Task Narrative_IsEitherVerifiedOrRejected_NeverAFailure()
    {
        var config = new HypothesisConfig("MinimumVariance", 0.4, ["AAPL", "KO", "JNJ"], [], new Dictionary<string, double>(), "LedoitWolf", 504, 10);
        var run = new OptimizationRunDto(Guid.NewGuid(), Guid.Empty, DateTime.UtcNow, ObjectiveKind.MinimumVariance, OptimizationRunStatus.Succeeded, "",
            ["AAPL", "KO", "JNJ"], new DateOnly(2024, 10, 15), new DateOnly(2026, 10, 15), 504, "Mock", true, "", new PortfolioSettingsDto(),
            0.061, 0.124, 0.17, 0, 0, 0, [],
            [
                new AllocationDto("KO", "Coca-Cola", "Consumer Staples", 0.40, 0.05, 0.15, 0.35),
                new AllocationDto("JNJ", "Johnson & Johnson", "Health Care", 0.35, 0.06, 0.16, 0.33),
                new AllocationDto("AAPL", "Apple Inc.", "Information Technology", 0.25, 0.09, 0.27, 0.32),
            ],
            new RunAnalyticsDto([], [], [], [], [], null, 0, []), "");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await new InterpretationService(Live()).InterpretAsync(config, run, null, [], [], new HashSet<string> { "AAPL", "KO", "JNJ", "MSFT" }, true, CancellationToken.None);
        Console.WriteLine($"narrative ({sw.Elapsed.TotalSeconds:0.0} s): {r.ModelEventStatus} – {r.ModelEventSummary}; source {r.Interpretation.Source}; tokens {r.InputTokens}+{r.OutputTokens}");
        foreach (var s in r.Interpretation.Sections) Console.WriteLine($"  [{s.Key}] {string.Join(" ", s.Paragraphs)}");
        Assert.NotEqual("error", r.ModelEventStatus);
        Assert.True(r.InputTokens > 0);
    }
}
