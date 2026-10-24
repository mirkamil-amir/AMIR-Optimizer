# Machine learning & strategy comparison

The ML engine (`src/PortfolioOptimizer.MachineLearning`) is a separate research module. It forecasts and ranks stocks,
turns rankings into portfolios with its own rules, and backtests them walk-forward. It **never calls the mean-variance
optimizer** (enforced by an architecture test). The classical optimizer is unchanged and does not depend on it.

> ML results are statistical estimates from historical data. A positive test-period metric is not evidence of a
> durable edge: test periods are short, stock universes are small and survivorship-biased, and many configurations
> can be tried. The application states this next to every result.

## Pipeline (one experiment)

1. **Panel.** Adjusted closes, volumes and sectors of the selected universe plus the benchmark (default SPY), from the
   active provider only, on the benchmark's trading dates – so only the benchmark's stored span is used (a note says so when
   stocks have older prices). The data window and a SHA-256 snapshot hash are stored with the experiment.
2. **Decision grid.** Decision dates every `step` trading days (default 5). With a longer horizon, consecutive labels
   overlap and are dependent; purging and the non-overlapping IC t-statistic account for this.
3. **Features** at the close of day *t* using prices up to and including *t* only.
4. **Labels** from prices of days *t+1 … t+h*; a label becomes known at *t+h* (its availability date).
5. **Chronological split** of decision dates: 60% train → 20% validation → 20% test, with **purging** (a sample stays
   in an earlier period only if its label window ends before the next period's first decision date) and an optional
   extra embargo. No random shuffling anywhere.
6. **Hyperparameter search**: each grid point is trained on train, scored on validation only.
7. **Evaluation model**: the selected hyperparameters refit on train + validation samples whose labels were realized
   before the test period; evaluated **once** on the untouched test period, next to naive baselines.
8. **Explainability** on the test period.
9. **Deployment model**: refit on every realized label for current predictions. Its out-of-sample record is the
   evaluation model's test result; it has no test result of its own.
10. **ML portfolio** from the deployment model's latest predictions, and a **walk-forward backtest** over the test
    period through the shared simulator.

Minimums: 200 training samples, 50 evaluation samples and 3 evaluation dates; otherwise the experiment fails with an
explanation instead of producing unreliable numbers.

## Features (`features-2026.10-v1`, 20 features)

| Group | Features |
|---|---|
| Price momentum | `ret_5`, `mom_21`, `mom_63`, `mom_126`, `mom_12_1` (skips the last month), `ma_gap_50`, `ma_gap_200` |
| Volatility & downside | `vol_21`, `vol_63`, `downside_vol_63`, `drawdown_126`, `max_ret_21` |
| Relative to benchmark | `beta_126`, `corr_126`, `excess_mom_63` |
| Trading activity | `rel_volume_21_63`, `log_dollar_volume_21` |
| Sector context | `sector_rel_mom_63` (vs. the median of ≥ 3 sector peers in the universe) |
| Market context | `market_trend_200`, `market_vol_63` (same value for all stocks on a date) |

Exact formulas are in `Features/FeatureCatalog.cs` and shown in the UI. Missing values (insufficient history) stay
missing; models impute them with **training-set medians**. Linear models z-score features with training-set means and
standard deviations. Preprocessing is fitted on training rows only and stored inside the model artifact.

## Targets

| Target | Definition (decision at close *t*) | Task |
|---|---|---|
| Forward return | P[t+h]/P[t] − 1 (adjusted) | regression |
| Forward excess return | forward return − benchmark forward return | regression |
| Probability of outperformance | 1 if forward excess return > 0 else 0 | classification |
| Forward volatility | annualized std of daily returns over t+1 … t+h (lower is more attractive) | regression |

Horizons: 5, 21 or 63 trading days.

## Models

| Family | Implementation | Default grid (selected on validation) |
|---|---|---|
| Ridge regression (regression targets) | closed form β = (ZᵀZ + λnI)⁻¹Zᵀ(y − ȳ) | λ ∈ {0.001, 0.01, 0.1, 1} |
| Logistic regression (outperformance target) | L2-penalized, Newton / IRLS | λ ∈ {0.001, 0.01, 0.1, 1} |
| Random forest | ML.NET FastForest (regression; on the 0/1 target its clamped output is the probability) | 200 trees; leaves {16, 64} × min leaf {20, 100}; feature fraction 0.6 |
| Gradient boosting | ML.NET FastTree (MART; binary classifier for the 0/1 target) | trees {100, 300} × leaves {8, 16}; learning rate 0.05; min leaf 50 |

Training is deterministic for a given seed (tested). LightGBM was not used: its NuGet package ships no macOS arm64
native binaries, which would break development on this machine.

## Evaluation metrics (test period)

- **Regression:** RMSE, MAE, out-of-sample R² against the training-mean forecast (Campbell–Thompson; negative = worse
  than the naive mean).
- **Classification:** accuracy, precision, recall, F1 at 0.5, ROC-AUC (Mann–Whitney), Brier score, log loss,
  calibration bins.
- **Ranking (all targets):** per-date Spearman information coefficient against the realized **forward excess return**
  (so different targets are judged on the same investment outcome), its mean, hit rate, top-K minus universe excess
  return, and a t-statistic computed only on dates at least one horizon apart (overlapping windows would overstate
  significance).
- **Baselines:** constant training mean / base rate, and a 12-1 momentum ranking.

## Explainability

- **Permutation importance** on the test period: increase in loss when one feature column is shuffled (repeated;
  mean ± standard deviation). Measures reliance, not causation; correlated features mask each other.
- **Model-native:** standardized coefficients (linear models) or split-gain importance (trees, in-sample).
- **Per-prediction drivers:** exact additive contributions βⱼzⱼ for linear models; for trees, occlusion (the change in
  prediction when a feature is replaced by its training median). Shown as associations, not reasons prices move.

## ML portfolio construction

Rank by score (inverted for volatility) → optional minimum score → top K → weights (equal, rank-linear,
inverse 63-day volatility, or score-proportional) → per-stock cap and optional sector caps by iterative capping (what
cannot be placed stays in cash) → optional cash reserve → optional turnover limit by partial rebalancing. Weights are
validated (≥ 0, Σ ≤ 1). A paper-trade proposal can be created from the result; nothing executes without confirmation.

## Walk-forward ML backtest

At each rebalance signal date *t* the strategy (re)trains, if due, on samples whose labels were realized by *t*
(label end ≤ *t*), scores every stock with features at *t*, builds weights, and the shared simulator fills orders at
the **next trading day's close** with proportional costs. Retraining frequency and an optional rolling training
window are configurable. The rebalance log records the model's training cutoff and sample count at every decision.
An equal-weight strategy on the same universe and dates and the benchmark run alongside.

## Model registry & artifacts

Every trained model is an `MlModelVersion` row (role *Evaluation* or *Deployment*, hyperparameters, feature set,
training window, sample count, metrics) plus an artifact file: a ZIP with `manifest.json` and the parameters
(`linear.json`, or a preprocessor and the ML.NET model). The SHA-256 of the file is stored in the database and
verified on every load; a mismatch or missing file is reported as "Model artifact unavailable" instead of predicting.
Artifacts are written atomically to `<ModelArtifacts:RootPath>/<experimentId>/<versionId>.model`; only paths of that
generated form are accepted when reading. Back up the artifact directory together with the database.

## Strategy comparison

`StrategyComparisonEngine` re-evaluates strategies on **one price grid** with the same shared `PortfolioSimulator`:

| Fairness rule | How it is enforced |
|---|---|
| Same period and decision dates | one evaluation window and one rebalancing calendar for all strategies |
| Same universe | intersection (default) or union of the two runs' universes; exclusions are listed |
| Same capital, costs, execution | initial capital, proportional cost in bps, next-close fills, cash at 0% |
| No look-ahead | the classical strategy re-optimizes with its **stored settings** on a trailing window ending at the signal date; the ML strategy retrains with its **stored hyperparameters** on labels realized before the signal date |
| Comparable outputs | identical metric definitions (`StrategyMetrics`) for all strategies and the benchmark |

Strategies: mathematical optimization, machine learning, equal weight, benchmark (buy and hold), and an optional
experimental hybrid (the ML model screens the top candidates; the mean-variance optimizer weights them with the run's
settings). Outputs: growth, drawdown, rolling Sharpe, risk/return, monthly returns, average weights and sector
exposure, turnover and costs, holdings overlap, and a Sharpe ranking for the period with an explicit note that
differences over one period can be due to chance.

A comparison stores its inputs, data snapshot hash and results; it is never recalculated or edited. Deleting removes it.

## Currencies, benchmarks and missing history

Prices of every stock and the benchmark are converted into the experiment's currency with the ECB rate of each price date
(rules in [currency.md](currency.md)); only prices within the benchmark's stored span are converted and checked, because
the panel uses no others. A stock with more than 5 % of those prices without a rate is excluded, with the cause (before
the first stored rate / gaps / after the latest rate). Background jobs wait up to 5 min for missing rates to download
(decision D-35).

**Stock eligibility** (`MlResultDocument.Eligibility`, "Setup" tab): per stock the stored prices, prices within the
benchmark's span, prices without an exchange rate, usable prices, samples (labeled / dropped for missing features), the
latest rank and weight, and the last stage reached with its reason (excluded for data, no usable samples, no prediction,
predicted but not selected – rank or minimum score –, selected). Data eligibility is separate from model ranking and
portfolio rules; stocks are never added to reach a count.

Stocks may trade in different currencies (`UniverseCurrency`). In a single-market analysis in that market's own currency, a
foreign benchmark (e.g. SPY for DAX stocks in EUR) is replaced by the market's index benchmark (^GDAXI) with a note in the
experiment's warnings. Stocks or a benchmark without
enough stored history (300 observations) are downloaded first – the background worker runs jobs in order, so the
experiment starts once the data is stored. Comparisons apply the same rules.

## Explanations for students

The ML workspace shows "What your result means" (`interpretMlExperiment` in `frontend/.../lib/interpretation.ts`):
the test window and independent dates, whether the IC is distinguishable from zero (t-statistic), comparison with the
momentum baseline, out-of-sample R² or AUC in plain words, an overfitting warning when training skill far exceeds test
skill, how predictions became weights (and a warning when top K covers the whole universe), and the backtest versus
equal weight after costs. The Learn section has a 12-part guide per model family plus guides for portfolio
construction and strategy comparison (`lib/strategy-guide.ts`).

## Leakage safeguards (tested)

Tests in `tests/PortfolioOptimizer.MachineLearning.Tests`:

- `Features_NeverUseFutureObservations` – features at *t* are unchanged when all prices after *t* are altered.
- `Labels_UseExactlyTheNextHorizon_AndAreMissingBeyondTheData` – labels use exactly *t+1 … t+h*; near the end of
  the data they are missing, not truncated.
- `Split_IsChronological_WithPurgedBoundaries` – no training label window overlaps later decision dates.
- `Preprocessing_IsFittedOnTrainingRowsOnly`, `ModelSelection_DoesNotDependOnTheTestPeriod`.
- `OnARandomWalk_NoModelShowsOutOfSampleSkill` – on a zero-drift random-walk market no model shows significant IC
  (|t| < 3) or positive out-of-sample R²; `WithAPlantedSignal_ModelsFindItOutOfSample` is the positive control.
- `WalkForwardBacktest_DecisionsUseOnlyPastData` – retraining uses only samples with label end ≤ signal date.
- `MachineLearning_NeverUsesTheMeanVarianceOptimizer`, `Optimization_DoesNotReferenceMachineLearning` – module
  boundaries.
