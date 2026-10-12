using System.Net;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Infrastructure.MarketData;

namespace PortfolioOptimizer.UnitTests.MarketData;

public class ProviderTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 12, 22, 0, 0, TimeSpan.Zero));

    private static YahooFinanceProvider Yahoo(StubHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("https://example.test") }, Options.Create(new YahooFinanceOptions()), Clock);

    // Shape taken from a real response (AAPL, 2025-01-02 .. 2025-01-03); 3rd row has nulls as on halted days.
    private const string ChartJson = """
    {"chart":{"result":[{"meta":{"currency":"USD","symbol":"AAPL","exchangeName":"NMS","fullExchangeName":"NasdaqGS","instrumentType":"EQUITY","gmtoffset":-18000,"longName":"Apple Inc.","regularMarketPrice":340.97,"regularMarketTime":1791835201},
    "timestamp":[1735828200,1735914600,1736173800],
    "indicators":{"quote":[{"open":[248.93,243.36,null],"high":[249.1,244.18,null],"low":[241.82,241.89,null],"close":[243.85000610351562,243.36000061035156,null],"volume":[55740700,40244100,null]}],
    "adjclose":[{"adjclose":[242.0931396484375,241.60667419433594,null]}]}}],"error":null}}
    """;

    [Fact]
    public async Task Yahoo_ParsesBars_ConvertsTimestampsToExchangeDates_AndCountsMissingRows()
    {
        var h = new StubHandler(HttpStatusCode.OK, ChartJson);
        var history = await Yahoo(h).GetHistoricalPricesAsync("AAPL", new(2025, 1, 1), new(2025, 1, 10), CancellationToken.None);

        Assert.Equal("USD", history.Currency);
        Assert.Equal("Apple Inc.", history.CompanyName);
        Assert.Equal(2, history.Bars.Count);
        Assert.Equal(new DateOnly(2025, 1, 2), history.Bars[0].Date);
        Assert.Equal(243.85m, history.Bars[0].Close);
        Assert.Equal(242.093140m, history.Bars[0].AdjustedClose);
        Assert.Equal(3, history.RawRowCount);
        Assert.Equal(1, history.RejectedRows);
        Assert.Contains("period1=", h.LastUri!.Query);
    }

    [Fact]
    public async Task Yahoo_MapsShareClassSymbols()
    {
        var h = new StubHandler(HttpStatusCode.OK, ChartJson);
        await Yahoo(h).GetHistoricalPricesAsync("BRK.B", new(2025, 1, 1), new(2025, 1, 10), CancellationToken.None);
        Assert.Contains("/BRK-B", h.LastUri!.AbsolutePath);
    }

    [Fact]
    public async Task Yahoo_UnknownSymbol_ThrowsProviderException()
    {
        var body = """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""";
        var ex = await Assert.ThrowsAsync<MarketDataProviderException>(() =>
            Yahoo(new StubHandler(HttpStatusCode.NotFound, body)).GetHistoricalPricesAsync("ZZZZ", new(2025, 1, 1), new(2025, 1, 10), CancellationToken.None));
        Assert.Contains("delisted", ex.Message);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Yahoo_HtmlInsteadOfJson_IsReportedClearly()
    {
        var ex = await Assert.ThrowsAsync<MarketDataProviderException>(() =>
            Yahoo(new StubHandler(HttpStatusCode.OK, "<html>bot check</html>")).GetHistoricalPricesAsync("AAPL", new(2025, 1, 1), new(2025, 1, 10), CancellationToken.None));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public async Task Yahoo_Quote_ParsesRegularMarketPrice()
    {
        var q = await Yahoo(new StubHandler(HttpStatusCode.OK, ChartJson)).GetLatestQuoteAsync("AAPL", CancellationToken.None);
        Assert.NotNull(q);
        Assert.Equal(340.97m, q!.Price);
        Assert.Equal("USD", q.Currency);
    }

    [Fact]
    public async Task AlphaVantage_ThrottleMessage_IsTransientAndRedactsKey()
    {
        var body = """{"Information":"Thank you for using Alpha Vantage! Our standard API rate limit is 25 requests per day. key SECRETKEY"}""";
        var provider = new AlphaVantageProvider(new HttpClient(new StubHandler(HttpStatusCode.OK, body)) { BaseAddress = new Uri("https://example.test") },
            Options.Create(new AlphaVantageOptions { ApiKey = "SECRETKEY" }), Clock);
        var ex = await Assert.ThrowsAsync<MarketDataProviderException>(() =>
            provider.GetHistoricalPricesAsync("IBM", new(2026, 9, 1), new(2026, 10, 1), CancellationToken.None));
        Assert.True(ex.IsTransient);
        Assert.DoesNotContain("SECRETKEY", ex.Message);
    }

    [Fact]
    public async Task AlphaVantage_ParsesDailySeries()
    {
        var body = """
        {"Meta Data":{"2. Symbol":"IBM"},"Time Series (Daily)":{
          "2026-10-09":{"1. open":"227.7750","2. high":"229.2850","3. low":"226.1000","4. close":"228.5000","5. volume":"3100000"},
          "2026-10-08":{"1. open":"225.0000","2. high":"228.0000","3. low":"224.5000","4. close":"227.0000","5. volume":"2900000"}}}
        """;
        var provider = new AlphaVantageProvider(new HttpClient(new StubHandler(HttpStatusCode.OK, body)) { BaseAddress = new Uri("https://example.test") },
            Options.Create(new AlphaVantageOptions { ApiKey = "k" }), Clock);
        var h = await provider.GetHistoricalPricesAsync("IBM", new(2026, 10, 1), new(2026, 10, 12), CancellationToken.None);
        Assert.Equal(2, h.Bars.Count);
        Assert.Equal(new DateOnly(2026, 10, 8), h.Bars[0].Date);
        Assert.Null(h.Bars[0].AdjustedClose);
        Assert.Equal(228.5m, h.Bars[1].Close);
    }

    [Fact]
    public async Task Mock_IsDeterministicAndFlaggedSimulated()
    {
        var mock = new MockMarketDataProvider(Options.Create(new MockProviderOptions()), Clock);
        var a = await mock.GetHistoricalPricesAsync("AAPL", new(2024, 1, 1), new(2024, 3, 1), CancellationToken.None);
        var b = await mock.GetHistoricalPricesAsync("AAPL", new(2023, 6, 1), new(2024, 3, 1), CancellationToken.None);
        Assert.True(mock.Descriptor.IsSimulated);
        Assert.NotEmpty(a.Bars);
        var common = a.Bars.First();
        Assert.Equal(common.Close, b.Bars.Single(x => x.Date == common.Date).Close);
        Assert.All(a.Bars, x => Assert.True(x.Close > 0));
    }
}
