# Final report – AMIR Optimizer platform upgrade

> Historical report of the first upgrade (2026-10-14, morning). The later audit/repair pass (market universes,
> automatic prices, education layer, test-data isolation) is documented in [implementation-status.md](implementation-status.md).
> File names below refer to that time (`data-sources.md` is now `market-data.md`, `methodology.md` is now
> `quantitative-strategies.md`).

Status as of 2026-10-14. All work is uncommitted on `main` (162 files changed: 103 added, 56 modified, 3 removed).
Numbers below come from commands run on the development machine (macOS arm64, .NET 10.0, Node 22, Podman 5).

## 1. Architecture changes and module boundaries

- New pure library **`PortfolioOptimizer.MachineLearning`** (features, labels, purged splits, four model families,
  evaluation, explainability, ML portfolio construction, walk-forward strategy, artifact format). No I/O. An
  architecture test forbids it from using the mean-variance optimizer, and another forbids the optimizer from
  referencing ML.
- **Shared `PortfolioSimulator`** extracted from `BacktestEngine` into the Optimization project; the classical
  backtester, ML backtests and strategy comparisons all execute through it. The extraction was verified to give
  bit-identical results for nine existing backtest configurations.
- Application layer: `MachineLearning/` (experiment service, data loader, runner), `Comparisons/` (engine, service,
  runner), `Administration/`, catalogue and stock-discovery services; every user-owned query is owner-scoped.
- Infrastructure: Npgsql/PostgreSQL, Identity stores, audit log, administrator bootstrap, data-protection keys in the
  database, local model-artifact store; the background worker also runs ML experiments and comparisons.
- API: auth/admin/stocks/ML/comparison controllers, fallback authorization policy, antiforgery filter, rate-limit
  policies, `migrate` command.
- Tool: `tools/PortfolioOptimizer.DataMigration` (SQLite → PostgreSQL importer).
- Details: [architecture](architecture.md).

## 2. PostgreSQL configuration and migrations

- `ConnectionStrings:DefaultConnection` (required; env var or user-secrets). Compose provides PostgreSQL 17 with an
  owner role (migrations) and a DML-only application role (the API).
- Migrations: `InitialPostgres`, `StockCatalogueMetadata`, `MachineLearningExperiments`, `StrategyComparisons`.
  Compose runs them in the one-shot `migrate` service; locally `Database:MigrateOnStartup` (Development default).
  The API refuses to start against an outdated schema.
- Legacy data: `tools/PortfolioOptimizer.DataMigration` (dry run against the development SQLite database: 17
  tables read, balance checks passed, nothing written; the committing import is covered by an integration test).
- Backup/restore (`pg_dump`/`pg_restore` plus the artifact volume): [deployment](deployment.md).

## 3. Authentication and administrator bootstrap

- ASP.NET Core Identity with HttpOnly, SameSite=Strict cookie sessions; registration → *Pending* → administrator
  approves/rejects/suspends/reactivates; status checked on every request; lockout after 5 failures; antiforgery on
  unsafe methods; rate limits; audit trail.
- Bootstrap: set `Admin__Email` and `Admin__Password` (compose: `ADMIN_EMAIL`, `ADMIN_PASSWORD` in `.env`). The
  administrator is created only while none exists; afterwards the settings are ignored. Change the password after
  the first sign-in. Details: [security](security.md).

## 4. Stock selection workflow

Stock discovery lists the 503-stock S&P 500 catalogue by sector with search, price-history filter, metadata and
labelled quotes (*Last close* / *Delayed quote*, provider, date, retrieval time). Users select stocks across sectors
into an **in-memory** selection (not stored in browser storage or on the server, kept across navigation, cleared on
reload and sign-out; verified by unit and end-to-end tests). The selection tray fetches missing prices (≤ 50 symbols)
and hands the universe to **Optimize** or **Machine learning**; the universe is stored only when a run is submitted.

## 5. Mathematical optimization methods

