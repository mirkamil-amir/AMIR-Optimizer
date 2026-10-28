# Subsystem inventory and process review

What exists, where it is implemented, its invariants and how to diagnose it – checked against the code on
2026-10-28. Architecture overview: [architecture.md](../architecture.md); formulas:
[formula-reference.md](formula-reference.md). The matching skill is given per subsystem (`.claude/skills/<name>`).

| Subsystem | Exists | Implementation | Skill |
|---|---|---|---|
| Web frontend (React 19, Vite, TanStack Query, Recharts) | yes | `frontend/portfolio-dashboard/src` | platform-architecture |
| HTTP API (.NET 10, controllers under `/api/v1`, Problem Details) | yes | `src/PortfolioOptimizer.Api` | platform-architecture |
| Use cases, DTOs, validation | yes | `src/PortfolioOptimizer.Application` | per area below |
| Pure optimization, statistics, backtesting | yes | `src/PortfolioOptimizer.Optimization` | optimization-backtesting, quant-formulas |
| Pure machine learning | yes | `src/PortfolioOptimizer.MachineLearning` | ml-pipeline |
| Persistence (PostgreSQL 17, EF Core/Npgsql, migrations) | yes | `src/PortfolioOptimizer.Infrastructure/Persistence` | platform-architecture |
| Market-data providers (Yahoo, Alpha Vantage, Mock) | yes | `src/PortfolioOptimizer.Infrastructure/MarketData` | market-data-fx |
| FX providers (ECB reference rates, MockFx) | yes | `src/PortfolioOptimizer.Infrastructure/Fx/FxProviders.cs` | market-data-fx |
| Background jobs (in-process queue, one worker) | yes | `src/PortfolioOptimizer.Infrastructure/Jobs` | platform-architecture |
| Scheduled refresh (prices, news) | yes | `Jobs/ScheduledMarketDataRefresh.cs`, `News/NewsRefreshWorker.cs` | market-data-fx |
| Auth (cookies, approval, roles, antiforgery, rate limits) | yes | `src/PortfolioOptimizer.Api/Security`, `Infrastructure/Identity` | platform-architecture |
| Paper portfolio ledger and trading | yes | `Domain/Portfolio.cs`, `Application/Portfolios`, `Application/Trading` | portfolio-accounting |
| AI research workflow | yes | `Application/Research` | (docs: [agentic architecture](../agentic-architecture.md)) |
| Cache layer (Redis or other) | **no** – by design, queries are fixed first ([performance](../performance.md)) | – | – |
| Metrics/tracing (OpenTelemetry etc.) | **no** – `ILogger` logs and `/health`, `/health/ready` only | `Api/Program.cs` | platform-architecture |
| CI | added 2026-10-28: `.github/workflows/quant-maintenance.yml` (not yet run on GitHub) | – | maintain-knowledge |

## Quantitative paths (input → result)

1. **Math Optimization** – `POST /api/v1/portfolios/{id}/optimization-runs` → `OptimizationService.RunAsync`: owner-scoped
   portfolio → `FxService.GetSeriesInCurrencyAsync` (stored adjusted closes, active provider only, converted with dated
   rates, last `LookbackDays + 1` prices) → minimum-history filter → `CommonWindow.Choose` → `ReturnMatrix.Build`
   (intersection) → `CovarianceEstimator.Estimate` (μ, Σ annualized) → `MeanVarianceOptimizer.Optimize` →
   `AllocationValidator.Validate` → stored `OptimizationRun` (weights, analytics JSON, data hash, diagnostics).
2. **Backtest / comparison** – queued job → `BacktestService` → `BacktestEngine.Run`, or `StrategyComparisonService` →
   `StrategyComparisonEngine` (`BacktestEngine.CreateDecisionRule` or the ML rule) – both on the shared
   `PortfolioSimulator.Run` → `StrategyMetrics.Compute` / `PerformanceMetrics`.
