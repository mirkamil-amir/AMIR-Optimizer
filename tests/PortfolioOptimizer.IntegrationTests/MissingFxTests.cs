using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Fx;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Exchange rates that stop: the simulated source publishes no JPY rate for the last 60 days. Every layer must say so
/// explicitly – no conversion with a rate of 1 or an old rate beyond the carry-forward window, no plausible total.
/// </summary>
public sealed class MissingFxTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    /// <summary>The simulated rates, except JPY ends 60 days ago.</summary>
    private sealed class StaleJpyProvider(TimeProvider clock) : IFxRateProvider
    {
        private readonly MockFxProvider _inner = new(clock);
        public string Name => _inner.Name;
        public bool IsSimulated => true;
        public string Description => "test: JPY rates end 60 days ago";
        public IReadOnlySet<string> SupportedCurrencies => _inner.SupportedCurrencies;
        public async Task<IReadOnlyList<FxObservation>> GetRatesAsync(IReadOnlyCollection<string> currencies, DateOnly from, DateOnly to, CancellationToken ct)
        {
            var cutoff = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-60);
            return (await _inner.GetRatesAsync(currencies, from, to, ct)).Where(o => o.Currency != "JPY" || o.Date <= cutoff).ToList();
        }
    }

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync(testServices: s =>
        {
            s.RemoveAll<IFxRateProvider>();
            s.AddSingleton<IFxRateProvider>(sp => new StaleJpyProvider(sp.GetRequiredService<TimeProvider>()));
        });
        _client = await _factory.CreateAdminSessionAsync();
        await _client.SyncAndWaitAsync(new SyncRequest { Symbols = ["AAPL", "SAP.DE", "SIE.DE", "7203.T", "SPY"], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3) });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<PortfolioDto> CreatePortfolioAsync() =>
        await (await _client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
        {
            Name = "Stale JPY",
            BaseCurrency = "EUR",
            Settings = new PortfolioSettingsDto { Universe = ["AAPL", "SAP.DE", "SIE.DE", "7203.T"], MaxWeight = 0.6, LookbackDays = 504, Objective = ObjectiveKind.MinimumVariance },
        })).ReadAsync<PortfolioDto>();

    [Fact]
    public async Task Optimizer_ExcludesTheListing_WithTheReason()
    {
        var p = await CreatePortfolioAsync();
        var run = await (await _client.PostJsonAsync($"/api/v1/portfolios/{p.Id}/optimization-runs", new RunOptimizationRequest())).ReadAsync<OptimizationRunDto>();
        Assert.Equal(OptimizationRunStatus.Succeeded, run.Status);
        Assert.DoesNotContain("7203.T", run.Symbols);
        Assert.Contains(run.Diagnostics, d => d.StartsWith("7203.T: excluded") && d.Contains("JPY→EUR"));
        Assert.Contains("7203.T", run.Analytics.ExcludedForData);
    }

    [Fact]
    public async Task Holdings_WithoutAUsableRate_ShowNoTotal_AndSayWhy()
    {
        var p = await CreatePortfolioAsync();
        // A position bought while rates were available (booked directly: today's purchase would correctly be refused).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var portfolio = await db.Portfolios.Include(x => x.Holdings).SingleAsync(x => x.Id == p.Id);
            var toyota = await db.Assets.SingleAsync(a => a.Symbol == "7203.T");
            var aapl = await db.Assets.SingleAsync(a => a.Symbol == "AAPL");
            var then = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90);
            portfolio.Buy(toyota, 100, 3000m, then, new TradeFx(0.0062m, then, "MockFx"), 1m, Guid.NewGuid(), DateTime.UtcNow);
            portfolio.Buy(aapl, 10, 200m, then, new TradeFx(0.91m, then, "MockFx"), 1m, Guid.NewGuid(), DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var h = await (await _client.GetAsync($"/api/v1/portfolios/{p.Id}/holdings")).ReadAsync<HoldingsResponse>();
        Assert.Null(h.Summary.MarketValue);
        Assert.Null(h.Summary.TotalValue);
        Assert.Contains(h.Summary.ValuationProblems!, x => x.Contains("7203.T (JPY) cannot be valued in EUR"));
        var toyotaRow = h.Holdings.Single(x => x.Symbol == "7203.T");
        Assert.Null(toyotaRow.MarketValue);
        Assert.NotNull(toyotaRow.LatestPrice);                    // the price itself is known – only the conversion is missing
        Assert.Contains("JPY→EUR", toyotaRow.ValuationProblem);
        Assert.NotNull(h.Holdings.Single(x => x.Symbol == "AAPL").MarketValue);   // other positions are still valued

        var list = await (await _client.GetAsync("/api/v1/portfolios")).ReadAsync<List<PortfolioSummaryDto>>();
        Assert.Null(list.Single(x => x.Id == p.Id).TotalValue);

        // A rebalance would sell or buy at an unknown rate: the position is left alone with an explicit note.
        var proposal = await (await _client.PostJsonAsync($"/api/v1/portfolios/{p.Id}/rebalance-preview",
            new RebalancePreviewRequest { TargetWeights = new Dictionary<string, double> { ["AAPL"] = 0.5, ["7203.T"] = 0.2 } })).ReadAsync<TradeProposalDto>();
        var line = proposal.Lines.Single(l => l.Symbol == "7203.T");
        Assert.Equal(ProposalAction.NoPrice, line.Action);
        Assert.Contains("No exchange rate", line.Note);
        Assert.Null(line.FxRate);
    }

    [Fact]
    public async Task ForeignTradeWithoutARate_IsRejectedByTheDomain()
    {
        var portfolio = new Portfolio { Id = Guid.NewGuid(), Name = "x", BaseCurrency = "EUR" };
        portfolio.Deposit(10_000, DateTime.UtcNow, "test");
        var toyota = new Asset { Id = 1, Symbol = "7203.T", Currency = "JPY" };
        var ex = Assert.Throws<DomainException>(() => portfolio.Buy(toyota, 1, 3000m, DateOnly.FromDateTime(DateTime.UtcNow), 0, Guid.NewGuid(), DateTime.UtcNow));
        Assert.Contains("exchange rate with its source is required", ex.Message);
        var eur = new Asset { Id = 2, Symbol = "SAP.DE", Currency = "EUR" };
        Assert.Throws<DomainException>(() => portfolio.Buy(eur, 1, 200m, DateOnly.FromDateTime(DateTime.UtcNow), new TradeFx(1.1m, null, "ECB"), 0, Guid.NewGuid(), DateTime.UtcNow));
    }
}
