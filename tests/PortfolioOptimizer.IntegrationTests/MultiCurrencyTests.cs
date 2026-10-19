using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Stocks from different markets in one portfolio: USD (AAPL, MSFT), EUR (SAP.DE, SIE.DE), JPY (7203.T), GBP (AZN.L), valued,
/// optimized, traded and reported in one base currency. Simulated prices and simulated FX (deterministic, no network).
/// </summary>
public sealed class MultiCurrencyTests : IAsyncLifetime
{
    private static readonly string[] Mixed = ["AAPL", "MSFT", "SAP.DE", "SIE.DE", "7203.T", "AZN.L"];
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _client = await _factory.CreateAdminSessionAsync();
        await _client.SyncAndWaitAsync(new SyncRequest { Symbols = [.. Mixed, "SPY", "^GDAXI"], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3) });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<PortfolioDto> CreatePortfolioAsync(string currency, params string[] universe) =>
        await (await _client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
        {
            Name = $"Mixed {currency}",
            InitialInvestment = 100_000m,
            BaseCurrency = currency,
            Settings = new PortfolioSettingsDto
            {
                Universe = [.. universe.Length > 0 ? universe : Mixed], MaxWeight = 0.35, LookbackDays = 504, Objective = ObjectiveKind.MinimumVariance,
                BenchmarkSymbol = "SPY",
            },
        })).ReadAsync<PortfolioDto>();

    private async Task<OptimizationRunDto> OptimizeAsync(Guid portfolioId) =>
        await (await _client.PostJsonAsync($"/api/v1/portfolios/{portfolioId}/optimization-runs", new RunOptimizationRequest())).ReadAsync<OptimizationRunDto>();

    private async Task<PaperTradeResultDto> ExecuteAsync(Guid portfolioId, Guid proposalId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/portfolios/{portfolioId}/paper-trades")
        {
            Content = new StringContent(JsonSerializer.Serialize(new ExecutePaperTradesRequest { ProposalId = proposalId, Confirm = true }, ApiFactory.Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await (await _client.SendAsync(request)).ReadAsync<PaperTradeResultDto>();
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("CHF")]
    [InlineData("JPY")]
    public async Task PortfoliosInAnySupportedCurrency_CanHoldStocksFromOtherMarkets(string currency)
    {
        var p = await CreatePortfolioAsync(currency);
        Assert.Equal(currency, p.BaseCurrency);
        Assert.Equal(Mixed.Order(), p.Settings.Universe.Order());
    }

    [Fact]
    public async Task FxEndpoints_ExposeTheSource_Convention_AndConsistentRates()
    {
        var info = await (await _client.GetAsync("/api/v1/fx/currencies")).ReadAsync<FxInfoDto>();
        Assert.Equal("MockFx", info.Source);
        Assert.True(info.IsSimulated);
        Assert.Contains("units of the target currency per one unit of the source", info.Convention);
        Assert.Superset(new HashSet<string> { "EUR", "USD", "GBP", "JPY", "CHF" }, info.Currencies.ToHashSet());
        var eurUsd = await (await _client.GetAsync("/api/v1/fx/rate?from=EUR&to=USD")).ReadAsync<FxRateDto>();
        var usdEur = await (await _client.GetAsync("/api/v1/fx/rate?from=USD&to=EUR")).ReadAsync<FxRateDto>();
        Assert.Equal(1.0, eurUsd.Rate * usdEur.Rate, 12);
        Assert.Contains("not a rate at which money can be exchanged", eurUsd.Note);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/fx/rate?from=XAU&to=USD")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/fx/rate?from=eur&to=USD")).StatusCode);
    }

    [Fact]
    public async Task FxHistoryAndBoard_ShowPublishedRates_WithChanges()
    {
        var h = await (await _client.GetAsync("/api/v1/fx/history?from=EUR&to=USD&days=90")).ReadAsync<FxHistoryDto>();
        Assert.True(h.Points.Count > 50);
        Assert.All(h.Points, p => Assert.True(p.Date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday));
        Assert.Contains("SIMULATED", h.Note);
        var board = await (await _client.GetAsync("/api/v1/fx/board?base=EUR&quotes=USD,GBP,JPY,CHF,EUR")).ReadAsync<FxBoardDto>();
        Assert.Equal(["USD", "GBP", "JPY", "CHF"], board.Rows.Select(r => r.Quote));     // the base itself is left out
        var usd = board.Rows.Single(r => r.Quote == "USD");
        Assert.Equal(h.Points[^1].Rate, usd.Rate!.Value, 9);                             // same published rate as the chart
        Assert.NotNull(usd.Change1D);
        Assert.NotNull(usd.Change1M);
        Assert.Contains("not live", board.Note);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/fx/board?base=EUR&quotes=usd")).StatusCode);

        // Every supported currency (incl. TRY) fits on one board.
        var info = await (await _client.GetAsync("/api/v1/fx/currencies")).ReadAsync<FxInfoDto>();
        Assert.Contains("TRY", info.Currencies);
        var all = await (await _client.GetAsync($"/api/v1/fx/board?base=EUR&quotes={string.Join(',', info.Currencies)}")).ReadAsync<FxBoardDto>();
        Assert.Equal(info.Currencies.Count - 1, all.Rows.Count);
        Assert.NotNull(all.Rows.Single(r => r.Quote == "TRY").Rate);
    }

    [Fact]
    public async Task NewListings_TakeTheCurrencyOfTheirExchange()
    {
        var de = await (await _client.PostJsonAsync("/api/v1/assets", new CreateAssetRequest { Symbol = "ZZTEST.DE" })).ReadAsync<AssetDto>();
        Assert.Equal("EUR", de.Currency);
        var us = await (await _client.PostJsonAsync("/api/v1/assets", new CreateAssetRequest { Symbol = "ZZTEST" })).ReadAsync<AssetDto>();
        Assert.Equal("USD", us.Currency);
        var wrong = await _client.PostJsonAsync("/api/v1/assets", new CreateAssetRequest { Symbol = "ZZTEST.T", Currency = "USD" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("trades in JPY", await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnsupportedOrInvalidCurrencies_AreRejected_NotGuessed()
    {
        var r = await _client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest { Name = "X", BaseCurrency = "XAU", Settings = new PortfolioSettingsDto { Universe = ["AAPL"] } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("not supported", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/v1/portfolios/{Guid.NewGuid()}/holdings?currency=usd")).StatusCode);
    }

    [Fact]
    public async Task MixedUniverse_IsOptimizedInTheBaseCurrency_WithConvertedReturns()
    {
        var p = await CreatePortfolioAsync("EUR");
        var run = await OptimizeAsync(p.Id);
        Assert.True(run.Status == OptimizationRunStatus.Succeeded, run.Message + " | " + string.Join("; ", run.Diagnostics));
        Assert.Equal("EUR", run.Currency);
        Assert.Equal("MockFx", run.FxSource);
        Assert.Equal(Mixed.Order(), run.Symbols.Order());
        Assert.Contains(run.Diagnostics, d => d.Contains("converted to EUR") && d.Contains("GBP") && d.Contains("JPY") && d.Contains("USD"));
        Assert.Contains(run.Diagnostics, d => d.Contains("Benchmark SPY (USD) converted to EUR"));
        // Native currencies are kept per stock; the exposure adds up to the invested share.
        Assert.Equal("JPY", run.Allocation.Single(a => a.Symbol == "7203.T").Currency);
        Assert.Equal("GBP", run.Allocation.Single(a => a.Symbol == "AZN.L").Currency);
        Assert.Equal(run.Allocation.Sum(a => a.Weight), run.CurrencyExposure!.Sum(e => e.Weight), 9);
        Assert.All(run.Allocation, a => Assert.InRange(a.Weight, -1e-9, 0.35 + 1e-6));

        // The same stocks analysed in USD give different (USD-based) statistics – currency movements are part of the returns.
        var usd = await CreatePortfolioAsync("USD");
        var usdRun = await OptimizeAsync(usd.Id);
        Assert.Equal("USD", usdRun.Currency);
        Assert.NotEqual(run.Volatility!.Value, usdRun.Volatility!.Value, 6);
        var aaplEur = run.Analytics.AssetStatistics.Single(s => s.Symbol == "AAPL");
        var aaplUsd = usdRun.Analytics.AssetStatistics.Single(s => s.Symbol == "AAPL");
        Assert.NotEqual(aaplUsd.Volatility, aaplEur.Volatility, 6);
        var sapEur = run.Analytics.AssetStatistics.Single(s => s.Symbol == "SAP.DE");
        var sapUsd = usdRun.Analytics.AssetStatistics.Single(s => s.Symbol == "SAP.DE");
        Assert.NotEqual(sapEur.Volatility, sapUsd.Volatility, 6);
    }

    [Fact]
    public async Task SingleMarketAnalysis_StaysInItsCurrency_WithItsOwnBenchmark()
    {
        var p = await CreatePortfolioAsync("EUR", "SAP.DE", "SIE.DE");
        var run = await OptimizeAsync(p.Id);
        Assert.Equal("EUR", run.Currency);
        Assert.Null(run.FxSource);                                          // nothing to convert
        Assert.Equal("^GDAXI", run.Settings.BenchmarkSymbol);              // SPY replaced for a DAX-only selection
    }

    [Fact]
    public async Task MixedPortfolio_TradesThroughASimulatedExchange_AndReconciles()
    {
        var p = await CreatePortfolioAsync("EUR");
        var run = await OptimizeAsync(p.Id);
        var proposal = await (await _client.GetAsync($"/api/v1/optimization-runs/{run.Id}/trade-proposal")).ReadAsync<TradeProposalDto>();
        Assert.Contains(proposal.Notes, n => n.Contains("converted to EUR") && n.Contains("SIMULATED currency exchange"));
        var buys = proposal.Lines.Where(l => l.Action == ProposalAction.Buy).ToList();
        Assert.Contains(buys, l => l.Currency == "JPY");
        foreach (var l in buys)
        {
            Assert.NotNull(l.FxRate);
            if (l.Currency == "EUR") Assert.Equal(1m, l.FxRate);
            else Assert.NotEqual(1m, l.FxRate);
            // Estimated value in EUR = quantity × native price × rate.
            Assert.Equal(l.OrderQuantity * l.Price!.Value * l.FxRate!.Value, l.EstimatedTradeValue, 6);
        }
        Assert.True(proposal.EstimatedCashAfter >= 0);

        var result = await ExecuteAsync(p.Id, proposal.Id);
        Assert.Equal(buys.Count, result.Orders.Count(o => o.Status == OrderStatus.Filled));
        Assert.Contains(result.Orders, o => o.Currency == "GBP" && o.FxRate is > 0 && o.Message!.Contains("no real currency was exchanged"));

        var tx = await (await _client.GetAsync($"/api/v1/portfolios/{p.Id}/transactions?pageSize=100")).ReadAsync<PagedResult<TransactionDto>>();
        foreach (var t in tx.Items)
        {
            Assert.Equal(Math.Round(t.Quantity * t.Price * t.FxRate, 6), Math.Round(t.GrossAmount, 6));
            if (t.Currency == "EUR") Assert.Equal((1m, (string?)null), (t.FxRate, t.FxSource));
            else Assert.Equal("MockFx", t.FxSource);
        }
        // Cash reconciles in EUR: initial − Σ gross − Σ fees.
        var portfolio = await (await _client.GetAsync($"/api/v1/portfolios/{p.Id}")).ReadAsync<PortfolioDto>();
        Assert.Equal(100_000m - tx.Items.Sum(t => t.GrossAmount + t.Fees), portfolio.CashBalance, 6);
    }

    [Fact]
    public async Task ChangingTheReportingCurrency_RecalculatesValues_WithoutChangingHoldings()
    {
        var p = await CreatePortfolioAsync("EUR");
        var run = await OptimizeAsync(p.Id);
        var proposal = await (await _client.GetAsync($"/api/v1/optimization-runs/{run.Id}/trade-proposal")).ReadAsync<TradeProposalDto>();
        await ExecuteAsync(p.Id, proposal.Id);

        var eur = await (await _client.GetAsync($"/api/v1/portfolios/{p.Id}/holdings")).ReadAsync<HoldingsResponse>();
        Assert.Equal("EUR", eur.Summary.Currency);
        Assert.NotNull(eur.Summary.TotalValue);
        var held = eur.Holdings.Where(h => h.Quantity > 0).ToList();
        // Market value = quantity × native price × rate of the price date; the sum reconciles with the summary.
        foreach (var h in held)
            Assert.Equal(h.Quantity * h.LatestPrice!.Value * (h.FxRate ?? 1m), h.MarketValue!.Value, 6);
        Assert.Equal(held.Sum(h => h.MarketValue!.Value), eur.Summary.MarketValue!.Value, 6);
        Assert.Equal(eur.Summary.MarketValue + eur.Summary.CashBalance, eur.Summary.TotalValue);
        Assert.Contains(eur.Summary.CurrencyExposure!, e => e.Currency == "JPY");

        var usd = await (await _client.GetAsync($"/api/v1/portfolios/{p.Id}/holdings?currency=USD")).ReadAsync<HoldingsResponse>();
        Assert.Equal("USD", usd.Summary.Currency);
        Assert.Equal("EUR", usd.Summary.AccountCurrency);
        Assert.NotNull(usd.Summary.AccountToReportingRate);
        Assert.NotEqual(eur.Summary.TotalValue, usd.Summary.TotalValue);
        Assert.Contains(usd.Summary.Warnings, w => w.Contains("Nothing was exchanged"));
        foreach (var h in usd.Holdings.Where(h => h.Quantity > 0))
        {
            var e = held.Single(x => x.AssetId == h.AssetId);
            Assert.Equal(e.Quantity, h.Quantity);                           // quantities unchanged
            Assert.Equal(e.LatestPrice, h.LatestPrice);                     // native price unchanged
            Assert.Equal(e.Currency, h.Currency);
            if (h.Currency == "USD") Assert.Null(h.FxRate);
        }
        // Cash is converted at the reported account→reporting rate.
        Assert.Equal(eur.Summary.CashBalance * usd.Summary.AccountToReportingRate!.Value, usd.Summary.CashBalance, 6);

        // Nothing changed in the stored portfolio.
        var after = await (await _client.GetAsync($"/api/v1/portfolios/{p.Id}/holdings")).ReadAsync<HoldingsResponse>();
        Assert.Equal(eur.Summary.TotalValue, after.Summary.TotalValue);
        Assert.Equal(eur.Summary.CashBalance, after.Summary.CashBalance);
    }

    [Fact]
    public async Task Performance_UsesTheRateOfEachDay_AndMatchesTheHoldingsView()
    {
        var p = await CreatePortfolioAsync("EUR");
        var run = await OptimizeAsync(p.Id);
        var proposal = await (await _client.GetAsync($"/api/v1/optimization-runs/{run.Id}/trade-proposal")).ReadAsync<TradeProposalDto>();
        await ExecuteAsync(p.Id, proposal.Id);
        var perf = await (await _client.GetAsync($"/api/v1/portfolios/{p.Id}/performance")).ReadAsync<PerformanceDto>();
        Assert.Equal("EUR", perf.Currency);
        var holdings = await (await _client.GetAsync($"/api/v1/portfolios/{p.Id}/holdings")).ReadAsync<HoldingsResponse>();
        // The last daily value (closes × that day's rate) equals today's holdings valuation when the price date is the last day.
        Assert.Equal((double)holdings.Summary.TotalValue!.Value, perf.Series[^1].Value, 2);
        var usdPerf = await (await _client.GetAsync($"/api/v1/portfolios/{p.Id}/performance?currency=USD")).ReadAsync<PerformanceDto>();
        Assert.Equal("USD", usdPerf.Currency);
        Assert.NotEqual(perf.Series[^1].Value, usdPerf.Series[^1].Value);
    }

    [Fact]
    public async Task HistoricalConversion_UsesStoredDatedRates_NotTodaysRate()
    {
        var p = await CreatePortfolioAsync("EUR", "AAPL", "MSFT");
        var run = await OptimizeAsync(p.Id);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var usd = await db.FxRates.AsNoTracking().Where(r => r.Source == "MockFx" && r.Currency == "USD" && r.Date >= run.PeriodStart && r.Date <= run.PeriodEnd)
            .OrderBy(r => r.Date).ToListAsync();
        Assert.True(usd.Count > 400);
        Assert.True(usd.Select(r => r.UnitsPerEur).Distinct().Count() > 100);  // dated, not one constant rate
        Assert.All(usd, r => Assert.True(r.IsSimulated));
        // The AAPL return statistic in EUR equals the one recomputed from native closes × dated rates.
        var prices = await db.HistoricalPrices.AsNoTracking().Where(x => x.Asset!.Symbol == "AAPL" && x.Source == "Mock" && x.Date >= run.PeriodStart && x.Date <= run.PeriodEnd)
            .OrderBy(x => x.Date).Select(x => new { x.Date, x.AdjustedClose }).ToListAsync();
        var rates = usd.ToDictionary(r => r.Date, r => (double)r.UnitsPerEur);
        var eurPrices = prices.Select(x => (double)x.AdjustedClose!.Value / rates[x.Date]).ToList();     // EUR per USD = 1 / (USD per EUR)
        var native = prices.Select(x => (double)x.AdjustedClose!.Value).ToList();
        var eurTotal = eurPrices[^1] / eurPrices[0] - 1;
        var usdTotal = native[^1] / native[0] - 1;
        var fxTotal = rates[prices[0].Date] / rates[prices[^1].Date] - 1;
        Assert.Equal((1 + usdTotal) * (1 + fxTotal) - 1, eurTotal, 9);
    }

    [Fact]
    public async Task Backtests_AndMlExperiments_RecordTheirCurrency()
    {
        var bt = await (await _client.PostJsonAsync("/api/v1/backtests", new CreateBacktestRequest
        {
            Universe = [.. Mixed], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1), EndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
            Objective = ObjectiveKind.MinimumVariance, Currency = "GBP", MaxWeight = 0.5,
        })).ReadAsync<BacktestDto>();
        Assert.Equal("GBP", bt.Config.Currency);
        var ml = await (await _client.PostJsonAsync("/api/v1/ml/experiments", new CreateMlExperimentRequest
        {
            Universe = ["AAPL", "MSFT", "SAP.DE", "SIE.DE", "7203.T", "AZN.L"], BenchmarkSymbol = "SPY", Currency = "EUR",
        })).ReadAsync<MlExperimentDto>();
        Assert.Equal("EUR", ml.Currency);
    }
}
