---
name: platform-architecture
description: MatrixView's backend and frontend plumbing – layer boundaries, API contracts (controllers, DTOs, Problem Details, frontend types and query hooks), authentication/authorization/antiforgery/rate limits, PostgreSQL persistence and EF migrations, the in-process background job worker, health checks and logging, deployment configuration, the absence of a cache layer – and the frontend's standard for long lists and large matrices (SearchableTable, PannableArea, windowed rendering). Use when adding or changing an endpoint, entity, migration, job, auth rule, frontend data hook or any list/table/matrix that can grow, or when diagnosing request failures, job failures or slow pages.
---

# Platform architecture

Docs: `docs/architecture.md`, `docs/database.md`, `docs/security.md`, `docs/performance.md`, `docs/deployment.md`,
`docs/quant/subsystem-inventory.md` (process review).

## Layers and dependencies
`Domain` ← `Application` (use cases, DTOs, validation; sees `IApplicationDbContext`, `ICurrentUser`) ← `Infrastructure`
(EF Core/Npgsql, Identity, providers, jobs) ← `Api` (controllers under `/api/v1`). `Optimization` and `MachineLearning`
are pure (no I/O); `MachineLearning` never references the mean-variance optimizer, `Optimization` never references ML.

## API contract
* Controller (`src/PortfolioOptimizer.Api/Controllers`) → application service → DTO (`src/PortfolioOptimizer.Application/Dtos`).
  Mirror every DTO in `frontend/portfolio-dashboard/src/lib/types.ts` and add a hook in `lib/queries.ts`.
* Errors: `ValidationException` (400), `NotFoundException` (404), `ForbiddenException` (403), `ConflictException`
  (409), `DomainException` and `FxRateUnavailableException` (422) → Problem Details by
  `src/PortfolioOptimizer.Api/Infrastructure/ApiExceptionHandler.cs`.
* Quantitative fields are fractions (0.05 = 5 %), money is `decimal` in the stated currency, dates are ISO dates,
  undefined metrics are `{ value: null, reason }` – a contract change that alters units is a breaking change.

