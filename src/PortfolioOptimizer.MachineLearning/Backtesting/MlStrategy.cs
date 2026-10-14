using PortfolioOptimizer.MachineLearning.Data;
using PortfolioOptimizer.MachineLearning.Labels;
using PortfolioOptimizer.MachineLearning.Models;
using PortfolioOptimizer.MachineLearning.Strategy;
using PortfolioOptimizer.Optimization.Backtesting;

namespace PortfolioOptimizer.MachineLearning.Backtesting;

/// <summary>How the ML strategy is refreshed during a walk-forward backtest.</summary>
public sealed record WalkForwardSettings
{
    /// <summary>Retrain after this many rebalances (0 = keep the initial model for the whole period).</summary>
    public int RetrainEveryRebalances { get; init; } = 3;

    /// <summary>Training window in trading days ending at the decision date (null = expanding window).</summary>
    public int? TrainingWindowDays { get; init; }

    public int MinimumTrainingSamples { get; init; } = 200;
}

/// <summary>What the ML strategy saw and decided at one signal date.</summary>
public sealed record MlRebalanceRecord(
    DateOnly SignalDate,
    DateOnly ModelTrainedThrough,
    int ModelTrainingSamples,
    bool Retrained,
    IReadOnlyList<string> Selected,
    IReadOnlyDictionary<string, double> TargetWeights,
    IReadOnlyList<string> Notes);

/// <summary>
/// The ML portfolio rule for the shared <see cref="PortfolioSimulator"/>: at each signal date t it (re)trains – if due –
/// on samples whose labels were realized by t (label end t' + h ≤ t), scores every stock with features at t, and builds
/// weights with <see cref="PortfolioConstructor"/>. It never calls the mean-variance optimizer.
/// </summary>
public sealed class MlStrategy
{
    private readonly Dataset _dataset;
    private readonly IModelTrainer _trainer;
    private readonly Hyperparameters _hyperparameters;
    private readonly PortfolioConstructionSettings _construction;
    private readonly WalkForwardSettings _walkForward;
    private readonly int _seed;
    private readonly Dictionary<string, int> _assetIndex;
    private readonly List<MlRebalanceRecord> _records = [];
    private ITrainedModel? _model;
    private DateOnly _modelTrainedThrough;
    private int _modelSamples;
    private int _signalsSinceTraining;

    public MlStrategy(Dataset dataset, IModelTrainer trainer, Hyperparameters hyperparameters, PortfolioConstructionSettings construction,
        WalkForwardSettings walkForward, int seed, ITrainedModel? initialModel = null, DateOnly? initialModelTrainedThrough = null, int initialModelSamples = 0)
    {
        _dataset = dataset;
        _trainer = trainer;
        _hyperparameters = hyperparameters;
        _construction = construction;
        _walkForward = walkForward;
        _seed = seed;
        _model = initialModel;
        _modelTrainedThrough = initialModelTrainedThrough ?? DateOnly.MinValue;
        _modelSamples = initialModelSamples;
        _assetIndex = dataset.Panel.Symbols.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
    }

    public IReadOnlyList<MlRebalanceRecord> Records => _records;

    public PortfolioDecision Decide(SignalContext ctx)
    {
        var panel = _dataset.Panel;
        var t = panel.IndexOnOrBefore(ctx.Date);
        if (t < 0 || panel.Dates[t] != ctx.Date)
            return PortfolioDecision.Hold([], $"{ctx.Date:yyyy-MM-dd} is not a trading day of the benchmark calendar; holdings unchanged.");

        var retrained = false;
        var due = _model is null || (_walkForward.RetrainEveryRebalances > 0 && _signalsSinceTraining >= _walkForward.RetrainEveryRebalances);
        if (due)
        {
            var from = _walkForward.TrainingWindowDays is { } w ? t - w : int.MinValue;
            // Point-in-time: only samples whose label window ended on or before today's close.
            var training = _dataset.Samples.Where(s => s.IsLabeled && s.LabelAvailableIndex <= t && s.DecisionIndex >= from).ToList();
            if (training.Count >= _walkForward.MinimumTrainingSamples)
            {
                _model = _trainer.Train(new TrainingData(training.Select(s => s.Features).ToList(), training.Select(s => s.Label).ToList(), _dataset.Features.Names),
                    _hyperparameters, TargetCatalog.TaskOf(_dataset.Target), _seed, CancellationToken.None);
                _modelTrainedThrough = panel.Dates[training.Max(s => s.LabelAvailableIndex)];
                _modelSamples = training.Count;
                _signalsSinceTraining = 0;
                retrained = true;
            }
        }
        _signalsSinceTraining++;
        if (_model is null)
            return PortfolioDecision.Hold([], "Not enough realized labels yet to train the model; the portfolio stays in cash.");

        var candidates = new List<(int Asset, double[] Features)>();
        foreach (var symbol in ctx.Symbols)
        {
            if (!_assetIndex.TryGetValue(symbol, out var a)) continue; // e.g. the benchmark: not investable by this strategy
            var x = _dataset.Features.Compute(a, t);
            if (x is null || x.Count(double.IsNaN) > Dataset.MaxMissingFeatureFraction * x.Length) continue;
            candidates.Add((a, x));
        }
        var eligible = candidates.Select(c => panel.Symbols[c.Asset]).ToList();
        if (candidates.Count == 0) return PortfolioDecision.Hold(eligible, "No stock has complete features on this date; holdings unchanged.");

        var predictions = _model.Predict(candidates.Select(c => c.Features).ToList());
        var higherIsBetter = TargetCatalog.HigherIsBetter(_dataset.Target);
        var scored = candidates.Select((c, i) => new Candidate(panel.Symbols[c.Asset], panel.Sectors[c.Asset],
            higherIsBetter ? predictions[i] : -predictions[i], _dataset.Features.TrailingVolatility(c.Asset, t))).ToList();
        var current = ctx.Symbols.Select((s, i) => (s, w: ctx.CurrentWeights[i])).Where(x => x.w > 0).ToDictionary(x => x.s, x => x.w);
        var built = PortfolioConstructor.Build(scored, _construction, current);

        var weights = ctx.Symbols.Select(s => built.Weights.GetValueOrDefault(s)).ToArray();
        _records.Add(new MlRebalanceRecord(ctx.Date, _modelTrainedThrough, _modelSamples, retrained, built.Selected, built.Weights, built.Notes));
        var message = $"Model trained through {_modelTrainedThrough:yyyy-MM-dd} ({_modelSamples} samples){(retrained ? ", retrained" : "")}."
                      + (built.Notes.Count > 0 ? " " + string.Join(" ", built.Notes) : "");
        return new PortfolioDecision(weights, eligible, true, message);
    }
}

/// <summary>Equal weights across every stock with complete features on the signal date (the simple baseline).</summary>
public static class EqualWeightRule
{
    public static Func<SignalContext, PortfolioDecision> Create(Dataset dataset, double cashReserve)
    {
        var index = dataset.Panel.Symbols.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        return ctx =>
        {
            var t = dataset.Panel.IndexOnOrBefore(ctx.Date);
            var eligible = ctx.Symbols.Where(s => index.TryGetValue(s, out var a) && t >= 0 && dataset.Features.Compute(a, t) is not null).ToList();
            if (eligible.Count == 0) return PortfolioDecision.Hold(eligible, "No eligible stock.");
            var w = (1 - cashReserve) / eligible.Count;
            return new PortfolioDecision(ctx.Symbols.Select(s => eligible.Contains(s) ? w : 0).ToArray(), eligible, true, "Equal weight.");
        };
    }
}
