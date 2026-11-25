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
| 9 | Price downloads took ~1.4 s per symbol (142 symbols: 3 min 21 s) | job timings; per-symbol gap 0.50 s fixed = request spacing; provider answers in ~0.2 s | the throttle paused *after* each answer with one request in flight, `appsettings.json` kept 500 ms, and each symbol made an extra quote request | start-spaced throttle (250 ms, ≤ 4 in flight), parallel download with sequential storage, quotes batched after the job; 100 symbols × 5 years: 62 s → 36 s → per-symbol gap 0.25 s (the provider limit) | done, measured |
| 10 | Optimizations of ~230 stocks took ~2 min and could time out (504 after 120 s); "maximum return solver did not converge" | API log 116–129 s; benchmark (230 assets, Debug): frontier 154 s, maximum Sharpe 5.4 s; max-return LP hit 100,000 ADMM iterations | the maximum-return LP was solved with ADMM (slow on LPs); 30 frontier points solved cold at 1e-9; convergence checked every iteration | max return solved exactly by the greedy rule (bounds + disjoint sector caps; ADMM only with turnover), frontier warm-started at 1e-7 and capped at 1,500 iterations, last point = the max-return portfolio, early polish every 50 iterations, checks every 5 iterations; frontier 154 s → 6.9 s, maximum Sharpe 5.4 s → 0.65 s (benchmark); a 230-stock run through the API 10–11 s | done, measured |
| 8 | Reality check | rate-limited endpoint (30/min/user): stack benchmark repetitions partly measured 429 responses | – | measured in-process instead | measured in-process only |
| 11 | Pages hung again once the price table grew (2026-11-02, 13.8M prices, 2,184 instruments) | API log: `/market-data/summary` 1.2–4.0 s (every page, every 60 s), `/market-data/status` 2.8–4.9 s, `/watchlist` 2.5–2.9 s, single stock pages 1–2.8 s; `EXPLAIN ANALYZE` on the development database: summary aggregate 2,360 ms, "has prices" `GROUP BY` 1,036 ms (181k buffers read, parallel seq scan of every price), single-stock queries 2–27 ms | the fixes of #1 and #3 were set-based but still read **every price**: their cost grew with the price table (fine at 55k prices, 1–2 s at 13.8M); while they ran, their parallel scans also delayed the cheap requests | "has prices" = the newest price per instrument through `LatestPricesPerAsset` (one backward index lookup per instrument; 46 ms, same 2,184 instruments); summary: newest bar per tracked instrument the same way + latest retrieval via the new index `(Source, RetrievedAtUtc)` (1 ms; 94 MB, built in 7.4 s – migration `PriceRetrievalIndex`). Measured as SQL on the development data (index tested in a rolled-back transaction); results compared with the old queries by `DataAccessEfficiencyTests` | done; deployed: `/market-data/summary` 30–70 ms in the API log |
| 12 | The stock list and watchlist still took ~2 s per page after #11, and again after every quote refresh (list → refresh → list again) | API log: `GET /stocks` 1.7–2.2 s for a page of 50; `EXPLAIN ANALYZE`: the last two bars per stock (`StockDiscoveryService.QuotesAsync`, join on per-stock last dates) became a hash join over a **seq scan of all 13.8M prices**, 2,979 ms; for one stock the same query uses the index (27 ms) | the planner switches to a full scan once a page holds many stocks | last two bars per stock through `LatestPricesPerAsset(…, 2, …)`, previous bar kept only within 20 days of the last as before: 3 ms for 50 stocks, 14 ms for 200 (SQL); `ListQuotes_MatchEachStocksOwnQuote` checks a batched page against each stock's own quote | done, measured as SQL |
| 13 | Atlas opened slowly and stayed sluggish during downloads | API log: `/market-data/status` 1.3–3.2 s on 3–5M prices (2.8–4.9 s on 13.8M), polled every 2 s while a job runs; the status carried the last 10 jobs **with every per-instrument log** (~2,150 each) and Atlas rendered all of them (hidden `<details>` still render) and searched `symbols × logs` (~4.6M comparisons per poll) | the per-instrument count/first/last/retrieval/adjusted aggregate read every price; job logs travelled with every status | stored `PriceCoverage` (one row per asset and source): rewritten for the asset after every price upsert (2 ms per asset), rebuilt for all assets on start (864 ms on 5.1M prices, so manual SQL, restores or imports never leave it stale); the status reads it. Status jobs without logs (`LogsIncluded` false) plus `PendingSymbols`/`LastSymbol` for running jobs; a job's log loads when it is opened (`GET /market-data/jobs/{id}`, paged `SearchableTable`). Tests: stored coverage equals the prices after downloads and after an outside delete + rebuild | done, measured as SQL |
| 14 | Every first visit downloaded the whole bundle uncompressed; every visit re-checked every file (2026-11-25, AMIR-43) | browser: `/stocks` 1,702 KB of scripts on the wire, Lens 1,123 KB; `curl`: no `Content-Encoding`, no `Cache-Control` on hashed `/assets/*` (ETag revalidation per file) | nginx served files as stored | `nginx.conf`: gzip for CSS/JS/JSON/SVG (level 5, ≥ 1 KB) – **not** for proxied API responses (`gzip_proxied` off: small, and user data next to request-controlled text is the BREACH pattern); `/assets/` `Cache-Control: public, max-age=31536000, immutable` and 404 for a missing file; the page `no-cache` (a deployment is picked up at once). `/stocks`: 1,702 → 293 KB on the wire on a first visit, 0 KB and no revalidation on a repeat visit; first contentful paint on a repeat visit 528 → 288 ms | done, measured |
| 15 | The Math Optimization page downloaded the full per-instrument status (1.18 MB) for one check, every 60 s and every 2 s during downloads (2026-11-25, AMIR-43) | browser: `/market-data/status` 1,181 KB, 335–1,279 ms; `optimize.tsx` used it only for "benchmark outside the universe has no prices" | the page reused Atlas's hook | `POST /stocks/history-coverage` for the benchmark alone (`observations = 0` ⇔ freshness Missing: no stored close of the active source). The page's API data 84 KB in total; the status is no longer requested there. A benchmark absent from the status list now gets the warning too (before: none, although it had no prices) | done, measured |
| 16 | The API accepted requests only after rebuilding the price coverage (2026-11-25, AMIR-43) | 4 restarts: 4.0–8.4 s to `/health/ready`, of which the rebuild 1.6–5.2 s (5.3M prices) | the safety-net rebuild ran in `InitializeAsync`, before `app.Run` | `PriceCoverageRebuildService` runs it once after the start; readers see the stored coverage until it commits (one transaction, never empty); full and per-asset rebuilds take `pg_advisory_xact_lock` so a download during the rebuild cannot collide with it (`FullAndPerAssetRebuilds_RunConcurrently_WithoutCollision_AndEndCorrect` fails without the lock). Ready in 2.1–2.2 s (warm; rebuild ≈ 1.5 s afterwards) | done, measured |
| 17 | Optimizations of ~230 stocks still took ~12 s (2026-11-25, AMIR-44) | stored runs: 14 stocks 2.2–8.1 s, 50 stocks 13.7 s, 228 stocks 11.8 s; profiling harness on the same 228-stock universe (real prices, Release): frontier 10,862 ms of 12,001 ms; temporary solver counters: early polish 209 attempts, **183 failed**, 5,642 ms (53 % of the frontier); 10,653 ADMM iterations ≈ 4.7 s; factorizations 0.26 s | every 50 iterations the early polish built and LU-solved the dense KKT system of the current active set and discarded it when it missed the strict tolerance – again and again for the same active set | the early polish is a function of the active set alone (residual arguments 0, deterministic KKT build, solve and check), so `AdmmQpSolver` remembers the sets that failed in a solve and does not try them again – identical results by construction: weights, statistics and all 27 frontier points bit-identical on the real universe, and on the seeded 120-stock universe of `LargeUniverseTests` (pinned to 1e-9, computed with the old and the new code: difference 0). Frontier 10,862 → 6,845 ms | done, measured |
| 18 | The covariance estimate took 0.84 s and allocated ~320 MB per run (2026-11-25, AMIR-44) | harness: estimate median 840 ms (228 stocks × 378 days); `LedoitWolf` built `x_k x_kᵀ − S` as two n × n matrices per day (2·t·n² doubles) | allocation per day | the same sum element by element over the symmetric matrix: 26 ms; weights within 5.6·10⁻¹⁷, statistics 3.3·10⁻¹⁶, frontier 2.7·10⁻¹⁵ of the previous code, same iteration count; `LargeUniverseTests.LedoitWolf_EqualsTheDirectDayByDaySum` checks it against the textbook form to 1e-12 | done, measured |
| 19 | Each ADMM iteration still cost ~0.35 ms on ~230 stocks (2026-11-25, AMIR-45) | micro-benchmark of one iteration (228 variables, 230 rows): dense `Aᵀ·v` 144 µs + `A·x` 134 µs of ~350 µs; the same products on a sparse copy 18 + 13 µs | the constraint matrix is stored dense although it is mostly bound rows with one entry each | `AdmmQpSolver` multiplies with a sparse (CSR) copy of A when at most a quarter of its entries are non-zero; the KKT factorization and the polish keep the dense matrix. Bit-identical results again (the sparse product adds the same non-zero terms in the same order): weights, statistics and all 27 frontier points of the real 228-stock universe, `LargeUniverseTests` pins unchanged. Real 228-stock universe: frontier 6,745 → 3,425 ms, maximum Sharpe 233 → 97 ms, run total 7.1 → 3.6 s (12.0 s before AMIR-44) | done, measured |
| 20 | The polish step still took 57 % of a frontier (2026-11-25, AMIR-46) | temporary solver counters, real 228-stock universe: frontier 3,371 ms, of which polish 1,938 ms (76 attempts, ~25 ms each, 50 failing with different active sets), checks 474 ms; 3 of 29 solves stop at the iteration cap | the polish LU-solved the KKT system of all 228 weights plus every active row (~460 × 460) although most weights are pinned to a bound | `SolveReduced` eliminates the weights pinned by active single-entry rows (x_j = b/c), solves the free weights and the other active rows (same regularization and refinement), and recovers each pinned row's multiplier from stationarity – the full system's solution, of the size of the free weights; the full system remains for a weight pinned twice or none free. Same acceptance checks. Real universe: frontier 3,425 → 1,558 ms, maximum Sharpe 97 → 50 ms, run total 3.6 → 1.7 s; weights within 5.9·10⁻¹⁷, frontier 2.7·10⁻¹⁵, same iterations and points; `LargeUniverseTests` pins hold; `OptimizerTests.QpSolver_PolishWithBoundsAndBudget_GivesTheClosedFormAndMultipliers` (closed form incl. multipliers), `QpSolver_VariablePinnedTwice_AndEveryVariablePinned_StillSolve` (fallbacks) | done, measured |

