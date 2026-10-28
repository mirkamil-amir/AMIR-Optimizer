---
name: quant-formulas
description: Financial formulas and conventions of MatrixView – returns, annualization, Sharpe/Sortino, volatility, drawdown, VaR/ES, beta, tracking error, covariance (sample, Ledoit–Wolf), risk contributions – and how to verify, change or add one with its formula-reference entry and tests. Use before touching any metric or statistic, when a number looks wrong, or when asked to audit or explain a calculation.
---

# Quantitative formulas and conventions

**Canonical source:** `docs/quant/formula-reference.md` (one entry per formula: math, units, conventions, edge cases,
code symbols, tests, status). This skill says how to work with it; it does not repeat the formulas.

## When it applies
Any change to `src/PortfolioOptimizer.Optimization/Statistics/*`, `AnalyticsConventions`, `PortfolioMath`, metric code in
`StrategyMetrics`, or to text that explains a metric (`frontend/portfolio-dashboard/src/lib/glossary.ts`,
`strategy-guide.ts`, `interpretation.ts`); questions like "why is this Sharpe ratio X?". Optimizer/backtest specifics:
`/optimization-backtesting`; ML metrics: `/ml-pipeline`; FX: `/market-data-fx`; ledger/TWR: `/portfolio-accounting`.

## Conventions that bind every formula
* Fractions everywhere (0.05 = 5 %); A = 252 observations per year (`AnalyticsConventions.TradingDaysPerYear`).
* Statistics on **daily simple returns of adjusted closes**; raw closes only for paper valuation and fills.
* Expected return (optimizer input) = arithmetic mean × 252; realized annual return = geometric CAGR. Never mix them in
  one comparison.
* Sharpe = (mean − r_f/252)/sd × √252 realized; (E[r] − r_f)/σ ex-ante. The daily risk-free rate is simple division.
* Sample statistics use n−1 (Sortino's downside deviation uses n – a stated convention).
* Undefined → `MetricValue.Unavailable(reason)` / `null`, never NaN, ∞ or a substituted 0.
* Multi-asset series are aligned on the **intersection** of dates (`ReturnMatrix`), backtests on the **union** calendar.
* All instruments are converted into one analysis currency first (`FxService`, F-FX-003).

## Data flow (where the numbers come from)
Stored prices (`HistoricalPrices`, one provider) → `FxService.GetSeriesInCurrencyAsync` → `ReturnMatrix.Build` /
`ReturnCalculator` → `CovarianceEstimator.Estimate` (μ, Σ annualized) and `PerformanceMetrics` → DTOs
(`src/PortfolioOptimizer.Application/Dtos`) → `MetricDto` → UI formats them (`lib/format.ts`); the frontend computes no
financial result except the teaching examples in `frontend/portfolio-dashboard/src/lib/worked-examples.ts`.

## Changing or adding a formula safely
1. Find its entry (`grep -n "<File>.cs" docs/quant/formula-reference.md`); read conventions, edge cases, callers.
2. Write the test first, independent of the production path: a hand-computed value (`MetricsTests`) or a
   re-implementation from the published definition (`tests/PortfolioOptimizer.UnitTests/Optimization/IndependentFormulaTests.cs`).
   Use explicit tolerances (1e-12 closed form, ~1e-7 numerical gradients, solver results 1e-6).
3. Make the change in the pure project (`Optimization`/`MachineLearning`), not in a service or the UI.
4. Check every caller (`git grep -n "<Symbol>"`); one definition per quantity – do not add a fourth copy of monthly returns.
5. Update the entry (formula, `Code`, `Tests`, `Status`, `Severity` for defects, `Verified`), run
   `python3 scripts/quant/maintain.py index --write`, update the glossary/guide text that explains it.
6. Verify: `python3 scripts/quant/maintain.py validate` and `python3 scripts/quant/maintain.py test-quant`.

## Verifying a formula (audit procedure)
Reconstruct the definition from a reference (cite it in the entry) → compare with code: units, frequency, currency,
dates, denominators, annualization → edge cases (empty, n < 2, zero variance, constant prices, singular Σ, missing FX)
→ numerical stability (cancellation, ill-conditioning, tolerances) → point-in-time (only data dated ≤ decision) →
cross-layer consistency (DTO units, UI labels) → classify with the audit categories
(`docs/quant/audit-2026-10-28.md#classification`). A formula is never "verified" because it matches its own docs.

## Known limitations and open questions
See the risk register in `docs/quant/audit-2026-10-28.md` – notably R-01 (A = 252 on union calendars with Sunday
sessions), R-05 (Overview "Total return" vs benchmark), R-08 (duplicated monthly returns). No parametric VaR, factor
models or bootstrap confidence intervals exist.

## Related
`/maintain-knowledge` (tooling and CI), `/optimization-backtesting`, `/ml-pipeline`, `/market-data-fx`,
`/portfolio-accounting`, `/ship-change`; docs: `docs/quantitative-strategies.md`, `docs/quant/README.md`.
