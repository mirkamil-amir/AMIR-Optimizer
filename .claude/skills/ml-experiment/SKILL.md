---
name: ml-experiment
description: Run ML Optimizations (machine-learning experiments) in MatrixView through the API on real stored prices (one or several model families/targets on the same universe, linked to a portfolio), wait for them, backtest or compare them with the portfolio's other optimizations, and report out-of-sample results honestly against baselines and equal weight. Use when asked to train, evaluate, compare or benchmark ML models or to produce ML evaluation numbers.
---

# Run & report ML experiments

In the UI an experiment is an **ML Optimization** of a portfolio: the tab next to Math Optimization in the portfolio
workspace (`/portfolios/<id>/ml-optimize/<experimentId>`); link results there when reporting.

Requires a running stack (see `/run-app`). Uses the API as the administrator; never print the password.

## 1. Check the data first

Experiments use stored prices of the active provider only. Each stock needs several years of history
(`historyYears`, default 8) plus the benchmark (default SPY).

```bash
set -a; . ./.env; set +a; B=http://localhost:5080/api/v1; J=$(mktemp)
T=$(curl -s -c $J -b $J $B/auth/antiforgery | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
python3 -c 'import json,os;print(json.dumps({"email":os.environ["ADMIN_EMAIL"],"password":os.environ["ADMIN_PASSWORD"]}))' \
  | curl -s -o /dev/null -c $J -b $J -H "X-XSRF-TOKEN: $T" -H 'Content-Type: application/json' --data @- $B/auth/login
T=$(curl -s -c $J -b $J $B/auth/antiforgery | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')   # token rotates on sign-in
curl -s -b $J "$B/stocks?withPrices=true&pageSize=200" | python3 -c 'import sys,json;[print(s["symbol"],s["observations"],s["firstDate"]) for s in json.load(sys.stdin)["items"]]'
```

Missing history is queued automatically when the experiment is created (≤ 50 symbols); you can also prefetch with
`POST $B/market-data/sync` `{"symbols":[…]}` and wait for the job.

## 2. Queue experiments

`POST $B/ml/experiments` (202). Key fields: `portfolioId` (one of the user's portfolios – its base currency is used and the
experiment becomes one of its optimizations; `GET $B/portfolios` lists them), `universe` (≥ 5), `benchmarkSymbol`, `modelFamily`
(`RidgeRegression`, `LogisticRegression`, `RandomForest`, `GradientBoosting`), `target` (`ForwardReturn`,
`ForwardExcessReturn`, `OutperformBenchmark`, `ForwardVolatility`), `horizonDays`, `historyYears`, `topK`.
Ridge supports regression targets only; logistic only `OutperformBenchmark`. Catalogue: `GET $B/ml/models`.

- Set `topK` to at most half the universe – with `topK ≥ universe` the ML portfolio is just equal weight.
- Stocks of several markets may be mixed: prices are converted into the analysis currency (the portfolio's) with dated
  ECB rates; a stock with > 5 % of prices without a rate is excluded with the cause (see `results.eligibility`). In a
  single-market analysis in its own currency a foreign benchmark is switched to the index benchmark (SPY → ^GDAXI for
  DAX stocks in EUR) and noted in `warnings`. Missing history is downloaded before training (also noted); the
  experiment uses only the benchmark's stored span, so check the benchmark's history too.
- Without `portfolioId` the experiment is unlinked and cannot be backtested or compared from the UI;
  `PUT $B/ml/experiments/{id}/portfolio` `{"portfolioId":"…"}` links it later (same currency only).
- At most **2 active experiments per user** (409 otherwise): queue two, wait, queue more.
- In zsh build per-experiment variables (`f=${spec%%:*}; t=${spec##*:}`); `set -- $spec` does not split.

```bash
curl -s -c $J -b $J -H "X-XSRF-TOKEN: $T" -H 'Content-Type: application/json' \
  -d '{"name":"Ridge excess 21d","universe":["AAPL","MSFT","NVDA","AMZN","GOOGL","META","JPM","JNJ","XOM","PG"],"benchmarkSymbol":"SPY","modelFamily":"RidgeRegression","target":"ForwardExcessReturn","horizonDays":21,"historyYears":8,"topK":5}' \
  $B/ml/experiments | python3 -c 'import sys,json;d=json.load(sys.stdin);print(d.get("summary",{}).get("id"), d.get("errors") or d.get("detail") or "")'
```

Poll `GET $B/ml/experiments?pageSize=50` until no item is `Queued`/`Running`. A `Failed` experiment carries its
reason in `error` – report it; never substitute numbers.

## 3. Collect results

- `GET $B/ml/experiments/{id}` → `results.testMetrics` (regression: `outOfSampleR2`; classification: `rocAuc`,
  `brierScore`; ranking: `meanInformationCoefficient`, `informationCoefficientTStat`, `nonOverlappingDates`,
  `topKExcessOverUniverse`), `results.baselines` (training mean / base rate, 12-1 momentum), `results.testPeriod`,
  `results.selectedHyperparameters`.
- `GET $B/ml/experiments/{id}/backtest` → `performance[]` (ML strategy, equal weight, benchmark) and `rebalances`.
- `results.eligibility[]` → per stock: stored prices, prices in the benchmark span, prices without an FX rate, samples,
  rank, weight and the stage/reason where it stopped – use it to explain excluded or unselected stocks.
- Other periods: `POST $B/backtests/from-strategy` `{"portfolioId":…,"strategy":{"kind":"MachineLearning","id":…},"startDate":…,"endDate":…}`
  (results before the test period are flagged "Partly in-sample"). Against another optimization of the same portfolio:
  `POST $B/strategy-comparisons` `{"portfolioId":…,"strategyA":{"kind":"Mathematical","id":<run>},"strategyB":{"kind":"MachineLearning","id":<experiment>}}`;
  `GET $B/portfolios/{id}/strategies` lists the candidates. Comparisons and backtests are queued jobs (poll by id).

## 4. Report honestly

- Report **test-period** metrics only, with the test window, number of decision dates and non-overlapping dates.
  Never present `trainMetrics`/`validationMetrics` as performance.
- Always put baselines and the equal-weight/benchmark backtest next to the model numbers.
- Treat |IC t-stat| < 2, negative out-of-sample R², AUC ≈ 0.5 as **no evidence of skill**. Say so plainly.
- Never call a model profitable or superior; mention survivorship bias (today's constituents) and small samples.
- If two models give identical backtests, check `rebalances[].selected` before calling it a bug (same top-K set ⇒
  same equal-weight portfolio).
- Experiments, backtests and comparisons are runs in the user's account: on the development stack create them only
  when the user asked for these numbers; otherwise use `./scripts/e2e.sh`. Delete only experiments you created as probes
  (`DELETE $B/ml/experiments/{id}`, 204); never others.
- `rm -f $J` when done.
