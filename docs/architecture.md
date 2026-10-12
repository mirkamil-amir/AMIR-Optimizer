# Architecture

## Overview

```
┌──────────────────────────┐   /api/v1 (JSON, Problem Details)   ┌──────────────────────────────┐
│ React + TS dashboard      │ ──────────────────────────────────▶ │ ASP.NET Core Web API (.NET 10)│
│ Vite · shadcn/ui (Base UI)│   dev: Vite proxy / prod: nginx     │ Controllers · Swagger UI      │
│ TanStack Query · Recharts │                                     └──────────────┬───────────────┘
└──────────────────────────┘                                                    │ DI
                                     ┌──────────────────────────────────────────▼───────────────┐
                                     │ Application: use-case services, DTOs, validation          │
                                     │  MarketDataService · PortfolioService · OptimizationService│
                                     │  TradingService · PortfolioAnalyticsService · Backtest…    │
                                     └──────┬───────────────────────────┬────────────────────────┘
                                            │                           │
                      ┌─────────────────────▼──────┐        ┌───────────▼──────────────────────┐
                      │ Domain: entities & rules    │        │ Optimization (pure math)          │
                      │ Portfolio · Holding (WAC)   │        │ returns · metrics · covariance    │
                      │ ledger · proposals · runs   │        │ ADMM QP solver · MV optimizer     │
                      └─────────────────────────────┘        │ efficient frontier · backtester   │
                                                             └───────────────────────────────────┘
                      ┌────────────────────────────────────────────────────────────────────────┐
                      │ Infrastructure: EF Core (SQLite) · migrations · seeding                 │
                      │ Market data providers (Yahoo, Alpha Vantage, Mock) + resilience/throttle│
                      │ Background job queue (Channel) · job worker · scheduled refresh         │
                      └────────────────────────────────────────────────────────────────────────┘
```

| Project | Responsibility | Depends on |
|---|---|---|
| `PortfolioOptimizer.Optimization` | Pure numerical code: return/risk metrics, covariance estimators, QP solver, mean-variance optimizer, efficient frontier, backtest engine. No I/O. | MathNet.Numerics |
| `PortfolioOptimizer.Domain` | Entities and business rules (cash/holding accounting, cost model). | – |
| `PortfolioOptimizer.Application` | Use cases, DTOs, request validation, orchestration, provider/queue/persistence abstractions. | Domain, Optimization, EF Core abstractions |
| `PortfolioOptimizer.Infrastructure` | EF Core DbContext + migrations, providers, HTTP resilience, background services, seeding. | Application |
| `PortfolioOptimizer.Api` | HTTP endpoints, Problem Details, Swagger, health checks, CORS, rate limiting, request logging. | Application, Infrastructure |

Design notes:

* **Persistence boundary.** Application services use `IApplicationDbContext` (EF Core `DbSet`s) instead of a
  repository layer – a deliberate trade-off to avoid boilerplate. Nothing in the application layer is SQLite-specific.
* **Provider abstraction.** `IMarketDataProvider` (history + latest quote) with an `IMarketDataProviderRegistry`.
  Providers normalize data through the shared `PriceNormalizer`.
* **Optimization isolation.** `IQuadraticProgramSolver` and `IPortfolioOptimizer` isolate the numerical engine;
  a different solver (e.g. a native OSQP binding) can be swapped in without touching application code.
* **Long-running work.** Market-data syncs and backtests are persisted as jobs, queued on an in-process
  `Channel`, and processed by a `BackgroundService`. HTTP returns `202 Accepted` with a status URL. On restart,
  queued jobs are re-enqueued and interrupted jobs are marked failed.
* **Money vs. math.** Monetary amounts and quantities are `decimal`; matrix math is `double`. Conversion happens at
  explicit boundaries (price series → returns; weights → target values).

## Data model

| Entity | Notes |
|---|---|
| `Asset` | Symbol (unique), name, exchange, currency, type, sector, enabled flag |
| `HistoricalPrice` | One row per (asset, source, date) – unique index. Raw OHLC + `AdjustedClose`, volume, source, retrieval time, simulated flag |
| `LatestQuote` | Latest quote per (asset, source) |
| `MarketDataSyncJob` / `DataSyncLog` | Download jobs and per-asset data-quality counters (received, inserted, updated, rejected, duplicates) |
| `ApplicationSettings` | Single row: active provider, default currency/benchmark/risk-free rate, staleness threshold |
| `Portfolio` | Cash balance, JSON settings (universe, constraints, costs…), concurrency `Version` |
| `Holding` | Quantity, weighted-average cost, realized P&L |
| `CashLedgerEntry` / `TradeExecution` | Immutable, append-only ledger |
| `OptimizationRun` (+ `OptimizationRunWeight`) | Inputs snapshot, data window, data-snapshot hash, outputs, solver diagnostics, analytics JSON |
| `TradeProposal` (+ lines) | Pending/Executed/Superseded; portfolio version at creation |
| `PaperTradeBatch` / `SimulatedOrder` | One confirmed execution, unique per (portfolio, Idempotency-Key) |
| `BacktestRun` | Config JSON, status, result JSON |

## Consistency & safety of simulated execution

1. A proposal never changes holdings. Execution is a separate `POST /paper-trades` that requires `confirm: true`
   and an `Idempotency-Key` header.
2. The same key returns the stored result (`idempotentReplay: true`); a different key for an executed proposal → 409.
3. A proposal is refused if the portfolio `Version` changed since it was created (holdings changed) → 409.
4. Cash, holdings, executions, ledger entries, orders and the proposal status are written in one DB transaction;
   `Portfolio.Version` is an optimistic-concurrency token.
5. Buys are capped at available cash (after fees) at the execution price; sells at the held quantity.

## Security posture

This is a **local, single-user** application: there is no authentication or authorization, and all portfolios are
visible to anyone who can reach the API. Do not expose it to a network without adding authentication and
user-scoped data access. Secrets (Alpha Vantage key) are read from server configuration/environment variables only
and are never returned by the API; provider error messages redact the key.
