using System.Net;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Market universes and automatic quote loading: a sign-in starts one shared background refresh cycle (deterministic Mock
/// provider here), pages and single stocks refresh through the backend, and index constituents are maintained by import.
/// </summary>
public sealed class MarketDataRefreshTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync(refreshOnSignIn: true);
        _admin = await _factory.CreateAdminSessionAsync();
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<QuoteRefreshRunDto> WaitForCycleAsync(HttpClient client)
    {
        for (var i = 0; i < 600; i++)
        {
            var response = await client.GetAsync("/api/v1/market-data/quote-refresh/latest");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var run = await response.ReadAsync<QuoteRefreshRunDto>();
                if (run.Status is not (JobStatus.Queued or JobStatus.Running)) return run;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Quote refresh cycle did not finish.");
    }

    [Fact]
    public async Task SignIn_StartsOneSharedRefreshCycle_AndPricesAppearWithoutManualRefresh()
    {
        var run = await WaitForCycleAsync(_admin);
        Assert.Equal("Login", run.Trigger);
        Assert.Equal(JobStatus.Succeeded, run.Status);
        Assert.True(run.SymbolsRequested > 900, $"cycle covered {run.SymbolsRequested} instruments");
        Assert.Equal(0, run.Failed);

        // Every DAX 40 member has a price now, in EUR, flagged as simulated (Mock provider), with timestamps.
        var dax = await (await _admin.GetAsync("/api/v1/stocks?index=DAX40&pageSize=100")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Equal(40, dax.TotalCount);
        Assert.All(dax.Items, s =>
        {
            Assert.NotNull(s.Quote.Price);
            Assert.Equal("EUR", s.Quote.Currency);
            Assert.True(s.Quote.IsSimulated);
            Assert.NotNull(s.Quote.RetrievedAtUtc);
            Assert.NotNull(s.Quote.AsOfUtc);
            Assert.Contains("DAX40", s.Indices);
        });

        // A second sign-in shortly after reuses the cycle instead of calling the provider again.
        using var again = _factory.CreateSession();
        await again.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var latest = await WaitForCycleAsync(again);
        Assert.Equal(run.Id, latest.Id);
    }

    [Fact]
    public async Task Markets_ListEveryUniverse_WithProvenance()
    {
        var markets = await (await _admin.GetAsync("/api/v1/markets")).ReadAsync<List<MarketDto>>();
        Assert.Equal(["US", "DE", "FR", "GB", "JP"], markets.Select(m => m.Country));
        var indices = markets.SelectMany(m => m.Indices).ToDictionary(i => i.Code);
        Assert.Equal(40, indices["DAX40"].Members);
        Assert.Equal(30, indices["DAX30"].Members);
        Assert.True(indices["DAX30"].IsHistorical);
        Assert.False(indices["DAX40"].IsHistorical);
        Assert.Equal(100, indices["FTSE100"].Members);
        Assert.Equal("GBP", indices["FTSE100"].Currency);
        Assert.InRange(indices["NIKKEI225"].Members, 220, 225);
        Assert.InRange(indices["SP500"].Members, 495, 510);
        Assert.All(indices.Values, i => Assert.Contains("wikipedia.org", i.SourceUrl));
        Assert.All(indices.Values, i => Assert.Equal("CC BY-SA 4.0", i.Licence));
    }

    [Fact]
    public async Task Stocks_FilterByMarketIndexAndSector_AcrossMarkets()
    {
        // France includes the Amsterdam-listed CAC 40 member ArcelorMittal.
        var france = await (await _admin.GetAsync("/api/v1/stocks?country=FR&pageSize=200")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Contains(france.Items, s => s.Symbol == "MT.AS" && s.Country == "NL");
        Assert.Contains(france.Items, s => s.Symbol == "MC.PA");

        var japanTech = await (await _admin.GetAsync("/api/v1/stocks?index=NIKKEI225&sector=Information%20Technology&pageSize=200"))
            .ReadAsync<PagedResult<StockListItemDto>>();
        Assert.True(japanTech.TotalCount > 10);
        Assert.All(japanTech.Items, s => Assert.Equal("JPY", s.Currency));

        var sectors = await (await _admin.GetAsync("/api/v1/sectors?index=FTSE100")).ReadAsync<List<SectorDto>>();
        Assert.Equal(100, sectors.Sum(s => s.StockCount));

        var search = await (await _admin.GetAsync("/api/v1/stocks?search=toyota")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Contains(search.Items, s => s.Symbol == "7203.T");

        var invalid = await _admin.GetAsync("/api/v1/stocks?sort=price");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task PageAndSingleRefresh_UseTheBackend_WithCacheAndCooldown()
    {
        await WaitForCycleAsync(_admin);
        // Within the cache window a page refresh reuses the stored quotes.
        var page = await (await _admin.PostJsonAsync("/api/v1/stocks/quotes/refresh", new RefreshQuotesRequest { Symbols = ["SAP.DE", "7203.T", "TSCO.L"] }))
            .ReadAsync<QuoteRefreshResultDto>();
        Assert.All(page.Items, i => Assert.Equal("Fresh", i.Outcome));
        Assert.Equal(3, page.Stocks.Count);
        Assert.Equal("GBP", Assert.Single(page.Stocks, s => s.Symbol == "TSCO.L").Quote.Currency);

        // A forced single refresh within the 30 s cooldown returns the stored quote instead of calling the provider again.
        var single = await (await _admin.PostAsync("/api/v1/stocks/SAP.DE/quote/refresh", null)).ReadAsync<QuoteRefreshResultDto>();
        Assert.Equal("Fresh", Assert.Single(single.Items).Outcome);
        Assert.NotNull(Assert.Single(single.Stocks).Quote.Price);

        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsync("/api/v1/stocks/NOPE.DE/quote/refresh", null)).StatusCode);
        var tooMany = new RefreshQuotesRequest { Symbols = Enumerable.Range(0, 101).Select(i => $"X{i}").ToList() };
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostJsonAsync("/api/v1/stocks/quotes/refresh", tooMany)).StatusCode);
    }

    [Fact]
    public async Task QuoteEndpoints_RequireAnApprovedSession_AndAntiforgery()
    {
        using var anonymous = _factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/stocks/SAP.DE/quote/refresh", null)).StatusCode);
        using var raw = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.GetAsync("/api/v1/markets")).StatusCode);
    }

    [Fact]
    public async Task UniverseImport_IsAdminOnly_AndReplacesTheMembership()
    {
        var (user, _) = await _factory.CreateApprovedUserAsync(_admin);
        var request = new ImportUniverseRequest
        {
            Csv = "symbol,name,country,exchange,mic,currency,sector,industry,isin\nSAP.DE,SAP,DE,XETRA,XETR,EUR,Information Technology,Software,DE0007164600\nSIE.DE,Siemens,DE,XETRA,XETR,EUR,Industrials,Conglomerate,",
            Source = "Integration test constituent file",
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostJsonAsync("/api/v1/admin/catalogue/universes/DAX40", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/api/v1/admin/catalogue/quote-refresh", null)).StatusCode);

        var result = await (await _admin.PostJsonAsync("/api/v1/admin/catalogue/universes/DAX40", request)).ReadAsync<UniverseImportResultDto>();
        Assert.Equal(2, result.Members);
        Assert.Equal(38, result.Removed);
        var dax = await (await user.GetAsync("/api/v1/stocks?index=DAX40")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Equal(2, dax.TotalCount);
        var sap = await (await user.GetAsync("/api/v1/stocks/SAP.DE")).ReadAsync<StockDetailDto>();
        Assert.Equal("DE0007164600", sap.Isin);
        // Instruments are never deleted: a former member stays in the catalogue.
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/stocks/ADS.DE")).StatusCode);

        var newIndex = request with { Name = null };
        var missingName = await _admin.PostJsonAsync("/api/v1/admin/catalogue/universes/MDAX", newIndex);
        Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);
        user.Dispose();
    }

    [Fact]
    public async Task Analyses_RequireOneCurrency_AndUseAMatchingBenchmark()
    {
        var mixed = new CreateMlExperimentRequest { Universe = ["AAPL", "MSFT", "NVDA", "SAP.DE", "SIE.DE"], BenchmarkSymbol = "SPY" };
        var rejected = await _admin.PostJsonAsync("/api/v1/ml/experiments", mixed);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("mixes currencies", await rejected.Content.ReadAsStringAsync());

        var dax = new CreateMlExperimentRequest { Universe = ["SAP.DE", "SIE.DE", "ALV.DE", "DTE.DE", "BAS.DE"], BenchmarkSymbol = "SPY" };
        var experiment = await (await _admin.PostJsonAsync("/api/v1/ml/experiments", dax)).ReadAsync<MlExperimentDto>();
        Assert.Equal("^GDAXI", experiment.BenchmarkSymbol);
        Assert.Contains(experiment.Warnings, w => w.Contains("SPY") && w.Contains("^GDAXI"));
    }
}
