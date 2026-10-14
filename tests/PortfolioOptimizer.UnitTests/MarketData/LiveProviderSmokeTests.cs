using Microsoft.Extensions.Options;
using PortfolioOptimizer.Infrastructure.MarketData;

namespace PortfolioOptimizer.UnitTests.MarketData;

/// <summary>Runs only when PO_LIVE_SMOKE=1: these tests call real providers over the network (never in normal test runs).</summary>
public sealed class LiveSmokeFactAttribute : FactAttribute
{
    public LiveSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PO_LIVE_SMOKE") != "1") Skip = "Live provider smoke test; set PO_LIVE_SMOKE=1 to run.";
    }
}

/// <summary>
/// Smoke tests against the real providers with one representative stock per market. Kept small: Yahoo is unofficial
/// and Alpha Vantage free keys allow 25 requests per day (these use 2).
/// </summary>
public class LiveProviderSmokeTests
{
    private static HttpClient Client(string baseUrl) => new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

    [LiveSmokeFact]
    public async Task Yahoo_QuotesEveryMarket_InItsCurrency()
    {
        var yahoo = new YahooFinanceProvider(Client("https://query1.finance.yahoo.com"), Options.Create(new YahooFinanceOptions()), TimeProvider.System);
        var expected = new Dictionary<string, string>
        {
            ["AAPL"] = "USD", ["BRK-B"] = "USD", ["SAP.DE"] = "EUR", ["MC.PA"] = "EUR", ["MT.AS"] = "EUR", ["TSCO.L"] = "GBP", ["7203.T"] = "JPY",
        };
        var results = await yahoo.GetLatestQuotesAsync([.. expected.Keys], CancellationToken.None);
        foreach (var r in results)
        {
            Assert.True(r.Quote is not null, $"{r.Symbol}: {r.Error}");
            Assert.Equal(expected[r.Symbol], r.Quote!.Currency);
            Assert.True(r.Quote.Price > 0);
            Assert.True(DateTime.UtcNow - r.Quote.AsOfUtc < TimeSpan.FromDays(7), $"{r.Symbol} quote is older than a week");
        }
        // London is quoted in pence; stored prices must be pounds (Tesco trades at a few pounds, not hundreds).
        Assert.InRange(results.Single(r => r.Symbol == "TSCO.L").Quote!.Price, 1m, 20m);

        var history = await yahoo.GetHistoricalPricesAsync("TSCO.L", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-20)), DateOnly.FromDateTime(DateTime.UtcNow), CancellationToken.None);
        Assert.Equal("GBP", history.Currency);
        Assert.All(history.Bars, b => Assert.InRange(b.Close, 1m, 20m));
    }

    [LiveSmokeFact]
    public async Task AlphaVantage_QuotesUsAndXetra_WhenAKeyIsConfigured()
    {
        var key = Environment.GetEnvironmentVariable("ALPHAVANTAGE_API_KEY") ?? Environment.GetEnvironmentVariable("MarketData__AlphaVantage__ApiKey");
        Assert.False(string.IsNullOrWhiteSpace(key), "Set ALPHAVANTAGE_API_KEY for this smoke test.");
        var options = Options.Create(new AlphaVantageOptions { ApiKey = key });
        var provider = new AlphaVantageProvider(Client("https://www.alphavantage.co"), options, new AlphaVantageQuota(options, TimeProvider.System), TimeProvider.System);
        var results = await provider.GetLatestQuotesAsync(["IBM", "7203.T"], CancellationToken.None);
        var ibm = results.Single(r => r.Symbol == "IBM");
        Assert.True(ibm.Quote is not null, ibm.Error);
        Assert.Equal("USD", ibm.Quote!.Currency);
        Assert.True(results.Single(r => r.Symbol == "7203.T").IsUnsupported); // Tokyo is not covered; no request is sent
        await Task.Delay(1500);
        var sap = await provider.GetLatestQuoteAsync("SAP.DE", CancellationToken.None);
        Assert.NotNull(sap);
        Assert.Equal("EUR", sap!.Currency);
        Assert.Equal(23, provider.RemainingDailyRequests); // exactly two requests were sent
    }
}
