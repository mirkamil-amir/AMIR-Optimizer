# Repository audit (baseline before the platform upgrade)

Audit date: 2026-10-13. Baseline commit `dd5759d`. Baseline: `dotnet build` clean, `dotnet test` **79/79 passed**
(70 unit, 9 integration), `npm run build` clean.

## 1. Current architecture

| Project | Role | Notes |
|---|---|---|
| `PortfolioOptimizer.Optimization` | Pure math | Returns, Ledoit–Wolf covariance, custom ADMM QP solver with active-set polishing, mean-variance optimizer (5 objectives), efficient frontier, look-ahead-free event backtester. No I/O. |
| `PortfolioOptimizer.Domain` | Entities | Portfolio with weighted-average-cost accounting, immutable ledger, runs, proposals, sync jobs. |
| `PortfolioOptimizer.Application` | Use cases | One service per area, `IApplicationDbContext` (EF `DbSet`s), DTOs, validation exceptions. |
| `PortfolioOptimizer.Infrastructure` | EF Core (SQLite), providers (Yahoo, Alpha Vantage, Mock) with resilience + throttling, in-process job queue/worker, scheduled refresh, seeding. |
| `PortfolioOptimizer.Api` | Controllers under `/api/v1`, Problem Details, Swagger, rate limiting, health checks. |
| `frontend/portfolio-dashboard` | React 19 + Vite + shadcn/ui (Base UI) + TanStack Query + Recharts. 9 pages. |

Toolchain: .NET 10 SDK (10.0.401), Node 22, Podman 5.8 (arm64 VM). Containers are **linux/arm64** on this machine.

## 2. Working functionality (preserve)

Market-data sync with data-quality counters; raw vs adjusted closes kept separate; per-source storage, simulated
data flagged. Classical optimizer: min variance, max Sharpe, target return, max return for a volatility cap, equal
weight; weight bounds, cash reserve, sector caps, turnover, max holdings / min position (heuristic); infeasibility
explanations; persisted runs with a data-snapshot hash. Whole-share trade proposals, idempotent confirmed paper
execution with optimistic concurrency. Realized analytics (VaR/CVaR, beta, TE, drawdowns). Async backtests.

## 3. Missing functionality (vs. the target platform)

* PostgreSQL (SQLite only; migrations are SQLite-specific).
* Authentication, approval workflow, roles, per-user data ownership, audit log (none – all data is global).
* Instrument catalogue with sector browsing, search, and quote provenance; temporary multi-sector selection.
* Any machine-learning capability (feature pipeline, labels, time-aware validation, model registry, artifacts).
* Strategy comparison on a shared evaluation engine.
* Frontend unit tests and end-to-end tests.

## 4. Proposed module boundaries

```
Domain  ◀── Application ◀── Infrastructure ◀── Api
              │   │
              │   └──▶ Optimization      (classical engine, pure)
              └──────▶ MachineLearning   (new, pure: features, labels, validation, models, ranking, ML portfolios)
Optimization.Backtesting.WeightScheduleSimulator  ← shared, engine-agnostic accounting used by the comparison
```

* `MachineLearning` never references the mean-variance optimizer; `Optimization` never references `MachineLearning`.
* The comparison service (Application) consumes both through stable result contracts and evaluates every strategy
  with the same simulator (same calendar, execution timing, costs, cash treatment).
* Identity lives in Infrastructure; Application sees `ICurrentUser` and ownership filters only.

## 5. Database migration risks

* The existing SQLite database contains real data (local file: 11 assets, 13,805 Yahoo prices, 1 portfolio with
  8 holdings, executions, ledger, 2 optimization runs, 2 backtests; plus the `portfoliooptimizer_portfolio-data`
  container volume). SQLite migrations cannot be replayed on PostgreSQL → a fresh PostgreSQL baseline migration
  plus an explicit **SQLite → PostgreSQL import tool** that copies every table and assigns ownership to a chosen
  user. The SQLite file is never modified.
* Type changes: TEXT GUIDs → `uuid`, TEXT timestamps → `timestamptz` (all values UTC), JSON-encoded string lists →
  `text[]`, INTEGER booleans → `boolean`, TEXT decimals → `numeric(18,6)`.
* Automatic `Migrate()` on start is acceptable for local development but not for deployments → a separate
  `migrate` command run by a privileged migration identity; the API runs with a DML-only role.

## 6. Authentication risks

* Every endpoint is anonymous today; portfolios, runs and backtests have no owner. Introducing ownership requires
  backfilling an owner for imported data.
* Settings (data provider switch) and the shared instrument catalogue must become administrator-only.
* Cookie authentication for a same-origin SPA needs CSRF protection (antiforgery header) and SameSite cookies;
  suspension must take effect immediately (status checked server-side per request, not only at sign-in).

## 7. ML implementation options and trade-offs

| Option | Verdict |
|---|---|
| ML.NET FastForest (random forest) / FastTree (gradient boosting) | **Use.** Managed trainers verified to train on osx-arm64 in this environment; support regression and binary classification and per-prediction feature contributions. |
| LightGBM (NuGet `LightGBM` 4.7.0) | **Rejected.** The package ships native binaries for linux-x64, osx-x64 and win-x64 only – no arm64 – so it cannot run in this machine's arm64 containers. |
| ML.NET OLS (MKL) for ridge | Rejected: MKL native components are x64-only. Ridge is implemented exactly in C# (closed form, MathNet). |
| Logistic regression | Implemented in C# (L2-regularised Newton/IRLS) for exact, interpretable coefficients. |
| Python service | Not justified: every required model family is available in-process; a second runtime would add deployment and security surface without a material modelling advantage. |
| Deep learning / RNNs | Not added: daily data for tens–hundreds of stocks is too small for a defensible advantage. |

## 8. Testing and deployment strategy

* Unit tests for math, ML features/labels/splits/leakage (synthetic data with known structure), portfolio
  construction, simulator accounting.
* Integration tests against **real PostgreSQL** via Testcontainers (Podman socket supported) or an explicit
  `PO_TEST_POSTGRES` connection string; authentication/authorization matrix (anonymous, pending, approved,
  suspended, admin), ownership isolation, persistence of runs/experiments/comparisons.
* Frontend: Vitest + Testing Library for components; Playwright end-to-end against the compose stack with the
  deterministic Mock provider.
* Compose: PostgreSQL 17 with a persistent volume, one-shot `migrate` service, API (least-privilege DB role), web.
