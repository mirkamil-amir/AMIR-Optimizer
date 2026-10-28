---
name: optimization-backtesting
description: MatrixView's Math Optimization (long-only mean-variance QP, ADMM/OSQP solver, constraints, efficient frontier, independent allocation validation), the event-driven backtest simulator shared by backtests, ML walk-forward and strategy comparisons, and the Portfolio Reality Check. Use when changing objectives, constraints, solver settings, backtest timing/costs/execution rules or comparison logic, or when an optimization is infeasible, fails validation or a backtest result looks wrong.
---

# Optimization, backtesting and comparisons

Formulas: `docs/quant/formula-reference.md` (F-OPT-*, F-BT-*, F-RISK-003..007). Student-facing docs:
`docs/quantitative-strategies.md` (keep both in sync, plus `frontend/portfolio-dashboard/src/lib/strategy-guide.ts`).

## Architecture and data flow
* Request: `POST /api/v1/portfolios/{id}/optimization-runs` → `OptimizationService.RunAsync` → `EvaluateAsync`
  (`src/PortfolioOptimizer.Application/Optimization/OptimizationService.cs`): owner-scoped portfolio, ≤ 500 stocks
  (`MaxUniverse`), converted prices of the last `LookbackDays + 1` dates, ≥ 61 prices per stock, `CommonWindow.Choose`,
  `ReturnMatrix.Build`, `CovarianceEstimator.Estimate`, `MeanVarianceOptimizer.Optimize`, `AllocationValidator.Validate`,
  analytics (frontier, correlations, equal-weight and benchmark references, in-sample curves) → stored `OptimizationRun`.
* Pure engine (`src/PortfolioOptimizer.Optimization`): `Portfolio/MeanVarianceOptimizer.cs` (objectives, constraint rows,
  pre-checks, cardinality heuristic, cleaning, verification), `Solvers/AdmmQpSolver.cs`, `Portfolio/AllocationValidator.cs`
  (written separately from the solver – keep it independent), `Portfolio/DiversificationMath.cs`.
* Backtests: `Backtesting/BacktestEngine.cs` (signal rule, baselines, metrics) on `Backtesting/PortfolioSimulator.cs`
  (`PriceGrid`, execution, costs, valuation). Callers: `Application/Backtesting/BacktestService.cs`,
  `Application/Comparisons/StrategyComparisonEngine.cs`, `MachineLearning/Training/ExperimentEngine.cs`. All strategies of
  a comparison use the same simulator, calendar, costs and metrics – never give one strategy its own accounting.
* Reality check: `Application/Analytics/RealityCheckService.cs` (thresholds in `docs/portfolio-reality-check.md`).

## Business rules and assumptions
* Weights are fractions of total value; `Σw = 1 − cash reserve`; long only (negative bounds rejected); cash earns 0.
* Infeasible → explanation and no weights; a validator violation fails the run – nothing is relaxed or repaired.
* Turnover limit applies only when an asset of the universe is held (optimizer and validator agree – audit Q-02).
* Max holdings / min position are a heuristic (remove smallest, re-solve), not MIQP; say so in diagnostics.
* Backtest timing: signal at the close of s with data ≤ s; fill at the next trading day's close net of costs; if an
  exchange of an involved asset is closed, wait ≤ `PortfolioSimulator.MaxExecutionDelayDays` (5) trading days, then keep
  closed positions; no price for > `PriceGrid.StaleAfterDays` (10) days = no longer trading (audit Q-01).
* Optimization never executes trades or changes holdings (only proposals/bookings do – `/portfolio-accounting`).

## Edge cases to keep handled
Zero-variance assets (fixed at 0 with a diagnostic), no asset above r_f (max Sharpe undefined), target above the max
achievable return, volatility limit below GMV, sector caps that cannot absorb the budget, singular Σ (N ≥ T), a stock
listed after the window start (`CommonWindow`), benchmark missing dates (reference omitted, never filled).

## Making changes safely
* New objective: `OptimizationObjective` + `SolveObjective` branch + closed-form/hand test in `OptimizerTests` + label in
  `frontend/portfolio-dashboard/src/lib/format.ts` + guide entry + `interpretOptimization` branch (see
  `docs/quantitative-strategies.md#implemented-strategies`) + formula entry.
* Solver tolerances: `QpSettings` and `FrontierSettings`; results are cleaned at 1e-8 and verified at 1e-6 – keep the
  verification tolerance ≥ the solver's accuracy (risk R-07).
* Simulator changes alter every stored-strategy comparison on re-run: add a deterministic before/after fixture to
  `tests/PortfolioOptimizer.UnitTests/Optimization/ExchangeCalendarTests.cs` or `BacktestTests.cs`, keep
  `BacktestTests.Backtest_HasNoLookAheadBias` green, and record the behaviour change in the docs.

## Tests and verification
```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet test tests/PortfolioOptimizer.UnitTests --filter "FullyQualifiedName~Optimization"   # optimizer, validator, backtests, metrics
./scripts/test.sh --filter "FullyQualifiedName~StrategyComparisonTests|FullyQualifiedName~RealityCheckTests|FullyQualifiedName~ApiTests"
python3 scripts/quant/maintain.py validate && python3 scripts/quant/maintain.py review-changes
```
Integration tests need PostgreSQL (Podman socket via `scripts/test.sh`). User-visible flows: `./scripts/e2e.sh e2e/optimizations.spec.ts`.

## Known limitations
Historical means as expected returns (no Black–Litterman/robust optimization), no holiday calendars, survivorship
bias in universes, proportional costs only, dense linear algebra (hundreds of assets at most). Risk register:
`docs/quant/audit-2026-10-28.md`.

## Related
`/quant-formulas`, `/ml-pipeline` (ML strategies in comparisons), `/market-data-fx` (inputs), `/portfolio-accounting`
(proposals from a run), `/ship-change`.
