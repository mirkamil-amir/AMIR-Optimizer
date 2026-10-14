using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MachineLearning;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>
/// Machine-learning research engine (separate from the mean-variance optimizer): feature-based stock forecasting and
/// ranking with chronological validation, ML-driven portfolios and walk-forward backtests.
/// </summary>
/// <remarks>
/// Lifecycle: POST an experiment (202, queued) → poll GET until Succeeded/Failed → read metrics, predictions, model
/// versions and the backtest → optionally POST predictions to score the latest data with the stored deployment model.
/// Experiments are private to their owner.
/// </remarks>
[ApiController]
[Route("api/v1/ml")]
public sealed class MachineLearningController(MlExperimentService experiments) : ControllerBase
{
    /// <summary>Model families, targets, features (with definitions), weighting methods and methodology.</summary>
    [HttpGet("models")]
    public MlCatalogDto Models() => MlExperimentService.Catalog();

    /// <summary>Submits an experiment; training, validation, testing and the backtest run in the background.</summary>
    /// <response code="202">Queued; poll the returned experiment.</response>
    /// <response code="409">Too many active experiments for this user.</response>
    [HttpPost("experiments")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    [ProducesResponseType<MlExperimentDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MlExperimentDto>> Create(CreateMlExperimentRequest request, CancellationToken ct)
    {
        var experiment = await experiments.CreateAsync(request, ct);
        return AcceptedAtAction(nameof(Get), new { id = experiment.Summary.Id }, experiment);
    }

    /// <summary>The caller's experiments, newest first.</summary>
    [HttpGet("experiments")]
    public Task<PagedResult<MlExperimentSummaryDto>> List(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        experiments.ListAsync(page, pageSize, ct);

    /// <summary>Status, configuration, periods, metrics, explanations, model versions and the latest predictions.</summary>
    [HttpGet("experiments/{id:guid}")]
    [ProducesResponseType<MlExperimentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<MlExperimentDto> Get(Guid id, CancellationToken ct) => experiments.GetAsync(id, ct);

    /// <summary>Training, validation and out-of-sample test metrics with baselines and explanations.</summary>
    [HttpGet("experiments/{id:guid}/metrics")]
    [ProducesResponseType<MlResultDocument>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<MlResultDocument>> Metrics(Guid id, CancellationToken ct) =>
        (await experiments.GetAsync(id, ct)).Results is { } r ? r : NoContent();

    /// <summary>Walk-forward backtest of the ML strategy over the test period (vs equal weight and the benchmark).</summary>
    [HttpGet("experiments/{id:guid}/backtest")]
    [ProducesResponseType<MlBacktestDocument>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<MlBacktestDocument>> Backtest(Guid id, CancellationToken ct) =>
        await experiments.GetBacktestAsync(id, ct) is { } b ? b : NoContent();

    /// <summary>Stored model versions (evaluation and deployment) with training ranges and artifact checksums.</summary>
    [HttpGet("experiments/{id:guid}/models")]
    public Task<IReadOnlyList<MlModelVersionDto>> ModelVersions(Guid id, CancellationToken ct) => experiments.GetModelVersionsAsync(id, ct);

    /// <summary>Prediction sets of the experiment, newest first.</summary>
    [HttpGet("experiments/{id:guid}/predictions")]
    public Task<IReadOnlyList<MlPredictionSetDto>> Predictions(Guid id, CancellationToken ct) => experiments.GetPredictionSetsAsync(id, ct);

    /// <summary>Scores the latest stored data with the experiment's deployment model (no retraining) and stores the result.</summary>
    [HttpPost("experiments/{id:guid}/predictions")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    [ProducesResponseType<MlPredictionSetDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MlPredictionSetDto>> Predict(Guid id, CancellationToken ct)
    {
        var set = await experiments.PredictAsync(id, ct);
        return CreatedAtAction(nameof(Predictions), new { id }, set);
    }

    /// <summary>Deletes a finished experiment with its model artifacts and predictions.</summary>
    [HttpDelete("experiments/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await experiments.DeleteAsync(id, ct);
        return NoContent();
    }
}
