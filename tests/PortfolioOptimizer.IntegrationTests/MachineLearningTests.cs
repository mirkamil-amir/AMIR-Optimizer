using System.Net;
using Npgsql;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>ML experiment lifecycle through the API: submit → background training → results, model registry, predictions.</summary>
public sealed class MachineLearningTests : IAsyncLifetime
{
    private static readonly string[] Universe = ["AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "META", "JPM", "JNJ", "XOM", "PG"];
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        await _admin.SyncAndWaitAsync(new SyncRequest { StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-7) });
        (_user, _) = await _factory.CreateApprovedUserAsync(_admin);
    }

    public async Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private static CreateMlExperimentRequest Request(ModelFamily family = ModelFamily.RidgeRegression, PredictionTarget target = PredictionTarget.ForwardExcessReturn) => new()
    {
        Universe = [.. Universe],
        BenchmarkSymbol = "SPY",
        ModelFamily = family,
        Target = target,
        HorizonDays = 21,
        HistoryYears = 6,
        TopK = 4,
        MaxWeight = 0.3,
    };

    private async Task<MlExperimentDto> WaitAsync(HttpClient client, Guid id)
    {
        for (var i = 0; i < 600; i++)
        {
            var e = await (await client.GetAsync($"/api/v1/ml/experiments/{id}")).ReadAsync<MlExperimentDto>();
            if (e.Summary.Status is not (JobStatus.Queued or JobStatus.Running)) return e;
            await Task.Delay(200);
        }
        throw new TimeoutException("Experiment did not finish.");
    }

    [Fact]
    public async Task Catalog_DescribesModelsTargetsAndFeatures()
    {
        var catalog = await (await _user.GetAsync("/api/v1/ml/models")).ReadAsync<MlCatalogDto>();
        Assert.Equal(4, catalog.ModelFamilies.Count);
        Assert.Contains(catalog.ModelFamilies, f => f.Value == "LogisticRegression" && f.SupportedTargets.SequenceEqual(["OutperformBenchmark"]));
        Assert.Equal(20, catalog.Features.Count);
        Assert.All(catalog.Features, f => Assert.False(string.IsNullOrWhiteSpace(f.Definition)));
        Assert.Contains("purged", catalog.ValidationMethodology);
    }

    [Fact]
    public async Task Experiment_TrainsValidatesTests_AndStoresVersionedModels()
    {
        var created = await _user.PostJsonAsync("/api/v1/ml/experiments", Request());
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var queued = await created.ReadAsync<MlExperimentDto>();
        Assert.Equal(JobStatus.Queued, queued.Summary.Status);

        var done = await WaitAsync(_user, queued.Summary.Id);
        Assert.True(done.Summary.Status == JobStatus.Succeeded, done.Summary.Error);
        Assert.True(done.Summary.UsesSimulatedData);
        var r = done.Results!;
        Assert.True(r.TrainPeriod.End < r.ValidationPeriod.Start && r.ValidationPeriod.End < r.TestPeriod.Start);
        Assert.Contains("y = (P[t+21]", r.TargetDefinition);
        Assert.NotNull(r.TestMetrics.Regression);
        Assert.NotNull(r.TestMetrics.Ranking.MeanInformationCoefficient);
        Assert.Equal(4, r.Trials.Count);
        Assert.Contains(r.Baselines, b => b.Name == "Training mean");
        Assert.Equal(r.FeatureNames.Count, r.PermutationImportance.Count);
        Assert.Equal("Standardized coefficients", r.NativeExplanation.Method);
        Assert.NotEmpty(done.DataSnapshotHash!);

        // Model registry: an evaluation model (trained before the test period) and a deployment model, with checksums.
        Assert.Equal(["Evaluation", "Deployment"], done.ModelVersions.Select(v => v.Role));
        var evaluation = done.ModelVersions[0];
        Assert.True(evaluation.TrainedThrough < r.TestPeriod.Start);
        Assert.All(done.ModelVersions, v => Assert.Equal(64, v.ArtifactSha256.Length));

        var predictions = done.LatestPredictions!;
        Assert.Equal("Deployment", predictions.ModelRole);
        Assert.Equal(4, predictions.Predictions.Count(p => p.Selected));
        Assert.All(predictions.Predictions.Where(p => p.Selected), p => Assert.InRange(p.TargetWeight, 0.01, 0.3 + 1e-9));
        Assert.Equal(Enumerable.Range(1, predictions.Predictions.Count), predictions.Predictions.Select(p => p.Rank));
        Assert.All(predictions.Predictions, p => Assert.NotEmpty(p.Drivers));
        Assert.NotNull(predictions.IntervalMethod);

        var backtest = await (await _user.GetAsync($"/api/v1/ml/experiments/{done.Summary.Id}/backtest")).ReadAsync<MlBacktestDocument>();
        Assert.Equal(3, backtest.Performance.Count);
        Assert.True(backtest.Dates[0] >= r.TestPeriod.Start);
        Assert.All(backtest.Rebalances, b => Assert.True(b.ModelTrainedThrough <= b.SignalDate));

        // Re-predict with the stored deployment model (artifact verified, no retraining).
        var again = await (await _user.PostJsonAsync($"/api/v1/ml/experiments/{done.Summary.Id}/predictions", new { })).ReadAsync<MlPredictionSetDto>();
        Assert.Equal(predictions.ModelVersionId, again.ModelVersionId);
        Assert.Equal(predictions.Predictions.Select(p => p.Symbol).OrderBy(s => s), again.Predictions.Select(p => p.Symbol).OrderBy(s => s));
        var sets = await (await _user.GetAsync($"/api/v1/ml/experiments/{done.Summary.Id}/predictions")).ReadAsync<List<MlPredictionSetDto>>();
        Assert.Equal(2, sets.Count);

        // History lists the experiment with its out-of-sample headline metric.
        var list = await (await _user.GetAsync("/api/v1/ml/experiments")).ReadAsync<PagedResult<MlExperimentSummaryDto>>();
        Assert.Contains(list.Items, e => e.Id == done.Summary.Id && e.TestRankCorrelation is not null);
    }

    [Fact]
    public async Task TamperedArtifact_IsRefused()
    {
        var created = await (await _user.PostJsonAsync("/api/v1/ml/experiments", Request(ModelFamily.LogisticRegression, PredictionTarget.OutperformBenchmark))).ReadAsync<MlExperimentDto>();
        var done = await WaitAsync(_user, created.Summary.Id);
        Assert.True(done.Summary.Status == JobStatus.Succeeded, done.Summary.Error);
        Assert.NotNull(done.Results!.TestMetrics.Classification);
        Assert.All(done.LatestPredictions!.Predictions, p => Assert.InRange(p.PredictedValue, 0, 1));

        // Corrupt the recorded checksum (equivalent to a modified file): the model must not be loaded.
        await using (var conn = new NpgsqlConnection(_factory.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE \"MlModelVersions\" SET \"ArtifactSha256\" = repeat('0', 64) WHERE \"ExperimentId\" = @id", conn);
            cmd.Parameters.AddWithValue("id", done.Summary.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        var predict = await _user.PostJsonAsync($"/api/v1/ml/experiments/{done.Summary.Id}/predictions", new { });
        Assert.Equal(HttpStatusCode.InternalServerError, predict.StatusCode);
        Assert.Contains("checksum mismatch", await predict.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Experiments_ArePrivate_AndDeletable()
    {
        var created = await (await _user.PostJsonAsync("/api/v1/ml/experiments", Request() with { HistoryYears = 4 })).ReadAsync<MlExperimentDto>();
        var (other, _) = await _factory.CreateApprovedUserAsync(_admin);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/ml/experiments/{created.Summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/ml/experiments/{created.Summary.Id}")).StatusCode);
        var othersList = await (await other.GetAsync("/api/v1/ml/experiments")).ReadAsync<PagedResult<MlExperimentSummaryDto>>();
        Assert.Empty(othersList.Items);

        var done = await WaitAsync(_user, created.Summary.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await _user.DeleteAsync($"/api/v1/ml/experiments/{done.Summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.GetAsync($"/api/v1/ml/experiments/{done.Summary.Id}")).StatusCode);
    }

    [Fact]
    public async Task InvalidRequests_AreRejectedBeforeQueueing()
    {
        var unknown = await _user.PostJsonAsync("/api/v1/ml/experiments", Request() with { Universe = ["AAPL", "MSFT", "NVDA", "JPM", "NOPE1"] });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("NOPE1", await unknown.Content.ReadAsStringAsync());

        var unsupported = await _user.PostJsonAsync("/api/v1/ml/experiments", Request(ModelFamily.RidgeRegression, PredictionTarget.OutperformBenchmark));
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);

        var badFeature = await _user.PostJsonAsync("/api/v1/ml/experiments", Request() with { Features = ["mom_21", "made_up"] });
        Assert.Equal(HttpStatusCode.BadRequest, badFeature.StatusCode);

        var tooSmall = await _user.PostJsonAsync("/api/v1/ml/experiments", Request() with { Universe = ["AAPL", "MSFT"] });
        Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);
    }

    [Fact]
    public async Task MissingHistory_IsDownloadedBeforeTraining_AndReported()
    {
        // Catalogue stocks without stored prices: their history is queued first (jobs run in order), and the experiment
        // says so instead of failing or silently using partial data.
        var request = Request() with { Universe = ["CVX", "COP", "SLB", "EOG", "OXY"] };
        var created = await (await _user.PostJsonAsync("/api/v1/ml/experiments", request)).ReadAsync<MlExperimentDto>();
        Assert.Contains(created.Warnings, w => w.Contains("downloaded before training") && w.Contains("CVX"));
        var done = await WaitAsync(_user, created.Summary.Id);
        Assert.Equal(JobStatus.Succeeded, done.Summary.Status);
        Assert.Contains(done.Warnings, w => w.Contains("downloaded before training"));
    }
}
