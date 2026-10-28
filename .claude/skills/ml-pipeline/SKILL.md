---
name: ml-pipeline
description: Internals of MatrixView's ML Optimization code – market panel, point-in-time features and labels, chronological purged splits, training-only preprocessing, model trainers (ridge, logistic, random forest, gradient boosting), evaluation metrics (OOS R², AUC, Brier, IC t-stat), ML portfolio construction and the walk-forward backtest. Use when changing anything under src/PortfolioOptimizer.MachineLearning or the ML application services, adding a feature/target/model, or investigating leakage or suspicious ML results. To run experiments through the API use /ml-experiment instead.
---

# ML pipeline (code)

Docs: `docs/machine-learning.md` (method), formulas F-ML-001..009 in `docs/quant/formula-reference.md`. Running and
reporting experiments: `/ml-experiment`.

## Architecture and data flow
`POST /api/v1/ml/experiments` → `MlExperimentService` (validation, ≤ 2 active per user – `MaxActiveExperimentsPerUser`)
→ queued job → `MlExperimentRunner` → `MlDataLoader` (stored adjusted closes of the active provider, converted into the
analysis currency, restricted to the benchmark's stored span) → `MarketPanel.Build` (benchmark calendar, ≤ 5-day
interior forward fill) → `ExperimentEngine.Run` (`src/PortfolioOptimizer.MachineLearning/Training/ExperimentEngine.cs`):
features (`Features/FeatureEngine.cs`, catalogue `FeatureCatalog.cs`) → labels (`Labels/Targets.cs`) → `Dataset.Build`
→ `ChronologicalSplitter.Split` → grid search on validation → evaluation model on train+validation, scored once on test
→ baselines, permutation importance → deployment model on all realized labels → walk-forward backtest
(`Backtesting/MlStrategy.cs` on the shared `PortfolioSimulator`) → stored experiment + artifact
(`Infrastructure/ModelArtifacts/LocalModelArtifactStore.cs`, checksum-verified).

## Rules (must not break)
* Features at t use only indices ≤ t; labels use t+1 … t+h; a sample's label is available at t+h.
* Splits are chronological and purged (label end before the next period's start minus embargo); no shuffling.
* Preprocessing (median imputation, z-scores – `FeaturePreprocessor`) is fitted on the training rows of each model.
* The test period is evaluated once; never select anything on it; never present train/validation metrics as performance.
* Ranking metrics use the realized forward **excess** return for every target; the IC t-stat uses dates ≥ h apart.
* ML never references the mean-variance optimizer (architecture test `ModelTests.MachineLearning_NeverUsesTheMeanVarianceOptimizer`).
* A new or changed feature formula bumps `FeatureCatalog.Version`; comparisons refuse experiments of another version
  (`PortfolioStrategyService`).

## Adding a feature, target or model
* Feature: definition in `FeatureCatalog.All` (window, requirements) + branch in `FeatureEngine.Value` + hand-computed
  test in `tests/PortfolioOptimizer.MachineLearning.Tests/FeatureAndLabelTests.cs` + leakage case
  (`Features_NeverUseFutureObservations` perturbs later prices) + version bump + formula entry.
* Model: implement `IModelTrainer` (`Models/ModelContracts.cs`), register in `ModelRegistry`, default grid, determinism and
  artifact round-trip tests (`ModelTests`), guide entry and `guideForModel` mapping in the frontend.
* Target: `PredictionTarget` + `LabelEngine.Compute` + `TargetCatalog` (task kind, direction, minimum horizon) + tests.

## Failure modes and diagnosis
"Only N decision dates have realized labels" (too little history, long horizon), experiments limited by the
benchmark's stored span (warning in `results.warnings`), stocks excluded for FX or history (`results.eligibility[]`),
identical backtests of two models (same top-K set – check `rebalances[].selected`), timeouts in `MlExperimentRunner`.

## Tests and verification
```bash
dotnet test tests/PortfolioOptimizer.MachineLearning.Tests          # features, labels, splits, leakage, models, metrics (~25 s)
./scripts/test.sh --filter "FullyQualifiedName~MachineLearningTests|FullyQualifiedName~StrategyComparisonTests"
```
Leakage tests must stay meaningful: perturb data after t and assert nothing at t changes; on a random walk no model may
show out-of-sample skill (`ValidationAndLeakageTests.OnARandomWalk_NoModelShowsOutOfSampleSkill`).

## Known limitations and open questions
Price/volume features only; short test periods; no multiple-testing correction; survivorship bias. Open audit risks:
R-03 (panel fill depends on a later observation), R-04 (equal-weight baseline eligibility differs from the ML rule) in
`docs/quant/audit-2026-10-28.md`.

## Related
`/ml-experiment`, `/quant-formulas`, `/optimization-backtesting` (shared simulator, comparisons), `/market-data-fx`.
