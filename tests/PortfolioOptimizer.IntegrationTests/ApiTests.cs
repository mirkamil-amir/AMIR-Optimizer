using System.Net;
using System.Text.Json;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

public sealed class ApiTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _client = null!;
    private CurrentUserDto _user = null!;

    private static readonly string[] Universe = ["AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "JPM", "XOM"];

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        // Download simulated history for the universe and the benchmark through the real background job pipeline.
        await _admin.SyncAndWaitAsync(new SyncRequest { StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3) });
        (_client, _user) = await _factory.CreateApprovedUserAsync(_admin);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<PortfolioDto> CreatePortfolioAsync(PortfolioSettingsDto? settings = null) =>
        await (await _client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
        {
            Name = "Integration",
            InitialInvestment = 100_000m,
            BaseCurrency = "USD",
            Settings = settings ?? new PortfolioSettingsDto { Universe = [.. Universe], MaxWeight = 0.4, LookbackDays = 504, Objective = ObjectiveKind.MinimumVariance },
        })).ReadAsync<PortfolioDto>();

    [Fact]
    public async Task HealthAndReadiness_AreHealthy()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal("Healthy", await anonymous.GetStringAsync("/health/ready"));
    }

    [Fact]
    public async Task Swagger_DocumentsEndpoints()
    {
        using var anonymous = _factory.CreateClient();
        var json = await anonymous.GetStringAsync("/swagger/v1/swagger.json");
        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/portfolios/{id}/paper-trades", out _));
        Assert.True(paths.TryGetProperty("/api/v1/backtests", out _));
        Assert.True(paths.TryGetProperty("/api/v1/auth/login", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/users", out _));
    }

    [Fact]
    public async Task MarketDataStatus_ShowsSimulatedCoverage()
    {
        var status = await (await _client.GetAsync("/api/v1/market-data/status")).ReadAsync<MarketDataStatusDto>();
        Assert.Equal("Mock", status.ActiveProvider);
        Assert.True(status.ActiveProviderIsSimulated);
        Assert.All(status.Coverage.Where(c => c.IsEnabled), c => Assert.True(c.Observations > 500 && c.IsSimulated));
        // Catalogue instruments nobody downloaded yet are listed too, as missing (downloadable), not hidden.
        Assert.All(status.Coverage.Where(c => c.Observations == 0), c => Assert.Equal(DataFreshness.Missing, c.Freshness));
        // The latest price is in the instrument's own trading currency, and the status says which.
        Assert.All(status.Coverage, c => Assert.Matches("^[A-Z]{3}$", c.Currency));
        Assert.Contains(status.Coverage, c => c.Currency == "USD");
        // The whole catalogue, by type (Market Data shows stocks & ETFs, indexes and currencies in separate boxes).
        Assert.Contains(status.Coverage, c => c.AssetType == AssetType.Index);
        Assert.Contains(status.Coverage, c => c.AssetType == AssetType.Equity);
        Assert.NotNull(status.Currencies);
        Assert.All(status.Currencies!, c => Assert.True(c.Observations > 0 && c.LatestUnitsPerEur > 0 && c.FirstDate <= c.LastDate));
    }

    [Fact]
    public async Task SecondSync_IsIncremental()
    {
        // Approved users may refresh named symbols (whole-universe syncs are for administrators).
        var job = await (await _client.PostJsonAsync("/api/v1/market-data/sync", new SyncRequest { Symbols = ["AAPL"], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3) })).ReadAsync<SyncJobDto>();
        SyncJobDto status;
        do
        {
            await Task.Delay(100);
            status = await (await _client.GetAsync($"/api/v1/market-data/jobs/{job.Id}")).ReadAsync<SyncJobDto>();
        } while (status.Status is JobStatus.Queued or JobStatus.Running);

        var log = Assert.Single(status.Logs);
        Assert.Equal(0, log.RowsInserted);
        Assert.True(log.RowsReceived < 20, $"Expected only the overlap window to be requested, got {log.RowsReceived} rows.");
    }

    [Fact]
    public async Task InvalidRequests_ReturnProblemDetails()
    {
        var bad = await _client.PostJsonAsync("/api/v1/portfolios", new { name = "", initialInvestment = -5, baseCurrency = "usd" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType?.MediaType);
        var body = await bad.Content.ReadAsStringAsync();
        Assert.Contains("errors", body);

        var unknownAsset = await _client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
        {
            Name = "x", Settings = new PortfolioSettingsDto { Universe = ["NOPE"] },
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownAsset.StatusCode);
        Assert.Contains("Unknown asset", await unknownAsset.Content.ReadAsStringAsync());

        var missing = await _client.GetAsync($"/api/v1/portfolios/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);

        var badPage = await _client.GetAsync("/api/v1/assets/AAPL/prices?pageSize=999999");
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }

    [Fact]
    public async Task FullWorkflow_OptimizeProposeExecute_UpdatesHoldingsAndLedger()
    {
        var portfolio = await CreatePortfolioAsync();

        var run = await (await _client.PostJsonAsync($"/api/v1/portfolios/{portfolio.Id}/optimization-runs", new RunOptimizationRequest())).ReadAsync<OptimizationRunDto>();
        Assert.True(run.Status == OptimizationRunStatus.Succeeded, run.Message + " | " + string.Join("; ", run.Diagnostics));
        Assert.True(run.UsesSimulatedData);
        Assert.Equal(0.99, run.Allocation.Sum(a => a.Weight), 6);
        Assert.All(run.Allocation, a => Assert.InRange(a.Weight, 0, 0.4 + 1e-6));
        Assert.NotEmpty(run.Analytics.Frontier);
        Assert.Equal(16, run.DataSnapshotHash.Length);

        var proposal = await (await _client.GetAsync($"/api/v1/optimization-runs/{run.Id}/trade-proposal")).ReadAsync<TradeProposalDto>();
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.True(proposal.EstimatedCashAfter >= 0);
        Assert.Contains(proposal.Notes, n => n.Contains("SIMULATED"));
        var buys = proposal.Lines.Where(l => l.Action == ProposalAction.Buy).ToList();
        Assert.NotEmpty(buys);
        Assert.All(buys, l => Assert.Equal(Math.Floor(l.OrderQuantity), l.OrderQuantity));

        // Proposals never change holdings.
        var before = await (await _client.GetAsync($"/api/v1/portfolios/{portfolio.Id}/holdings")).ReadAsync<HoldingsResponse>();
        Assert.DoesNotContain(before.Holdings, h => h.Quantity > 0);

        // Missing idempotency key is rejected.
        var noKey = await _client.PostJsonAsync($"/api/v1/portfolios/{portfolio.Id}/paper-trades", new ExecutePaperTradesRequest { ProposalId = proposal.Id, Confirm = true });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        var key = Guid.NewGuid().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/portfolios/{portfolio.Id}/paper-trades")
        {
            Content = JsonContent(new ExecutePaperTradesRequest { ProposalId = proposal.Id, Confirm = true }),
        };
        request.Headers.Add("Idempotency-Key", key);
        var result = await (await _client.SendAsync(request)).ReadAsync<PaperTradeResultDto>();
        Assert.False(result.IdempotentReplay);
        Assert.Equal(buys.Count, result.Orders.Count(o => o.Status == OrderStatus.Filled));

        var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/portfolios/{portfolio.Id}/paper-trades")
        {
            Content = JsonContent(new ExecutePaperTradesRequest { ProposalId = proposal.Id, Confirm = true }),
        };
        replayRequest.Headers.Add("Idempotency-Key", key);
        var replay = await (await _client.SendAsync(replayRequest)).ReadAsync<PaperTradeResultDto>();
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(result.BatchId, replay.BatchId);

        var again = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/portfolios/{portfolio.Id}/paper-trades")
        {
            Content = JsonContent(new ExecutePaperTradesRequest { ProposalId = proposal.Id, Confirm = true }),
        };
        again.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(again)).StatusCode);

        var holdings = await (await _client.GetAsync($"/api/v1/portfolios/{portfolio.Id}/holdings")).ReadAsync<HoldingsResponse>();
        Assert.Equal(buys.Count, holdings.Holdings.Count(h => h.Quantity > 0));
        Assert.Equal(result.CashAfter, holdings.Summary.CashBalance);
        Assert.Equal(proposal.EstimatedCashAfter, holdings.Summary.CashBalance);
        Assert.Equal(-holdings.Summary.TotalFees, holdings.Summary.UnrealizedPnl, 6);
        Assert.Equal(holdings.Summary.TotalValue, holdings.Summary.MarketValue + holdings.Summary.CashBalance);

        var tx = await (await _client.GetAsync($"/api/v1/portfolios/{portfolio.Id}/transactions")).ReadAsync<PagedResult<TransactionDto>>();
        Assert.Equal(buys.Count, tx.TotalCount);
        var ledger = await (await _client.GetAsync($"/api/v1/portfolios/{portfolio.Id}/cash-ledger?pageSize=500")).ReadAsync<PagedResult<CashEntryDto>>();
        Assert.Equal(holdings.Summary.CashBalance, ledger.Items.Sum(e => e.Amount));

        var risk = await (await _client.GetAsync($"/api/v1/portfolios/{portfolio.Id}/risk")).ReadAsync<RiskDto>();
        Assert.NotNull(risk.AnnualizedVolatility.Value);
        Assert.Equal(1.0, risk.Contributions.Sum(c => c.RiskContribution ?? 0), 6);

        // A rebalance right after execution is within the drift threshold: no further buys of held names.
        var rebalance = await (await _client.PostJsonAsync($"/api/v1/portfolios/{portfolio.Id}/rebalance-preview", new RebalancePreviewRequest())).ReadAsync<TradeProposalDto>();
        Assert.DoesNotContain(rebalance.Lines, l => l.Action == ProposalAction.Sell);
    }

    [Fact]
    public async Task HistoryCoverage_CountsStoredPricesOfAWholeSelection()
    {
        var response = await _client.PostJsonAsync("/api/v1/stocks/history-coverage",
            new HistoryCoverageRequest { Symbols = ["AAPL", "msft", "NOPE"] });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var coverage = await response.ReadAsync<List<HistoryCoverageDto>>();
        Assert.Equal(["AAPL", "MSFT", "NOPE"], coverage.Select(c => c.Symbol));
        Assert.True(coverage[0].Observations > 60);
        Assert.Equal(0, coverage[2].Observations);

        var tooMany = await _client.PostJsonAsync("/api/v1/stocks/history-coverage",
            new HistoryCoverageRequest { Symbols = Enumerable.Range(0, 3001).Select(i => $"S{i}").ToList() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task MathOptimization_RefusesMoreThan500Stocks()
    {
        var portfolio = await CreatePortfolioAsync();
        var response = await _client.PostJsonAsync($"/api/v1/portfolios/{portfolio.Id}/optimization-runs", new RunOptimizationRequest
        {
            Settings = new PortfolioSettingsDto { Universe = Enumerable.Range(0, 501).Select(i => $"S{i}").ToList() },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InfeasibleConstraints_AreStoredWithExplanationAndNoWeights()
    {
        var portfolio = await CreatePortfolioAsync(new PortfolioSettingsDto { Universe = ["AAPL", "MSFT"], MaxWeight = 0.3, LookbackDays = 252 });
        var response = await _client.PostJsonAsync($"/api/v1/portfolios/{portfolio.Id}/optimization-runs", new RunOptimizationRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await response.ReadAsync<OptimizationRunDto>();
        Assert.Equal(OptimizationRunStatus.Infeasible, run.Status);
        Assert.Empty(run.Allocation);
        Assert.Contains("cannot reach the invested fraction", run.Message);

        var proposal = await _client.GetAsync($"/api/v1/optimization-runs/{run.Id}/trade-proposal");
        Assert.Equal(HttpStatusCode.Conflict, proposal.StatusCode);
    }

    [Fact]
    public async Task Backtest_RunsInBackground()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var created = await _client.PostJsonAsync("/api/v1/backtests", new CreateBacktestRequest
        {
            Universe = ["AAPL", "MSFT", "NVDA", "JPM", "XOM"],
            StartDate = today.AddYears(-2),
            EndDate = today,
            LookbackDays = 126,
            Objective = ObjectiveKind.MinimumVariance,
            MaxWeight = 0.5,
            BenchmarkSymbol = "SPY",
        });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var bt = await created.ReadAsync<BacktestDto>();

        for (var i = 0; i < 200 && bt.Status is JobStatus.Queued or JobStatus.Running; i++)
        {
            await Task.Delay(100);
            bt = await (await _client.GetAsync($"/api/v1/backtests/{bt.Id}")).ReadAsync<BacktestDto>();
        }
        Assert.Equal(JobStatus.Succeeded, bt.Status);
        Assert.True(bt.UsesSimulatedData);
        Assert.NotNull(bt.Result);
        Assert.Contains(bt.Result!.Metrics, m => m.Name.StartsWith("Benchmark"));
        Assert.All(bt.Result.Rebalances, r => Assert.True(r.ExecutionDate > r.SignalDate));
    }

    [Fact]
    public async Task Data_PersistsAcrossRestart()
    {
        var portfolio = await CreatePortfolioAsync();
        await using var restarted = new ApiFactory(_factory.ConnectionString);
        using var client = restarted.CreateSession();
        await client.LoginAsync(_user.Email, ApiFactory.UserPassword);
        var loaded = await (await client.GetAsync($"/api/v1/portfolios/{portfolio.Id}")).ReadAsync<PortfolioDto>();
        Assert.Equal(portfolio.Name, loaded.Name);
        Assert.Equal(100_000m, loaded.CashBalance);
    }

    private static StringContent JsonContent<T>(T body) =>
        new(JsonSerializer.Serialize(body, ApiFactory.Json), System.Text.Encoding.UTF8, "application/json");
}
