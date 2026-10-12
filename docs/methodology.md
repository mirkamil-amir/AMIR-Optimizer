# Financial methodology & optimization assumptions

All rates, weights and returns are fractions (0.05 = 5%). The trading-day convention is **252 days per year**
(`AnalyticsConventions`), used everywhere: optimization, reporting and backtesting.

## Prices and returns

* **Adjusted close** (split- and dividend-adjusted, total return) is used for all return statistics. If a source
  provides no adjusted series (Alpha Vantage free tier) the raw close is used and this is flagged in diagnostics.
* **Raw close** (or the latest quote, if newer) is the price at which simulated trades execute and at which the paper
  portfolio is valued.
* Daily simple return: `r[t] = P[t] / P[t−1] − 1`. Log returns are available (`ReturnCalculator.LogReturns`).
* Multi-asset return matrices are aligned on the **intersection** of dates (no forward-filling, no pairwise-complete
  estimation) so the covariance matrix stays positive semi-definite. Dropped observations are reported.
* Sources are never mixed: all analytics read the active provider's series only. Simulated (mock) rows are flagged.

## Metrics

| Metric | Definition |
|---|---|
| Expected return (optimizer input) | mean(daily r) × 252 (arithmetic) |
| Annualized return (realized) | (Π(1+r))^(252/n) − 1 (geometric, CAGR) |
| Volatility | sample std (n−1) of daily returns × √252 |
| Sharpe | (mean(r) − rf/252) / std(r) × √252; ex-ante: (E[r] − rf) / σ |
| Sortino | (mean(r) − rf/252) / sqrt(mean(min(0, r − rf/252)²)) × √252 |
| Max drawdown | min over t of V[t] / max(V[0..t]) − 1 |
| Historical VaR (c) | −quantile(r, 1−c) (one day, linear interpolation); requires ≥ 1/(1−c) observations |
| CVaR / ES (c) | −mean(r | r ≤ quantile(r, 1−c)) |
| Beta | cov(rp, rb) / var(rb) |
| Tracking error | std(rp − rb) × √252 |
| Information ratio | mean(rp − rb) × 252 / tracking error |
| Turnover | one-way: ½ Σ |w_new − w_old| |
| Risk contribution | wᵢ (Σw)ᵢ / wᵀΣw (sums to 1) |

Undefined cases (insufficient history, zero volatility, zero tracking error…) return an explicit *unavailable* value
with a reason – never NaN.

The **risk-free rate** is a user assumption (default 4%), not observed market data.

## Covariance

* **Sample**: unbiased (n−1) estimator, annualized × 252.
* **Ledoit–Wolf (default)**: shrinkage towards a scaled identity (Ledoit & Wolf 2004); the shrinkage intensity is
  reported per run. It produces a well-conditioned matrix when the sample is short relative to the asset count.

## Optimization

Variables are asset weights `w` as fractions of **total portfolio value** (assets + cash).

Constraints (all linear): `Σw = 1 − cash reserve`, per-asset bounds `[min, max]` (long-only, short selling not
supported), sector caps `Σ_{i∈s} wᵢ ≤ cap`, and an optional turnover limit `½Σ|w − w₀| ≤ T` (linearized with auxiliary
variables; ignored when the portfolio holds only cash). Excluded symbols and assets with zero sample variance are
fixed at 0.

| Objective | Formulation | Exactness |
|---|---|---|
| Minimum variance | min wᵀΣw | exact convex QP |
| Target return | min wᵀΣw s.t. wᵀμ ≥ target | exact convex QP (checked against the max achievable return first) |
| Maximum Sharpe | Cornuejols–Tütüncü homogenization: min yᵀΣy s.t. (μ − rf/(1−c))ᵀy = 1, all constraints scaled by κ ≥ 0; w = y/κ | exact convex QP |
| Max return for volatility limit | bisection on the target return of the efficient frontier until σ(r) = limit | exact up to tolerance 1e-9 in return |
| Equal weight | 1/N of the invested fraction | reference portfolio |

