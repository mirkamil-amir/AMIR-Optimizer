using System.Net;
using PortfolioOptimizer.Application.Dtos;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Before an analysis: which prices and exchange rates are missing, and downloading them when the user agrees
/// (the frontend asks in a dialog). Runs as an approved, non-administrator user with simulated prices and rates.
/// </summary>
public sealed class DataReadinessTests : IAsyncLifetime
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

    [Fact]
    public async Task MissingHistoryAndRates_AreReported_ThenDownloadedOnRequest()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await _admin.SyncAndWaitAsync(new SyncRequest { Symbols = ["AAPL", "SAP.DE"], StartDate = today.AddYears(-2) });
        var request = new DataCheckRequest { Symbols = ["AAPL", "SAP.DE"], BenchmarkSymbol = "SPY", From = today.AddYears(-4), Currency = "USD" };

        var before = await (await _user.PostJsonAsync("/api/v1/market-data/check", request)).ReadAsync<DataCheckDto>();
        Assert.False(before.Complete);
        Assert.True(before.Downloadable);
        Assert.Equal("USD", before.Currency);
        Assert.Equal(["AAPL", "SAP.DE", "SPY"], before.Prices.Select(p => p.Symbol).Order());
        Assert.All(before.Prices, p => Assert.True(p.NeedsOlder && p.Downloadable, p.Symbol));
        Assert.Equal(0, before.Prices.Single(p => p.Symbol == "SPY").StoredPrices);
        var usd = Assert.Single(before.Rates);                       // EUR is the pivot; EUR→USD needs the USD series
        Assert.Equal("USD", usd.Currency);

        var started = await _user.PostJsonAsync("/api/v1/market-data/download-missing", request);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var download = await started.ReadAsync<DataDownloadDto>();
        Assert.Equal(["AAPL", "SAP.DE", "SPY"], download.PriceSymbols.Order());
        Assert.Equal(["USD"], download.Currencies);
        Assert.NotEmpty(download.SyncJobIds);

        DataCheckDto after = before;
        for (var i = 0; i < 300 && !after.Complete; i++)
        {
            await Task.Delay(200);
            after = await (await _user.PostJsonAsync("/api/v1/market-data/check", request)).ReadAsync<DataCheckDto>();
        }
        Assert.True(after.Complete, string.Join(" | ", after.Prices.Select(p => $"{p.Symbol}: {p.Explanation}").Concat(after.Rates.Select(r => r.Explanation))));
        Assert.False(after.Downloadable);

        // Asking again downloads nothing.
        var again = await (await _user.PostJsonAsync("/api/v1/market-data/download-missing", request)).ReadAsync<DataDownloadDto>();
        Assert.Empty(again.SyncJobIds);
        Assert.Empty(again.Currencies);
    }

    [Fact]
    public async Task Check_RequiresAnApprovedUser_AndValidatesTheRequest()
    {
        using var anonymous = _factory.CreateSession();
        var request = new DataCheckRequest { Symbols = ["AAPL"], From = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostJsonAsync("/api/v1/market-data/check", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.PostJsonAsync("/api/v1/market-data/check", request with { Symbols = [] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _user.PostJsonAsync("/api/v1/market-data/check", request with { From = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), To = null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.PostJsonAsync("/api/v1/market-data/check", request with { Symbols = ["NOPE.XX"] })).StatusCode);
    }
}
