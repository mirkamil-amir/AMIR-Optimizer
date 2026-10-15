# Portfolio Reality Check

`/reality-check/{runId}` (button "Portfolio reality check" on a successful optimization result) and
`GET /api/v1/optimization-runs/{runId}/reality-check` (owner-scoped: another user's run is 404). Code:
`Optimization/Portfolio/DiversificationMath.cs` (pure math), `Application/Analytics/RealityCheckService.cs`.

It challenges a saved allocation before anyone trusts it. It answers: what does the analysis show, which assumptions
drive it, where is it fragile, which comparisons are meaningful, what limits the conclusion, and what to look at next.
It never labels a portfolio safe/best and never recommends trades.

## Inputs

The run's saved weights, its own price source and estimation window (`PeriodStart`–`PeriodEnd`), its covariance method,
objective, constraints and benchmark. If the stored prices for that window changed since the run (data snapshot hash
differs), the report says so and uses the current data.

## Calculations

| Section | Definition |
|---|---|
| Concentration | invested weights normalized to 1; positions ≥ 0.5 %; largest and top-three weight; HHI = Σwᵢ²; effective holdings = 1/HHI; sector weights from the catalogue classification |
| Risk | σₚ = √(wᵀΣw) (annualized, run's estimator); risk share RCᵢ = wᵢ(Σw)ᵢ / wᵀΣw (Σ = 1); diversification ratio Σwᵢσᵢ / σₚ; effective risk contributors 1/ΣRCᵢ² |
| Correlation | average pairwise correlation of the holdings and the highest pair; the same average on **stress days** = worst 10 % of days of an equal-weight basket of the holdings (only when ≥ 20 such days) |
| In-sample history | fixed weights (daily rebalanced, cash earns nothing) on the window's returns: cumulative return, realized volatility, maximum drawdown, 1-day historical VaR 95 % and expected shortfall 95 %, worst day, worst 20-day return; benchmark on identical dates (else omitted), beta. **Labelled in-sample: these returns chose the weights.** |
| Out-of-sample history | the same on prices stored **after** the window, when ≥ 20 trading days exist for every holding; otherwise the reason is shown |
| Sensitivity | same objective and constraints (turnover limit dropped – no holdings context) re-solved with one change: (1) nothing (re-solve), (2) the other covariance estimator (sample ↔ Ledoit–Wolf), (3) earlier half and (4) later half of the window (each ≥ 60 returns, else skipped), (5) expected returns shrunk 50 % toward their average, (6) equal weight. Reported: one-way turnover ½Σ\|Δw\| vs the saved weights, largest change, holdings, and the scenario's return/volatility **measured with the run's estimates** |
| Costs | build from cash = invested × cost rate; switching to the most different scenario = 2 × turnover × rate (and at 3× the rate); proportional only |

## Findings

Each statement carries a kind: **Fact** (computed from stored data), **Estimate** (model output), **Interpretation**
(a rule applied to a number, with the rule as basis), **Caution** (limits the conclusion). Interpretation thresholds:

| Rule | Threshold |
|---|---|
| single large position | largest weight ≥ 25 % |
| concentrated | effective holdings < 5 |
| sector concentration | largest sector ≥ 40 % |
| risk more concentrated than weights | a risk share ≥ 35 % |
| holdings move together | average correlation ≥ 0.6 |
| diversification weakens in bad periods | stress-day correlation ≥ average + 0.10 |
| fragile allocation | any scenario turnover ≥ 30 % |
| stable across the tested assumptions | all scenario turnovers ≤ 10 % |

Always added: the in-sample caution; SIMULATED data caution; data-changed caution. Next steps are educational
(walk-forward backtest, strategy comparison on equal dates, guide on estimation error, minimum-variance contrast).

## Verification

* `DiversificationMathTests`: HHI and effective N by hand (incl. cash reserve), uncorrelated and perfectly correlated
  two-asset closed forms (risk shares 0.8/0.2, diversification ratio 1.3416 / 1), drawdown −50 % and cumulative −34 % on
  a three-day path, stress correlation on a constructed crash series, input validation.
* `RealityCheckTests`: the recomputed ex-ante volatility equals the run's stored volatility (1e-6), risk shares and
  sector weights sum to 1, ES ≥ VaR, re-solve turnover < 2 %, min-variance unaffected by shrunk returns, max-Sharpe at
  least as sensitive to returns as min-variance, all findings labelled, no trading instructions, cross-user 404, 401.
* e2e journey F (`journeys.spec.ts`).

## Limitations

Survivorship bias (today's index members), one estimation window, one-day historical VaR/ES, correlations that change
in crises, no FX, proportional costs without spreads/impact, out-of-sample periods that are usually short. Sensitivity
covers four assumption changes, not all sources of estimation error (no bootstrap/resampling yet – see
[roadmap.md](roadmap.md)).
