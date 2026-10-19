# Performance

Audit, fixes and measurements of 2026-10-18/19 (decision D-33). Every number below was measured; conditions and limits are
stated with each table. Re-run the measurements with the tools at the end.

## Summary

Every page waited **2.4 s** for the navigation's data-status box: `/market-data/status` (and `/markets`, `/sectors` on
Portfolio Creation) counted "instruments with stored prices" with a correlated `EXISTS` per instrument, which PostgreSQL
executed as a **full scan of the price table for each of 912 instruments** (769,101 buffer reads). The status was also
re-requested every 60 s by every open page and carried 83 KB of per-instrument detail the box did not use. The news page
made the first viewer after each 30-minute window wait for every due source in sequence (**10.1 s**). Price helpers ran one
query per stock (N+1).

Fixes: one set-based query for "has prices" (2.4 s → 11–25 ms), a compact `/market-data/summary` for the navigation,
stored-articles-first news with a coalescing background refresh (10.1 s → 0.2 s), batched price queries with an
index-friendly "latest N per asset" query. **No cache and no Redis were added**: the slow paths were query shapes and
blocking provider calls, and PostgreSQL already is the shared, durable store for prices and news.

## Bottleneck inventory

| # | Symptom | Evidence | Root cause | Fix | Status |
|---|---|---|---|---|---|
| 1 | Every page took 3–4 s to settle | browser: slowest request on all 11 pages was `/market-data/status` (2.5–3.3 s); API median 2,410 ms | `Assets WHERE IsEnabled OR EXISTS(prices of asset)`; `EXPLAIN`: seq scan of 55k prices × 912 loops. Fresh statistics did not change the plan (tested in a rolled-back transaction) – the planner expects an early match for `EXISTS` | `PriceQueryService.AssetIdsWithPricesAsync` (one grouped query, 8.8–13 ms) used by status, markets, sectors, the "with price history" filter and the admin overview | done, measured |
| 2 | Portfolio Creation waited 2.4 s twice | `/markets` 2,474 ms, `/sectors` 2,407 ms | same pattern (count per index member / per sector) | same | done, measured |
| 3 | 83 KB + 2.4 s per page per minute for a 4-line box | sidebar used the full status; `refetchInterval` 60 s (2 s during a sync) | wrong granularity | `GET /market-data/summary` (counts, latest retrieval, running sync; one aggregate) for the sidebar and the portfolio overview card; same polling policy | done, measured |
| 4 | First news view after each refresh window took ~10 s | measured 10,147 ms; fetch states showed 9 sources fetched sequentially inside the request | provider fetches in the request path | never-fetched sources still fetched in the request; due sources refreshed by `NewsRefreshWorker` (one run at a time, concurrent requests merged); `refreshing` flag + 3 s re-check in the UI; one query for all fetch states | done, measured |
| 5 | Many SQL commands per request | in-process benchmark: risk 45, holdings 30, reality check 30, optimization 26 | one query per stock in `GetSeriesAsync`, `GetDailyObservationsAsync`, `GetLatestPricesAsync`, `LastTwoClosesAsync` | batched queries; "latest N per asset" via `IApplicationDbContext.LatestPricesPerAsset` (LATERAL + index) | done, measured |
| 6 | First batching attempt made valuation 2–3× slower | stack benchmark: risk 33 → 89 ms, holdings 23 → 57 ms | EF translated "take N per group" into a window function over all prices of the source (25.8 ms vs 0.4 ms) | replaced by the LATERAL query in Infrastructure | fixed, re-measured |
| 7 | An optimization takes 0.6–1.5 s | timing inside `EvaluateAsync`: data + estimation + solve ≈ 20–40 ms; analytics (efficient frontier, 30 QP solves) ≈ 95 % | ADMM convergence per frontier point on real covariance matrices | parallel frontier tried and **reverted**: first call faster, median slower (643–658 ms sequential vs 777–812 ms parallel; MathNet already multi-threads) | open (see remaining work) |
| 8 | Reality check | rate-limited endpoint (30/min/user): stack benchmark repetitions partly measured 429 responses | – | measured in-process instead | measured in-process only |

## Redis decision matrix