## Redis decision matrix

The application runs one API instance (compose); rate limits, news single-flight and the job queue are in-process by design, and PostgreSQL is the store of record for prices, quotes, news and runs.

| Candidate | Decision | Reason |
|---|---|---|
| Financial news responses | **Do not implement** | Feed queries are 5–8 ms (median); the 10 s was provider latency in the request path, fixed by background refresh. Articles already live in PostgreSQL with source, publication and fetch times; a second cache would duplicate them and add a staleness layer. |
| Quotes, price history, company profiles | **Do not implement** | Stored in PostgreSQL by the sync/quote jobs with source and retrieval time; reads are 3–25 ms. Provider quotas are protected by existing per-source cooldowns and the batch refresh cycle. |
| Market/sector/coverage counts | **Do not implement** | Were 2.4 s because of the query shape; now 11–25 ms. Caching would have hidden the defect. |
| Optimization / reality-check results | **Do not implement** | Saved runs are persisted (PostgreSQL); an identical recomputation needs the same settings, universe, data snapshot hash, currency and code version – rare in this workload, and a cache would add invalidation risk to financial results. |
| Matrix Pilot / ML | **Do not implement** | Cycles and experiments are durable rows; a pilot decision must never be served from a cache (one decision per period, fail closed); caching model output would need prompt/model/user-scoped keys for little gain. |
| Exchange rates (converter, conversions) | **Do not implement** (D-34) | The 6–72 s were ECB calls inside requests (per added currency, endless BGN refetch, weekend-start ranges); fixed by fetching the whole rate set per request, closure-tolerant coverage, single-flight and a 25 s bound. Rates are stored in `FxRates`; reads are 6–21 ms. A provider cache in Redis would sit in front of the same stored rows. |
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

