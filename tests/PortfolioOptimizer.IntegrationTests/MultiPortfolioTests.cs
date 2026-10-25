using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Research;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Several independent portfolios per user: overview, isolation of holdings/runs/transactions, manual bookkeeping and its
/// accounting rules, performance with external cash flows, archive/delete lifecycle and cross-user protection.
/// </summary>
public sealed class MultiPortfolioTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;
    private CurrentUserDto _aliceUser = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        await _admin.SyncAndWaitAsync(new SyncRequest { StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3) });
        (_alice, _aliceUser) = await _factory.CreateApprovedUserAsync(_admin);
        (_bob, _) = await _factory.CreateApprovedUserAsync(_admin);
    }

    public async Task DisposeAsync()
    {
        _alice.Dispose();
        _bob.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private static async Task<PortfolioDto> CreateAsync(HttpClient client, string name, string[] universe, string currency = "USD", decimal cash = 100_000m,
        string? description = null) =>
        await (await client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
        {
            Name = name, Description = description, BaseCurrency = currency, InitialInvestment = cash, Universe = [.. universe],
        })).ReadAsync<PortfolioDto>();

    private static Task<HttpResponseMessage> RecordAsync(HttpClient client, Guid portfolioId, RecordTransactionRequest body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/portfolios/{portfolioId}/journal")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, ApiFactory.Json), System.Text.Encoding.UTF8, "application/json"),
        };
        if (key is not "") request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static async Task<PortfolioTransactionDto> BookAsync(HttpClient client, Guid portfolioId, RecordTransactionRequest body) =>
        await (await RecordAsync(client, portfolioId, body)).EnsureStatus(HttpStatusCode.Created).ReadAsync<PortfolioTransactionDto>();

    private static async Task<HoldingsResponse> HoldingsAsync(HttpClient client, Guid id) =>
        await (await client.GetAsync($"/api/v1/portfolios/{id}/holdings")).ReadAsync<HoldingsResponse>();

    private static async Task<decimal> LedgerSumAsync(HttpClient client, Guid id) =>
        (await (await client.GetAsync($"/api/v1/portfolios/{id}/cash-ledger?pageSize=500")).ReadAsync<PagedResult<CashEntryDto>>()).Items.Sum(e => e.Amount);

    // ------------------------------------------------------------------ multiple portfolios and overview

    [Fact]
    public async Task User_HasSeveralIndependentPortfolios_AndTheOverviewListsOnlyTheirs()
    {
        var a = await CreateAsync(_alice, "Long-term core", ["AAPL", "MSFT", "JPM"], description: "Diversified core holdings");
        var b = await CreateAsync(_alice, "Tech research", ["NVDA", "GOOGL"], cash: 50_000m);
        var c = await CreateAsync(_bob, "Bob's", ["XOM", "JPM"]);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("Diversified core holdings", a.Description);
        Assert.Equal("Active", a.Status);
        // The chosen working universe is stored as optimizer input – nothing is bought.
        Assert.Equal(["AAPL", "MSFT", "JPM"], a.Settings.Universe);
        Assert.Equal(["NVDA", "GOOGL"], b.Settings.Universe);
        Assert.True(b.Settings.MaxWeight >= 0.5, "two stocks need a feasible cap of at least 50 %");
        Assert.DoesNotContain((await HoldingsAsync(_alice, a.Id)).Holdings, h => h.Quantity > 0);

        var overview = await (await _alice.GetAsync("/api/v1/portfolios/overview?currency=USD")).ReadAsync<PortfolioOverviewDto>();
        Assert.Equal([a.Id, b.Id], overview.Portfolios.Select(p => p.Id));
        Assert.Equal(2, overview.ActivePortfolios);
        Assert.Equal(150_000m, overview.TotalValue);
        Assert.Equal(150_000m, overview.TotalNetContributions);
        Assert.All(overview.Portfolios, p => Assert.Equal(0m, p.ProfitLoss));
        Assert.Empty(overview.TotalProblems);

        var bobs = await (await _bob.GetAsync("/api/v1/portfolios/overview")).ReadAsync<PortfolioOverviewDto>();
        Assert.Equal([c.Id], bobs.Portfolios.Select(p => p.Id));
        Assert.Equal(100_000m, bobs.TotalValue);
    }

    [Fact]
    public async Task Overview_ConvertsPortfoliosInOtherCurrencies_WithADatedRate()
    {
        await CreateAsync(_alice, "USD", ["AAPL", "MSFT"]);
        await CreateAsync(_alice, "EUR", ["SAP.DE", "ADS.DE"], currency: "EUR", cash: 10_000m);
        var usd = await (await _alice.GetAsync("/api/v1/portfolios/overview?currency=USD")).ReadAsync<PortfolioOverviewDto>();
        var eur = await (await _alice.GetAsync("/api/v1/portfolios/overview?currency=EUR")).ReadAsync<PortfolioOverviewDto>();
        Assert.NotNull(usd.TotalValue);
        Assert.NotNull(eur.TotalValue);
        // Not a naive sum of 100,000 USD + 10,000 EUR.
        Assert.NotEqual(110_000m, usd.TotalValue);
        Assert.Contains("never", usd.Methodology);
        Assert.Equal(HttpStatusCode.BadRequest, (await _alice.GetAsync("/api/v1/portfolios/overview?currency=usd")).StatusCode);
    }

    // ------------------------------------------------------------------ isolation between portfolios of one user

    [Fact]
    public async Task OptimizationRunsHoldingsAndTransactions_NeverCrossBetweenPortfolios()
    {
        var a = await CreateAsync(_alice, "A", ["AAPL", "MSFT", "JPM", "XOM"]);
        var b = await CreateAsync(_alice, "B", ["NVDA", "GOOGL", "AMZN"]);
        // Minimum variance is defined for any data (maximum Sharpe is refused, with an explanation, when nothing beats the risk-free rate).
        RunOptimizationRequest MinVar(PortfolioDto p) => new() { Settings = p.Settings with { Objective = ObjectiveKind.MinimumVariance } };
        var runA = await (await _alice.PostJsonAsync($"/api/v1/portfolios/{a.Id}/optimization-runs", MinVar(a))).ReadAsync<OptimizationRunDto>();
        var runB = await (await _alice.PostJsonAsync($"/api/v1/portfolios/{b.Id}/optimization-runs", MinVar(b))).ReadAsync<OptimizationRunDto>();
        Assert.True(runA.Status == OptimizationRunStatus.Succeeded, runA.Message);
        Assert.True(runB.Status == OptimizationRunStatus.Succeeded, runB.Message);
        Assert.All(runA.Allocation, w => Assert.Contains(w.Symbol, a.Settings.Universe));
        Assert.All(runB.Allocation, w => Assert.Contains(w.Symbol, b.Settings.Universe));

        var runsA = await (await _alice.GetAsync($"/api/v1/portfolios/{a.Id}/optimization-runs")).ReadAsync<PagedResult<OptimizationRunSummaryDto>>();
        var runsB = await (await _alice.GetAsync($"/api/v1/portfolios/{b.Id}/optimization-runs")).ReadAsync<PagedResult<OptimizationRunSummaryDto>>();
        Assert.Equal([runA.Id], runsA.Items.Select(r => r.Id));
        Assert.Equal([runB.Id], runsB.Items.Select(r => r.Id));

        // A proposal from A's run cannot be used to trade in B.
        var proposalA = await (await _alice.PostJsonAsync($"/api/v1/optimization-runs/{runA.Id}/trade-proposals", new { })).ReadAsync<TradeProposalDto>();
        Assert.Equal(a.Id, proposalA.PortfolioId);
        var foreign = await _alice.PostJsonAsync($"/api/v1/portfolios/{b.Id}/rebalance-preview", new RebalancePreviewRequest { OptimizationRunId = runA.Id });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        // A run or a proposal changes no holdings; a booking in A changes only A.
        Assert.DoesNotContain((await HoldingsAsync(_alice, a.Id)).Holdings, h => h.Quantity > 0);
        await BookAsync(_alice, a.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = 10, Price = 150m, Fee = 1m });
        Assert.Equal(10m, (await HoldingsAsync(_alice, a.Id)).Holdings.Single(h => h.Quantity > 0).Quantity);
        Assert.DoesNotContain((await HoldingsAsync(_alice, b.Id)).Holdings, h => h.Quantity > 0);
        Assert.Equal(100_000m, (await HoldingsAsync(_alice, b.Id)).Summary.CashBalance);
        var journalB = await (await _alice.GetAsync($"/api/v1/portfolios/{b.Id}/journal")).ReadAsync<PagedResult<PortfolioTransactionDto>>();
        Assert.Equal(0, journalB.TotalCount);
    }

    // ------------------------------------------------------------------ manual transactions and accounting

    [Fact]
    public async Task ManualTransactions_FollowTheAccountingRules_AndTheLedgerReconciles()
    {
        var p = await CreateAsync(_alice, "Books", ["AAPL", "MSFT"], cash: 10_000m);

        var buy = await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Buy, Symbol = "aapl", Quantity = 10, Price = 100m, Fee = 1m, Note = "first lot" });
        Assert.Equal(-1_001m, buy.CashAmount);
        Assert.Equal(8_999m, buy.CashBalanceAfter);
        Assert.Equal("AAPL", buy.Symbol);
        Assert.NotNull(buy.ExecutionId);

        // Rejected bookings change nothing at all.
        async Task AssertRejected(RecordTransactionRequest body, HttpStatusCode expected)
        {
            var before = await HoldingsAsync(_alice, p.Id);
            var journalBefore = (await (await _alice.GetAsync($"/api/v1/portfolios/{p.Id}/journal")).ReadAsync<PagedResult<PortfolioTransactionDto>>()).TotalCount;
            Assert.Equal(expected, (await RecordAsync(_alice, p.Id, body)).StatusCode);
            var after = await HoldingsAsync(_alice, p.Id);
            Assert.Equal(before.Summary.CashBalance, after.Summary.CashBalance);
            Assert.Equal(before.Holdings.Sum(h => h.Quantity), after.Holdings.Sum(h => h.Quantity));
            Assert.Equal(journalBefore, (await (await _alice.GetAsync($"/api/v1/portfolios/{p.Id}/journal")).ReadAsync<PagedResult<PortfolioTransactionDto>>()).TotalCount);
        }
        await AssertRejected(new() { Type = PortfolioTransactionType.Sell, Symbol = "AAPL", Quantity = 15, Price = 100m }, (HttpStatusCode)422);   // more than held
        await AssertRejected(new() { Type = PortfolioTransactionType.Sell, Symbol = "MSFT", Quantity = 1, Price = 100m }, (HttpStatusCode)422);    // never held
        await AssertRejected(new() { Type = PortfolioTransactionType.Buy, Symbol = "MSFT", Quantity = 1000, Price = 100m }, (HttpStatusCode)422);  // insufficient cash
        await AssertRejected(new() { Type = PortfolioTransactionType.Withdrawal, Amount = 20_000m }, (HttpStatusCode)422);
        await AssertRejected(new() { Type = PortfolioTransactionType.Dividend, Symbol = "MSFT", Amount = 5m }, (HttpStatusCode)422);              // never held
        await AssertRejected(new() { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = 1 }, HttpStatusCode.BadRequest);              // no price
        await AssertRejected(new() { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = 1.5m, Price = 10m }, HttpStatusCode.BadRequest); // whole shares only
        await AssertRejected(new() { Type = PortfolioTransactionType.Buy, Symbol = "NOPE", Quantity = 1, Price = 10m }, HttpStatusCode.BadRequest);
        await AssertRejected(new() { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = -1, Price = 10m }, HttpStatusCode.BadRequest);
        await AssertRejected(new() { Type = PortfolioTransactionType.Deposit }, HttpStatusCode.BadRequest);
        await AssertRejected(new() { Type = PortfolioTransactionType.Deposit, Amount = 10m, Date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1) }, HttpStatusCode.BadRequest);
        await AssertRejected(new() { Type = PortfolioTransactionType.Deposit, Amount = 10m, Date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10) }, HttpStatusCode.BadRequest); // before latest booking
        Assert.Equal(HttpStatusCode.BadRequest, (await RecordAsync(_alice, p.Id, new() { Type = PortfolioTransactionType.Deposit, Amount = 1m }, key: "")).StatusCode);

        // Sell part of the position: weighted-average cost (fees capitalized), realized P&L after the sell fee.
        var sell = await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Sell, Symbol = "AAPL", Quantity = 4, Price = 110m, Fee = 1m });
        Assert.Equal(439m, sell.CashAmount);
        Assert.Equal(440m - 1m - 4m * 100.1m, sell.RealizedPnl);

        await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Withdrawal, Amount = 1_000m });
        var deposit = await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Deposit, Amount = 500m });
        await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Dividend, Symbol = "AAPL", Amount = 5.25m });
        var fee = await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Fee, Amount = 2m, Note = "custody" });

        var expectedCash = 10_000m - 1_001m + 439m - 1_000m + 500m + 5.25m - 2m;
        var holdings = await HoldingsAsync(_alice, p.Id);
        Assert.Equal(expectedCash, holdings.Summary.CashBalance);
        Assert.Equal(expectedCash, fee.CashBalanceAfter);
        Assert.Equal(expectedCash, await LedgerSumAsync(_alice, p.Id));
        var aapl = holdings.Holdings.Single(h => h.Symbol == "AAPL");
        Assert.Equal(6m, aapl.Quantity);
        Assert.Equal(100.1m, aapl.AverageCost, 6);
        // Contributions are deposits − withdrawals; the dividend and fee are not contributions.
        Assert.Equal(10_000m - 1_000m + 500m, holdings.Summary.NetContributions);

        var journal = await (await _alice.GetAsync($"/api/v1/portfolios/{p.Id}/journal")).ReadAsync<PagedResult<PortfolioTransactionDto>>();
        Assert.Equal(6, journal.TotalCount);
        Assert.Equal(PortfolioTransactionType.Fee, journal.Items[0].Type);
        Assert.Equal("first lot", journal.Items.Single(t => t.Id == buy.Id).Note);

        // Reversal of a deposit: a new opposite booking; the original stays and cannot be reversed twice.
        var reversal = await (await _alice.SendAsync(WithKey(HttpMethod.Post, $"/api/v1/portfolios/{p.Id}/journal/{deposit.Id}/reverse", new ReverseTransactionRequest())))
            .EnsureStatus(HttpStatusCode.Created).ReadAsync<PortfolioTransactionDto>();
        Assert.Equal(-500m, reversal.CashAmount);
        Assert.Equal(deposit.Id, reversal.ReversesId);
        Assert.Equal(expectedCash - 500m, await LedgerSumAsync(_alice, p.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await _alice.SendAsync(WithKey(HttpMethod.Post, $"/api/v1/portfolios/{p.Id}/journal/{deposit.Id}/reverse", new ReverseTransactionRequest()))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _alice.SendAsync(WithKey(HttpMethod.Post, $"/api/v1/portfolios/{p.Id}/journal/{reversal.Id}/reverse", new ReverseTransactionRequest()))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await _alice.SendAsync(WithKey(HttpMethod.Post, $"/api/v1/portfolios/{p.Id}/journal/{buy.Id}/reverse", new ReverseTransactionRequest()))).StatusCode);
        var after = await (await _alice.GetAsync($"/api/v1/portfolios/{p.Id}/journal")).ReadAsync<PagedResult<PortfolioTransactionDto>>();
        Assert.Equal(reversal.Id, after.Items.Single(t => t.Id == deposit.Id).ReversedById);
        Assert.Equal(HoldingsTotal(await HoldingsAsync(_alice, p.Id)), HoldingsTotal(holdings));
    }

    private static decimal HoldingsTotal(HoldingsResponse h) => h.Holdings.Sum(x => x.Quantity);

    private static HttpRequestMessage WithKey<T>(HttpMethod method, string url, T body, string? key = null)
    {
        var r = new HttpRequestMessage(method, url) { Content = new StringContent(JsonSerializer.Serialize(body, ApiFactory.Json), System.Text.Encoding.UTF8, "application/json") };
        r.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return r;
    }

    [Fact]
    public async Task RepeatedSubmission_WithTheSameKey_BooksOnce()
    {
        var p = await CreateAsync(_alice, "Idempotent", ["AAPL", "MSFT"], cash: 10_000m);
        var body = new RecordTransactionRequest { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = 2, Price = 100m };
        var first = await (await RecordAsync(_alice, p.Id, body, "same-key")).EnsureStatus(HttpStatusCode.Created).ReadAsync<PortfolioTransactionDto>();
        var second = await (await RecordAsync(_alice, p.Id, body, "same-key")).EnsureStatus(HttpStatusCode.Created).ReadAsync<PortfolioTransactionDto>();
        Assert.False(first.IdempotentReplay);
        Assert.True(second.IdempotentReplay);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2m, (await HoldingsAsync(_alice, p.Id)).Holdings.Single(h => h.Quantity > 0).Quantity);
        Assert.Equal(HttpStatusCode.Conflict,
            (await RecordAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Deposit, Amount = 5m }, "same-key")).StatusCode);
    }

    [Fact]
    public async Task ForeignCurrencyTrade_UsesTheDatedReferenceRate()
    {
        var p = await CreateAsync(_alice, "EUR book", ["AAPL", "SAP.DE"], currency: "EUR", cash: 10_000m);
        var buy = await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = 3, Price = 100m });
        Assert.Equal("USD", buy.Currency);
        Assert.NotEqual(1m, buy.FxRate);
        Assert.NotNull(buy.FxRateDate);
        Assert.NotNull(buy.FxSource);
        Assert.Equal(-Math.Round(3 * 100m * buy.FxRate, 6), buy.CashAmount);
    }

    // ------------------------------------------------------------------ performance with cash flows

    [Fact]
    public async Task Performance_RemovesDepositsAndWithdrawals_ButCountsDividends()
    {
        // Three portfolios with the same trade 60 trading days ago; history is written directly (bookings cannot be backdated via the API).
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90);
        Guid plain, withFlows, withDividend;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var aapl = await db.Assets.SingleAsync(a => a.Symbol == "AAPL");
            var source = (await db.Settings.SingleAsync()).ActiveProvider;
            var close = await db.HistoricalPrices.Where(x => x.AssetId == aapl.Id && x.Source == source && x.Date >= start).OrderBy(x => x.Date).FirstAsync();
            var day = close.Date;
            var t0 = day.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);

            Portfolio Make(string name)
            {
                var p = new Portfolio { Id = Guid.NewGuid(), OwnerId = _aliceUser.Id, Name = name, CreatedAtUtc = t0.AddHours(-1), UpdatedAtUtc = t0 };
                p.Deposit(10_000m, t0.AddHours(-1), "initial");
                p.Buy(aapl, 1, close.Close, day, 0m, Guid.NewGuid(), t0);
                db.Portfolios.Add(p);
                return p;
            }
            var p1 = Make("plain");
            var p2 = Make("flows");
            // A large deposit on a Saturday and a withdrawal three weeks later: external flows, not gains or losses.
            var saturday = day.AddDays(14);
            while (saturday.DayOfWeek != DayOfWeek.Saturday) saturday = saturday.AddDays(1);
            p2.Deposit(1_000_000m, saturday.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc), "big deposit");
            p2.Withdraw(500_000m, saturday.AddDays(21).ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc), "withdrawal");
            var p3 = Make("dividend");
            p3.ReceiveDividend(aapl, 100m, day.AddDays(10).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc), "dividend");
            await db.SaveChangesAsync();
            (plain, withFlows, withDividend) = (p1.Id, p2.Id, p3.Id);
        }

        async Task<PerformanceDto> Perf(Guid id) => await (await _alice.GetAsync($"/api/v1/portfolios/{id}/performance")).ReadAsync<PerformanceDto>();
        var a = await Perf(plain);
        var b = await Perf(withFlows);
        var c = await Perf(withDividend);
        Assert.NotNull(a.TotalReturn.Value);
        // Value jumps by ~1,000,000 but the time-weighted return barely differs (only AAPL's weight changes).
        Assert.True(b.Series[^1].Value > 500_000);
        Assert.InRange(b.TotalReturn.Value!.Value - a.TotalReturn.Value!.Value, -0.01, 0.01);
        // The deposit booked on a Saturday is applied (not dropped) and shows in the cash series.
        Assert.Equal(10_000d - (a.Series[0].Value - a.Series[0].Cash!.Value) + 500_000d, b.Series[^1].Cash!.Value, 2);
        // A dividend is income: about +1 % on a 10,000 portfolio.
        Assert.InRange(c.TotalReturn.Value!.Value - a.TotalReturn.Value!.Value, 0.008, 0.012);
        Assert.Contains("never counted as gains or losses", a.Methodology);
    }

    // ------------------------------------------------------------------ lifecycle

    [Fact]
    public async Task Archive_KeepsHistoryReadable_BlocksChanges_AndDeleteRemovesIt()
    {
        var p = await CreateAsync(_alice, "Lifecycle", ["AAPL", "MSFT"], cash: 10_000m);
        await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = 1, Price = 100m });
        var proposal = await (await _alice.PostJsonAsync($"/api/v1/portfolios/{p.Id}/rebalance-preview",
            new RebalancePreviewRequest { TargetWeights = new Dictionary<string, double> { ["MSFT"] = 0.5 } })).ReadAsync<TradeProposalDto>();

        var renamed = await (await _alice.PutJsonAsync($"/api/v1/portfolios/{p.Id}/details", new UpdatePortfolioDetailsRequest { Name = "Renamed", Description = "purpose" }))
            .ReadAsync<PortfolioDto>();
        Assert.Equal(("Renamed", "purpose"), (renamed.Name, renamed.Description));
        Assert.Equal(p.Settings.Universe, renamed.Settings.Universe);

        var archived = await (await _alice.PostJsonAsync($"/api/v1/portfolios/{p.Id}/archive", new { })).ReadAsync<PortfolioDto>();
        Assert.Equal("Archived", archived.Status);
        Assert.Equal((HttpStatusCode)422, (await RecordAsync(_alice, p.Id, new() { Type = PortfolioTransactionType.Deposit, Amount = 1m })).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await _alice.PostJsonAsync($"/api/v1/portfolios/{p.Id}/optimization-runs", new RunOptimizationRequest())).StatusCode);
        var execute = WithKey(HttpMethod.Post, $"/api/v1/portfolios/{p.Id}/paper-trades", new ExecutePaperTradesRequest { ProposalId = proposal.Id, Confirm = true });
        Assert.Equal((HttpStatusCode)422, (await _alice.SendAsync(execute)).StatusCode);
        // Still readable, and listed as archived (not in the active total).
        Assert.Equal(1m, (await HoldingsAsync(_alice, p.Id)).Holdings.Single(h => h.Quantity > 0).Quantity);
        var overview = await (await _alice.GetAsync("/api/v1/portfolios/overview")).ReadAsync<PortfolioOverviewDto>();
        Assert.Equal("Archived", overview.Portfolios.Single(x => x.Id == p.Id).Status);
        Assert.Equal(0, overview.ActivePortfolios);
        Assert.Equal(1, overview.ArchivedPortfolios);

        (await _alice.PostJsonAsync($"/api/v1/portfolios/{p.Id}/restore", new { })).EnsureStatus(HttpStatusCode.OK);
        await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Deposit, Amount = 1m });
        await _alice.PostJsonAsync($"/api/v1/portfolios/{p.Id}/archive", new { });
        Assert.Equal(HttpStatusCode.NoContent, (await _alice.DeleteAsync($"/api/v1/portfolios/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _alice.GetAsync($"/api/v1/portfolios/{p.Id}")).StatusCode);
    }

    [Fact]
    public async Task ActivePortfolio_CanBeDeleted_WithItsHistory()
    {
        var p = await CreateAsync(_alice, "Delete me", ["AAPL", "MSFT"], cash: 10_000m);
        await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = 1, Price = 100m });
        var run = await (await _alice.PostJsonAsync($"/api/v1/portfolios/{p.Id}/optimization-runs", new RunOptimizationRequest())).ReadAsync<OptimizationRunDto>();

        Assert.Equal(HttpStatusCode.NoContent, (await _alice.DeleteAsync($"/api/v1/portfolios/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _alice.GetAsync($"/api/v1/portfolios/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _alice.GetAsync($"/api/v1/optimization-runs/{run.Id}")).StatusCode);
    }

    [Fact]
    public async Task QuoteRefresh_CoversOnlyTheOpenPositions()
    {
        var p = await CreateAsync(_alice, "Refresh", ["AAPL", "MSFT"], cash: 10_000m);
        var empty = await (await _alice.PostAsync($"/api/v1/portfolios/{p.Id}/quotes/refresh", null)).ReadAsync<QuoteRefreshResultDto>();
        Assert.Empty(empty.Items);

        await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = 1, Price = 100m });
        var result = await (await _alice.PostAsync($"/api/v1/portfolios/{p.Id}/quotes/refresh", null)).ReadAsync<QuoteRefreshResultDto>();
        Assert.Equal("AAPL", Assert.Single(result.Items).Symbol);
    }

    // ------------------------------------------------------------------ cross-user protection

    [Fact]
    public async Task AnotherUser_CannotReadOrChangeAnyPartOfAPortfolio()
    {
        var p = await CreateAsync(_alice, "Private", ["AAPL", "MSFT"], cash: 10_000m);
        var buy = await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Buy, Symbol = "AAPL", Quantity = 1, Price = 100m });
        var deposit = await BookAsync(_alice, p.Id, new RecordTransactionRequest { Type = PortfolioTransactionType.Deposit, Amount = 50m });
        var run = await (await _alice.PostJsonAsync($"/api/v1/portfolios/{p.Id}/optimization-runs", new RunOptimizationRequest())).ReadAsync<OptimizationRunDto>();

        foreach (var url in new[]
                 {
                     $"/api/v1/portfolios/{p.Id}", $"/api/v1/portfolios/{p.Id}/holdings", $"/api/v1/portfolios/{p.Id}/journal",
                     $"/api/v1/portfolios/{p.Id}/transactions", $"/api/v1/portfolios/{p.Id}/cash-ledger", $"/api/v1/portfolios/{p.Id}/orders",
                     $"/api/v1/portfolios/{p.Id}/performance", $"/api/v1/portfolios/{p.Id}/risk", $"/api/v1/portfolios/{p.Id}/optimization-runs",
                     $"/api/v1/portfolios/{p.Id}/trade-proposals", $"/api/v1/optimization-runs/{run.Id}",
                 })
            Assert.True((await _bob.GetAsync(url)).StatusCode == HttpStatusCode.NotFound, url);

        Assert.Equal(HttpStatusCode.NotFound, (await RecordAsync(_bob, p.Id, new() { Type = PortfolioTransactionType.Deposit, Amount = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.SendAsync(WithKey(HttpMethod.Post, $"/api/v1/portfolios/{p.Id}/journal/{deposit.Id}/reverse", new ReverseTransactionRequest()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.PutJsonAsync($"/api/v1/portfolios/{p.Id}/details", new UpdatePortfolioDetailsRequest { Name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.PostJsonAsync($"/api/v1/portfolios/{p.Id}/archive", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.PostJsonAsync($"/api/v1/portfolios/{p.Id}/restore", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.DeleteAsync($"/api/v1/portfolios/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.PostAsync($"/api/v1/portfolios/{p.Id}/quotes/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.PostJsonAsync($"/api/v1/portfolios/{p.Id}/optimization-runs", new RunOptimizationRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.PostJsonAsync($"/api/v1/optimization-runs/{run.Id}/trade-proposals", new { })).StatusCode);
        // Research cannot be attached to someone else's portfolio either.
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.PostJsonAsync("/api/v1/research", new StartResearchRequest { Objective = "low risk US", PortfolioId = p.Id })).StatusCode);

        var bobs = await (await _bob.GetAsync("/api/v1/portfolios/overview")).ReadAsync<PortfolioOverviewDto>();
        Assert.Empty(bobs.Portfolios);
        // Nothing changed for Alice.
        var holdings = await HoldingsAsync(_alice, p.Id);
        Assert.Equal(10_000m - 100m + 50m, holdings.Summary.CashBalance);
        Assert.Equal(buy.Quantity, holdings.Holdings.Single(h => h.Quantity > 0).Quantity);
        Assert.Equal("Private", (await (await _alice.GetAsync($"/api/v1/portfolios/{p.Id}")).ReadAsync<PortfolioDto>()).Name);
    }

    [Fact]
    public async Task Research_StartedFromAPortfolio_UsesItsCurrency_AndKeepsTheLink()
    {
        var eur = await CreateAsync(_alice, "EUR research", ["SAP.DE", "ADS.DE"], currency: "EUR", cash: 10_000m);
        var session = await (await _alice.PostJsonAsync("/api/v1/research",
            new StartResearchRequest { Objective = "Low risk portfolio of US stocks", Market = "US", PortfolioId = eur.Id })).ReadAsync<ResearchSessionDto>();
        Assert.Equal(eur.Id, session.PortfolioId);
        Assert.Equal("EUR research", session.PortfolioName);
        Assert.Equal("EUR", session.PortfolioCurrency);
        Assert.Contains(session.Events, e => e.Name == "portfolio_context");
    }
}
