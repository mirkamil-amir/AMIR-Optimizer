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
        Assert.Equal(["US", "DE", "FR", "GB", "JP", "CH", "ES", "IT", "SE", "DK", "CA", "AU", "HK", "IN", "SG",
            "NO", "FI", "BE", "PT", "IE", "AT", "PL", "HU", "GR", "IS", "IL", "KR", "CN", "MY", "TH", "BR", "ID", "MX", "ZA", "NZ"],
            markets.Select(m => m.Country));
        var indices = markets.SelectMany(m => m.Indices).ToDictionary(i => i.Code);
        Assert.Equal(40, indices["DAX40"].Members);
        Assert.Equal(30, indices["DAX30"].Members);
        Assert.True(indices["DAX30"].IsHistorical);
        Assert.False(indices["DAX40"].IsHistorical);
        Assert.Equal(100, indices["FTSE100"].Members);
        Assert.Equal("GBP", indices["FTSE100"].Currency);
        Assert.InRange(indices["NIKKEI225"].Members, 220, 225);
        Assert.InRange(indices["SP500"].Members, 495, 510);
        var constituentLists = indices.Values.Where(i => !i.Code.StartsWith("ETF")).ToList();
        Assert.Equal(36, constituentLists.Count);
        Assert.All(constituentLists, i => Assert.Contains("wikipedia.org", i.SourceUrl));
        Assert.All(constituentLists, i => Assert.Equal("CC BY-SA 4.0", i.Licence));
        Assert.Equal(48, indices["ASX50"].Members);
        Assert.Equal("CHF", indices["SMI"].Currency);
        Assert.Equal("^HSI", indices["HSI"].BenchmarkSymbol);
        // Markets without an index history at the price provider have no benchmark – stated, never substituted.
        Assert.Null(indices["BUX"].BenchmarkSymbol);
        Assert.Null(indices["OMXI15"].BenchmarkSymbol);
        Assert.Equal("^BVSP", indices["IBOV"].BenchmarkSymbol);
        Assert.Equal(200, indices["KOSPI200"].Members);
        // The index-ETF lists of the newer markets say they are curated, not Wikipedia data.
        var etfLists = indices.Values.Where(i => i.Code.StartsWith("ETF")).ToList();
        Assert.Equal(20, etfLists.Count);
        Assert.All(etfLists, i => Assert.StartsWith("Curated list", i.Source));
        Assert.All(etfLists, i => Assert.InRange(i.Members, 1, 3));
    }

    [Fact]
    public async Task NewMarkets_KeepTheirTradingCurrencies_AndEtfsAreFunds()
    {
        var singapore = await (await _admin.GetAsync("/api/v1/stocks?index=STI&pageSize=100")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Equal(30, singapore.TotalCount);
        Assert.Equal("USD", singapore.Items.Single(s => s.Symbol == "J36.SI").Currency);
        Assert.Equal("SGD", singapore.Items.Single(s => s.Symbol == "D05.SI").Currency);
        var india = await (await _admin.GetAsync("/api/v1/stocks?index=NIFTY50&pageSize=100")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Contains(india.Items, s => s.Symbol == "M&M.NS" && s.Currency == "INR");
        // A symbol with '&' works in a URL path when encoded.
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"/api/v1/stocks/{Uri.EscapeDataString("M&M.NS")}")).StatusCode);

        var canadianEtfs = await (await _admin.GetAsync("/api/v1/stocks?index=ETFCA&pageSize=10")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Contains(canadianEtfs.Items, s => s.Symbol == "XIU.TO" && s.Currency == "CAD");
        Assert.All(canadianEtfs.Items, s => Assert.Equal(AssetType.Etf, s.AssetType));

        // Markets added later: venue currencies, the documented foreign-currency listings, numeric and series symbols.
        var hungary = await (await _admin.GetAsync("/api/v1/stocks?index=BUX&pageSize=50")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Equal("EUR", hungary.Items.Single(s => s.Symbol == "GSPARK.BD").Currency);
        Assert.Equal("HUF", hungary.Items.Single(s => s.Symbol == "OTP.BD").Currency);
        var korea = await (await _admin.GetAsync("/api/v1/stocks?index=KOSPI200&pageSize=200")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Contains(korea.Items, s => s.Symbol == "005930.KS" && s.Currency == "KRW" && s.Country == "KR");
        var brazil = await (await _admin.GetAsync("/api/v1/stocks?country=BR&pageSize=100")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Contains(brazil.Items, s => s.Symbol == "PETR4.SA" && s.Currency == "BRL");
        Assert.Contains(brazil.Items, s => s.Symbol == "BOVA11.SA" && s.AssetType == AssetType.Etf);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"/api/v1/stocks/{Uri.EscapeDataString("PE&OLES.MX")}")).StatusCode);
    }

    [Fact]
    public async Task Stocks_FilterBySeveralMarkets_ForARegion()
    {
        async Task<int> Count(string country) =>
            (await (await _admin.GetAsync($"/api/v1/stocks?country={country}&pageSize=1")).ReadAsync<PagedResult<StockListItemDto>>()).TotalCount;
        var germany = await Count("DE");
        var spain = await Count("ES");
        // A region is several markets in one request; the sector list narrows the same way.
        Assert.Equal(germany + spain, await Count("DE,ES"));
        Assert.Equal(germany + spain, await Count(" de , es "));
        var sectors = await (await _admin.GetAsync("/api/v1/sectors?country=DE,ES")).ReadAsync<List<SectorDto>>();
        Assert.Equal(germany + spain, sectors.Sum(s => s.StockCount));
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
    public async Task MixedCurrencyAnalyses_AreConverted_SingleMarketKeepsItsBenchmark()
    {
        // Mixing USD and EUR stocks is allowed: the experiment is computed in the requested currency (prices converted with dated FX).
        var mixed = new CreateMlExperimentRequest { Universe = ["AAPL", "MSFT", "NVDA", "SAP.DE", "SIE.DE"], BenchmarkSymbol = "SPY", Currency = "EUR" };
        var accepted = await (await _admin.PostJsonAsync("/api/v1/ml/experiments", mixed)).ReadAsync<MlExperimentDto>();
        Assert.Equal("SPY", accepted.BenchmarkSymbol);                     // kept and converted, not replaced

        var dax = new CreateMlExperimentRequest { Universe = ["SAP.DE", "SIE.DE", "ALV.DE", "DTE.DE", "BAS.DE"], BenchmarkSymbol = "SPY" };
        var experiment = await (await _admin.PostJsonAsync("/api/v1/ml/experiments", dax)).ReadAsync<MlExperimentDto>();
        Assert.Equal("^GDAXI", experiment.BenchmarkSymbol);
        Assert.Contains(experiment.Warnings, w => w.Contains("SPY") && w.Contains("^GDAXI"));
    }
}