The application runs one API instance (compose); rate limits, news single-flight and the job queue are in-process by design, and PostgreSQL is the store of record for prices, quotes, news and runs.

| Candidate | Decision | Reason |
|---|---|---|
| Financial news responses | **Do not implement** | Feed queries are 5–8 ms (median); the 10 s was provider latency in the request path, fixed by background refresh. Articles already live in PostgreSQL with source, publication and fetch times; a second cache would duplicate them and add a staleness layer. |
| Quotes, price history, company profiles | **Do not implement** | Stored in PostgreSQL by the sync/quote jobs with source and retrieval time; reads are 3–25 ms. Provider quotas are protected by existing per-source cooldowns and the batch refresh cycle. |
| Market/sector/coverage counts | **Do not implement** | Were 2.4 s because of the query shape; now 11–25 ms. Caching would have hidden the defect. |
| Optimization / reality-check results | **Do not implement** | Saved runs are persisted (PostgreSQL); an identical recomputation needs the same settings, universe, data snapshot hash, currency and code version – rare in this workload, and a cache would add invalidation risk to financial results. |
| AI research / ML | **Do not implement** | Sessions and experiments are durable rows; the deterministic path has no external model latency to cache; caching model output would need prompt/model/user-scoped keys for little gain. |
| Rate limiting, job coordination | **Defer** | Only needed with several API instances; then move rate limits, the news single-flight and the job queue together. |
| Session state | **Do not implement** | Cookie authentication; data-protection keys are already persisted. |

Revisit when the deployment runs more than one API instance or when measurements show repeated identical expensive reads.

## Measurements

**Environment:** MacBook (8 cores, 8 GB RAM); Podman VM 4 CPUs, 8 GiB; containers from the `performance` branch; the
user's development database (1 user, 1 portfolio, 916 instruments, 44 tracked, 55,404 daily prices, Yahoo provider);
read-only GET requests after sign-in, waiting until the sign-in quote refresh had finished. Single machine, single user –
absolute numbers will differ elsewhere; the ratios of the fixed paths are large enough to be repeatable (seen on every one
of 21 calls per endpoint).

### API (stack, `scripts/bench/api_bench.py`, 1 first call + 20 repetitions)

| Endpoint | Median before | Median after | p95 before | p95 after |
|---|---:|---:|---:|---:|
| `/market-data/status` | 2,410.0 ms | 24.5 ms | 2,492.3 ms | 31.1 ms |
| `/markets` | 2,473.6 ms | 11.2 ms | 2,511.0 ms | 11.8 ms |
| `/sectors` | 2,406.5 ms | 11.0 ms | 2,462.6 ms | 12.6 ms |
| `/portfolios/{id}/holdings` | 23.1 ms | 19.1 ms | 29.0 ms | 21.5 ms |
| `/portfolios/{id}/risk` | 32.9 ms | 30.1 ms | 36.3 ms | 36.7 ms |
| `/portfolios/{id}/performance` | 13.5 ms | 12.0 ms | 14.5 ms | 14.5 ms |
| `/portfolios` (every page) | 12.6 ms | 11.5 ms | 14.0 ms | 13.1 ms |
| `/news` (sources not due) | 8.0 ms | 7.2 ms | 8.9 ms | 12.5 ms |
| other endpoints (stocks, history, fx, watchlist, settings, runs) | 2–26 ms | 3–26 ms | – | – |

Differences of a few milliseconds on the unchanged endpoints are run-to-run noise.

### News with due sources (stack, first request after the 30-minute window)

| | Before | After |
|---|---:|---:|
| First request | 10,147 ms | 195 ms (stored articles, `refreshing: true`) |
| Background refresh of 9 sources | in the request | 8.8 s, off the request; all succeeded |
| Following requests | 23–28 ms | 31–36 ms (during the refresh) |

### Pages (browser, full load until network idle, Playwright against the stack)

