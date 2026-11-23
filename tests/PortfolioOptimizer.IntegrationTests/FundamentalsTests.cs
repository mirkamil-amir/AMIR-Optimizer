using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Infrastructure.MarketData;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>Stock-list fundamentals: page request → background fetch (simulated provider) → stored figures → metrics, with the unavailable paths.</summary>
public sealed class FundamentalsTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        (_user, _) = await _factory.CreateApprovedUserAsync(_admin);
    }

    public async Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<List<StockFundamentalsDto>> Get(string symbols) =>
        (await _user.GetAsync($"/api/v1/stocks/fundamentals?symbols={symbols}")).ReadAsync<List<StockFundamentalsDto>>().Result;

    /// <summary>Asks again until no stock is pending (the worker fetches in the background).</summary>
    private async Task<List<StockFundamentalsDto>> Settled(string symbols)
    {
        for (var i = 0; i < 100; i++)
        {
            var r = await Get(symbols);
            if (r.All(x => x.Status != "Pending")) return r;
            await Task.Delay(100);
        }
        throw new TimeoutException("Fundamentals stayed pending.");
    }

    [Fact]
    public async Task Page_IsFetchedInTheBackground_ThenServedFromStorage()
    {
        var first = await Get("AAPL,MSFT");
        Assert.All(first, x => Assert.Contains(x.Status, new[] { "Pending", "Available" }));

        var r = await Settled("AAPL,MSFT");
        var aapl = r.Single(x => x.Symbol == "AAPL");
        Assert.Equal("Available", aapl.Status);
        Assert.True(aapl.IsSimulated);
        Assert.NotNull(aapl.RetrievedAtUtc);
        // Simulated figures (MockFundamentalsProvider): growth 1100/1000 − 1; ROIC 240 × (1 − 50/200) / ((1400 + 1600)/2).
        Assert.Equal(0.1, aapl.RevenueGrowth!.Value!.Value, 9);
        Assert.Equal(0.12, aapl.Roic!.Value!.Value, 9);
        Assert.Equal("USD", aapl.MarketCap!.Currency);
        Assert.Equal(new DateOnly(2025, 12, 31), aapl.FreeCashFlow!.AsOf);
        Assert.Equal(25, aapl.PeRatio!.Value);
        Assert.True(aapl.Ebitda!.Value > aapl.Ebit!.Value);
        Assert.NotNull(aapl.Revenue!.Value);

        var mock = _factory.Services.GetRequiredService<MockFundamentalsProvider>();
        var calls = mock.Calls;
        await Get("AAPL,MSFT");
        await Get("aapl");
        Assert.Equal(calls, mock.Calls); // fresh figures: no further provider request
    }

    [Fact]
    public async Task Bank_Missing_Failing_AndEtf_AreReportedHonestly()
    {
        (await _admin.PostJsonAsync("/api/v1/admin/catalogue/universes/FUNDTEST", new ImportUniverseRequest
        {
            Csv = "symbol,name,country,currency\nBANKQ,Bank Co,US,USD\nMISSINGQ,Missing Co,US,USD\nFAILQ,Failing Co,US,USD",
            Name = "Fundamentals test", Country = "US", Currency = "USD", Source = "integration test",
        })).EnsureStatus(HttpStatusCode.OK);

        var r = await Settled("BANKQ,MISSINGQ,FAILQ,SPY");
        var bank = r.Single(x => x.Symbol == "BANKQ");
        Assert.Equal("Available", bank.Status);
        Assert.Null(bank.Roic!.Value);
        Assert.Contains("banks", bank.Roic.Limitation);
        Assert.Null(bank.Ebit!.Value);
        Assert.Null(bank.Ebitda!.Value);
        Assert.NotNull(bank.RevenueGrowth!.Value);

        var missing = r.Single(x => x.Symbol == "MISSINGQ");
        Assert.Equal("NotFound", missing.Status);
        Assert.Null(missing.MarketCap);

        var failing = r.Single(x => x.Symbol == "FAILQ");
        Assert.Equal("Error", failing.Status);
        Assert.Contains("retried", failing.Message);

        Assert.Equal("NotApplicable", r.Single(x => x.Symbol == "SPY").Status);
    }

    [Fact]
    public async Task ScheduledRefresh_QueuesEveryCompanyWithoutFigures_AndNotFetchedOnesAgain()
    {
        async Task<int> Run()
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<FundamentalsService>().ScheduleDueAsync(CancellationToken.None);
        }
        var first = await Run();
        Assert.True(first > 10, $"queued {first}"); // every seeded company, no ETFs or indices

        // Page requests go ahead of the run and are served once fetched.
        var r = await Settled("AAPL,MSFT,NVDA");
        Assert.All(r, x => Assert.Equal("Available", x.Status));
        // Stored figures are not due again: a later run queues fewer companies (independent of how far the run got).
        Assert.True(await Run() < first);
    }

    [Fact]
    public async Task RequiresSignIn_AndLimitsThePageSize()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/stocks/fundamentals?symbols=AAPL")).StatusCode);
        var tooMany = string.Join(',', Enumerable.Range(0, FundamentalsService.MaxSymbols + 1).Select(i => $"S{i}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.GetAsync($"/api/v1/stocks/fundamentals?symbols={tooMany}")).StatusCode);
    }
}
