using System.Net;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>Portfolio Reality Check of a saved run: consistency with the run, sensitivity scenarios, ownership.</summary>
public sealed class RealityCheckTests : IAsyncLifetime
{
    private static readonly string[] Universe = ["AAPL", "MSFT", "NVDA", "JPM", "XOM", "JNJ"];
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        await _admin.SyncAndWaitAsync(new SyncRequest { Symbols = [.. Universe, "SPY"], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-2) });
        (_user, _) = await _factory.CreateApprovedUserAsync(_admin);
    }

    public async Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<OptimizationRunDto> RunAsync(HttpClient client, ObjectiveKind objective)
    {
        var portfolio = await (await client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
        {
            Name = "Reality", InitialInvestment = 100_000m, BaseCurrency = "USD",
            Settings = new PortfolioSettingsDto { Universe = [.. Universe], MaxWeight = 0.5, LookbackDays = 400, Objective = objective },
        })).ReadAsync<PortfolioDto>();
        var run = await (await client.PostJsonAsync($"/api/v1/portfolios/{portfolio.Id}/optimization-runs", new RunOptimizationRequest())).ReadAsync<OptimizationRunDto>();
        Assert.True(run.Status == OptimizationRunStatus.Succeeded, run.Message);
        return run;
    }

    [Fact]
    public async Task Report_IsConsistentWithTheRun_AndLabelsEveryFinding()
    {
        var run = await RunAsync(_user, ObjectiveKind.MinimumVariance);
        var r = await (await _user.GetAsync($"/api/v1/optimization-runs/{run.Id}/reality-check")).ReadAsync<RealityCheckDto>();

        Assert.Equal(run.Id, r.RunId);
        Assert.True(r.UsesSimulatedData);
        Assert.False(r.DataChangedSinceRun);
        Assert.Equal(run.Observations, r.Observations);
        // Ex-ante volatility recomputed from the same window equals the run's stored volatility.
        Assert.Equal(run.Volatility!.Value, r.Risk.Volatility, 6);
        Assert.Equal(1, r.Risk.Contributions.Sum(c => c.RiskShare), 6);
        Assert.InRange(r.Concentration.EffectiveHoldings, 1, Universe.Length);
        Assert.Equal(1, r.Concentration.Sectors.Sum(s => s.Weight), 6);
        Assert.True(r.Risk.DiversificationRatio >= 1 - 1e-9);

        Assert.StartsWith("In-sample", r.InSample.Label);
        Assert.Equal("SPY", r.InSample.BenchmarkSymbol);
        Assert.NotNull(r.InSample.ExpectedShortfall95.Value);
        Assert.True(r.InSample.ExpectedShortfall95.Value >= r.InSample.ValueAtRisk95.Value);
        Assert.Null(r.OutOfSample); // the window ends at the latest stored price
        Assert.NotNull(r.OutOfSampleUnavailableReason);

        var resolved = Assert.Single(r.Scenarios, s => s.Key == "resolved");
        Assert.True(resolved.Turnover < 0.02, $"re-solve turnover {resolved.Turnover}");
        // Minimum variance ignores expected returns: shrinking them changes nothing.
        Assert.True(Assert.Single(r.Scenarios, s => s.Key == "returns").Turnover < 0.02);
        Assert.Contains(r.Scenarios, s => s.Key == "covariance" && s.Status == "Optimal");
        Assert.Contains(r.Scenarios, s => s.Key == "equal" && s.Holdings == Universe.Length);
        Assert.All(r.Scenarios.Where(s => s.Status == "Optimal"), s => Assert.Equal(0.99, s.Weights.Sum(w => w.Weight), 4));

        Assert.All(r.Findings, f => Assert.Contains(f.Kind, new[] { "Fact", "Estimate", "Interpretation", "Caution" }));
        Assert.Contains(r.Findings, f => f.Kind == "Caution" && f.Text.Contains("SIMULATED"));
        Assert.Contains(r.Findings, f => f.Kind == "Caution" && f.Text.Contains("flatter"));
        // Findings describe; they never tell the user to trade.
        Assert.DoesNotContain(r.Findings, f => System.Text.RegularExpressions.Regex.IsMatch(f.Text, @"\b(buy|sell|you should)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains("not a recommendation", r.Disclaimer);
        Assert.NotEmpty(r.NextSteps);
    }

    [Fact]
    public async Task MaximumSharpe_IsMoreSensitiveToExpectedReturns_ThanMinimumVariance()
    {
        var minVar = await (await _user.GetAsync($"/api/v1/optimization-runs/{(await RunAsync(_user, ObjectiveKind.MinimumVariance)).Id}/reality-check")).ReadAsync<RealityCheckDto>();
        var sharpeRun = await RunAsync(_user, ObjectiveKind.MaximumSharpe);
        var sharpe = await (await _user.GetAsync($"/api/v1/optimization-runs/{sharpeRun.Id}/reality-check")).ReadAsync<RealityCheckDto>();
        var mvReturns = minVar.Scenarios.Single(s => s.Key == "returns").Turnover ?? 0;
        var msReturns = sharpe.Scenarios.Single(s => s.Key == "returns").Turnover ?? 0;
        Assert.True(msReturns >= mvReturns, $"max-Sharpe {msReturns} vs min-variance {mvReturns}");
    }

    [Fact]
    public async Task OtherUsersRuns_Are404_AndAnonymousIs401()
    {
        var run = await RunAsync(_user, ObjectiveKind.MinimumVariance);
        var (other, _) = await _factory.CreateApprovedUserAsync(_admin);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/optimization-runs/{run.Id}/reality-check")).StatusCode);
        using var anonymous = _factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/optimization-runs/{run.Id}/reality-check")).StatusCode);
        other.Dispose();
    }
}
