# Architecture

## Overview

```
┌────────────────────────────┐  /api/v1 · cookie session · X-XSRF-TOKEN   ┌───────────────────────────────────┐
│ React + TS dashboard        │ ─────────────────────────────────────────▶ │ ASP.NET Core Web API (.NET 10)     │
│ Vite · shadcn/ui (Base UI)  │  dev: Vite proxy · prod: nginx (same origin)│ Identity · policies · antiforgery  │
│ TanStack Query · Recharts   │                                            │ rate limits · Problem Details      │
│ in-memory stock selection   │                                            └─────────────────┬─────────────────┘
└────────────────────────────┘                                                              │ DI
             ┌──────────────────────────────────────────────────────────────────────────────▼───────────────────┐
             │ Application: use-case services (owner-scoped), DTOs, validation                                     │
             │ MarketData · QuoteRefresh · Universes · StockDiscovery · Portfolios · Optimization · Trading · …     │
             │ MachineLearning (MlExperimentService, runner) · Comparisons (engine, service, runner) · Admin       │
             └───────┬───────────────────────┬──────────────────────────────┬───────────────────────────────────┘
                     │                       │                              │
   ┌─────────────────▼─────────┐ ┌───────────▼──────────────────────┐ ┌─────▼──────────────────────────────────┐
   │ Domain                     │ │ Optimization (pure math)          │ │ MachineLearning (pure, no I/O)          │
   │ Portfolio · Holding (WAC)  │ │ metrics · covariance · ADMM QP    │ │ panel · features · labels · splits      │
   │ ledger · proposals · runs  │ │ mean-variance · frontier          │ │ ridge/logistic · ML.NET trees · metrics │
   │ users/audit · ML entities  │ │ PortfolioSimulator (shared) ◀─────┼─┤ explainability · portfolio construction │
   └────────────────────────────┘ └───────────────────────────────────┘ │ walk-forward ML strategy · artifacts   │
                                                                        └────────────────────────────────────────┘
   ┌──────────────────────────────────────────────────────────────────────────────────────────────────────────┐
   │ Infrastructure: EF Core + Npgsql (PostgreSQL) · migrations · DatabaseInitializer · Identity stores         │
   │ AuditLog · AdminBootstrapper · data-protection keys in DB · LocalModelArtifactStore                       │
   │ market-data providers (Yahoo, Alpha Vantage, Mock) + resilience · job queue/worker · scheduled refresh     │
   └──────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

| Project | Responsibility | Depends on |
|---|---|---|
| `PortfolioOptimizer.Optimization` | Metrics, covariance, QP solver, mean-variance optimizer, frontier, `BacktestEngine`, the shared `PortfolioSimulator` and `StrategyMetrics`. No I/O. | MathNet.Numerics |
| `PortfolioOptimizer.MachineLearning` | Features, labels, purged chronological splits, models, evaluation, explainability, ML portfolio construction, walk-forward ML strategy, artifact format. No I/O, no database. Must not use the mean-variance optimizer (architecture test). | Optimization (simulator and shared statistics), Microsoft.ML, Microsoft.ML.FastTree |
| `PortfolioOptimizer.Domain` | Entities and business rules; users, audit and ML entities. | – |
| `PortfolioOptimizer.Application` | Use cases, DTOs, validation, owner scoping, orchestration of jobs. | Domain, Optimization, MachineLearning |
| `PortfolioOptimizer.Infrastructure` | `AppDbContext` (Identity + data protection), migrations, seeding, Identity services, audit log, artifact store, providers, background worker. | Application |
| `PortfolioOptimizer.Api` | Controllers, authorization policies, antiforgery filter, rate limits, security headers, Swagger, health checks, `migrate` and `purge-test-data` commands, e2e environment marker. | Application, Infrastructure |
| `tools/universe-builder` | Builds index constituent CSVs from pinned Wikipedia revisions (Python, standard library). | – |
| `tools/PortfolioOptimizer.DataMigration` | One-off SQLite → PostgreSQL importer (binary COPY, one transaction, balance checks). | Microsoft.Data.Sqlite, Npgsql |

## Design notes

- **Persistence boundary.** Services use `IApplicationDbContext` (EF Core `DbSet`s). PostgreSQL-specific mappings
  (`jsonb`, `text[]`, `timestamptz`) live in `AppDbContext`. Decimal ordering and aggregation now run in SQL.
- **Ownership.** `Portfolio`, `OptimizationRun`, `BacktestRun`, `MlExperiment` and `StrategyComparison` have an
  `OwnerId`; services filter by `ICurrentUser`. Catalogue, prices and settings are shared.
- **Background jobs.** Price syncs, backtests, ML experiments and comparisons are persisted rows, queued on an
  in-process `Channel` and processed sequentially by `BackgroundJobWorker`. HTTP returns `202` with a status URL; the
  UI polls. Queued jobs resume after a restart; interrupted ones are marked failed.
- **One simulator.** `PortfolioSimulator` executes any decision rule (classical re-optimization, ML strategy, equal
  weight, hybrid) on a `PriceGrid`: signals at the close of day *t* see data ≤ *t*, fills at the next close,
  proportional costs, cash at 0%. Extracting it from `BacktestEngine` was verified to produce bit-identical results
  for nine existing backtest configurations. Strategy comparisons and ML backtests are therefore comparable by
  construction.
- **ML isolation.** The ML engine works on in-memory panels and returns plain results; the application layer loads
  data, stores experiments, model versions and prediction sets, and writes artifacts through `IModelArtifactStore`.
- **Money vs. math.** Money and quantities are `decimal`; numerical code is `double`.

## Data model (additions)

| Entity | Notes |
|---|---|
| `ApplicationUser` / Identity tables | status (Pending/Approved/Rejected/Suspended), display name, created/last sign-in, who changed the status, when and why |
| `AuditEvent` | action, actor, target, details, success, IP, time (written via a separate context) |
| `DataProtectionKey` | ASP.NET Core data-protection key ring |
| `Asset` (extended) | industry, description, metadata source and update time (catalogue) |
| `MlExperiment` | owner, configuration (jsonb), universe, status, data window and snapshot hash, results (jsonb) |
| `MlModelVersion` | role (Evaluation/Deployment), hyperparameters, feature set, training window, metrics, artifact path, SHA-256, size |
| `MlPredictionSet` | predictions for one as-of date by one model version, plus the derived ML portfolio |
| `StrategyComparison` | owner, frozen inputs, referenced run/experiment, data snapshot hash, results (immutable) |

Existing entities (prices, quotes, portfolios, holdings, ledger, proposals, orders, optimization runs, backtests) are
unchanged apart from `OwnerId` and higher precision for average cost (28,12).

## Simulated execution safety

Unchanged: proposals never change holdings; execution requires `confirm: true` and an `Idempotency-Key`; the
portfolio `Version` guards against stale proposals; cash, holdings, ledger and orders are written in one transaction.
Executions are now also audited.

## Security

See [security](security.md): administrator approval, per-request status checks, owner scoping, antiforgery, rate
limits, least-privilege database role, audit trail.

## Market data flow (2026-10-14)

* **Universes:** `UniverseSeeder` loads `Seed/universes/*.csv` (or an override directory) through `UniverseService`
  into `MarketIndices`, `IndexMemberships` and `Assets`; administrators can replace a constituent list via the API.
* **Quotes:** `QuoteRefreshService` is the single backend path to providers for quotes – a de-duplicated background
  cycle per sign-in/schedule/admin request (batch requests via `IMarketDataProvider.GetLatestQuotesAsync`), and
  synchronous page/single refreshes with cache window and cooldown. Results land in `LatestQuotes`; cycles are
  recorded in `QuoteRefreshRuns`.
* **Symbols and units:** canonical Yahoo-style symbols, translated per provider by `SymbolMapper`; minor currency
  units converted by `CurrencyUnits` at the provider boundary.
* **Analyses:** `UniverseCurrency` enforces single-currency universes and a matching benchmark;
  `MarketDataService.EnsureHistoryAsync` queues missing history before ML experiments and comparisons (the worker runs
  jobs in order).

## Frontend structure

* `pages/` – one route each (`stocks`, `optimize`, `machine-learning`, `strategy-comparison`, `learn`, `admin`,
  `admin-overview`, …); `components/` – shared UI (`universe-nav`, `stock-selection`, `strategy-insights`, charts,
  tables, shadcn/Base UI primitives).
* `lib/` – pure, unit-tested logic: `api.ts` (fetch + antiforgery), `auth.tsx`, `selection.tsx` (memory only),
  `queries.ts` (TanStack Query hooks), `identity.ts` (header labels), `quote-status.ts`, `interpretation.ts`
  (result explanations), `strategy-guide.ts` + `worked-examples.ts` (12-part strategy explanations), `glossary.ts`.
* Business logic stays out of React components; components render what `lib/` computes.

See also [decisions.md](decisions.md).
