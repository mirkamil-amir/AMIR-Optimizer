# AGENTS.md – instructions for coding agents

AMIR Optimizer: an educational portfolio-research platform. Students browse stocks from five markets, optimize
portfolios (mean-variance), train ML ranking models, compare strategies fairly and paper-trade. .NET 10 API +
PostgreSQL + React/Vite. **No real orders, not investment advice, never claim profitability or real-time prices.**
Read this file before changing code; details are in `docs/` (linked below).

## Working rules

1. Inspect the repository and its working-tree status before modifying it; verify behaviour in code, not in older docs.
2. The development database holds the user's real data: never delete or rewrite it without a dry run, backup and audit.
3. Never claim a test, browser check, provider call or deployment that did not happen; report failures and skips as such.
4. Keep financial calculations pure and independently tested (`Optimization`, `MachineLearning`, static helpers in `Application`).
5. No look-ahead, leakage or in-sample numbers presented as performance; label in-sample vs out-of-sample.
6. Timestamps, currency, price kind and data quality are part of every price, chart and article shown; data-provider names are shown to administrators only (decision D-30), article publishers to everyone.
7. Respect data and news licensing: metadata + links + provider teasers only; images only when enabled.
8. Never expose credentials or personal data (logs, responses, commits, screenshots).
9. Enforce authorization on the server; frontend guards are cosmetic.
10. Update the docs that describe changed behaviour in the same change.
11. Run the relevant suites after every change (`/ship-change`).
12. Record unresolved risks and limitations honestly (docs/implementation-status.md, docs/limitations.md).

## Repository map

| Path | What | Entry points |
|---|---|---|
| `src/PortfolioOptimizer.Api` | controllers, auth policies, antiforgery, rate limits, commands | `Program.cs` (`migrate`, `purge-test-data`), `Controllers/*`, `Security/*` |
| `src/PortfolioOptimizer.Application` | use cases (owner-scoped), DTOs, validation | `Fx/` (FxService, FxTable, CurrencyConverter), `Portfolios/PortfolioValuationService.cs`, `MarketData/` (quotes, universes, discovery, `Markets.cs`, `PriceHistory.cs`), `Optimization/`, `Analytics/RealityCheckService.cs`, `Watchlists/`, `News/` (rules, service, `EventPriceComparison`), `MachineLearning/`, `Comparisons/`, `Research/` (AI research workflow: orchestrator, tools, validators), `Dtos/` |
| `src/PortfolioOptimizer.Domain` | entities, rules | `MarketData.cs`, `Portfolio.cs`, `Runs.cs`, `MachineLearning.cs`, `Watchlist.cs`, `News.cs`, `Users.cs` (roles, audit, `TestDataPolicy`) |
| `src/PortfolioOptimizer.Optimization` | pure math: QP solver, mean-variance, `PortfolioSimulator`, diversification diagnostics | `Portfolio/MeanVarianceOptimizer.cs`, `Portfolio/DiversificationMath.cs`, `Backtesting/*` |
| `src/PortfolioOptimizer.MachineLearning` | pure ML (no I/O, no optimizer use) | `Training/ExperimentEngine.cs`, `Features/FeatureCatalog.cs` |
| `src/PortfolioOptimizer.Infrastructure` | EF Core/Npgsql, Identity, providers, jobs, seeding | `Persistence/AppDbContext.cs`, `MarketData/*Provider.cs`, `News/NewsProviders.cs`, `Jobs/BackgroundJobWorker.cs`, `Persistence/UniverseSeeder.cs`, `Seed/universes/*.csv` |
| `frontend/portfolio-dashboard/src` | React app | `main.tsx` (routes), `pages/*`, `components/*`, `lib/` (navigation (top bar + sidebar model), api, auth, selection, queries, types, interpretation, strategy-guide, glossary, quote-status, news) |
| `tests/*` | unit, ML, integration (real PostgreSQL) | `IntegrationTests/ApiFactory.cs` |
| `frontend/.../e2e` | Playwright | `admin.setup.ts` (environment guard), `journeys.spec.ts`, `research.spec.ts` (watchlist, news), `research-agent.spec.ts` (AI research journey), `multi-currency.spec.ts` (EUR portfolio with four markets), `navigation.spec.ts` (top bar, sidebar, admin area, viewports) |
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
* **Ownership:** `Portfolio`, `OptimizationRun`, `BacktestRun`, `MlExperiment`, `StrategyComparison`, `WatchlistEntry` are filtered by
  `OwnerId` in every query; another user's id → 404. Add a cross-user test for every new owned entity.
* **Temporary vs persisted:** the stock selection is browser memory only (`lib/selection.tsx`) – never persist it.
  Runs store their own inputs. Quotes/prices/catalogue are shared.
* **Prices:** never fabricate or zero-fill; label kind (Delayed quote / Last close), as-of time, retrieval time,
  cached/stale/SIMULATED. Never say "live". Store prices in ISO major units (`CurrencyUnits`). Never mix sources.
