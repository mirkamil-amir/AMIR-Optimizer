# AGENTS.md – instructions for coding agents

AMIR Optimizer: an educational portfolio-research platform. Students browse stocks from five markets, optimize
portfolios (mean-variance), train ML ranking models, compare strategies fairly and paper-trade. .NET 10 API +
PostgreSQL + React/Vite. **No real orders, not investment advice, never claim profitability or real-time prices.**
Read this file before changing code; details are in `docs/` (linked below).

## Repository map

| Path | What | Entry points |
|---|---|---|
| `src/PortfolioOptimizer.Api` | controllers, auth policies, antiforgery, rate limits, commands | `Program.cs` (`migrate`, `purge-test-data`), `Controllers/*`, `Security/*` |
| `src/PortfolioOptimizer.Application` | use cases (owner-scoped), DTOs, validation | `MarketData/` (quotes, universes, discovery, `Markets.cs`), `Optimization/`, `MachineLearning/`, `Comparisons/`, `Dtos/` |
| `src/PortfolioOptimizer.Domain` | entities, rules | `MarketData.cs`, `Portfolio.cs`, `Runs.cs`, `MachineLearning.cs`, `Users.cs` (roles, audit, `TestDataPolicy`) |
| `src/PortfolioOptimizer.Optimization` | pure math: QP solver, mean-variance, `PortfolioSimulator` | `Portfolio/MeanVarianceOptimizer.cs`, `Backtesting/*` |
| `src/PortfolioOptimizer.MachineLearning` | pure ML (no I/O, no optimizer use) | `Training/ExperimentEngine.cs`, `Features/FeatureCatalog.cs` |
| `src/PortfolioOptimizer.Infrastructure` | EF Core/Npgsql, Identity, providers, jobs, seeding | `Persistence/AppDbContext.cs`, `MarketData/*Provider.cs`, `Jobs/BackgroundJobWorker.cs`, `Persistence/UniverseSeeder.cs`, `Seed/universes/*.csv` |
| `frontend/portfolio-dashboard/src` | React app | `main.tsx` (routes), `pages/*`, `components/*`, `lib/` (api, auth, selection, queries, types, interpretation, strategy-guide, glossary, quote-status) |
| `tests/*` | unit, ML, integration (real PostgreSQL) | `IntegrationTests/ApiFactory.cs` |
| `frontend/.../e2e` | Playwright | `admin.setup.ts` (environment guard), `journeys.spec.ts` |
| `tools/` | `universe-builder` (constituent CSVs), `PortfolioOptimizer.DataMigration` (SQLite import) | |

## Run and test

Toolchain: `export PATH="$HOME/.dotnet:/opt/homebrew/opt/node@22/bin:$PATH" DOTNET_ROOT="$HOME/.dotnet"`.
Secrets live in `.env` (gitignored) – load with `set -a; . ./.env; set +a`; never print or commit them.

* Stack: `podman compose up --build -d` → web http://localhost:8081, API http://localhost:5080/swagger.
* Local: `podman compose up -d db`; API `dotnet run` in `src/PortfolioOptimizer.Api` with `ConnectionStrings__DefaultConnection` (owner role) → :5080; `npm run dev` in the frontend → :5173.
* Tests: `./scripts/test.sh` (unit + ML + integration), `npm test`, `npm run build`, `npm run lint`, `./scripts/e2e.sh` (disposable stack). Details: [docs/testing.md](docs/testing.md).
* Migration: `ConnectionStrings__DefaultConnection=… dotnet ef migrations add <Name> -p src/PortfolioOptimizer.Infrastructure -s src/PortfolioOptimizer.Api -o Persistence/Migrations`, then `dotnet build`.

## Business rules you must not break

* **Auth:** every endpoint requires an approved account unless explicitly anonymous; admin endpoints use the
  `Administrator` policy; status is checked per request. Unsafe methods need `X-XSRF-TOKEN`. Never rely on frontend
  guards for security.
* **Ownership:** `Portfolio`, `OptimizationRun`, `BacktestRun`, `MlExperiment`, `StrategyComparison` are filtered by
  `OwnerId` in every query; another user's id → 404. Add a cross-user test for every new owned entity.