3. **ML Optimization** – queued job → `MlExperimentRunner` → `MlDataLoader` (converted panel on the benchmark's span) →
   `ExperimentEngine.Run` (features, labels, split, models, metrics, walk-forward backtest) → stored experiment + artifact.
4. **Holdings, performance, risk** – `PortfolioAnalyticsService` (valuation via `PortfolioValuationService`, TWR replay of
   executions and cash ledger, historical-simulation risk).
5. **Trade proposals** – `TradingService` → `TradeProposalCalculator.Compute` → confirmed execution in the domain
   (`Portfolio` buy/sell, weighted-average cost) – the only path besides manual bookings that changes holdings.

## Process review

### Market-data ingestion (`MarketDataService`, providers)
*Workflow:* sync job → plan ranges per symbol (incremental from the latest stored date minus an overlap; full when
forced) → provider fetch with throttling (`ThrottlingHandler`) and retry/fallback (Yahoo spark ≤ 20 symbols) →
`PriceNormalizer.Normalize` → upsert by (asset, source, date). *Invariants:* unique index `(AssetId, Source, Date)`
(`AppDbContext`), one source per analysis, provider currency must equal the listing currency (else the bars are
rejected with an error), adjusted-close changes beyond a tolerance trigger a full re-download (new dividend/split).
*Failure modes:* HTTP 429/400 from Yahoo (logged per symbol, job continues), Alpha Vantage quota (25/day), currency
mismatch (listing suffix wrong). *Diagnose:* `DataSyncLog` rows per symbol (Admin → Data), `podman compose logs api`.

### FX (`FxService`, `FxDownloader`, `FxTable`)
*Workflow:* a conversion asks for the currencies and date span it needs; missing history is downloaded in 2-year chunks
(background, contiguous per currency, D-35), web requests wait ≤ 25 s, jobs ≤ 5 min. *Invariants:* rates stored as
units per EUR; never a hole inside a currency's stored span; never 1.0 or today's rate for history; ≤ 4 days carry
forward. *Failure modes:* provider failure blocks the same chunk for an hour; long market closures make values "not
available". *Diagnose:* the conversion's `Notes`/`Excluded` reasons in diagnostics; `FxRates` table by currency.

### Background jobs (`BackgroundJobWorker`, `ChannelBackgroundJobQueue`)
*Workflow:* one in-process channel, sequential execution. On start, jobs left `Running` by a previous process are
marked failed ("Interrupted by application restart") and `Queued` ones are re-enqueued. *Invariants:* at most 2 active
ML experiments per user (409); each job loads its owner-scoped entities. *Failure modes:* a crash fails running jobs;
several API instances would each run a worker (no shared queue – documented limitation). *Diagnose:* job status and
`Error` columns; API logs.

### Persistence and migrations
PostgreSQL via EF Core; schema changes only through migrations (`dotnet ef migrations add …`), applied by the `migrate`
service (containers) or on Development start. Bookings are atomic and idempotent (unique `(PortfolioId,
IdempotencyKey)`), reversals unique per original. The development database holds the user's data: destructive SQL
needs a dry run, a `pg_dump` backup and an audit ([CLAUDE.md](../../CLAUDE.md)).

### API, auth and security boundaries
Every endpoint requires an approved account unless anonymous; admin endpoints use the `Administrator` policy; unsafe
methods need the antiforgery header; expensive endpoints are rate limited. Owned entities are filtered by `OwnerId`
(another user's id → 404). Provider keys stay server-side. Details: [security](../security.md).

### Frontend data flow
Server state through TanStack Query hooks (`lib/queries.ts`) over the API client (`lib/api.ts`, antiforgery, retries);
types mirror the DTOs (`lib/types.ts`). The stock selection is browser memory only (`lib/selection.tsx`). The frontend
does **not** compute financial results; it formats backend numbers (fractions → percent) and derives labelled
interpretations from them (`lib/interpretation.ts`); the only formulas are the teaching examples (F-EDU-001).

### Configuration that changes results
Risk-free rate, lookback, covariance method, constraints and costs are per-run settings (stored with the run).
Global: active market-data provider (admin setting; analyses never mix sources), `MarketData:*` options, stale-price days.
`TradingDaysPerYear` is a code constant (252).

## Lower-priority opportunities (not changed)
* Shared monthly-return helper (R-08); a test pinning the Overview "Total return" (R-05).
* Exchange holiday calendars would replace the "missing price = closed" inference (R-02).
* A shared job queue for more than one API instance.