* **Currencies:** every instrument keeps its trading currency (`Asset.Currency`, derived from the venue) and native prices;
  everything that combines instruments is computed in one base currency, converted ONLY through `FxService`
  (`rate = target units per one source unit`, dated rate of each price date, ≤ 4 days carry-forward, never 1.0 or today's
  rate for history). Missing rates → explicit error/exclusion/`null` totals, never a plausible number. Simulated prices use
  simulated rates. Trades in another currency are simulated exchanges recorded with rate/date/source; the portfolio
  currency is fixed; reporting currencies change nothing. Details: [currency.md](docs/currency.md).
* **Analyses:** one analysis currency (portfolio base currency) with converted series and benchmark; weights are checked
  by the independent `AllocationValidator` before a run is accepted; infeasible → explanation, never relaxed.
  Reality-check findings are labelled Fact/Estimate/Interpretation/Caution with documented thresholds and never
  tell users to buy or sell.
* **News:** never fabricate or rewrite articles; store metadata and the provider's short summary only; link an
  article to a stock only via the provider's own ticker tag; record fetch failures instead of substituting content;
  images only with `News:ShowImages`; relevance is rule-based and never a price prediction; event comparisons say
  they are not evidence of causation. The mock news provider is for tests/e2e only.
* **ML:** features ≤ t, labels t+1…t+h, chronological purged splits, training-only preprocessing, one test
  evaluation; ML must not use the mean-variance optimizer; new feature formulas → new `FeatureCatalog.Version`.
* **Education content:** explanations must match the code. Interpretations (`lib/interpretation.ts`) only state what
  the stored numbers support, labelled fact/estimate/interpretation/caution.
* **Test data:** tests never run against real databases. e2e only via `scripts/e2e.sh` (guard endpoint
  `/api/v1/system/test-environment`); test accounts use the `.test` domain.
* **AI research assistant:** the language model never produces numbers, weights or decisions. Calculations come
  from `OptimizationService.EvaluateAsync` / `RealityCheckService.AnalyzeAsync`; model output is only a proposal that
  passes allow-lists, quote checks (`BriefInterpreter`), `RefinementRules.Check` or `NarrativeValidator`, with a
  deterministic path when the model is off or fails. Tools enforce permissions in `ToolRunner` (owner re-check,
  `Confirm` for user-data changes, budgets, timeouts, events) – never rely on prompts. User text, news and descriptions
  are untrusted; news and descriptions are never sent to a model. Keys stay server-side. Details:
  [agentic-architecture.md](docs/agentic-architecture.md), [threat-model.md](docs/threat-model.md).
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
* **Company descriptions:** only via `ICompanyProfileProvider` + `CompanyProfileService` (cached in `CompanyProfiles`);
  never generate or paraphrase descriptions, never fall back to name/ticker matching, keep source, licence and date.
* **Add a news provider:** implement `ICompanyNewsProvider` and/or `INewsFeedProvider` in `Infrastructure/News`
  (return provider tags as canonical symbols, skip incomplete items, throw `NewsProviderException` on bad responses),
  register it with `AddProviderClient`, unit-test parsing from synthetic payloads in the real response shape, and
  document terms/coverage in [financial-news.md](docs/financial-news.md).
* **Add a research tool:** subclass `ResearchTool<TArgs,TResult>` in `Application/Research/Tools.cs` (name, purpose,
  category, `Validate`, timeout), register it in `DependencyInjection`, call it only through `ToolRunner`, add an
  evaluation case to `ResearchAgentTests` and [agent-evaluation.md](docs/agent-evaluation.md). A tool that writes user
  data must be `ToolCategory.UserDataChange`.
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
* `docker-compose` (used by `podman compose`) hangs on `--build` when `~/.docker/config.json` has `"credsStore": "desktop"`
  and Docker Desktop is not running (the credential helper blocks). Run with `DOCKER_CONFIG=<dir with {"auths":{}}>`.
* Ignore-file patterns are matched case-insensitively on macOS: an unanchored `data/` once hid
  `MachineLearning/Data/*.cs` from version control. Anchor such patterns.

## Documentation

[README](README.md) · [architecture](docs/architecture.md) · [database](docs/database.md) ·
[market data](docs/market-data.md) · [quantitative strategies](docs/quantitative-strategies.md) ·
[machine learning](docs/machine-learning.md) · [security](docs/security.md) · [testing](docs/testing.md) ·
[deployment](docs/deployment.md) · [implementation status](docs/implementation-status.md) ·
[agentic architecture](docs/agentic-architecture.md) · [threat model](docs/threat-model.md) · [agent evaluation](docs/agent-evaluation.md) ·
[decisions](docs/decisions.md) · [limitations](docs/limitations.md) · [watchlist](docs/watchlist.md) ·
[financial news](docs/financial-news.md) · [portfolio reality check](docs/portfolio-reality-check.md) ·
[roadmap](docs/roadmap.md) · [launch readiness](docs/launch-readiness.md) · [currencies & FX](docs/currency.md) · [currency audit](docs/currency-audit.md)