Open after AMIR-43…46 (2026-11-25). Each item says what is known and what was **not yet checked**; measure before fixing.

**Next (correctness and the biggest unknowns)**

1. **All objectives before/after on real data** – AMIR-46 (reduced polish) affects every objective, but the before/after
   comparison on the real 228-stock universe covered maximum Sharpe and the frontier only. Compare minimum variance, target
   return, maximum return for a volatility limit, the max-holdings/minimum-position heuristic, sector caps and the turnover
   limit (old vs new code, stated tolerance), like `LargeUniverseTests`.
2. **Optimizations through the API** – all AMIR-44…46 timings are from the pure engine (harness). Not measured: a run
   end to end (data loading, FX conversion, analytics JSON, storage), and why runs of ~14 stocks took 2–8 s in October
   (the solver needs milliseconds for them – profile `GetSeriesInCurrencyAsync`/`CommonWindow`/benchmark series).
3. **Universes up to 500 stocks** (`MaxUniverse`) – only 228 measured; check time and memory of the dense KKT and frontier.
4. **Dropped frontier points** – 3 of 30 points are left out because their solve stops at the 1,500-iteration cap
   (`FrontierSettings`, `if (!sol.IsOptimal) continue` in `EfficientFrontier`). Raising the cap for those points or
   keeping the unpolished point changes what users see – needs a decision.