* **Temporary vs persisted:** the stock selection is browser memory only (`lib/selection.tsx`) – never persist it.
  Runs store their own inputs. Quotes/prices/catalogue are shared.
* **Prices:** never fabricate or zero-fill; label kind (Delayed quote / Last close), as-of time, retrieval time,
  cached/stale/SIMULATED. Never say "live". Store prices in ISO major units (`CurrencyUnits`). Never mix sources.
* **Analyses:** one currency per universe (`UniverseCurrency`); weights are validated; infeasible → explanation.
* **ML:** features ≤ t, labels t+1…t+h, chronological purged splits, training-only preprocessing, one test
  evaluation; ML must not use the mean-variance optimizer; new feature formulas → new `FeatureCatalog.Version`.
* **Education content:** explanations must match the code. Interpretations (`lib/interpretation.ts`) only state what
  the stored numbers support, labelled fact/estimate/interpretation/caution.
* **Test data:** tests never run against real databases. e2e only via `scripts/e2e.sh` (guard endpoint
  `/api/v1/system/test-environment`); test accounts use the `.test` domain.
* Money `decimal`, math `double`; errors via `ValidationException`/`NotFoundException`/`ConflictException`/`ForbiddenException`.

## How to …

* **Add a market or index:** add the venue to `Markets.Venues` (suffix, MIC, currency, time zone, Alpha Vantage
  suffix or null) with `SymbolMapper` tests; add a parser + sector mapping in `tools/universe-builder/build_universes.py`,
  run it, validate symbols, commit the CSV (it is re-imported automatically on start). Benchmarks:
  `UniverseSeeder.Benchmarks`. Docs: [market-data.md](docs/market-data.md).
* **Update constituents:** admin upload, `Universes__Directory`, or re-run the builder (see market-data.md).
* **Add a data provider:** implement `IMarketDataProvider` (incl. `Supports`, `QuoteBatchSize`, `RemainingDailyRequests`,
  batch quotes, currency normalization), register it in `Infrastructure/DependencyInjection.cs`, unit-test parsing
  from recorded responses, add an opt-in live smoke test.
* **Add a mathematical strategy:** see [quantitative-strategies.md](docs/quantitative-strategies.md#implemented-strategies)
  (optimizer + tests + label + guide entry + interpretation branch).
* **Add an ML model:** implement `IModelTrainer` in `MachineLearning/Models`, register it in `ModelRegistry`, add a
  default grid, determinism and artifact round-trip tests, a guide entry and `guideForModel` mapping.
* **Add an endpoint:** controller → application service → DTO; mirror types in `lib/types.ts` and hooks in
  `lib/queries.ts`; integration test incl. authorization.

## Prohibited shortcuts

Hard-coding market data or prices; calling providers from the browser; exposing or logging API keys; disabling
authorization/antiforgery "for testing"; running e2e or seeding against a non-test database; deleting user data
without a dry run, backup and audit; presenting in-sample or training metrics as performance; bypassing provider bot
protection (Stooq); force-pushing or committing without being asked.

## Known pitfalls

* Repo in iCloud-synced `~/Desktop`: `node_modules` is a symlink to `node_modules.nosync`; after `npm ci` restore it
  (docs/testing.md). Prefer `./node_modules/.bin/<tool>` over `npx`. Use `git grep` – `grep -r` over `bin/obj` can hang.
* A local API on :5080 blocks the container API; the Vite dev server proxies to whichever owns :5080.
* Sign-in is rate limited (10/min/IP; e2e stack raises it). Max 2 active ML experiments per user (409).
* Yahoo spark: ≤ 20 symbols; occasional HTTP 400 handled by retry/fallback. Alpha Vantage free: 25 requests/day.
* zsh does not word-split `$var` in scripts.
* Container commands default to Production: `purge-test-data` refuses there unless `--allow-production`.

## Documentation

[README](README.md) · [architecture](docs/architecture.md) · [database](docs/database.md) ·
[market data](docs/market-data.md) · [quantitative strategies](docs/quantitative-strategies.md) ·
[machine learning](docs/machine-learning.md) · [security](docs/security.md) · [testing](docs/testing.md) ·
[deployment](docs/deployment.md) · [implementation status](docs/implementation-status.md) ·
[decisions](docs/decisions.md) · [limitations](docs/limitations.md)