Cash earns 0% inside the optimizer (conservative; Sharpe uses the full risk-free hurdle on the invested part).

**Solver.** `AdmmQpSolver` implements the OSQP algorithm (Stellato et al., 2020) densely with MathNet.Numerics
(MIT license): reduced KKT system with Cholesky factorization, over-relaxation (α = 1.6), adaptive ρ, primal/dual
infeasibility certificates, and **solution polishing** (active-set equality-constrained KKT solve with iterative
refinement), giving ~1e-9 accurate solutions for the problem sizes involved (tens of assets). Limitations: dense
linear algebra (fine up to a few hundred assets), no warm start.

**Non-convex limits (heuristic).** *Max holdings* and *minimum position size* are cardinality-type constraints. The
optimizer solves the convex problem, removes the smallest offending position, and re-solves until the limits hold.
The result is feasible and reported as such in diagnostics, but not guaranteed globally optimal (that would require
mixed-integer QP).

**Validation of every result.** Weights are cleaned (|w| < 1e-8 → 0, re-normalized within 1e-5) and then checked
against every constraint with tolerance 1e-6. Any violation is reported as a solver failure – arbitrary weights are
never returned. Infeasible configurations are detected before solving where possible (sum of minimum weights, sum of
maximum weights, holdings × max weight, sector capacities, target above maximum achievable return, volatility limit
below the minimum-variance volatility, no asset above the risk-free rate) and otherwise via the solver's primal
infeasibility certificate, and always come with an explanation.

**Efficient frontier.** Minimum-variance portfolios for 30 evenly spaced target returns between the GMV return and
the maximum achievable return under the base constraints (cardinality heuristics are not applied to the frontier).

**Reproducibility.** Each run stores the settings snapshot, universe, estimation window, observation count, price
source, and a SHA-256-based hash (16 hex chars) of the exact (symbol, date, price) observations used.

## From weights to orders

```
portfolio value V = cash + Σ qty × latest price          (assets without a price are excluded and reported)
target value      = V × w
target quantity   = floor(target value / price)          (or floor to 4 decimals with fractional shares)
order             = target − current quantity            (sells first, then buys by descending target value)
fee               = max(minimum fee, bps × trade value)
```

A buy that would exceed available cash after fees is reduced to the largest affordable quantity, so the plan never
over-spends. During a rebalance, positions whose weight is within the drift threshold of target are not traded.
Deviations from target (rounding, fees, missing or stale prices) are explained on the proposal.

## Accounting

Weighted-average cost: buy commissions are capitalized into the average cost; a (partial) sale realizes
`qty × price − sell fee − qty × average cost` and does not change the average cost of the remaining shares.
Dividends are **not** credited to the paper portfolio (its realized performance is price return; benchmark
comparisons in that view use adjusted closes, i.e. total return – documented as a limitation).

Portfolio performance uses daily values `cash + Σ qty × raw close`, with trades dated at the close of the price used,
and time-weighted returns that remove external cash flows.

## Backtesting

* Signal at the close of each rebalance date *s* (last trading day of the month/quarter; and the first day), using
  only adjusted closes dated ≤ *s* over the lookback window.
* Orders fill at the **next trading day's** adjusted close, net of proportional costs; holdings earn returns from
  that close onward. A unit test perturbs all prices after a signal date and asserts earlier decisions are unchanged.
* An asset becomes investable only when it has a full lookback window (no back-filling), so late listings enter
  later. If optimization fails at a rebalance, holdings are kept and the event is logged.
* Fractional units, cash at 0%, dividends reinvested through adjusted prices.
* Comparisons: buy-and-hold of the initial optimized allocation, equal-weight buy-and-hold, and the benchmark.
* **Survivorship bias:** the universe is today's symbol list, not a point-in-time constituent list.

Backtested and in-sample results are hypothetical and are not forecasts.
