using System.Net;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.MachineLearning.Models;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Comparing and backtesting the optimizations of one portfolio on the shared evaluation engine: mathematical vs
/// mathematical, ML vs ML, mathematical vs ML; only within the same portfolio of the same user.
/// </summary>
public sealed class StrategyComparisonTests : IAsyncLifetime
{
    private static readonly string[] Universe = ["AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "META", "JPM", "JNJ", "XOM", "PG"];
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;
    private PortfolioDto _portfolio = null!;
    private OptimizationRunDto _run = null!;
    private OptimizationRunDto _run2 = null!;
    private MlExperimentDto _experiment = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        await _admin.SyncAndWaitAsync(new SyncRequest { StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-7) });
        (_user, _) = await _factory.CreateApprovedUserAsync(_admin);

        _portfolio = await CreatePortfolioAsync(_user, "Comparison", "USD");
        _run = await RunAsync(_portfolio.Id, ObjectiveKind.MinimumVariance);
        _run2 = await RunAsync(_portfolio.Id, ObjectiveKind.MaximumSharpe);
        _experiment = await ExperimentAsync(_portfolio.Id, ModelFamily.RidgeRegression, 6);
    }

    public async Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private static async Task<PortfolioDto> CreatePortfolioAsync(HttpClient client, string name, string currency) =>
        await (await client.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
        {
            Name = name,
            BaseCurrency = currency,
            Settings = new PortfolioSettingsDto { Universe = [.. Universe], MaxWeight = 0.3, LookbackDays = 252, Objective = ObjectiveKind.MinimumVariance },
        })).ReadAsync<PortfolioDto>();

    private async Task<OptimizationRunDto> RunAsync(Guid portfolioId, ObjectiveKind objective)
    {
        var run = await (await _user.PostJsonAsync($"/api/v1/portfolios/{portfolioId}/optimization-runs", new RunOptimizationRequest
        {
            Settings = new PortfolioSettingsDto { Universe = [.. Universe], MaxWeight = 0.3, LookbackDays = 252, Objective = objective },
            PersistSettings = false,
        })).ReadAsync<OptimizationRunDto>();
        Assert.Equal(OptimizationRunStatus.Succeeded, run.Status);
        return run;
    }

    private async Task<MlExperimentDto> ExperimentAsync(Guid? portfolioId, ModelFamily family, int years)
    {
        var created = await (await _user.PostJsonAsync("/api/v1/ml/experiments", new CreateMlExperimentRequest
        {
            Universe = [.. Universe], ModelFamily = family, HistoryYears = years, TopK = 4, MaxWeight = 0.3, PortfolioId = portfolioId,
        })).ReadAsync<MlExperimentDto>();
        var done = await WaitAsync<MlExperimentDto>(_user, $"/api/v1/ml/experiments/{created.Summary.Id}", e => e.Summary.Status);
        Assert.True(done.Summary.Status == JobStatus.Succeeded, done.Summary.Error);
        return done;
    }

    private static async Task<T> WaitAsync<T>(HttpClient client, string url, Func<T, JobStatus> status)
    {
        for (var i = 0; i < 600; i++)
        {
            var value = await (await client.GetAsync(url)).ReadAsync<T>();
            if (status(value) is not (JobStatus.Queued or JobStatus.Running)) return value;
            await Task.Delay(200);
        }
        throw new TimeoutException(url);
    }

    private static StrategyRef Math(OptimizationRunDto run) => new() { Kind = OptimizationKind.Mathematical, Id = run.Id };
    private static StrategyRef Ml(MlExperimentDto e) => new() { Kind = OptimizationKind.MachineLearning, Id = e.Summary.Id };

    private CreateStrategyComparisonRequest Request(StrategyRef a, StrategyRef b) => new() { PortfolioId = _portfolio.Id, StrategyA = a, StrategyB = b };

    private async Task<StrategyComparisonDto> CompareAsync(CreateStrategyComparisonRequest request)
    {
        var response = await _user.PostJsonAsync("/api/v1/strategy-comparisons", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.ReadAsync<StrategyComparisonDto>();
        var done = await WaitAsync<StrategyComparisonDto>(_user, $"/api/v1/strategy-comparisons/{queued.Summary.Id}", c => c.Summary.Status);
        Assert.True(done.Summary.Status == JobStatus.Succeeded, done.Summary.Error);
        return done;
    }

    [Fact]
    public async Task MathematicalVsMl_UsesSharedConventions_AndRanksFairly()
    {
        var c = await CompareAsync(Request(Math(_run), Ml(_experiment)) with { IncludeHybrid = true });
        var r = c.Result!;
        // Defaults to the ML experiment's untouched test period.
        Assert.Equal(_experiment.Results!.TestPeriod.Start, c.Summary.EvaluationStart);
        Assert.Equal(_portfolio.Id, c.Summary.PortfolioId);
        Assert.Equal(["a", "b", "hybrid", "equal", "benchmark"], r.Strategies.Select(s => s.Key));
        Assert.Equal([StrategyKind.Mathematical, StrategyKind.MachineLearning], r.Strategies.Take(2).Select(s => s.Kind));
        Assert.StartsWith("A: ", r.Strategies[0].Name);
        Assert.All(r.Strategies, s => Assert.Equal(r.Dates.Count, s.Values.Count));
        Assert.All(r.Strategies, s => Assert.Equal(100_000, s.Values[0], 6));

        // Identical rebalance dates for every rebalancing strategy (shared simulator).
        var signalDates = r.Strategies.Where(s => s.Kind != StrategyKind.Benchmark).Select(s => string.Join(",", s.Rebalances.Select(x => x.SignalDate))).Distinct().ToList();
        Assert.Single(signalDates);
        Assert.All(r.Strategies[0].Rebalances, x => Assert.True(x.ExecutionDate > x.SignalDate));

        Assert.True(r.Fairness.IsFair, string.Join(" | ", r.Fairness.Issues));
        Assert.NotNull(r.RankingBySharpe);
        Assert.Equal(c.Summary.IsFair, true);
        var overlap = Assert.Single(r.Overlaps);
        Assert.InRange(overlap.JaccardSimilarity, 0, 1);
        Assert.All(r.Strategies.Take(2), s => Assert.True(s.Performance.Rebalances > 0 && s.Performance.TotalTransactionCosts > 0));
        Assert.Contains(r.Strategies, s => s.AverageSectorExposure.Count > 0);
        var strategies = c.Configuration.Strategies!;
        Assert.Equal(_run.Settings.MaxWeight, strategies[0].MathSettings!.MaxWeight);
        Assert.Equal(_experiment.Results!.SelectedHyperparameters, strategies[1].MlHyperparameters);
    }

    [Fact]
    public async Task MathematicalVsMathematical_AndMlVsMl_AreComparable()
    {
        var math = await CompareAsync(Request(Math(_run), Math(_run2)) with { IncludeHybrid = true });
        // Two mean-variance strategies: no hybrid (it needs one ML and one mathematical strategy); three years by default.
        Assert.Equal(["a", "b", "equal", "benchmark"], math.Result!.Strategies.Select(s => s.Key));
        Assert.All(math.Result.Strategies.Take(2), s => Assert.Equal(StrategyKind.Mathematical, s.Kind));
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3), math.Summary.EvaluationStart);
        Assert.Single(math.Result.Overlaps);

        var second = await ExperimentAsync(_portfolio.Id, ModelFamily.RandomForest, 6);
        var ml = await CompareAsync(Request(Ml(_experiment), Ml(second)));
        Assert.Equal(["a", "b", "equal", "benchmark"], ml.Result!.Strategies.Select(s => s.Key));
        Assert.All(ml.Result.Strategies.Take(2), s => Assert.Equal(StrategyKind.MachineLearning, s.Kind));
        Assert.Equal(2, ml.Summary.StrategyLabels!.Count);
    }

    [Fact]
    public async Task EvaluationOverlappingMlValidation_IsFlagged_AndNotRanked()
    {
        var start = _experiment.Results!.ValidationPeriod.Start;
        var c = await CompareAsync(Request(Math(_run), Ml(_experiment)) with { StartDate = start });
        Assert.False(c.Result!.Fairness.IsFair);
        Assert.Contains(c.Result.Fairness.Issues, i => i.Contains("partly in-sample"));
        Assert.Null(c.Result.RankingBySharpe);
    }

    [Fact]
    public async Task OnlyOptimizationsOfTheSamePortfolio_OfTheSameUser_CanBeCompared()
    {
        // An experiment of another portfolio, and one without a portfolio, are not part of this portfolio.
        var other = await CreatePortfolioAsync(_user, "Other", "USD");
        var foreign = await ExperimentAsync(other.Id, ModelFamily.RidgeRegression, 5);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.PostJsonAsync("/api/v1/strategy-comparisons", Request(Math(_run), Ml(foreign)))).StatusCode);
        var unlinked = await ExperimentAsync(null, ModelFamily.RidgeRegression, 5);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.PostJsonAsync("/api/v1/strategy-comparisons", Request(Math(_run), Ml(unlinked)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.PostJsonAsync("/api/v1/strategy-comparisons",
            Request(new StrategyRef { Kind = OptimizationKind.Mathematical, Id = Guid.NewGuid() }, Ml(_experiment)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.PostJsonAsync("/api/v1/strategy-comparisons", Request(Math(_run), Math(_run)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.PostJsonAsync("/api/v1/strategy-comparisons",
            Request(Math(_run), Ml(_experiment)) with { Rebalance = RebalanceKind.None })).StatusCode);

        // The portfolio's strategy list holds its runs and linked experiments only.
        var listed = await (await _user.GetAsync($"/api/v1/portfolios/{_portfolio.Id}/strategies")).ReadAsync<List<PortfolioStrategyDto>>();
        Assert.Contains(listed, s => s.Kind == OptimizationKind.Mathematical && s.Id == _run.Id);
        Assert.Contains(listed, s => s.Kind == OptimizationKind.MachineLearning && s.Id == _experiment.Summary.Id);
        Assert.DoesNotContain(listed, s => s.Id == foreign.Summary.Id || s.Id == unlinked.Summary.Id);

        // Another user sees none of it.
        var (stranger, _) = await _factory.CreateApprovedUserAsync(_admin);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/portfolios/{_portfolio.Id}/strategies")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostJsonAsync("/api/v1/strategy-comparisons", Request(Math(_run), Ml(_experiment)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutJsonAsync($"/api/v1/ml/experiments/{unlinked.Summary.Id}/portfolio",
            new AssignMlExperimentPortfolioRequest(_portfolio.Id))).StatusCode);
    }

    [Fact]
    public async Task AnUnlinkedExperiment_CanBeAssigned_ToAPortfolioOfItsCurrency()
    {
        var unlinked = await ExperimentAsync(null, ModelFamily.RidgeRegression, 5);
        Assert.Null(unlinked.Summary.PortfolioId);
        var withoutPortfolio = await (await _user.GetAsync("/api/v1/ml/experiments?unlinked=true")).ReadAsync<PagedResult<MlExperimentSummaryDto>>();
        Assert.Equal([unlinked.Summary.Id], withoutPortfolio.Items.Select(e => e.Id));
        var eur = await CreatePortfolioAsync(_user, "Euro", "EUR");
        var mismatch = await _user.PutJsonAsync($"/api/v1/ml/experiments/{unlinked.Summary.Id}/portfolio", new AssignMlExperimentPortfolioRequest(eur.Id));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        var assigned = await (await _user.PutJsonAsync($"/api/v1/ml/experiments/{unlinked.Summary.Id}/portfolio",
            new AssignMlExperimentPortfolioRequest(_portfolio.Id))).ReadAsync<MlExperimentDto>();
        Assert.Equal(_portfolio.Id, assigned.Summary.PortfolioId);
        var mine = await (await _user.GetAsync($"/api/v1/ml/experiments?portfolioId={_portfolio.Id}")).ReadAsync<PagedResult<MlExperimentSummaryDto>>();
        Assert.Contains(mine.Items, e => e.Id == unlinked.Summary.Id);
        await CompareAsync(Request(Ml(_experiment), Ml(assigned)));
    }

    [Fact]
    public async Task EveryOptimization_CanBeBacktested_OverAChosenPeriod()
    {
        var end = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = end.AddYears(-2);
        foreach (var strategy in new[] { Math(_run), Ml(_experiment) })
        {
            var response = await _user.PostJsonAsync("/api/v1/backtests/from-strategy", new CreateStrategyBacktestRequest
            {
                PortfolioId = _portfolio.Id, Strategy = strategy, StartDate = start, EndDate = end, TransactionCostBps = 10,
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var queued = await response.ReadAsync<BacktestDto>();
            var done = await WaitAsync<BacktestDto>(_user, $"/api/v1/backtests/{queued.Id}", b => b.Status);
            Assert.True(done.Status == JobStatus.Succeeded, done.Error);
            var r = done.Result!;
            Assert.Equal(strategy.Kind, done.Config.Strategy!.Kind);
            Assert.Equal(strategy.Id, done.Config.Strategy.SourceId);
            Assert.Equal(_portfolio.Id, done.Config.PortfolioId);
            Assert.True(r.Dates[0] >= start && r.Dates[^1] <= end);
            Assert.Equal(100_000, r.Values["Strategy"][0], 6);
            Assert.True(r.Rebalances.Count > 3 && r.Rebalances.Any(x => x.Weights.Count > 0));
            Assert.All(r.Rebalances, x => Assert.True(x.ExecutionDate > x.SignalDate));
            if (strategy.Kind == OptimizationKind.MachineLearning)
            {
                // The period starts before the experiment's test period: said so, never presented as out-of-sample.
                Assert.Contains(r.Warnings, w => w.StartsWith("Partly in-sample"));
                Assert.Contains("retrained", r.Methodology);
            }
        }
        var list = await (await _user.GetAsync($"/api/v1/backtests?portfolioId={_portfolio.Id}")).ReadAsync<PagedResult<BacktestSummaryDto>>();
        Assert.Equal([OptimizationKind.MachineLearning, OptimizationKind.Mathematical], list.Items.Take(2).Select(b => b.StrategyKind!.Value));

        var (stranger, _) = await _factory.CreateApprovedUserAsync(_admin);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostJsonAsync("/api/v1/backtests/from-strategy", new CreateStrategyBacktestRequest
        {
            PortfolioId = _portfolio.Id, Strategy = Math(_run), StartDate = start, EndDate = end,
        })).StatusCode);
    }

    [Fact]
    public async Task SavedComparisons_AreImmutable_Private_AndSurviveSourceDeletion()
    {
        var other = await ExperimentAsync(_portfolio.Id, ModelFamily.RidgeRegression, 5);
        var c = await CompareAsync(Request(Math(_run), Ml(other)));

        var (stranger, _) = await _factory.CreateApprovedUserAsync(_admin);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/strategy-comparisons/{c.Summary.Id}")).StatusCode);

        // Deleting the source experiment keeps the saved comparison with its frozen configuration and results.
        Assert.Equal(HttpStatusCode.NoContent, (await _user.DeleteAsync($"/api/v1/ml/experiments/{other.Summary.Id}")).StatusCode);
        var after = await (await _user.GetAsync($"/api/v1/strategy-comparisons/{c.Summary.Id}")).ReadAsync<StrategyComparisonDto>();
        Assert.Null(after.Summary.MlExperimentId);
        Assert.Equal(c.Result!.Strategies.Select(s => s.Values[^1]), after.Result!.Strategies.Select(s => s.Values[^1]));
        Assert.Equal(c.Configuration.Strategies![1].MlHyperparameters, after.Configuration.Strategies![1].MlHyperparameters);

        var list = await (await _user.GetAsync($"/api/v1/strategy-comparisons?portfolioId={_portfolio.Id}")).ReadAsync<PagedResult<StrategyComparisonSummaryDto>>();
        Assert.Contains(list.Items, x => x.Id == c.Summary.Id);
    }
}
