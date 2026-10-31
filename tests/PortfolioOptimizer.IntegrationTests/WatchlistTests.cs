using System.Net;
using PortfolioOptimizer.Application.Dtos;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>Watchlist persistence, duplicate prevention, owner isolation, refresh and price-history ranges.</summary>
public sealed class WatchlistTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;
    private CurrentUserDto _userInfo = null!;
    private string _email = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        await _admin.SyncAndWaitAsync(new SyncRequest { Symbols = ["AAPL", "MSFT"], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-2) });
        _email = $"watcher-{Guid.NewGuid():N}@test.local";
        (_user, _userInfo) = await _factory.CreateApprovedUserAsync(_admin, _email);
    }

    public async Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private static Task<HttpResponseMessage> Add(HttpClient client, string symbol) =>
        client.PostJsonAsync("/api/v1/watchlist", new AddWatchlistEntryRequest { Symbol = symbol });

    [Fact]
    public async Task Add_List_Remove_AndDuplicatePrevention()
    {
        var added = await (await Add(_user, "aapl")).EnsureStatus(HttpStatusCode.Created).ReadAsync<WatchlistAddResultDto>();
        Assert.Equal("AAPL", added.Item.Stock.Symbol);
        Assert.False(added.HistoryQueued); // two years already stored
        Assert.NotNull(added.Item.Stock.Quote.Price);

        (await Add(_user, "AAPL")).EnsureStatus(HttpStatusCode.Conflict);
        (await Add(_user, "NOPE123")).EnsureStatus(HttpStatusCode.NotFound);

        var list = await (await _user.GetAsync("/api/v1/watchlist?range=1Y")).ReadAsync<WatchlistDto>();
        var item = Assert.Single(list.Items);
        Assert.Equal("1Y", list.Range);
        Assert.NotNull(item.Period.Change);
        Assert.Equal("Adjusted close", item.Period.Basis);
        Assert.InRange(item.Period.Sparkline.Count, 2, 60);
        Assert.Contains("not a portfolio", list.Note);

        (await _user.DeleteAsync("/api/v1/watchlist/AAPL")).EnsureStatus(HttpStatusCode.NoContent);
        Assert.Empty((await (await _user.GetAsync("/api/v1/watchlist")).ReadAsync<WatchlistDto>()).Items);
        (await _user.DeleteAsync("/api/v1/watchlist/AAPL")).EnsureStatus(HttpStatusCode.NotFound);

        // Removing an entry keeps the shared prices.
        var history = await (await _user.GetAsync("/api/v1/stocks/AAPL/history?range=1M")).ReadAsync<PriceHistoryDto>();
        Assert.NotEmpty(history.Points);
    }

    [Fact]
    public async Task Entries_PersistAcrossSignOut_AndAreInvisibleToOtherUsers()
    {
        (await Add(_user, "MSFT")).EnsureStatus(HttpStatusCode.Created);
        (await _user.PostAsync("/api/v1/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _user.GetAsync("/api/v1/watchlist")).StatusCode);

        await _user.LoginAsync(_email, ApiFactory.UserPassword);
        var list = await (await _user.GetAsync("/api/v1/watchlist")).ReadAsync<WatchlistDto>();
        Assert.Contains(list.Items, i => i.Stock.Symbol == "MSFT");

        var (other, _) = await _factory.CreateApprovedUserAsync(_admin);
        Assert.Empty((await (await other.GetAsync("/api/v1/watchlist")).ReadAsync<WatchlistDto>()).Items);
        // Another user cannot remove the entry; the same stock on their own list is independent.
        (await other.DeleteAsync("/api/v1/watchlist/MSFT")).EnsureStatus(HttpStatusCode.NotFound);
        (await Add(other, "MSFT")).EnsureStatus(HttpStatusCode.Created);
        (await other.DeleteAsync("/api/v1/watchlist/MSFT")).EnsureStatus(HttpStatusCode.NoContent);
        Assert.Contains((await (await _user.GetAsync("/api/v1/watchlist")).ReadAsync<WatchlistDto>()).Items, i => i.Stock.Symbol == "MSFT");
        other.Dispose();
    }

    [Fact]
    public async Task AddingAStockWithoutHistory_QueuesADownload()
    {
        var added = await (await Add(_user, "CVX")).EnsureStatus(HttpStatusCode.Created).ReadAsync<WatchlistAddResultDto>();
        Assert.True(added.HistoryQueued);
        PriceHistoryDto? history = null;
        for (var i = 0; i < 100 && history is not { Points.Count: > 0 }; i++)
        {
            await Task.Delay(100);
            history = await (await _user.GetAsync("/api/v1/stocks/CVX/history?range=1Y")).ReadAsync<PriceHistoryDto>();
        }
        Assert.NotNull(history);
        Assert.NotEmpty(history.Points);
        Assert.True(history.IsSimulated); // mock provider in tests – the API labels it
        Assert.Equal("USD", history.Currency);
    }

    [Fact]
    public async Task History_ReportsUnsupportedAndUncoveredRanges_InsteadOfInventingData()
    {
        var oneDay = await (await _user.GetAsync("/api/v1/stocks/AAPL/history?range=1D")).ReadAsync<PriceHistoryDto>();
        Assert.False(oneDay.Supported);
        Assert.Empty(oneDay.Points);
        Assert.Contains("Intraday", oneDay.Limitation);

        var fiveYears = await (await _user.GetAsync("/api/v1/stocks/AAPL/history?range=5Y")).ReadAsync<PriceHistoryDto>();
        Assert.Null(fiveYears.Change); // only two years stored
        Assert.NotNull(fiveYears.AvailableFrom);
        Assert.Contains("Stored history starts", fiveYears.Limitation);

        (await _user.GetAsync("/api/v1/stocks/AAPL/history?range=2W")).EnsureStatus(HttpStatusCode.BadRequest);
        (await _user.GetAsync("/api/v1/watchlist?range=1D")).EnsureStatus(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkRefresh_CoversOnlyTheCallersStocks()
    {
        (await Add(_user, "AAPL")).EnsureStatus(HttpStatusCode.Created);
        var result = await (await _user.PostAsync("/api/v1/watchlist/quotes/refresh", null)).ReadAsync<QuoteRefreshResultDto>();
        var item = Assert.Single(result.Items);
        Assert.Equal("AAPL", item.Symbol);
    }

    [Fact]
    public async Task StockTrends_MatchTheWatchlist_ForOnePageOfStocks()
    {
        (await Add(_user, "AAPL")).EnsureStatus(HttpStatusCode.Created);
        var watched = Assert.Single((await (await _user.GetAsync("/api/v1/watchlist?range=1M")).ReadAsync<WatchlistDto>()).Items);

        var trends = await (await _user.GetAsync("/api/v1/stocks/trends?symbols=aapl,MSFT,NOPE123")).ReadAsync<List<StockTrendDto>>();
        Assert.Equal(["AAPL", "MSFT"], trends.Select(t => t.Symbol)); // unknown symbols are skipped, order kept
        var aapl = trends[0].Period;
        Assert.Equal("1M", aapl.Range);
        Assert.Equal(watched.Period.Change, aapl.Change);
        Assert.Equal(watched.Period.Sparkline, aapl.Sparkline);
        Assert.InRange(aapl.Sparkline.Count, 2, 60);

        Assert.Empty(await (await _user.GetAsync("/api/v1/stocks/trends")).ReadAsync<List<StockTrendDto>>());
        (await _user.GetAsync("/api/v1/stocks/trends?symbols=AAPL&range=1D")).EnsureStatus(HttpStatusCode.BadRequest);
        var tooMany = string.Join(',', Enumerable.Range(0, 201).Select(i => $"S{i}"));
        (await _user.GetAsync($"/api/v1/stocks/trends?symbols={tooMany}")).EnsureStatus(HttpStatusCode.BadRequest);

        using var anonymous = _factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/stocks/trends?symbols=AAPL")).StatusCode);
    }

    [Fact]
    public async Task Reorder_PersistsTheOwnersOrder_AndRefusesAStaleOrForeignList()
    {
        (await Add(_user, "AAPL")).EnsureStatus(HttpStatusCode.Created);
        (await Add(_user, "MSFT")).EnsureStatus(HttpStatusCode.Created);
        async Task<List<string>> Order(HttpClient c) =>
            (await (await c.GetAsync("/api/v1/watchlist")).ReadAsync<WatchlistDto>()).Items.Select(i => i.Stock.Symbol).ToList();
        Assert.Equal(["AAPL", "MSFT"], await Order(_user)); // new entries go to the end

        (await _user.PutJsonAsync("/api/v1/watchlist/order", new ReorderWatchlistRequest { Symbols = ["msft", "AAPL"] })).EnsureStatus(HttpStatusCode.NoContent);
        Assert.Equal(["MSFT", "AAPL"], await Order(_user));

        // A list that misses or invents a symbol is refused (409), duplicates are invalid (400); the stored order stays.
        (await _user.PutJsonAsync("/api/v1/watchlist/order", new ReorderWatchlistRequest { Symbols = ["AAPL"] })).EnsureStatus(HttpStatusCode.Conflict);
        (await _user.PutJsonAsync("/api/v1/watchlist/order", new ReorderWatchlistRequest { Symbols = ["AAPL", "NOPE123"] })).EnsureStatus(HttpStatusCode.Conflict);
        (await _user.PutJsonAsync("/api/v1/watchlist/order", new ReorderWatchlistRequest { Symbols = ["AAPL", "AAPL"] })).EnsureStatus(HttpStatusCode.BadRequest);
        Assert.Equal(["MSFT", "AAPL"], await Order(_user));

        // Another user's order is their own: naming this user's symbols does not touch this user's list.
        var (other, _) = await _factory.CreateApprovedUserAsync(_admin);
        (await other.PutJsonAsync("/api/v1/watchlist/order", new ReorderWatchlistRequest { Symbols = ["AAPL", "MSFT"] })).EnsureStatus(HttpStatusCode.Conflict);
        Assert.Equal(["MSFT", "AAPL"], await Order(_user));
        other.Dispose();

        using var anonymous = _factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutJsonAsync("/api/v1/watchlist/order", new ReorderWatchlistRequest { Symbols = ["AAPL"] })).StatusCode);
    }

    [Fact]
    public async Task Watchlist_RequiresAnApprovedAccount()
    {
        using var anonymous = _factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/watchlist")).StatusCode);
    }
}
