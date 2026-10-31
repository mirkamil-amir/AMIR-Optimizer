# Quantitative formula reference

The canonical list of every significant formula MatrixView computes, with where it lives, how it is tested and what was
verified. `python3 scripts/quant/maintain.py validate` parses this file: each entry's metadata table must name existing
code symbols, tests and skills, and every file in the quantitative code paths
(`scripts/quant/manifest.json`) must be referenced by an entry or listed as formula-free. The generated index
[`formula-index.json`](formula-index.json) must be regenerated (`maintain.py index --write`) when an entry changes.

How to read an entry: **Status** uses the audit categories (see [audit](audit-2026-10-28.md#classification)); **Code** lists
`path#Symbol` (the symbol must appear in the file); **Tests** lists `Class.Method` (.NET) or a test file path; **Verified**
is the date of the last independent check. Conventions shared by all entries are in [Conventions](#conventions). All
quantities are fractions (0.05 = 5 %) unless stated otherwise.

## Conventions

* **Trading days per year:** $A = 252$ (`AnalyticsConventions.TradingDaysPerYear`), used for every annualization.
* **Returns:** daily simple returns of adjusted closes (total return) for statistics; raw closes for paper-portfolio
  valuation and simulated fills. A "daily" return spans consecutive observations of the date grid in use (intersection
  grid for estimation, union calendar for backtests), not necessarily consecutive calendar days.
* **Risk-free rate:** an annual user assumption $r_f$ (default 4 %), converted to a daily rate by simple division
  $r_f/A$ – not compounded; ex-ante figures use $r_f$ directly.
* **Sample statistics:** $n-1$ denominators (sample variance/covariance), except where an entry states otherwise
  (Sortino downside deviation, Ledoit–Wolf internals).
* **Currency:** every series that combines instruments is first converted into one analysis currency with dated ECB
  reference rates (F-FX-003); identity conversion when native = base.
* **Undefined values** are `MetricValue.Unavailable(reason)` / `null` with a reason – never NaN, ∞ or 0 as a stand-in.

---

## Returns and performance

### F-RET-001 · Simple daily return

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/ReturnCalculator.cs#SimpleReturns` |
| Tests | `MetricsTests.SimpleReturns_AreComputedFromConsecutivePrices`; `MetricsTests.SimpleReturns_RejectNonPositivePrices` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$r_t = P_t / P_{t-1} - 1$ for consecutive observations $P_{t-1}, P_t > 0$ of one series. Dimensionless, per observation
interval. Non-positive or non-finite prices throw (never skipped silently). Callers: every statistic below.

### F-RET-002 · Log return

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/ReturnCalculator.cs#LogReturns` |
| Tests | `MetricsTests.LogReturns_SumToLogOfTotalGrowth` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\ell_t = \ln(P_t/P_{t-1})$. Implemented and tested but **not used by any analysis** (all statistics use simple returns);
kept for teaching. Using it for portfolio aggregation would be wrong ($\ell_p \ne \sum w_i \ell_i$).

### F-RET-003 · Cumulative return and wealth index

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/ReturnCalculator.cs#CumulativeReturn`; `src/PortfolioOptimizer.Optimization/Statistics/ReturnCalculator.cs#WealthIndex` |
| Tests | `MetricsTests.CumulativeReturn_Compounds`; `DiversificationMathTests.History_DrawdownAndCumulativeReturn_ByHand` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$R_{0,T} = \prod_{t=1}^{T}(1+r_t) - 1$; wealth index $W_0 = 1$, $W_t = W_{t-1}(1+r_t)$. Holding-period return of a value
series $V$ is $V_T/V_0 - 1$, identical when $r_t = V_t/V_{t-1}-1$.

### F-RET-004 · Return-matrix alignment and common window

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/ReturnMatrix.cs#Build`; `src/PortfolioOptimizer.Optimization/Statistics/CommonWindow.cs#Choose` |
| Tests | `MetricsTests.ReturnMatrix_AlignsOnCommonDates`; `CommonWindowTests.RecentListing_IsLeftOut_SoTheOthersKeepTheirWindow`; `CommonWindowTests.StaleHistory_IsLeftOut`; `CommonWindowTests.LeavesOutOnlyAsFewAssetsAsNeeded` |
| Skills | quant-formulas; optimization-backtesting |
| Verified | 2026-10-28 |

Dates = intersection of all assets' observation dates; returns are computed on that grid, so each row spans the same
interval for every asset and the sample covariance stays PSD (no pairwise-complete estimation, no forward fill).
Assumption: across markets with different holidays a "daily" return can span two sessions for some assets.
`CommonWindow` first drops assets whose prices end > 10 days before the latest, then the fewest most recent listings
needed for a target window (binary search, monotone). Exclusions are reported, never filled.

### F-PERF-001 · Annualized arithmetic mean return (expected return input)

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#AnnualizedMeanReturn`; `src/PortfolioOptimizer.Optimization/Statistics/CovarianceEstimator.cs#Estimate` |
| Tests | `IndependentFormulaTests.SampleCovariance_IsSymmetricPsd_AndAnnualizedBy252`; `IndependentFormulaTests.ArithmeticAnnualReturn_IsAtLeastTheGeometric` |
| Skills | quant-formulas; optimization-backtesting |
| Verified | 2026-10-28 |

$\hat\mu_i = A \cdot \frac{1}{T}\sum_t r_{t,i}$. Annual units, arithmetic (exceeds the geometric rate by ≈ σ²/2). A
historical estimate with large sampling error (standard error ≈ σ/√years), labelled "not a forecast" in the UI.

### F-PERF-002 · Annualized geometric return (CAGR)

| Field | Value |
|---|---|
| Status | Potential defect requiring more evidence |
| Severity | Medium |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#AnnualizedReturn` |
| Tests | `MetricsTests.AnnualizedReturn_IsGeometric` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\text{CAGR} = \left(\prod_t (1+r_t)\right)^{A/n} - 1$, $n$ = number of returns. Time is counted in observations, not
calendar days: on a union calendar of several markets with more than 252 dates per year (e.g. Tel Aviv's Sunday sessions
before 2026 combined with Mon–Fri markets) the exponent overstates elapsed years and the CAGR is understated (risk R-01;
not measured on stored data yet). Unavailable for $n<2$ or total loss. Portfolio history shows it only from 20 returns.

### F-PERF-003 · Annualized volatility

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#AnnualizedVolatility`; `src/PortfolioOptimizer.Optimization/Statistics/Descriptive.cs#SampleVariance` |
| Tests | `MetricsTests.AnnualizedVolatility_UsesSampleStdAndSqrt252` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\sigma = \sqrt{A}\, s$, $s^2 = \frac{1}{n-1}\sum (r_t-\bar r)^2$. Square-root-of-time scaling assumes uncorrelated daily
returns (an assumption, not an identity). The factor assumes 252 observations a year (see R-01 for union calendars).

### F-PERF-004 · Sharpe ratio (realized)

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#SharpeRatio`; `src/PortfolioOptimizer.Optimization/Backtesting/StrategyMetrics.cs#RollingSharpe`; `src/PortfolioOptimizer.Optimization/AnalyticsConventions.cs#DailyRate` |
| Tests | `MetricsTests.Sharpe_MatchesManualCalculation`; `MetricsTests.Sharpe_WithZeroVolatility_IsUnavailable`; `IndependentFormulaTests.Sharpe_UsesSimpleDailyRiskFreeRate` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\text{SR} = \sqrt{A}\,\frac{\bar r - r_f/A}{s}$ – the period Sharpe ratio scaled by $\sqrt A$ (numerator × A, denominator
× √A: compatible units). Unavailable when $s < 10^{-14}$. Rolling version: same on a trailing window. Reference:
Sharpe (1994), "The Sharpe Ratio", *Journal of Portfolio Management* 21(1).

### F-PERF-005 · Ex-ante Sharpe ratio

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/PortfolioMath.cs#Sharpe` |
| Tests | `OptimizerTests.MaximumSharpe_InteriorSolution_MatchesTangencyPortfolio` |
| Skills | quant-formulas; optimization-backtesting |
| Verified | 2026-10-28 |

$(\mathbf w^\top\hat{\boldsymbol\mu} - r_f)/\sqrt{\mathbf w^\top\hat\Sigma\mathbf w}$ with annual inputs; null for σ ≤ 1e-12.
Cash ($1-\sum w$) earns 0, so the full $r_f$ hurdle applies to the invested part (conservative).

### F-PERF-006 · Sortino ratio

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#SortinoRatio` |
| Tests | `MetricsTests.Sortino_UsesDownsideDeviationOnly` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\sqrt{A}\,(\bar r - m)/\text{DD}$, $m = r_f/A$, $\text{DD} = \sqrt{\frac1n\sum_t \min(0, r_t-m)^2}$ over **all** $n$
observations (Sortino & Price 1994 target semideviation; population denominator by convention). Unavailable with no
downside.

### F-PERF-007 · Tracking error

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#TrackingError` |
| Tests | `IndependentFormulaTests.TrackingErrorAndInformationRatio_ByHand` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\text{TE} = \sqrt{A}\, s(r^p_t - r^b_t)$ on aligned series (throws when lengths differ).

### F-PERF-008 · Information ratio

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#InformationRatio` |
| Tests | `IndependentFormulaTests.TrackingErrorAndInformationRatio_ByHand` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\text{IR} = A\cdot\overline{(r^p-r^b)}/\text{TE}$ (arithmetic active return; unavailable when TE ≈ 0).

### F-PERF-009 · Beta

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#Beta` |
| Tests | `MetricsTests.Beta_OfScaledBenchmark_IsScale` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\beta = \widehat{\text{cov}}(r^p, r^b)/\widehat{\text{var}}(r^b)$ (OLS slope with intercept, both $n-1$). Unavailable for a
constant benchmark. No significance test is reported.

### F-PERF-010 · Drawdown and maximum drawdown

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#DrawdownSeries`; `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#MaxDrawdown` |
| Tests | `MetricsTests.MaxDrawdown_FindsPeakToTrough` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\text{DD}_t = V_t/\max_{s\le t} V_s - 1 \le 0$; $\text{MDD} = \min_t \text{DD}_t$ (negative fraction). Needs ≥ 2 values.

### F-PERF-011 · Time-weighted return of a portfolio's recorded history

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Application/Analytics/PortfolioAnalyticsService.cs#GetPerformanceAsync` |
| Tests | `MultiPortfolioTests.Performance_RemovesDepositsAndWithdrawals_ButCountsDividends`; `MultiCurrencyTests.Performance_UsesTheRateOfEachDay_AndMatchesTheHoldingsView` |
| Skills | portfolio-accounting; quant-formulas |
| Verified | 2026-10-28 |

$V_t = \text{cash}_t + \sum_i q_{i,t}\,P^{raw}_{i,t}\,X_{i\to base,t}$; $r_t = (V_t - F_t)/V_{t-1} - 1$ with external flows
$F_t$ (deposits +, withdrawals −) at the **end** of day $t$; chained by F-RET-003. Dividends and standalone fees are
returns, not flows. A day without a usable rate for a held position is skipped and its flow moves to the next valued
day (never valued with a guessed rate). Assumption: end-of-day flow timing (a flow-weighted Dietz split is not used).
Benchmark: adjusted closes (total return) while the portfolio receives only dividends the user records – the
comparison is biased against the portfolio by the dividend yield (documented in the methodology text).

### F-PERF-012 · Return on net contributions ("Total return" card)

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Application/Analytics/PortfolioAnalyticsService.cs#GetHoldingsAsync` |
| Tests | — |
| Skills | portfolio-accounting |
| Verified | 2026-10-28 |

$V_{now}/C - 1$ with $C = \sum$ deposits − withdrawals (account currency); 0 when $C \le 0$. Not time-weighted: with
later deposits it differs from F-PERF-011, yet it is shown next to the benchmark's price return since the first trade
(risk R-05). The UI hint states the definition. Gap: no test pins this formula.

### F-PERF-013 · Calendar-month returns

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Backtesting/StrategyMetrics.cs#MonthlyReturns`; `src/PortfolioOptimizer.Optimization/Backtesting/BacktestEngine.cs#MonthlyReturns`; `src/PortfolioOptimizer.Application/Analytics/PortfolioAnalyticsService.cs#MonthlyReturns` |
| Tests | `IndependentFormulaTests.MonthlyReturns_ChainToTheTotalReturn` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$V_{\text{last of month}}/V_{\text{last of previous month}} - 1$ (first month from the first value), so monthly returns chain
to the total return. Three copies of the same rule exist (duplication, risk R-08).

### F-PERF-014 · Excess annualized return over the benchmark

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Backtesting/StrategyMetrics.cs#Compute` |
| Tests | `StrategyComparisonTests.MathematicalVsMl_UsesSharedConventions_AndRanksFairly` |
| Skills | quant-formulas; optimization-backtesting |
| Verified | 2026-10-28 |

$\text{CAGR}_p - \text{CAGR}_b$ on the same dates (a difference of geometric rates, not the CAGR of active returns).

---

## Risk and statistics

### F-RISK-001 · Historical Value at Risk

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#HistoricalVaR`; `src/PortfolioOptimizer.Optimization/Statistics/Descriptive.cs#Quantile` |
| Tests | `MetricsTests.HistoricalVaRAndCVaR_UseEmpiricalTail`; `MetricsTests.VaR_WithTooFewObservations_IsUnavailable`; `IndependentFormulaTests.HistoricalVaR_UsesType7Quantile` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\text{VaR}_c = -Q_{1-c}(r)$, one day, positive loss fraction; $Q$ = empirical quantile type 7 (Hyndman & Fan 1996,
"Sample Quantiles in Statistical Packages", *The American Statistician* 50(4)): $h=(n-1)p$, linear interpolation.
Requires $n \ge \lceil 1/(1-c)\rceil$ (20 for 95 %). Historical simulation; no parametric VaR is implemented.

### F-RISK-002 · Historical expected shortfall (CVaR)

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#HistoricalCVaR` |
| Tests | `MetricsTests.HistoricalVaRAndCVaR_UseEmpiricalTail`; `IndependentFormulaTests.HistoricalVaR_UsesType7Quantile` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$-\text{mean}\{r_t : r_t \le Q_{1-c}\}$. With an interpolated quantile the tail holds $\lfloor h\rfloor+1$ observations,
not exactly $(1-c)n$ (small-sample convention; Acerbi–Tasche weighting is not used).

### F-RISK-003 · Risk contributions (Euler allocation)

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/PortfolioMath.cs#RiskContributions` |
| Tests | `OptimizerTests.RiskContributions_SumToOne`; `IndependentFormulaTests.RiskContributions_SatisfyEulerIdentity_ForNumericalGradient` |
| Skills | quant-formulas; optimization-backtesting |
| Verified | 2026-10-28 |

$\text{RC}_i = w_i(\Sigma\mathbf w)_i/(\mathbf w^\top\Sigma\mathbf w)$, $\sum_i \text{RC}_i = 1$; $\text{RC}_i\,\sigma_p =
w_i\,\partial\sigma_p/\partial w_i$. Negative = diversifying. Null when variance < 1e-16.

### F-RISK-004 · Diversification ratio and effective risk contributors

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/DiversificationMath.cs#Risk` |
| Tests | `DiversificationMathTests.Risk_UncorrelatedPair_MatchesClosedForm`; `DiversificationMathTests.Risk_PerfectlyCorrelated_HasNoDiversificationBenefit` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

$\text{DR} = \sum_i w_i\sigma_i / \sigma_p \ge 1$ (Choueifaty & Coignard 2008); effective contributors $1/\sum \text{RC}_i^2$
(null if any share is negative). Average/highest pairwise correlation over held positions (w ≥ 0.5 %).

### F-RISK-005 · Concentration: HHI and effective holdings

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/DiversificationMath.cs#Concentration`; `src/PortfolioOptimizer.Optimization/Backtesting/StrategyMetrics.cs#Compute` |
| Tests | `DiversificationMathTests.Concentration_UsesInvestedWeights_HhiAndEffectiveHoldings`; `DiversificationMathTests.EqualWeights_HaveEffectiveHoldingsEqualToN` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

On invested weights normalized to 1: $\text{HHI} = \sum \tilde w_i^2$, effective holdings $1/\text{HHI}$; backtests use the
same quantity $(\sum w)^2/\sum w^2$ without normalizing first (identical).

### F-RISK-006 · Correlation under stress

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/DiversificationMath.cs#CorrelationUnderStress` |
| Tests | `DiversificationMathTests.CorrelationUnderStress_DetectsCorrelationThatRisesOnBadDays`; `DiversificationMathTests.CorrelationUnderStress_NeedsEnoughData` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

Average pairwise Pearson correlation on all days vs. on the days in the worst decile of the equal-weight basket (≥ 40
days, ≥ 20 stress days). Conditioning on the basket's tail biases correlations upward by construction (a known
selection effect, labelled "Estimate" with a caution in the reality check).

### F-RISK-007 · Fixed-weight historical profile (worst day, worst 20 days)

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/DiversificationMath.cs#History`; `src/PortfolioOptimizer.Optimization/Portfolio/DiversificationMath.cs#PortfolioReturns`; `src/PortfolioOptimizer.Application/Optimization/OptimizationService.cs#Cumulative` |
| Tests | `DiversificationMathTests.History_DrawdownAndCumulativeReturn_ByHand`; `DiversificationMathTests.CashReserve_EarnsNothing` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

$r^p_t = \sum_i w_i r_{t,i}$ (daily rebalanced to fixed weights; cash earns 0), worst rolling 20-observation return
$\min_t W_t/W_{t-20}-1$. In-sample when applied to the estimation window (labelled so).

### F-RISK-008 · Current-holdings risk (historical simulation)

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Application/Analytics/PortfolioAnalyticsService.cs#GetRiskAsync` |
| Tests | `ApiTests.FullWorkflow_OptimizeProposeExecute_UpdatesHoldingsAndLedger` |
| Skills | portfolio-accounting; quant-formulas |
| Verified | 2026-10-28 |

Weights $w_i = \text{market value}_i/\text{total value}$ (cash at zero risk) applied to the trailing window's converted daily
returns, then F-PERF-003/004, F-RISK-001/002/003, F-PERF-007/008/009 and sample covariance. Holdings without history
are dropped from the weights (warning), so risk is understated by their share. Coverage: only a smoke call.

### F-COV-001 · Sample covariance (annualized)

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/CovarianceEstimator.cs#Estimate`; `src/PortfolioOptimizer.Optimization/Statistics/Descriptive.cs#SampleCovariance` |
| Tests | `MetricsTests.SampleCovariance_MatchesManualCalculation`; `IndependentFormulaTests.SampleCovariance_IsSymmetricPsd_AndAnnualizedBy252` |
| Skills | quant-formulas; optimization-backtesting |
| Verified | 2026-10-28 |

$\hat\Sigma = \frac{A}{T-1}\,X_c^\top X_c$ on the T×N centered daily return matrix; symmetrized exactly; PSD by
construction (rank ≤ T−1, so singular when N ≥ T – the optimizer copes, Ledoit–Wolf is the default).

### F-COV-002 · Ledoit–Wolf shrinkage

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/CovarianceEstimator.cs#LedoitWolf` |
| Tests | `MetricsTests.LedoitWolf_ShrinkageIsBoundedAndKeepsMatrixPositiveDefinite`; `IndependentFormulaTests.LedoitWolf_MatchesThePaperWithNormalizedNorm` |
| Skills | quant-formulas; optimization-backtesting |
| Verified | 2026-10-28 |

Ledoit & Wolf (2004), "A well-conditioned estimator for large-dimensional covariance matrices", *J. Multivariate
Analysis* 88(2): $S = X_c^\top X_c/T$, $m = \text{tr}(S)/N$, $d^2 = \|S-mI\|^2$,
$\bar b^2 = \frac{1}{T^2}\sum_k \|x_kx_k^\top - S\|^2$, $\delta = \min(\bar b^2,d^2)/d^2$,
$\hat\Sigma = \frac{T}{T-1}\,A\,(\delta mI + (1-\delta)S)$. The code uses the plain Frobenius norm; the ratio δ is
invariant to the paper's 1/N normalization (checked). The $T/(T-1)$ rescaling (so δ = 0 equals F-COV-001) is an
implementation choice. Target: scaled identity (not constant correlation).

### F-COV-003 · Correlation from covariance

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/CovarianceEstimator.cs#ToCorrelation` |
| Tests | `IndependentFormulaTests.SampleCovariance_IsSymmetricPsd_AndAnnualizedBy252` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

$\rho_{ij} = \Sigma_{ij}/\sqrt{\Sigma_{ii}\Sigma_{jj}}$ clamped to [−1, 1]; 0 for a zero-variance asset; diagonal 1.

---

## Portfolio construction and optimization

### F-OPT-001 · Portfolio expected return, variance and volatility

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/PortfolioMath.cs#ExpectedReturn`; `src/PortfolioOptimizer.Optimization/Portfolio/PortfolioMath.cs#Variance`; `src/PortfolioOptimizer.Optimization/Portfolio/PortfolioMath.cs#Volatility` |
| Tests | `OptimizerTests.MinimumVariance_TwoAssets_MatchesClosedForm`; `DiversificationMathTests.Risk_UncorrelatedPair_MatchesClosedForm` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

$\mu_p = \mathbf w^\top\boldsymbol\mu$, $\sigma_p^2 = \mathbf w^\top\Sigma\mathbf w$ (floored at 0 against rounding), over the
period of Σ (annual). Weights are fractions of total value; cash $1-\sum w$ has zero return and risk.

### F-OPT-002 · Minimum-variance QP and constraint set

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#SolveMinVariance`; `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#ProblemSpec`; `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#PreCheckFeasibility`; `src/PortfolioOptimizer.Optimization/Portfolio/OptimizationModels.cs#PortfolioConstraints` |
| Tests | `OptimizerTests.MinimumVariance_InteriorSolution_MatchesAnalyticalGmv`; `OptimizerTests.SectorLimit_IsRespected`; `OptimizerTests.TurnoverLimit_IsRespectedAgainstCurrentWeights`; `OptimizerTests.MaxWeightTooSmall_IsInfeasibleWithExplanation`; `OptimizerTests.ConflictingSectorLimits_AreInfeasible` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

$\min \mathbf w^\top\Sigma\mathbf w$ s.t. $\sum w = 1-c$, $\ell_i \le w_i \le u_i$ (long only, $\ell \ge 0$), sector caps
$\sum_{i\in s} w_i \le \kappa_s$, turnover $\tfrac12\sum|w-w_0| \le \tau$ linearized with $t_i \ge \pm(w_i - w_{0,i})$,
$\sum t \le 2\tau$ (applied only when some asset of the universe is held). Excluded and zero-variance assets fixed at 0.
Pre-checks: Σ lower bounds, Σ upper bounds, top-K capacity, sector capacities.

### F-OPT-003 · Maximum Sharpe (homogenized QP)

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#SolveMaxSharpe` |
| Tests | `OptimizerTests.MaximumSharpe_InteriorSolution_MatchesTangencyPortfolio`; `OptimizerTests.MaximumSharpe_IsAtLeastAsGoodAsEveryFrontierPoint`; `OptimizerTests.MaxSharpe_WhenNoAssetBeatsRiskFree_IsInfeasible`; `IndependentFormulaTests.MaximumSharpe_WithCashReserve_BeatsEveryFeasibleMix` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

Cornuéjols & Tütüncü, *Optimization Methods in Finance* (2007), §8.2: with $y = w/\kappa$,
$\min y^\top\Sigma y$ s.t. $(\boldsymbol\mu - \tfrac{r_f}{1-c}\mathbf 1)^\top y = 1$, every row $\ell \le a^\top x \le u$ becomes
$\ell\kappa \le a^\top y \le u\kappa$, $\kappa \ge 0$; $w = y/\kappa$. Since $\sum w = 1-c$, the normalization equals
$\mu^\top w - r_f > 0$. Undefined (infeasible with explanation) when no feasible portfolio beats $r_f$.

### F-OPT-004 · Target return

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#SolveObjective` |
| Tests | `OptimizerTests.TargetReturn_IsMetWithMinimumVariance`; `OptimizerTests.TargetReturn_AboveMaximumAchievable_IsInfeasibleWithExplanation` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

F-OPT-002 plus $\mu^\top w \ge R^*$, after checking $R^* \le \max \mu^\top w$ (F-OPT-006).

### F-OPT-005 · Maximum return for a volatility limit

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#SolveMaxReturnForRisk` |
| Tests | `OptimizerTests.MaxReturnForRisk_RespectsVolatilityLimitAndBinds`; `OptimizerTests.MaxReturnForRisk_BelowMinimumVolatility_IsInfeasible` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

Bisection (≤ 60 steps, 1e-9 in return) on $R$ over $[\mu_{GMV}, \mu_{max}]$ of the min-variance frontier; valid because
$\sigma(R)$ is non-decreasing above the GMV return for convex feasible sets. Infeasible below the GMV volatility.

### F-OPT-006 · Maximum expected return (greedy LP)

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#GreedyMaxReturn`; `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#SolveMaxReturn` |
| Tests | `OptimizerTests.MaximumReturn_IsTheExactLinearProgramOptimum_WithBoundsAndSectorCaps` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

Start at lower bounds, fill the budget in descending μ up to asset and (disjoint) sector caps – optimal for this
laminar structure. With turnover rows, a QP with $\epsilon\,w^\top\Sigma w$ ($\epsilon = 10^{-6}$) is used instead.

### F-OPT-007 · Equal-weight reference

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#EqualWeights` |
| Tests | `OptimizerTests.CashReserve_ReducesInvestedFraction` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

$w_i = (1-c)/N_{eligible}$. Not repaired to meet caps: if 1/N violates a bound or sector cap the run reports
"infeasible" (the rule is fixed by definition). Max-holdings/min-position limits do not apply to it.

### F-OPT-008 · Cardinality heuristic (max holdings, minimum position)

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#Optimize` |
| Tests | `OptimizerTests.MaxHoldings_LimitsPositionCount` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

Non-convex: solve, remove the smallest offending position (lower bound 0 only), re-solve, ≤ N rounds. Feasible, not
globally optimal (MIQP not implemented); each removal is a diagnostic.

### F-OPT-009 · Efficient frontier

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#EfficientFrontier` |
| Tests | `OptimizerTests.EfficientFrontier_IsMonotone` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

Min-variance portfolios for evenly spaced targets in $[\mu_{GMV}, \mu_{max}]$ (warm-started, looser ADMM tolerance then
polish); points that do not converge are left out (display only).

### F-OPT-010 · Weight cleaning, verification and independent validation

| Field | Value |
|---|---|
| Status | Defect confirmed – fixed |
| Severity | Low |
| Code | `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#Clean`; `src/PortfolioOptimizer.Optimization/Portfolio/MeanVarianceOptimizer.cs#Verify`; `src/PortfolioOptimizer.Optimization/Portfolio/AllocationValidator.cs#Validate` |
| Tests | `AllocationValidatorTests.ViolatingCandidates_AreRejected_NotRepaired`; `AllocationValidatorTests.TheOptimizersOwnResults_PassIndependentValidation`; `AllocationValidatorTests.TurnoverLimit_WithoutHoldingsInTheUniverse_IsNotApplied_LikeTheOptimizer` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

Clean: $|w| < 10^{-8} \to 0$, clamp to bounds, rescale to $1-c$ when within 1e-5. Verify (tolerance 1e-6) and the separate
`AllocationValidator` reject – never repair – violations. **Q-02 (fixed):** the validator applied the turnover limit
when the current weights were all zero (holdings only outside the universe) while the optimizer, by design, did not:
every such run failed validation. Both now apply it only when an asset of the universe is held. Open (R-07): the
1e-5 rescaling window and `SolvedInaccurate` solutions (residuals ≤ 1e-5, accepted when polishing fails) exceed the 1e-6
verification tolerance, so such a solve near a cap fails the run instead of being cleaned (fails safe); not observed
with polished solutions (~1e-9).

### F-OPT-011 · QP solver (ADMM / OSQP)

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Optimization/Solvers/AdmmQpSolver.cs#Solve`; `src/PortfolioOptimizer.Optimization/Solvers/QuadraticProgram.cs#QuadraticProgram` |
| Tests | `OptimizerTests.QpSolver_SolvesSimpleBoxProblem`; `OptimizerTests.QpSolver_DetectsInfeasibleConstraints`; `OptimizerTests.HighlyCorrelatedAssets_ProduceValidWeights` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

$\min \tfrac12 x^\top Px + q^\top x$ s.t. $l \le Ax \le u$; Stellato et al. (2020), "OSQP: an operator splitting solver for
quadratic programs", *Math. Programming Computation* 12: dense KKT with Cholesky, α = 1.6, adaptive ρ, infeasibility
certificates, active-set polish. The solver internals were not re-derived in this audit (tests check results against
closed forms and an independent validator).

---

## Backtesting and strategy comparison

### F-BT-001 · Signal dates and point-in-time estimation

| Field | Value |
|---|---|
| Status | Defect confirmed – fixed |
| Severity | Medium |
| Code | `src/PortfolioOptimizer.Optimization/Backtesting/BacktestEngine.cs#Signal`; `src/PortfolioOptimizer.Optimization/Backtesting/PortfolioSimulator.cs#SignalIndices`; `src/PortfolioOptimizer.Optimization/Backtesting/PortfolioSimulator.cs#StaleAfterDays` |
| Tests | `BacktestTests.Backtest_HasNoLookAheadBias`; `BacktestTests.Backtest_ExcludesAssetsWithoutFullLookback`; `ExchangeCalendarTests.SignalOnAHoliday_KeepsTheClosedMarketInTheOptimization` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

Signals at the first calendar day and each last trading day of a month/quarter of the union calendar; estimation on
the last $L+1$ observations dated ≤ s (F-PERF-001, F-COV-002) of assets with a full lookback. **Q-01b (fixed):** an asset
whose exchange was closed on s was dropped from the optimization (and then sold); it now uses its latest close ≤ s
unless that is more than `StaleAfterDays` = 10 calendar days old.

### F-BT-002 · Execution, costs and valuation

| Field | Value |
|---|---|
| Status | Defect confirmed – fixed |
| Severity | High |
| Code | `src/PortfolioOptimizer.Optimization/Backtesting/PortfolioSimulator.cs#Run`; `src/PortfolioOptimizer.Optimization/Backtesting/PortfolioSimulator.cs#MaxExecutionDelayDays` |
| Tests | `BacktestTests.Backtest_ProducesConsistentSeriesAndMetrics`; `ExchangeCalendarTests.ExecutionOnAHoliday_WaitsForTheExchange_InsteadOfSellingIntoCash`; `ExchangeCalendarTests.LongClosure_TradesAroundTheClosedExchange_KeepingItsPositionAndNeverBorrowing`; `ExchangeCalendarTests.StoppedPrices_AreTreatedAsNoLongerTrading_AndSoldAtTheLastPrice`; `ValidationAndLeakageTests.Experiment_TransactionCostsAreAppliedToEveryStrategy` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

At the close of the execution day $e > s$: $V = \text{cash} + \sum q_iP_i$; costs $C = b\sum_i |\,\text{scale}\cdot(V-C)\,w_i - q_iP_i|$
(fixed point, 3 passes, $b$ = bps/10⁴); $q_i = \text{scale}(V-C)w_i/P_i$; cash = residual ≥ 0. **Q-01 (fixed, high):** on a
day when one exchange is closed (union calendar of several markets – e.g. 1 May in Europe while the US trades) the old
code sold that market's positions at the previous close and kept the money in cash until the next rebalance. Now the
rebalance waits up to 5 trading days for all involved assets to trade; afterwards closed positions keep their units
(nothing trades on a closed exchange), the others are scaled to fit (`scale` ≤ 1). Measured on a two-market
fixture: the old rule left 50 % in cash for May and ended May at 111,724 instead of 114,394 (see audit). Prices
missing for > 10 days count as "no longer trading" (sold at the last price). Valuation carries the last price forward.

### F-BT-003 · Benchmark and buy-and-hold series

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Backtesting/BacktestEngine.cs#BenchmarkSeries`; `src/PortfolioOptimizer.Optimization/Backtesting/StrategyMetrics.cs#BuyAndHold` |
| Tests | `BacktestTests.Backtest_ProducesConsistentSeriesAndMetrics`; `BacktestTests.BuyAndHold_HasSingleAllocation` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

$V_t = K\,P_{b,t}/P_{b,0}$ with the last price carried forward on dates without one; omitted with a warning when the
benchmark has no price on the first date.

### F-BT-004 · One-way turnover and implementation cost estimate

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Statistics/PerformanceMetrics.cs#Turnover`; `src/PortfolioOptimizer.Application/Analytics/RealityCheckService.cs#AnalyzeAsync` |
| Tests | `MetricsTests.Turnover_IsHalfSumOfAbsoluteChanges`; `RealityCheckTests.Report_IsConsistentWithTheRun_AndLabelsEveryFinding` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

$T = \tfrac12\sum_i|w_i' - w_i|$; proportional cost of a rebalance ≈ $2T\cdot b$ of value (sell T, buy T), consistent with
F-BT-002's $b\sum|\Delta|$.

### F-BT-005 · Strategy statistics shared by backtests and comparisons

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Optimization/Backtesting/StrategyMetrics.cs#Compute`; `src/PortfolioOptimizer.Optimization/Backtesting/StrategyMetrics.cs#DailyReturns`; `src/PortfolioOptimizer.Application/Comparisons/StrategyComparisonEngine.cs#Run` |
| Tests | `StrategyComparisonTests.MathematicalVsMl_UsesSharedConventions_AndRanksFairly`; `StrategyComparisonTests.MathematicalVsMathematical_AndMlVsMl_AreComparable` |
| Skills | optimization-backtesting |
| Verified | 2026-10-28 |

Every strategy of a comparison runs through the same simulator, calendar, costs and F-PERF/F-RISK functions; VaR/ES only
from 20 returns. Daily returns of a value series are 0 when the previous value is ≤ 0.

---

## Market data and currencies

### F-FX-001 · Rate convention, inverse and cross rates

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Application/Fx/FxTable.cs#TryRate`; `src/PortfolioOptimizer.Application/Fx/FxTable.cs#FxQuote` |
| Tests | `FxTableTests.Convention_IsTargetUnitsPerSourceUnit`; `FxTableTests.DirectAndInverse_AreConsistent`; `FxTableTests.CrossRates_GoThroughTheEuro_AndChainExactly`; `FxTableTests.Identity_IsOneByDefinition` |
| Skills | market-data-fx |
| Verified | 2026-10-28 |

ECB publishes units per EUR $e_X$. $\text{rate}(A\to B, d) = e_B(d)/e_A(d)$ = units of B per unit of A, so
$\text{amount}_B = \text{amount}_A\times\text{rate}$; inverse and triangular consistency hold exactly (same observations).

### F-FX-002 · Dated rate and carry-forward policy

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Application/Fx/FxTable.cs#Leg`; `src/PortfolioOptimizer.Application/Fx/FxTable.cs#DefaultMaxCarryForwardDays` |
| Tests | `FxTableTests.Weekend_UsesThePreviousObservation_AndSaysSo`; `FxTableTests.MissingOrTooOldRates_AreUnavailable_NeverOne`; `FxTableTests.CrossRate_ReportsTheOlderLeg` |
| Skills | market-data-fx |
| Verified | 2026-10-28 |

Latest observation on or before $d$, at most 4 calendar days old; otherwise unavailable (never 1.0 or today's rate).
Assumption: the ECB fixing (~16:00 CET) of day $d$ is used for closes of day $d$ – for Asian markets that close before
16:00 CET the rate is published hours after the close (a small intraday timing mismatch, documented in limitations).

### F-FX-003 · Price series conversion and FX return identity

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Application/Fx/FxService.cs#Convert`; `src/PortfolioOptimizer.Application/Fx/FxService.cs#MaxDroppedShare` |
| Tests | `CurrencyConverterTests.UsdStock_InEurPortfolio_UsesTheRateOfEachDate_NotTodays`; `CurrencyConverterTests.BaseReturn_EqualsLocalReturnTimesFxReturn`; `CurrencyConverterTests.PointsWithoutRates_AreDroppedAndCounted_NotFilled`; `CurrencyConverterTests.SameCurrency_IsReturnedUnchanged_SoConversionCannotHappenTwice`; `MultiCurrencyTests.HistoricalConversion_UsesStoredDatedRates_NotTodaysRate` |
| Skills | market-data-fx |
| Verified | 2026-10-28 |

$P^{base}_t = P^{native}_t\cdot\text{rate}(native\to base, t)$, hence $1+r^{base} = (1+r^{native})(1+r^{fx})$. Points
without a rate are dropped and counted by cause; a symbol with > 5 % dropped is excluded with the reason.

### F-MD-001 · Price normalization at ingestion

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Application/MarketData/PriceNormalizer.cs#Normalize` |
| Tests | `PriceNormalizerTests.RejectsInvalidRows_AndCollapsesDuplicates`; `PriceNormalizerTests.RoundsRawPricesToFourDecimals` |
| Skills | market-data-fx |
| Verified | 2026-10-28 |

Rejects missing/non-positive/non-finite closes, invalid adjusted closes, high < low and future dates; duplicate dates
collapse (last wins); raw prices rounded to 4 decimals, adjusted closes to 6. Assumption: 6 decimals limit the relative
precision of very small back-adjusted prices (e.g. an adjusted close of 0.05 → ±1e-5 relative, ≈ 0.1 bp on a daily
return). Adjustments themselves come from the provider; the app does not compute split/dividend factors.

### F-MD-002 · Price-history range change

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Application/MarketData/PriceHistory.cs#PriceHistory`; `src/PortfolioOptimizer.Application/MarketData/PriceHistory.cs#Downsample`; `src/PortfolioOptimizer.Application/MarketData/StockDiscoveryService.cs#GetTrendsAsync` |
| Tests | `PriceHistoryTests.OneMonth_UsesLastObservationOnOrBeforeTheStart_AsBase`; `PriceHistoryTests.AdjustedCloses_AreUsed_SoASplitIsNotALoss`; `PriceHistoryTests.MissingAdjustedClose_FallsBackToRawCloses_ForTheWholeSlice_AndSaysSo`; `PriceHistoryTests.Sparkline_KeepsFirstAndLastObservation`; `WatchlistTests.StockTrends_MatchTheWatchlist_ForOnePageOfStocks` |
| Skills | market-data-fx |
| Verified | 2026-11-01 |

$P_{end}/P_{base} - 1$ with base = last bar on or before end − range, one price kind for the whole slice. The same slice
serves the stock chart, the watchlist and the stock list's trend columns (`GET /stocks/trends`); their sparklines are the
slice's points downsampled evenly to at most 60, always keeping the first and last observation (display only – the
change uses the full slice). The stock list's change equals the watchlist's for the same stock and range (tested).

### F-MD-003 · Event price comparison (news)

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Application/News/EventPriceComparison.cs#Compute` |
| Tests | `EventPriceComparisonTests.DuringTheSession_ReferenceIsThePreviousClose`; `EventPriceComparisonTests.AfterTheClose_ReferenceIsThatDaysClose`; `EventPriceComparisonTests.MissingBenchmarkDate_GivesNoDifference_InsteadOfASubstitute` |
| Skills | market-data-fx |
| Verified | 2026-10-28 |

Stock change $P_{t+h}/P_{ref}-1$ minus benchmark change on the same dates, reference = last close before publication
(regular session hours, no holiday calendar). A market-adjusted difference, not an abnormal return; no causal claim.

---

## Portfolio accounting and trading

### F-TRD-001 · Trade proposal sizing

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Application/Trading/TradeProposalCalculator.cs#Compute`; `src/PortfolioOptimizer.Application/Trading/TradeProposalCalculator.cs#MaxAffordable` |
| Tests | `TradeProposalCalculatorTests.WholeShares_AreFlooredAndCashIsConsistent`; `TradeProposalCalculatorTests.Fees_NeverCauseNegativeCash`; `TradeProposalCalculatorTests.Sells_FundBuys_AndExitedPositionsAreSold`; `TradeProposalCalculatorTests.DriftThreshold_SkipsSmallRebalances`; `TradeProposalCalculatorTests.MissingPrice_IsReportedAndNotTraded` |
| Skills | portfolio-accounting |
| Verified | 2026-10-28 |

$V = \text{cash} + \sum q_iP_iX_i$ (priced assets only); target $q_i^* = \lfloor V w_i/(P_iX_i)\rfloor$ (or 4 decimals); sells
first, buys by descending target value, each capped at the largest affordable quantity
$q \le \min\left(\frac{\text{cash}}{P(1+b)}, \frac{\text{cash}-f_{min}}{P}\right)$, then decremented until it fits.

### F-TRD-002 · Fee model

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Domain/Portfolio.cs#TransactionCostModel` |
| Tests | `TradeProposalCalculatorTests.MinimumFee_Applies` |
| Skills | portfolio-accounting |
| Verified | 2026-10-28 |

$f = \max(\text{round}_2(|\text{value}|\cdot b), f_{min})$ for a positive trade value, else 0.

### F-TRD-003 · Weighted-average cost and realized P&L

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.Domain/Portfolio.cs#AverageCost`; `src/PortfolioOptimizer.Domain/Portfolio.cs#RealizedPnl` |
| Tests | `PortfolioAccountingTests.Buy_CapitalizesFeesIntoAverageCost`; `PortfolioAccountingTests.PartialSell_RealizesPnlAndKeepsAverageCost`; `PortfolioAccountingTests.FullSell_ResetsAverageCost_AndIncrementsVersion`; `PortfolioCashTests.Amounts_AreRoundedToStoredPrecision_SoTheLedgerReconciles` |
| Skills | portfolio-accounting |
| Verified | 2026-10-28 |

Buy: $\bar c' = (q\bar c + q_bP + f)/(q+q_b)$ (fees capitalized). Sell: realized $= q_sP - f - q_s\bar c$, $\bar c$ unchanged,
reset to 0 when flat. Money rounded to the stored precision before it changes cash.

### F-TRD-004 · Current valuation

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.Application/Portfolios/PortfolioValuationService.cs#ValueAsync` |
| Tests | `MultiCurrencyTests.ChangingTheReportingCurrency_RecalculatesValues_WithoutChangingHoldings`; `MultiPortfolioTests.Overview_ConvertsPortfoliosInOtherCurrencies_WithADatedRate`; `MissingFxTests` |
| Skills | portfolio-accounting; market-data-fx |
| Verified | 2026-10-28 |

$V = \text{cash}\cdot X_{base\to ccy}(\text{today}) + \sum_i q_iP_i\,X_{i\to ccy}(d_i)$ with each position's latest price $P_i$
dated $d_i$ and the rate of that date. A missing **rate** makes the market value and total `null` (problem listed); a
holding with **no price at all** is left out of the market value with a warning (the total is then understated – the
warning is the only signal, risk R-06). Stale prices are flagged, not excluded.

---

## Machine learning

### F-ML-001 · Features

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.MachineLearning/Features/FeatureEngine.cs#FeatureEngine`; `src/PortfolioOptimizer.MachineLearning/Features/FeatureCatalog.cs#FeatureCatalog` |
| Tests | `FeatureAndLabelTests.Momentum_MovingAverage_AndDrawdown_MatchHandComputation`; `FeatureAndLabelTests.BetaAndCorrelation_AreOneForAScaledCopyOfTheBenchmark`; `FeatureAndLabelTests.SectorRelativeMomentum_UsesSameDatePeerMedian`; `FeatureAndLabelTests.Features_NeverUseFutureObservations`; `FeatureAndLabelTests.Features_AreMissing_UntilTheWindowIsComplete` |
| Skills | ml-pipeline |
| Verified | 2026-10-28 |

20 features (`FeatureCatalog.All`, version `features-2026.10-v1`): k-day returns, 12-1 momentum $P_{t-21}/P_{t-252}-1$,
SMA gaps, annualized vol/downside vol (√252), drawdown from the 126-day high, max daily return, beta/correlation to the
benchmark (126 days, $n-1$), relative/log dollar volume, sector-relative momentum (same-date peer median, ≥ 3), market
trend/vol. Every value at $t$ reads indices ≤ $t$; windows need ≥ 90 % valid returns. Variance uses the one-pass
formula $(\sum r^2 - n\bar r^2)/(n-1)$, numerically adequate for daily returns (|mean| ≪ sd).

### F-ML-002 · Labels

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.MachineLearning/Labels/Targets.cs#Compute`; `src/PortfolioOptimizer.MachineLearning/Labels/Targets.cs#Describe`; `src/PortfolioOptimizer.MachineLearning/Data/Dataset.cs#Build` |
| Tests | `FeatureAndLabelTests.Labels_UseExactlyTheNextHorizon_AndAreMissingBeyondTheData`; `FeatureAndLabelTests.Dataset_RecordsLabelAvailability_AndKeepsTheLatestDateUnlabeled` |
| Skills | ml-pipeline |
| Verified | 2026-10-28 |

$y = P_{t+h}/P_t - 1$; excess: minus $B_{t+h}/B_t - 1$; outperform: $\mathbb 1[\text{stock} > \text{bench}]$; forward vol:
√252 × sample sd of returns $t+1..t+h$. Label available at index $t+h$. The benchmark has a price on every panel date
(the calendar is the benchmark's), so the binary label is never formed from a missing benchmark.

### F-ML-003 · Panel alignment and short-gap forward fill

| Field | Value |
|---|---|
| Status | Potential defect requiring more evidence |
| Severity | Low |
| Code | `src/PortfolioOptimizer.MachineLearning/Data/MarketPanel.cs#Build` |
| Tests | `FeatureAndLabelTests.Panel_ForwardFillsShortGapsOnly_AndNeverBackfills` |
| Skills | ml-pipeline |
| Verified | 2026-10-28 |

Benchmark calendar; interior gaps ≤ 5 days filled with the previous close. The fill **values** are past information, but
whether a gap counts as "interior" depends on a later observation existing: a stock that stops trading keeps NaN, one
that resumes is filled – sample availability inside a gap uses future knowledge (risk R-03). Impact is limited to
decision dates inside ≤ 5-day gaps; changing it alters past experiments and needs a `FeatureCatalog.Version` bump.

### F-ML-004 · Chronological split with purging and embargo

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.MachineLearning/Validation/ChronologicalSplitter.cs#Split`; `src/PortfolioOptimizer.MachineLearning/Validation/ChronologicalSplitter.cs#TrainAndValidation`; `src/PortfolioOptimizer.MachineLearning/Models/ModelContracts.cs#FeaturePreprocessor` |
| Tests | `ValidationAndLeakageTests.Split_IsChronological_WithPurgedBoundaries`; `ValidationAndLeakageTests.ModelSelection_DoesNotDependOnTheTestPeriod`; `ValidationAndLeakageTests.Preprocessing_IsFittedOnTrainingRowsOnly` |
| Skills | ml-pipeline |
| Verified | 2026-10-28 |

Decision dates split 60/20/20 by time; a sample stays in an earlier period only if $t+h <$ next period start − embargo
(López de Prado 2018, *Advances in Financial Machine Learning*, ch. 7). One evaluation on the untouched test period.
Preprocessing (`FeaturePreprocessor`): training-set median imputation, then z-scores with training means and $n-1$
standard deviations (0 for a constant column) – fitted on the rows a model is trained on only.

### F-ML-005 · Models: ridge, logistic regression and tree ensembles

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.MachineLearning/Models/LinearModels.cs#RidgeRegressionTrainer`; `src/PortfolioOptimizer.MachineLearning/Models/LinearModels.cs#LogisticRegressionTrainer`; `src/PortfolioOptimizer.MachineLearning/Models/TreeModels.cs#RandomForestTrainer`; `src/PortfolioOptimizer.MachineLearning/Models/TreeModels.cs#GradientBoostingTrainer` |
| Tests | `ModelTests.Ridge_WithTinyPenalty_RecoversAnExactLinearRelationship`; `ModelTests.Ridge_ShrinksCoefficientsAsPenaltyGrows`; `ModelTests.Logistic_RecoversDirectionAndProducesProbabilities`; `ModelTests.TreeModels_LearnNonLinearSignal_AndRoundTripThroughArtifacts`; `ModelTests.Trainers_AreDeterministicForASeed` |
| Skills | ml-pipeline |
| Verified | 2026-10-28 |

Ridge on training-standardized features: $(Z^\top Z + \lambda nI)\beta = Z^\top(y-\bar y)$, intercept $\bar y$
(minimizes $\frac1n\|y-\bar y-Z\beta\|^2 + \lambda\|\beta\|^2$). Logistic: Newton/IRLS on
$\ell(\beta) - \tfrac{\lambda n}{2}\|\beta_{1..m}\|^2$, Hessian $X^\top WX + \lambda n I$, no line search (penalty keeps it
well-posed). Tree models (ML.NET FastForest/FastTree) are not re-derived here; covered by determinism/round-trip tests.

### F-ML-006 · Regression, classification and ranking metrics

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.MachineLearning/Evaluation/Metrics.cs#Regression`; `src/PortfolioOptimizer.MachineLearning/Evaluation/Metrics.cs#Classification`; `src/PortfolioOptimizer.MachineLearning/Evaluation/Metrics.cs#Ranking`; `src/PortfolioOptimizer.MachineLearning/Evaluation/Metrics.cs#RocAuc`; `src/PortfolioOptimizer.MachineLearning/Evaluation/Metrics.cs#Spearman` |
| Tests | `ModelTests.OutOfSampleR2_IsRelativeToTheTrainingMean`; `ModelTests.Auc_Brier_AndCalibration_MatchDefinitions`; `ModelTests.Spearman_HandlesTiesAndMonotoneTransforms`; `ModelTests.Ranking_TopKAndSpread_UseRealizedOutcomes` |
| Skills | ml-pipeline |
| Verified | 2026-10-28 |

OOS $R^2 = 1 - \sum(\hat y-y)^2/\sum(\bar y_{train}-y)^2$ (Campbell & Thompson 2008, *RFS* 21(4)); AUC = Mann–Whitney
$(\sum \text{rank}_+ - n_+(n_++1)/2)/(n_+n_-)$ with average ranks; Brier and skill vs. the training base rate; log loss
clipped at 1e-12; decile calibration. Ranking: per-date Spearman IC on the realized forward **excess** return for every
target; t-statistic $\bar{IC}\sqrt{k}/s_{IC}$ on dates ≥ h apart (overlap would overstate it). No multiple-testing
correction across experiments (documented limitation).

### F-ML-007 · ML portfolio construction

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `src/PortfolioOptimizer.MachineLearning/Strategy/PortfolioConstruction.cs#Build`; `src/PortfolioOptimizer.MachineLearning/Strategy/PortfolioConstruction.cs#ApplyCaps` |
| Tests | `ModelTests.TopK_EqualWeight_SelectsHighestScores`; `ModelTests.RankAndInverseVolatilityWeighting_FollowTheirRules`; `ModelTests.Caps_AreRespected_AndExcessIsRedistributedOrHeldInCash`; `ModelTests.SectorLimits_AreEnforced`; `ModelTests.ThresholdAndTurnoverLimit_AreApplied` |
| Skills | ml-pipeline |
| Verified | 2026-10-28 |

Rank → threshold → top K → equal / rank $(K-i)$ / $1/\sigma_{63}$ / score − min + 10 % spread → iterative capping (excess to
uncapped names, remainder in cash) → turnover limit by $w = w_0 + \alpha(w^* - w_0)$, $\alpha = \tau/T$. Never uses the
mean-variance optimizer (architecture test).

### F-ML-008 · Walk-forward training cutoff and ML backtest

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.MachineLearning/Backtesting/MlStrategy.cs#Decide`; `src/PortfolioOptimizer.MachineLearning/Backtesting/MlStrategy.cs#EqualWeightRule`; `src/PortfolioOptimizer.MachineLearning/Training/ExperimentEngine.cs#RunBacktest` |
| Tests | `ValidationAndLeakageTests.WalkForwardBacktest_DecisionsUseOnlyPastData`; `ValidationAndLeakageTests.OnARandomWalk_NoModelShowsOutOfSampleSkill`; `ValidationAndLeakageTests.WithAPlantedSignal_ModelsFindItOutOfSample` |
| Skills | ml-pipeline |
| Verified | 2026-10-28 |

At signal $t$: train (if due) on samples with $t' + h \le t$; score features at $t$; F-ML-007; F-BT-002 executes.
Assumption: the equal-weight baseline counts a stock as eligible once it has a price (its feature vector may be all
missing), while the ML strategy needs ≤ 30 % missing features – the baseline can hold new listings earlier (risk R-04).

### F-ML-009 · Permutation importance

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `src/PortfolioOptimizer.MachineLearning/Explainability/PermutationImportance.cs#Compute` |
| Tests | `ValidationAndLeakageTests.WithAPlantedSignal_ModelsFindItOutOfSample` |
| Skills | ml-pipeline |
| Verified | 2026-10-28 |

Increase in test-period loss when one feature column is shuffled (3 repeats, seeded). Correlated features share or mask
importance (documented).

---

## Education content

### F-EDU-001 · Two-asset worked examples

| Field | Value |
|---|---|
| Status | Verified correct |
| Code | `frontend/portfolio-dashboard/src/lib/worked-examples.ts#minimumVariance`; `frontend/portfolio-dashboard/src/lib/worked-examples.ts#maximumSharpe` |
| Tests | `frontend/portfolio-dashboard/src/lib/worked-examples.test.ts` |
| Skills | quant-formulas |
| Verified | 2026-10-28 |

Two-asset GMV $w_A = (\sigma_B^2-\rho\sigma_A\sigma_B)/(\sigma_A^2+\sigma_B^2-2\rho\sigma_A\sigma_B)$; tangency
$w \propto \Sigma^{-1}(\mu - r_f)$ normalized; both clipped to [0, 1]. Illustrative constants, not market data.

### F-EDU-002 · Interactive guide figures (sample data)

| Field | Value |
|---|---|
| Status | Correct under documented assumptions |
| Code | `frontend/portfolio-dashboard/src/lib/guide-demos.ts#gmvMix`; `frontend/portfolio-dashboard/src/lib/guide-demos.ts#tangencyMix`; `frontend/portfolio-dashboard/src/lib/guide-demos.ts#targetReturnMix`; `frontend/portfolio-dashboard/src/lib/guide-demos.ts#maxReturnForRiskMix`; `frontend/portfolio-dashboard/src/lib/guide-demos.ts#equalWeightVolatility`; `frontend/portfolio-dashboard/src/lib/guide-demos.ts#constructWeights`; `frontend/portfolio-dashboard/src/lib/guide-demos.ts#ridgeShrink` |
| Tests | `frontend/portfolio-dashboard/src/lib/guide-demos.test.ts` |
| Skills | quant-formulas |
| Verified | 2026-11-01 |

The Codex figures recompute F-EDU-001 for slider values (correlation, $\mu_B$, target return, volatility limit; the
starting values reproduce the worked examples exactly – tested). Equal weight: $\sigma_p = \sigma\sqrt{\rho + (1-\rho)/N}$
for $N$ stocks with equal $\sigma$ and pairwise $\rho$. Scores → weights follows F-ML-007 (equal, rank $K-i$, score −
lowest selected + 10 % spread, iterative capping, rest in cash) without sector caps, cash reserve or turnover limit.
Ridge: $\beta/(1+\lambda)$, exact only for uncorrelated standardized features with the app's $\lambda n$ penalty (the
figure says so). Tree, boosting and cost figures use seeded sample data. Sample data only – never stored prices or user
data; every figure is labelled "Sample data – not market data".

---

## Categories audited and not implemented

| Category | State |
|---|---|
| Parametric (Gaussian) VaR, Cornish–Fisher, Monte Carlo VaR | Not implemented (historical simulation only) |
| Factor models, factor exposures, regression-based attribution | Not implemented |
| Black–Litterman, robust or resampled optimization | Not implemented |
| Short selling, leverage, borrowing costs | Not applicable (rejected by validation) |
| Country/currency constraints in the optimizer | Not implemented (sector caps only) |
| Confidence intervals / bootstrap for strategy metrics | Not implemented (limitations) |
| Split/dividend adjustment computation | Not applicable (provider-adjusted closes are stored) |
| Exchange holiday calendars | Not implemented (missing prices are read as closures, F-BT-002) |
| Outlier winsorization of returns | Not implemented (ML imputes missing features with training medians only) |
| Cache layer (e.g. Redis) | Not applicable – none exists by design ([performance](../performance.md)) |