Unchanged and independent: mean-variance with minimum variance, maximum Sharpe, target return, maximum return for a
volatility limit and equal weight; constraints for weight bounds, max holdings, minimum position, cash reserve,
turnover, sector caps; sample or Ledoit–Wolf covariance; custom ADMM QP solver with active-set polishing; results
verified against constraints, infeasibility explained; efficient frontier, risk contributions; whole-share trade
proposals. Runs persist with inputs, data snapshot hash and diagnostics. See [quantitative strategies](quantitative-strategies.md).

## 6. ML model families and targets

| Family | Implementation | Targets |
|---|---|---|
| Ridge regression | in-house closed form | forward return, forward excess return, forward volatility |
| Logistic regression | in-house L2 IRLS | probability of outperformance |
| Random forest | ML.NET FastForest | all four (probability: regression forest on the 0/1 label, clamped to [0, 1]) |
| Gradient boosting | ML.NET FastTree | all four (probability: FastTree binary classifier) |

Horizons 5/21/63 trading days. Unsupported family/target pairs are rejected (tested).

## 7. Features and validation methodology

20 price/volume features in six groups (`features-2026.10-v1`): momentum, moving-average gaps, volatility and
downside, drawdown, extreme returns, beta/correlation/excess momentum vs. benchmark, relative volume, dollar volume,
sector-relative momentum, market trend and volatility. Features use data ≤ *t*; labels use *t+1 … t+h*. Validation:
chronological 60/20/20 split with purging (and optional embargo), hyperparameter selection on validation only,
a single evaluation on the untouched test period, preprocessing fitted on training rows only. Leakage tests are
listed in [machine learning](machine-learning.md#leakage-safeguards-tested).

## 8. ML evaluation results on the available data

Dataset: Yahoo adjusted closes for 10 large US stocks (AAPL, MSFT, NVDA, AMZN, GOOGL, META, JPM, JNJ, XOM, PG),
benchmark SPY, 8 years requested (2018-10 → 2026-10), 21-day horizon, decision step 5 days, top K = 5, monthly
rebalancing, 10 bps costs. **Test period 2025-12-02 → 2026-09-14: 40 decision dates, 400 samples, only 8
non-overlapping dates.** Ranking outcome = realized 21-day excess return over SPY.

| Model · target | Test mean IC | IC t-stat (8 dates) | Out-of-sample R² | ROC-AUC | Top-5 minus universe (21-day) |
|---|---|---|---|---|---|
| Ridge · excess return | −0.047 | −0.57 | −0.028 | – | +0.11% |
| Random forest · excess return | −0.011 | −0.73 | −0.046 | – | +0.66% |
| Gradient boosting · excess return | −0.008 | +0.28 | −0.099 | – | +0.36% |
| Logistic · outperformance | −0.061 | −0.44 | – | 0.480 | +0.04% |
| Gradient boosting · outperformance | −0.076 | −0.78 | – | 0.486 | −0.34% |
| Baseline: 12-1 momentum rank | +0.026 | +1.12 | – | – | −0.08% |
| Baseline: training mean / base rate | – | – | 0 (by definition) | 0.500 | – |

Walk-forward backtest over the same test period (hypothetical, survivorship-biased universe):

| Strategy | Annualized return | Volatility | Sharpe | Max drawdown | Turnover |
|---|---|---|---|---|---|
| Equal weight (same universe, same rebalancing) | 26.1% | 12.4% | 1.61 | −8.7% | 0.80 |
| Benchmark SPY (buy & hold) | 18.2% | 12.6% | 1.07 | −8.9% | 0 |
| ML ridge · excess return | 23.9% | 23.3% | 0.86 | −17.7% | 1.56 |
| ML logistic · outperformance | 23.9% | 23.3% | 0.86 | −17.7% | 1.56 |
| ML gradient boosting · excess return | 20.0% | 17.0% | 0.92 | −13.4% | 4.28 |
| ML random forest · excess return | 19.7% | 18.5% | 0.85 | −13.2% | 3.49 |
| ML gradient boosting · outperformance | 10.1% | 19.3% | 0.39 | −18.4% | 4.10 |

**Interpretation.** No model shows out-of-sample skill: all test ICs are slightly negative with |t| < 1, every
out-of-sample R² is negative (worse than predicting the training mean), and both AUCs are below 0.5. No ML strategy
beat equal weight on a risk-adjusted basis; their higher volatility comes from concentrating in five high-beta names.
Ridge and logistic regression produced identical portfolios: their score orderings differ, but they selected the
same five stocks at all 11 rebalances (verified in the rebalance logs). With 10 stocks and 8 independent dates these
numbers cannot support any claim of profitability or superiority in either direction. Training metrics are stored
separately and are not reported here as performance.

## 9. From predictions to allocations

Rank by score (inverted for volatility) → optional minimum score → top K → equal, rank, inverse-volatility or
score-proportional weights → per-stock and sector caps by iterative capping (remainder in cash) → cash reserve →
optional turnover limit. Weights are validated (≥ 0, Σ ≤ 1). A paper-trade proposal can be created; execution needs
explicit confirmation. The ML engine never calls the mean-variance optimizer; only the optional, labelled *hybrid*
comparison strategy combines them.

## 10. How strategies are compared

`StrategyComparisonEngine` re-runs the classical strategy (stored optimization settings, trailing window) and the ML
strategy (stored hyperparameters, walk-forward retraining on realized labels) next to equal weight and the benchmark,
on one price grid with the same dates, universe (intersection or union; exclusions listed), capital, costs,
next-close execution and cash at 0%, through the shared simulator and identical metric code. The UI shows growth,
drawdown, rolling Sharpe, risk vs. return, performance table, monthly returns, allocation and sector exposure,
turnover, and holdings overlap. Comparisons are owner-scoped, stored with inputs and data snapshot hash, and immutable.
Incomplete inputs (missing prices, universe mismatch, too little history) produce warnings or a failed job.

## 11. Model artifacts and versioning

Each trained model is an `MlModelVersion` (Evaluation or Deployment role, hyperparameters, feature-set version,
training window, sample count, metrics) with a ZIP artifact (`manifest.json` + parameters) at
`<ModelArtifacts:RootPath>/<experimentId>/<versionId>.model`, written atomically. Its SHA-256 and size are stored
and verified on every load; a tampered or missing file is never used ("Model artifact unavailable"). Prediction sets
reference the model version that produced them. Predicting with a model whose feature-set version differs from the
current feature definitions is refused.

## 12. Files and projects added or changed

- **Added projects:** `src/PortfolioOptimizer.MachineLearning`, `tests/PortfolioOptimizer.MachineLearning.Tests`,
  `tools/PortfolioOptimizer.DataMigration`.
- **Backend (added):** Api `Controllers/{Auth,Admin,Stocks,MachineLearning,StrategyComparisons}Controller(s).cs`,
  `Security/{Authorization,AntiforgeryTokens}.cs`; Application `MachineLearning/*`, `Comparisons/*`,
  `Administration/*`, `MarketData/{CatalogueService,StockDiscoveryService}.cs`, DTOs; Domain `Users.cs`,
  `MachineLearning.cs`; Infrastructure `Identity/*`, `ModelArtifacts/*`, `Persistence/DatabaseOptions.cs`,
  four PostgreSQL migrations (replacing the SQLite migration), `Seed/catalogue-sp500.csv`; Optimization
  `Backtesting/{PortfolioSimulator,StrategyMetrics}.cs`.
- **Backend (modified):** `Program.cs`, `appsettings*.json`, Dockerfile, DbContext, initializer, worker, owner
  scoping in portfolio/optimization/trading/backtest services, `BacktestEngine`, exception handling.
- **Frontend:** pages `auth`, `admin`, `stocks`, `history`, `machine-learning`, `strategy-comparison`; components
  `route-guards`, `stock-selection`, `analysis-charts`, `performance-table`; `lib/{auth,selection}.tsx`; updated
  `api.ts`, `queries.ts`, `types.ts`, `app-shell`, `optimize`, `settings`, `ui/button`; nginx CSP; Vitest and
  Playwright setup (`vitest.config.ts`, `src/test/setup.ts`, `*.test.ts(x)`, `playwright.config.ts`, `e2e/*`).
- **Tests (added):** integration `AuthTests`, `StockDiscoveryTests`, `SqliteImportTests` (+ fixture),
  `MachineLearningTests`, `StrategyComparisonTests`, `PostgresTestServer`; unit `CatalogueParsingTests`.
- **Ops & docs:** `docker-compose.yml`, `.env.example`, `deploy/postgres/init/01-app-role.sh`, `scripts/test.sh`,
  `README.md`, `CLAUDE.md`, `.claude/skills/*`, `docs/{audit,architecture,machine-learning,security,deployment,
  limitations,data-sources,methodology,final-report}.md`.

## 13. Test commands and actual results (2026-10-14)

| Command | Result |
|---|---|
| `./scripts/test.sh` (Testcontainers PostgreSQL via Podman) | Unit **74/74**, ML **40/40**, Integration **39/39** passed (≈ 23 s, 4 s, 52 s) |
| `npm test` (Vitest) | **31/31** passed (6 files) |
| `npm run build` | passed (tsc + Vite) |
| `npm run lint` (oxlint) | 0 errors, 32 warnings (React-compiler/fast-refresh hints, mostly pre-existing) |
| `npm run test:e2e` against Vite dev server (:5173) | **9/9** passed |
| `npm run test:e2e` with `E2E_BASE_URL=http://localhost:8081` (compose stack) | **9/9** passed |
| `podman compose up --build -d` | db, migrate, api, web start; `/health` 200, web 200, API via nginx 200/401 as expected, CSP header present |

Defects found and fixed by the new tests: the API client retried an antiforgery failure with the stale token; after
sign-out the next user was redirected to the previous user's page and the in-memory selection survived; link-styled
buttons lost link semantics; the permutation-importance chart's labels overlapped; duplicated legends and an
unreadable turnover/cost chart on the comparison page.

## 14. Deployment

`cp .env.example .env` (replace every placeholder) → `podman compose up --build -d` → http://localhost:8081.
Production: `ASPNETCORE_ENVIRONMENT=Production`, TLS-terminating reverse proxy in front of `web`, no published API
or database ports, scheduled backups of the database and the `model-artifacts` volume. See [deployment](deployment.md).

## 15. Remaining limitations, credentials and external services

- Credentials you must provide: PostgreSQL owner and app passwords, bootstrap administrator (`.env`); optional Alpha
  Vantage key. The local `.env` contains generated development passwords and is gitignored.
- External services: Yahoo Finance (unofficial, personal use, may throttle or change), optional Alpha Vantage;
  Wikipedia catalogue snapshot (CC BY-SA). No point-in-time constituents → survivorship bias in all backtests.
- No e-mail confirmation, password reset or MFA; single in-process job worker; artifacts on a local volume.
- See [limitations](limitations.md).

## 16. Incomplete or partially verified acceptance criteria

- **Backup and restore** are documented but were not rehearsed end to end.
- **Full SQLite import** was verified as a dry run on the real database and as a committing import on a test
  fixture; it was not committed against the real development data (the target database already has data).
- **"Genuinely evaluated" ML on real data** is limited by the available data: 10 stocks with Yahoo history, which
  leaves 8 independent test dates. All four families were trained and evaluated (section 8), but the data is too
  small to draw conclusions. Larger universes require fetching prices for more catalogue stocks.
- **End-to-end tests** cover authentication, approval, roles, stock selection and page loads. Submitting an ML
  experiment or a comparison through the browser is covered by API integration tests, not Playwright.
- **CI:** no pipeline exists in the repository; tests were run locally only.
- Everything else in section 19 of the specification was implemented and verified by the tests and checks listed above.
