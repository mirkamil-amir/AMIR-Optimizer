using System.Net;
using PortfolioOptimizer.Application.Dtos;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>Sector browsing, search, quote provenance, on-demand price download and catalogue administration.</summary>
public sealed class StockDiscoveryTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        await _admin.SyncAndWaitAsync(new SyncRequest { Symbols = ["AAPL", "MSFT", "SPY"], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1) });
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
    public async Task Sectors_ComeFromTheCatalogue()
    {
        var sectors = await (await _user.GetAsync("/api/v1/sectors")).ReadAsync<List<SectorDto>>();
        Assert.Contains(sectors, s => s.Name == "Information Technology" && s.StockCount > 50 && s.WithPriceData == 2);
        Assert.Contains(sectors, s => s.Name == "Energy");
        Assert.Equal("Unclassified", sectors[^1].Name); // SPY (fund) has no GICS sector
        Assert.True(sectors.Sum(s => s.StockCount) > 500);
    }

    [Fact]
    public async Task Stocks_FilterBySector_AndSearch()
    {
        var energy = await (await _user.GetAsync("/api/v1/stocks?sector=Energy&pageSize=200")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.True(energy.TotalCount > 10);
        Assert.All(energy.Items, s => Assert.Equal("Energy", s.Sector));
        Assert.Contains(energy.Items, s => s.Symbol == "XOM" && s.Industry is not null);

        var search = await (await _user.GetAsync("/api/v1/stocks?search=apple")).ReadAsync<PagedResult<StockListItemDto>>();
        var apple = Assert.Single(search.Items, s => s.Symbol == "AAPL");
        Assert.Equal("Information Technology", apple.Sector);
        Assert.True(apple.Observations > 200);
        Assert.NotEqual(QuoteKind.None, apple.Quote.Kind); // mock quote and last bar share a date → LastClose
        Assert.True(apple.Quote.IsSimulated);
        Assert.Contains("SIMULATED", apple.Quote.Description);
        Assert.NotNull(apple.Quote.Price);

        var exactFirst = await (await _user.GetAsync("/api/v1/stocks?search=A")).ReadAsync<PagedResult<StockListItemDto>>();
        Assert.Equal("A", exactFirst.Items[0].Symbol);
    }

    [Fact]
    public async Task StockWithoutPrices_ReportsNoQuote_UntilFetchedOnDemand()
    {
        // CVX is in the catalogue only (not in the tracked default universe) and has no prices yet.
        var detail = await (await _user.GetAsync("/api/v1/stocks/CVX")).ReadAsync<StockDetailDto>();
        Assert.Equal(QuoteKind.None, detail.Stock.Quote.Kind);
        Assert.Null(detail.Stock.Quote.Price);
        Assert.False(detail.Stock.IsTracked);
        Assert.Null(detail.Description);
        Assert.Contains("No company description", detail.DescriptionNote);
        Assert.Contains("Wikipedia", detail.MetadataSource);

        await _user.SyncAndWaitAsync(new SyncRequest { Symbols = ["CVX"], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-3) });
        var after = await (await _user.GetAsync("/api/v1/stocks/CVX")).ReadAsync<StockDetailDto>();
        Assert.True(after.Stock.IsTracked);
        Assert.True(after.Stock.Observations > 40);
        var quote = await (await _user.GetAsync("/api/v1/stocks/CVX/quote")).ReadAsync<StockQuoteDto>();
        Assert.NotEqual(QuoteKind.None, quote.Kind);
        Assert.NotNull(quote.PreviousClose);
        Assert.NotNull(quote.ChangePercent);
    }

    [Fact]
    public async Task UnknownStock_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _user.GetAsync("/api/v1/stocks/NOPE123")).StatusCode);
    }

    [Fact]
    public async Task CatalogueImport_IsAdminOnly_AndFillsMissingMetadata()
    {
        var request = new ImportCatalogueRequest
        {
            Csv = "symbol,name,sector,industry,exchange,currency,type\nZZTEST,Test Corp,Energy,Oil & Gas Drilling,NYSE,USD,Equity\nAAPL,Renamed Apple,Energy,X,NASDAQ,USD,Equity",
            Source = "Integration test universe",
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await _user.PostJsonAsync("/api/v1/admin/catalogue/import", request)).StatusCode);

        var result = await (await _admin.PostJsonAsync("/api/v1/admin/catalogue/import", request)).ReadAsync<CatalogueImportResultDto>();
        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, result.Unchanged); // AAPL already has name/sector/industry; not overwritten without OverwriteMetadata
        var aapl = await (await _user.GetAsync("/api/v1/stocks/AAPL")).ReadAsync<StockDetailDto>();
        Assert.Equal("Information Technology", aapl.Stock.Sector);
        var added = await (await _user.GetAsync("/api/v1/stocks/ZZTEST")).ReadAsync<StockDetailDto>();
        Assert.Equal("Integration test universe", added.MetadataSource);
    }
}