## Security (enforced on the server)
Default policy: approved account (status checked per request); admin endpoints: `Administrator` policy
(`src/PortfolioOptimizer.Api/Security/Authorization.cs`); unsafe methods need `X-XSRF-TOKEN`
(`AntiforgeryTokens.cs`; filter registered in `Program.cs`); expensive endpoints `[EnableRateLimiting(RateLimitPolicies.Expensive)]`;
sign-in 10/min/IP. Owned entities are filtered by `OwnerId` in every query (another user's id → 404) – add a cross-user
test for every new owned entity. Never log or return credentials or personal data.

## Persistence
`src/PortfolioOptimizer.Infrastructure/Persistence/AppDbContext.cs` (indexes, e.g. unique `(AssetId, Source, Date)` for
prices); migrations in `Persistence/Migrations` (create with `dotnet ef migrations add`, then `dotnet build`); applied by
the API's `migrate` command (`migrate` compose service) with an owner role, the API runs with a DML-only role. Query
shape rules (no per-row `HistoricalPrices.Any`, batched price queries): `docs/performance.md`. **There is no cache
layer** (no Redis, no response cache) – fix queries first; measure with `PERF_BENCH=1` (`PerformanceBenchmarkTests`).

## Background work
`src/PortfolioOptimizer.Infrastructure/Jobs/BackgroundJobWorker.cs` with `ChannelBackgroundJobQueue.cs`: one worker,
sequential; on start, `Running` jobs are marked failed and `Queued` jobs re-enqueued. Hosted services also run
`ScheduledMarketDataRefresh` and the news refresh. Jobs must load their entities owner-scoped and be safe to re-run.

## Observability and configuration
`ILogger` with `RequestLoggingMiddleware.cs`; `/health` (liveness) and `/health/ready` (database) in `Program.cs`; no
metrics or tracing exporter. Configuration via environment (`.env`, never printed or committed), e.g.
`ConnectionStrings__DefaultConnection`, `ModelArtifacts__RootPath`; see `docs/deployment.md`.

## Frontend lists and large grids (owner's requirement, 2026-10-29)
Any list that can grow with the user's data (stocks of a universe, an allocation, predictions, eligibility, catalogue,
history) is never a plain `<ul>` or an unpaged table. Universes reach hundreds of stocks (largest stored Math run: 228).
* Use `SearchableTable` from `frontend/portfolio-dashboard/src/components/data-table.tsx`: search box with the row count
  (or a `breakdown` that counts kinds apart, see `/market-data-fx` and `lib/instrument-counts.ts`), sortable
  `DataTable`, paging with `RangePager` (rows per page 25/50/100/200, first/previous/next/last) and `maxRows`, so the
  table scrolls under a fixed header. In-page lists: `pageSize={25}`, `maxRows` 8–12, `dense`; full-page lists keep the
  defaults (50/15). Give it a stable `caption` – e2e specs find tables by it ("Recommended allocation", "Stocks in the
  universe"). Its search box never submits a surrounding form (Enter is swallowed) – the optimization forms rely on it.
* Inside a grid or flex column wrap it in `min-w-0` (a `<fieldset>` is as wide as its content by default: the universe
  table once widened the phone page by 9 px). Check 390 px for horizontal scroll.
* Data for many named stocks: `useStocksBySymbols` (one request per 200 symbols, the API's page limit). A single
  `useStocks({ symbols, pageSize: 200 })` silently drops everything after 200.
* Two-dimensional content larger than the screen (correlation matrix): `PannableArea` (`components/pannable-area.tsx`
  – both scroll bars, mouse drag pans, touch keeps native scrolling, focusable region), labels pinned with `sticky` on an
  **opaque** surface (`bg-popover`; cards are translucent over the world map), a "Full screen" button opening the same
  view in a viewport-sized `Dialog`, and **windowed rendering** – only the visible cells plus overscan
  (`lib/matrix-window.ts`). Rendering all 52,000 cells of a 228-stock matrix took 5.9 s to open; the window takes 0.2 s.
  Keep the card's data-table view as the accessible source of every value.
* Tests: `data-table.test.tsx` (SearchableTable), `pannable-area.test.tsx`, `correlation-heatmap.test.tsx`,
  `lib/matrix-window.test.ts`.

## Making a change safely
1. New endpoint: controller → service → DTO → `types.ts` + `queries.ts` → integration test with authorization
   (anonymous 401, other user 404) in `tests/PortfolioOptimizer.IntegrationTests` (`ApiFactory`).
2. New entity/column: migration + `dotnet build`; owned entity gets `OwnerId` and a cross-user test.
3. Frontend: guards are cosmetic; pages under `/portfolios/:id/…` take the portfolio from the URL; long lists and big
   matrices follow "Frontend lists and large grids" above.
4. Run `/ship-change`.

## Tests and diagnosis
```bash
./scripts/test.sh --filter "FullyQualifiedName~AuthTests|FullyQualifiedName~ApiTests|FullyQualifiedName~DataAccessEfficiencyTests"
PERF_BENCH=1 ./scripts/test.sh --filter PerformanceBenchmarkTests
cd frontend/portfolio-dashboard && npm test && npm run build && npm run lint
```
Request failures: Problem Details body + API logs (`podman compose logs api`); job failures: the job's `Error`/`Message`
column; slow pages: SQL count per request in `PerformanceBenchmarkTests`, `scripts/bench/api_bench.py`.

## Known limitations
One API instance (no shared queue), local artifact storage, no e-mail confirmation/MFA, Playwright not in CI.

## Related
`/run-app`, `/ship-change`, `/maintain-knowledge`, `/portfolio-accounting`, `/market-data-fx`.
