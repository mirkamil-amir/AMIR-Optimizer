using System.Net;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.MachineLearning.Models;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>Mathematical vs ML strategy comparison on the shared evaluation engine.</summary>
public sealed class StrategyComparisonTests : IAsyncLifetime
{
    private static readonly string[] Universe = ["AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "META", "JPM", "JNJ", "XOM", "PG"];
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;
    private OptimizationRunDto _run = null!;
    private MlExperimentDto _experiment = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        await _admin.SyncAndWaitAsync(new SyncRequest { StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-7) });
        (_user, _) = await _factory.CreateApprovedUserAsync(_admin);

        var portfolio = await (await _user.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
        {
            Name = "Comparison",
            Settings = new PortfolioSettingsDto { Universe = [.. Universe], MaxWeight = 0.3, LookbackDays = 252, Objective = ObjectiveKind.MinimumVariance },
        })).ReadAsync<PortfolioDto>();
        _run = await (await _user.PostJsonAsync($"/api/v1/portfolios/{portfolio.Id}/optimization-runs", new RunOptimizationRequest())).ReadAsync<OptimizationRunDto>();
        Assert.Equal(OptimizationRunStatus.Succeeded, _run.Status);

        var created = await (await _user.PostJsonAsync("/api/v1/ml/experiments", new CreateMlExperimentRequest
        {
            Universe = [.. Universe], ModelFamily = ModelFamily.RidgeRegression, HistoryYears = 6, TopK = 4, MaxWeight = 0.3,
        })).ReadAsync<MlExperimentDto>();
        _experiment = await WaitAsync<MlExperimentDto>($"/api/v1/ml/experiments/{created.Summary.Id}", e => e.Summary.Status);
        Assert.True(_experiment.Summary.Status == JobStatus.Succeeded, _experiment.Summary.Error);
    }

    public async Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private async Task<T> WaitAsync<T>(string url, Func<T, JobStatus> status)
    {
        for (var i = 0; i < 600; i++)
        {
            var value = await (await _user.GetAsync(url)).ReadAsync<T>();
            if (status(value) is not (JobStatus.Queued or JobStatus.Running)) return value;
            await Task.Delay(200);
        }
        throw new TimeoutException(url);
    }

    private async Task<StrategyComparisonDto> CompareAsync(CreateStrategyComparisonRequest request)
    {
        var response = await _user.PostJsonAsync("/api/v1/strategy-comparisons", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.ReadAsync<StrategyComparisonDto>();
        var done = await WaitAsync<StrategyComparisonDto>($"/api/v1/strategy-comparisons/{queued.Summary.Id}", c => c.Summary.Status);
        Assert.True(done.Summary.Status == JobStatus.Succeeded, done.Summary.Error);
        return done;
    }

    [Fact]
    public async Task OutOfSampleComparison_UsesSharedConventions_AndRanksFairly()
    {
        var c = await CompareAsync(new CreateStrategyComparisonRequest { OptimizationRunId = _run.Id, MlExperimentId = _experiment.Summary.Id, IncludeHybrid = true });
        var r = c.Result!;
        // Defaults to the ML experiment's untouched test period.
        Assert.Equal(_experiment.Results!.TestPeriod.Start, c.Summary.EvaluationStart);
        Assert.Equal(["math", "ml", "hybrid", "equal", "benchmark"], r.Strategies.Select(s => s.Key));
        Assert.All(r.Strategies, s => Assert.Equal(r.Dates.Count, s.Values.Count));
        Assert.All(r.Strategies, s => Assert.Equal(100_000, s.Values[0], 6));
        Assert.Equal(StrategyKind.Hybrid, r.Strategies.Single(s => s.Key == "hybrid").Kind);

        // Identical rebalance dates for every rebalancing strategy (shared simulator).
        var signalDates = r.Strategies.Where(s => s.Kind != StrategyKind.Benchmark).Select(s => string.Join(",", s.Rebalances.Select(x => x.SignalDate))).Distinct().ToList();
        Assert.Single(signalDates);
        Assert.All(r.Strategies.Single(s => s.Key == "math").Rebalances, x => Assert.True(x.ExecutionDate > x.SignalDate));

        Assert.True(r.Fairness.IsFair, string.Join(" | ", r.Fairness.Issues));
        Assert.NotNull(r.RankingBySharpe);
        Assert.NotEmpty(r.Fairness.SharedConventions);
        Assert.Equal(c.Summary.IsFair, true);
        var overlap = Assert.Single(r.Overlaps);
        Assert.InRange(overlap.JaccardSimilarity, 0, 1);
        Assert.All(r.Strategies.Where(s => s.Kind is StrategyKind.Mathematical or StrategyKind.MachineLearning),
            s => Assert.True(s.Performance.Rebalances > 0 && s.Performance.TotalTransactionCosts > 0));
        Assert.Contains(r.Strategies, s => s.AverageSectorExposure.Count > 0);
        Assert.Equal(_run.Settings.MaxWeight, c.Configuration.OptimizationSettings.MaxWeight);
        Assert.Equal(_experiment.Results!.SelectedHyperparameters, c.Configuration.MlHyperparameters);
    }

    [Fact]
    public async Task EvaluationOverlappingMlValidation_IsFlagged_AndNotRanked()
    {
        var start = _experiment.Results!.ValidationPeriod.Start;
        var c = await CompareAsync(new CreateStrategyComparisonRequest { OptimizationRunId = _run.Id, MlExperimentId = _experiment.Summary.Id, StartDate = start });
        Assert.False(c.Result!.Fairness.IsFair);
        Assert.Contains(c.Result.Fairness.Issues, i => i.Contains("partly in-sample"));
        Assert.Null(c.Result.RankingBySharpe);
    }

    [Fact]
    public async Task SavedComparisons_AreImmutable_Private_AndSurviveSourceDeletion()
    {
        var other = await (await _user.PostJsonAsync("/api/v1/ml/experiments", new CreateMlExperimentRequest
        {
            Universe = [.. Universe], ModelFamily = ModelFamily.RidgeRegression, HistoryYears = 5, TopK = 4, MaxWeight = 0.3,
        })).ReadAsync<MlExperimentDto>();
        var done = await WaitAsync<MlExperimentDto>($"/api/v1/ml/experiments/{other.Summary.Id}", e => e.Summary.Status);
        var c = await CompareAsync(new CreateStrategyComparisonRequest { OptimizationRunId = _run.Id, MlExperimentId = done.Summary.Id });

        var (stranger, _) = await _factory.CreateApprovedUserAsync(_admin);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/strategy-comparisons/{c.Summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostJsonAsync("/api/v1/strategy-comparisons",
            new CreateStrategyComparisonRequest { OptimizationRunId = _run.Id, MlExperimentId = done.Summary.Id })).StatusCode);

        // Deleting the source experiment keeps the saved comparison with its frozen configuration and results.
        Assert.Equal(HttpStatusCode.NoContent, (await _user.DeleteAsync($"/api/v1/ml/experiments/{done.Summary.Id}")).StatusCode);
        var after = await (await _user.GetAsync($"/api/v1/strategy-comparisons/{c.Summary.Id}")).ReadAsync<StrategyComparisonDto>();
        Assert.Null(after.Summary.MlExperimentId);
        Assert.Equal(c.Result!.Strategies.Select(s => s.Values[^1]), after.Result!.Strategies.Select(s => s.Values[^1]));
        Assert.Equal(c.Configuration.MlHyperparameters, after.Configuration.MlHyperparameters);

        var list = await (await _user.GetAsync("/api/v1/strategy-comparisons")).ReadAsync<PagedResult<StrategyComparisonSummaryDto>>();
        Assert.Contains(list.Items, x => x.Id == c.Summary.Id);
    }

    [Fact]
    public async Task InvalidSources_AreRejected()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _user.PostJsonAsync("/api/v1/strategy-comparisons",
            new CreateStrategyComparisonRequest { OptimizationRunId = Guid.NewGuid(), MlExperimentId = _experiment.Summary.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.PostJsonAsync("/api/v1/strategy-comparisons",
            new CreateStrategyComparisonRequest { OptimizationRunId = _run.Id, MlExperimentId = _experiment.Summary.Id, Rebalance = RebalanceKind.None })).StatusCode);
    }
}