5. **Deploy and verify AMIR-44…46** – not rebuilt into the development stack and not checked in the browser; AMIR-46's CI
   pending at the time of writing (PRs #109 → #110 → #111, stacked).

**Calculations not profiled yet**

6. Backtests and strategy comparisons (re-run the optimizer per rebalance – should benefit, unmeasured), ML Optimization
   training, the reality check (rate-limited endpoint: measure in-process on a larger universe), Matrix Pilot cycles.
7. Remaining solver cost: per iteration the dense residual product P·x (~96 µs per check) and the two triangular solves
   (~60 µs); a 228-stock run ≈ 1.7 s in the harness (frontier 1.6 s). Numerical guard: `LargeUniverseTests` (pinned to
   1e-9); timing: `PERF_BENCH=1 dotnet test tests/PortfolioOptimizer.UnitTests --filter Benchmark_Universe230`.

**Database and data**

8. PostgreSQL memory: `shared_buffers` is the default 128 MB for a 1.8 GB database (cache hit ratio 96.1 %) – measure
   page and query latency before/after a larger value; not changed.
9. Company figures: 3.3M rows (727 MB) and a daily refresh of ~8,900 Yahoo requests although statements change quarterly –
   refresh statements weekly/after reporting dates, valuation daily (AMIR-41 follow-up).
10. Table growth: `SyncLogs` 202k rows (39 MB) – check whether old logs are ever removed; review other append-only tables.
11. `PortfolioService.ListAsync` values each portfolio separately (~6 queries each; `/portfolios` ~380 ms for 4 portfolios
    in the browser): batch prices/FX across portfolios.
12. Queries that still read every price: the universe import (`UniverseService`, only when a constituents file changed)
    and the coverage rebuild after the start (#13/#16, ~1.5–6 s, in the background since AMIR-43). Never add a query whose
    cost grows with the number of stored prices to a request every page makes.

**Frontend**

13. Rendering not profiled: re-renders and long tasks on the stock list (fundamentals columns), large tables and charts.
14. Browser memory over a long session (heap 44–65 MB per page measured once; no leak test).
15. Bundle contents: `charts` chunk 432 KB (120 KB gzipped) not examined for splitting; dependency audit (unused npm/NuGet
    packages) not done.

**Memory, startup, footprint**

16. API memory 106 MB after a restart, 252 MB after some hours – not investigated (may be normal caches/GC).
17. Cold start after AMIR-43 not measured (warm: 2.1 s; cold before: 8.4 s).
18. Disk footprint not audited: dead code, the Podman VM disk, `node_modules`, old `.claude/worktrees`.

**Low value / later**

19. The settings row is read 2–3 times per request (≈1 ms each); a per-request memo is possible.
20. Multi-instance deployment: distributed rate limits, news single-flight and job queue (then Redis becomes reasonable).

## How to measure

* Running stack: `BENCH_EMAIL=… BENCH_PASSWORD=… python3 scripts/bench/api_bench.py --base http://localhost:8081 --runs 20
  --out result.json` (read-only; `--only <name>` for one endpoint; mind the 30/min limit on expensive endpoints).
* SQL commands and latency per request: `PERF_BENCH=1 [PERF_BENCH_OUT=file.json] [PERF_BENCH_SQL=<name prefix>] dotnet test
  tests/PortfolioOptimizer.IntegrationTests --filter FullyQualifiedName~PerformanceBenchmarkTests` (prints grouped SQL with
  `PERF_BENCH_SQL`).
* Query plans: `EXPLAIN (ANALYZE, BUFFERS)` as the owner role; never change data while measuring the development database.
