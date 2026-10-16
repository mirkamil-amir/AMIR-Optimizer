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
            Options.Create(new AlphaVantageOptions { ApiKey = "SECRETKEY" }), new AlphaVantageQuota(Options.Create(new AlphaVantageOptions()), Clock), Clock);
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
            Options.Create(new AlphaVantageOptions { ApiKey = "k" }), new AlphaVantageQuota(Options.Create(new AlphaVantageOptions()), Clock), Clock);
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

    // Shape of a real spark response (2026-10-14): two symbols, 5-minute bars, last bar may be null.
    private const string SparkJson = """
    {"AAPL":{"timestamp":[1792000000,1792000300,1792000600],"symbol":"AAPL","chartPreviousClose":341.27,"close":[342.0,342.59,null]},
     "TSCO.L":{"timestamp":[1792000000,1792000300],"symbol":"TSCO.L","chartPreviousClose":517.0,"close":[525.0,526.4]}}
    """;

    [Fact]
    public async Task Yahoo_SparkBatch_ParsesLastTrade_PreviousClose_AndConvertsPence()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SparkJson);
        var results = await Yahoo(handler).GetLatestQuotesAsync(["AAPL", "TSCO.L", "SAP.DE"], CancellationToken.None);
        Assert.Contains("symbols=AAPL%2CTSCO.L%2CSAP.DE", handler.LastUri!.Query);

        var aapl = Assert.Single(results, r => r.Symbol == "AAPL").Quote!;
        Assert.Equal(342.59m, aapl.Price);                   // last non-null close
        Assert.Equal(341.27m, aapl.PreviousClose);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792000300).UtcDateTime, aapl.AsOfUtc);
        Assert.Equal("USD", aapl.Currency);

        var tesco = Assert.Single(results, r => r.Symbol == "TSCO.L").Quote!;
        Assert.Equal(5.264m, tesco.Price);                   // 526.4 pence → 5.264 GBP
        Assert.Equal(5.17m, tesco.PreviousClose);
        Assert.Equal("GBP", tesco.Currency);

        var sap = Assert.Single(results, r => r.Symbol == "SAP.DE");
        Assert.Null(sap.Quote);                              // missing in the response → reported, never invented
        Assert.Contains("No data", sap.Error);
        Assert.DoesNotContain("Yahoo", sap.Error);
    }

    [Fact]
    public async Task Yahoo_History_InPence_IsStoredInPounds()
    {
        var json = ChartJson.Replace("\"currency\":\"USD\"", "\"currency\":\"GBp\"");
        var h = await Yahoo(new StubHandler(HttpStatusCode.OK, json)).GetHistoricalPricesAsync("TSCO.L", new(2025, 1, 1), new(2025, 1, 10), CancellationToken.None);
        Assert.Equal("GBP", h.Currency);
        Assert.Equal(2.4385m, h.Bars[0].Close);              // 243.85 pence
    }

    [Fact]
    public async Task Yahoo_RejectsBatchesLargerThanTheEndpointLimit()
    {
        var symbols = Enumerable.Range(0, 21).Select(i => $"S{i}").ToList();
        await Assert.ThrowsAsync<ArgumentException>(() => Yahoo(new StubHandler(HttpStatusCode.OK, "{}")).GetLatestQuotesAsync(symbols, CancellationToken.None));
    }

    private static AlphaVantageProvider AlphaVantage(StubHandler handler, int dailyLimit = 25) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") }, Options.Create(new AlphaVantageOptions { ApiKey = "k", DailyRequestLimit = dailyLimit }),
            new AlphaVantageQuota(Options.Create(new AlphaVantageOptions { DailyRequestLimit = dailyLimit }), Clock), Clock);

    [Fact]
    public async Task AlphaVantage_LondonQuote_UsesLonSuffix_AndConvertsPence()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
        {"Global Quote":{"01. symbol":"TSCO.LON","05. price":"517.0000","07. latest trading day":"2026-10-13","08. previous close":"511.0000"}}
        """);
        var q = await AlphaVantage(handler).GetLatestQuoteAsync("TSCO.L", CancellationToken.None);
        Assert.Contains("symbol=TSCO.LON", handler.LastUri!.Query);
        Assert.Equal(5.17m, q!.Price);
        Assert.Equal(5.11m, q.PreviousClose);
        Assert.Equal("GBP", q.Currency);
    }

    [Fact]
    public async Task AlphaVantage_DoesNotCoverTokyo_AndSendsNoRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var provider = AlphaVantage(handler);
        Assert.False(provider.Supports("7203.T"));
        var results = await provider.GetLatestQuotesAsync(["7203.T"], CancellationToken.None);
        Assert.True(Assert.Single(results).IsUnsupported);
        Assert.Null(handler.LastUri);
    }

    [Fact]
    public async Task AlphaVantage_StopsAtTheDailyQuota()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"Global Quote":{"05. price":"230.78","07. latest trading day":"2026-10-12"}}""");
        var provider = AlphaVantage(handler, dailyLimit: 2);
        var results = await provider.GetLatestQuotesAsync(["IBM", "MSFT", "AAPL"], CancellationToken.None);
        Assert.Equal(2, results.Count(r => r.Quote is not null));
        var blocked = Assert.Single(results, r => r.Quote is null);
        Assert.Contains("daily request limit", blocked.Error);
        Assert.DoesNotContain("Alpha Vantage", blocked.Error);
        Assert.True(blocked.IsTransient);
        Assert.Equal(0, provider.RemainingDailyRequests);
    }

    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var (status, body) = responses[Math.Min(_next++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Yahoo_RejectedBatch_IsRetried_ThenFallsBackToSingleRequests()
    {
        // Two rejected batch attempts, then per-symbol chart requests succeed.
        var handler = new SequenceHandler((HttpStatusCode.BadRequest, "{}"), (HttpStatusCode.BadRequest, "{}"), (HttpStatusCode.OK, ChartJson));
        var provider = new YahooFinanceProvider(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") },
            Options.Create(new YahooFinanceOptions()), TimeProvider.System);
        var results = await provider.GetLatestQuotesAsync(["AAPL"], CancellationToken.None);
        Assert.Equal(340.97m, Assert.Single(results).Quote!.Price);
        Assert.Equal(2, handler.Requests.Count(r => r.Contains("/spark")));
        Assert.Contains(handler.Requests, r => r.Contains("/chart/AAPL"));
    }
}