| Page | Load before | Load after | Requests before → after | Data before → after |
|---|---:|---:|---|---|
| `/` (My portfolios) | 3,140 ms | 703 ms | 7 → 7 | 97 → 17 KB |
| `/portfolios/{id}` | 3,254 ms | 1,038 ms | 9 → 9 | 105 → 25 KB |
| `/portfolios/{id}/optimize` | 3,163 ms | 1,060 ms | 9 → 10 | 308 → 316 KB |
| `/portfolios/{id}/holdings` | 3,074 ms | 990 ms | 7 → 7 | 93 → 13 KB |
| `/portfolios/{id}/performance` | 3,760 ms | 1,043 ms | 10 → 10 | 196 → 116 KB |
| `/news` | 3,339 ms | 628 ms | 4 → 4 | 107 → 26 KB |
| `/watchlist` | 3,539 ms | 630 ms | 4 → 4 | 92 → 11 KB |
| `/market-data` | 3,674 ms | 638 ms | 3 → 4 | 82 → 89 KB |
| `/stocks` (Portfolio Creation) | 4,058 ms | 702 ms | 10 → 9 | 152 → 48 KB |
| `/research` | 3,310 ms | 652 ms | 4 → 4 | 82 → 2 KB |
| `/settings` | 3,094 ms | 610 ms | 3 → 3 | 82 → 1 KB |

One visit per page (cold SPA start each); "load" includes Playwright's 500 ms network-idle wait, so ~0.5 s is the floor.
No page made duplicate identical requests before or after (TanStack Query already deduplicates). Optimize and Market data
make one extra small request (the sidebar summary next to the full status they need).

### Database work and SQL commands (in-process, `PERF_BENCH=1`, disposable PostgreSQL, mock data, 15 repetitions)

| Request | SQL commands before → after | Median before → after |
|---|---|---|
| optimization run (POST, 10 stocks; 10 reps) | 26 → 17 | 858 → 736 ms (noisy, CPU-bound, see #7) |
| `/risk` | 45 → 24 | 26.5 → 15.1 ms |
| `/holdings` | 30 → 16 | 12.8 → 8.1 ms |
| `/optimization-runs/{id}/reality-check` | 30 → 12 | 52.6 → 32.4 ms |
| `/portfolios` (2 portfolios) | 20 → 13 | 8.9 → 6.1 ms |
| `/portfolios/overview` | 23 → 16 | 9.8 → 7.4 ms |
| `/market-data/status` | 19 → 10 | 12.6 → 10.0 ms |

`EXPLAIN (ANALYZE, BUFFERS)` on the development database: instruments-with-prices 2,394.8 ms / 769,101 buffer hits as
`EXISTS` per instrument vs 13.2 ms / 876 buffer hits as one grouped query; latest close per asset 25.8 ms (window
function) vs 0.4 ms (LATERAL with index).

**Not measured:** container CPU and memory before the change (only an after snapshot exists: API 220 MB, DB 134 MB);
production-like concurrency; external provider request counts (unchanged by design – fetch windows and quote cooldowns
were not modified; the news change does not add fetches, it moves them out of the request).

## Remaining work (prioritized)

1. **Optimization analytics** (~95 % of a run): warm-start the ADMM solver from the neighbouring frontier point, or reduce
   the frontier points for the first view and compute more on demand. Needs numerical regression tests against the current
   frontier.
2. Reality check: measure without the rate limit on a larger universe; it reuses the batched series.
3. `PortfolioService.ListAsync` values each portfolio separately (~6 queries per portfolio): fine for a few portfolios; batch
   prices/FX across portfolios when users keep many.
4. The settings row is read 2–3 times per request (≈1 ms each); a per-request memo is possible but low value.
5. Multi-instance deployment: distributed rate limits, news single-flight and job queue (then Redis becomes reasonable).

## How to measure

* Running stack: `BENCH_EMAIL=… BENCH_PASSWORD=… python3 scripts/bench/api_bench.py --base http://localhost:8081 --runs 20
  --out result.json` (read-only; `--only <name>` for one endpoint; mind the 30/min limit on expensive endpoints).
* SQL commands and latency per request: `PERF_BENCH=1 [PERF_BENCH_OUT=file.json] [PERF_BENCH_SQL=<name prefix>] dotnet test
  tests/PortfolioOptimizer.IntegrationTests --filter FullyQualifiedName~PerformanceBenchmarkTests` (prints grouped SQL with
  `PERF_BENCH_SQL`).
* Query plans: `EXPLAIN (ANALYZE, BUFFERS)` as the owner role; never change data while measuring the development database.
