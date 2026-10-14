using PortfolioOptimizer.MachineLearning.Data;

namespace PortfolioOptimizer.MachineLearning.Validation;

public sealed record SplitSettings
{
    /// <summary>Fraction of labeled decision dates (chronological) used for training.</summary>
    public double TrainFraction { get; init; } = 0.6;

    /// <summary>Fraction used for validation (model/hyperparameter selection). The remainder is the untouched test period.</summary>
    public double ValidationFraction { get; init; } = 0.2;

    /// <summary>Extra trading days between a training label's end and the next period's first decision (on top of purging).</summary>
    public int EmbargoDays { get; init; }

    public int MinimumTrainingSamples { get; init; } = 200;
    public int MinimumEvaluationSamples { get; init; } = 50;
    public int MinimumEvaluationDates { get; init; } = 3;
}

public sealed record PeriodInfo(DateOnly Start, DateOnly End, int Samples, int DecisionDates, int Purged);

/// <summary>
/// Chronological train → validation → test split of labeled samples by decision date, with purging: a sample is kept in
/// an earlier period only if its label window [t, t+h] ends before the next period's first decision date (minus the
/// embargo). There is no random shuffling; scaling and imputation are fitted on the training period only (by the models).
/// </summary>
public sealed record ChronologicalSplit(
    IReadOnlyList<Sample> Train,
    IReadOnlyList<Sample> Validation,
    IReadOnlyList<Sample> Test,
    int ValidationStartIndex,
    int TestStartIndex,
    PeriodInfo TrainPeriod,
    PeriodInfo ValidationPeriod,
    PeriodInfo TestPeriod,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Training + validation samples whose labels are known before the test period starts (for the evaluation refit).</summary>
    public IReadOnlyList<Sample> TrainAndValidation(int embargo) =>
        Train.Concat(Validation).Where(s => s.LabelAvailableIndex < TestStartIndex - embargo).ToList();
}

public static class ChronologicalSplitter
{
    public static ChronologicalSplit Split(Dataset dataset, SplitSettings settings)
    {
        if (settings.TrainFraction <= 0 || settings.ValidationFraction <= 0 || settings.TrainFraction + settings.ValidationFraction >= 1)
            throw new ArgumentException("Train and validation fractions must be positive and leave room for a test period.");
        var labeled = dataset.Samples.Where(s => s.IsLabeled).ToList();
        var dates = labeled.Select(s => s.DecisionIndex).Distinct().OrderBy(t => t).ToArray();
        if (dates.Length < 3 * settings.MinimumEvaluationDates)
            throw new InvalidOperationException(
                $"Only {dates.Length} decision dates have realized labels; at least {3 * settings.MinimumEvaluationDates} are needed for a train/validation/test split. " +
                "Use more history, a shorter horizon or a smaller sample step.");

        var trainEnd = Math.Max(1, (int)Math.Floor(dates.Length * settings.TrainFraction));
        var validationEnd = Math.Max(trainEnd + 1, (int)Math.Floor(dates.Length * (settings.TrainFraction + settings.ValidationFraction)));
        var validationStart = dates[trainEnd];
        var testStart = dates[Math.Min(validationEnd, dates.Length - 1)];

        var trainCandidates = labeled.Where(s => s.DecisionIndex < validationStart).ToList();
        var train = trainCandidates.Where(s => s.LabelAvailableIndex < validationStart - settings.EmbargoDays).ToList();
        var validationCandidates = labeled.Where(s => s.DecisionIndex >= validationStart && s.DecisionIndex < testStart).ToList();
        var validation = validationCandidates.Where(s => s.LabelAvailableIndex < testStart - settings.EmbargoDays).ToList();
        var test = labeled.Where(s => s.DecisionIndex >= testStart).ToList();

        var warnings = new List<string>();
        void Require(IReadOnlyList<Sample> set, string name, int minSamples)
        {
            var distinct = set.Select(s => s.DecisionIndex).Distinct().Count();
            if (set.Count < minSamples || distinct < settings.MinimumEvaluationDates)
                throw new InvalidOperationException(
                    $"The {name} period has {set.Count} samples on {distinct} dates after purging (minimum {minSamples} samples on {settings.MinimumEvaluationDates} dates). " +
                    "Use more history, more stocks, a shorter horizon or a smaller sample step.");
        }
        Require(train, "training", settings.MinimumTrainingSamples);
        Require(validation, "validation", settings.MinimumEvaluationSamples);
        Require(test, "test", settings.MinimumEvaluationSamples);

        var panel = dataset.Panel;
        PeriodInfo Info(IReadOnlyList<Sample> set, int purged) => new(
            panel.Dates[set.Min(s => s.DecisionIndex)], panel.Dates[set.Max(s => s.DecisionIndex)], set.Count,
            set.Select(s => s.DecisionIndex).Distinct().Count(), purged);

        var testDates = test.Select(s => s.DecisionIndex).Distinct().Count();
        if (testDates < 12)
            warnings.Add($"The test period has only {testDates} decision dates; out-of-sample metrics are noisy – treat results as exploratory.");

        return new ChronologicalSplit(train, validation, test, validationStart, testStart,
            Info(train, trainCandidates.Count - train.Count),
            Info(validation, validationCandidates.Count - validation.Count),
            Info(test, 0),
            warnings);
    }
}
