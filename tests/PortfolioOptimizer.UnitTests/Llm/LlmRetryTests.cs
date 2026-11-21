using System.Net;
using System.Net.Http.Headers;
using PortfolioOptimizer.Infrastructure.Llm;

namespace PortfolioOptimizer.UnitTests.Llm;

public sealed class LlmRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly LanguageModelOptions Options = new() { MaxRetries = 3, RetryBaseSeconds = 5, MaxRetryWaitSeconds = 60 };

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 15)]
    [InlineData(2, 45)]
    public void Overload_WithoutAProviderDelay_BacksOffByThree(int attempt, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), LlmRetry.Delay(HttpStatusCode.ServiceUnavailable, null, null, attempt, Options, Now));

    [Fact]
    public void AfterTheLastRetry_OrForAClientError_ThereIsNoRetry()
    {
        Assert.Null(LlmRetry.Delay(HttpStatusCode.ServiceUnavailable, null, null, 3, Options, Now));
        Assert.Null(LlmRetry.Delay(HttpStatusCode.BadRequest, null, null, 0, Options, Now));
        Assert.Null(LlmRetry.Delay(HttpStatusCode.Unauthorized, null, null, 0, Options, Now));
    }

    [Fact]
    public void TheProvidersOwnDelay_IsUsed_FromHeaderOrGeminiBody()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), LlmRetry.Delay(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)), null, 0, Options, Now));
        Assert.Equal(TimeSpan.FromSeconds(20), LlmRetry.Delay(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(Now.AddSeconds(20)), null, 0, Options, Now));
        Assert.Equal(TimeSpan.FromSeconds(37.5), LlmRetry.Delay(HttpStatusCode.TooManyRequests, null, """{"details":[{"retryDelay": "37.5s"}]}""", 0, Options, Now));
    }

    [Fact]
    public void ADelayLongerThanTheLimit_MeansNoRetry_SoTheNextModelCanTakeOver() =>
        Assert.Null(LlmRetry.Delay(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.FromHours(1)), null, 0, Options, Now));
}
