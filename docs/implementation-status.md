# Implementation status

## 2026-10-15 (3): AI research assistant (agentic workflow, first vertical slice)

* **Delivered:** `/research` start page and workspace; `ResearchAgent` state machine with five typed tools through
  `ToolRunner` (ownership, confirmation, budgets, timeouts, events); brief with stated vs assumed fields and the one
  blocking question; transparent universe screen with reasons; hypotheses from the real optimizer + reality-check review
  (unsaved until confirmed); deterministic flags, suggestions and 7-section interpretation; refinement proposed then
  confirmed; comparison; confirmed, idempotent save as a normal optimization run; activity log and budget; optional
  language model (`Agent:Llm:Provider`, Anthropic SDK) with quote/allow-list/numeric validation and deterministic
  fallbacks. Docs: [agentic-architecture.md](agentic-architecture.md), [threat-model.md](threat-model.md),
  [agent-evaluation.md](agent-evaluation.md), decisions D-21–D-24, launch blockers in launch-readiness.md.
* **Verified:** 15 evaluation cases + journey (integration, real PostgreSQL), rule/validator unit tests, Vitest, e2e
  journey of 17 steps on the disposable stack, desktop/phone screenshots reviewed. Bugs found by the tests and fixed:
  "equal weighting" not recognised; infeasible per-stock caps were proposable; tool failures were not persisted in the
  activity log; company context accepted symbols outside the session; candidate table buried the shortlist under
  excluded rows; lowercase hypothesis labels.
* **Not verified:** live Anthropic calls (no key); explanation quality with users; German phrasing.
* **Known gaps:** no in-workflow history download, backtest/ML tools, or monitoring step (roadmap §0); sessions are kept
  until the user deletes them (no retention policy).

## 2026-10-15 (2): stock-detail panel and company descriptions

* **Panel too small – root cause:** the stock browser opened a right-hand `Sheet` limited to `sm:max-w-xl` (576 px) with a
  fixed 220 px chart. Replaced by `components/stock-detail-dialog.tsx`: a centered dialog of `min(68vw, 1280px)` ×
  `min(88vh, 960px)` from 1024 px, 94 % × 92 % on tablets, full screen on phones; one scroll area; chart height follows the
  viewport (240–520 px). Measured in Chromium: 1440×900 → 979×792 (68.0 % × 88.0 %); 1920×1080 → 1280×950 (66.7 % × 88.0 %);
  768×1024 → 722×942; 390×844 → full screen.
* **Navigation:** labelled Previous / Next / Close buttons, "n of N in the current list", ←/→ keys, Escape and outside
  click close; order = the list as displayed (server-side filters, sort, current page); no wraparound (buttons disabled at
  the ends and for a stock that left the list); the selection and filters are untouched. The panel body is keyed by
  symbol and every query checks the symbol of its data, so a slow response for the previous stock cannot appear.
* **Description empty – root cause:** `Asset.Description` was never written by any import or provider (code
  inspection: no code path assigns it; the seed CSVs have no description column), so the panel only ever showed
  "No company description…". Fixed with catalogue article
  references + Wikipedia summaries + a profile cache (see [market-data.md](market-data.md#company-descriptions)).
* **Bugs found by the new tests and fixed:** a first failed profile fetch was reported as "Available" (enum default);
  cached simulated text would have been served by the real provider after switching sources.
* **Verified:** unit (Wikipedia parsing, references, CSV import, Max range), integration (`CompanyProfileTests`: cache,
  not found, error + retry window, no reference, source switch, auth), Vitest (`stock-detail-dialog.test.tsx`: open, size
  classes, Previous/Next/boundaries, Close/Escape, filtered list, fallback, independent failures, stale response), e2e
  (`stock-detail.spec.ts` on the disposable stack), opt-in live smoke (real Wikipedia for AAPL, SAP, Toyota via de→en), and
  a browser check of real descriptions (local API with Wikipedia enabled on the disposable e2e database).

## Pass of 2026-10-15: watchlist, financial news, reality check

### Baseline (before changes, branch `worktree-master-challenge` from `main` 4b4d563)

* **Broken:** a clean checkout did not build (25 errors). `.gitignore` contained `data/`, which macOS matches
  case-insensitively, so `src/PortfolioOptimizer.MachineLearning/Data/{Dataset,MarketPanel}.cs` were never committed.
  The same latent risk existed in `.dockerignore` (`**/data`). Fixed first (both rules anchored to the API's runtime folder).
* **Working (re-verified):** after that fix 104 unit, 40 ML, 55 integration, 50 Vitest tests passed; lint 0 errors; build OK.
* **Missing:** watchlist, financial news, per-stock price history ranges, event/price comparison, a user-facing
  robustness analysis of optimization results, a security page per stock.
* **Not verifiable here:** licensed data, deployment beyond the local stack, real users.
* **Environment issue found:** `podman compose --build` hung because Docker Desktop's credential helper
  (`credsStore: desktop`) blocks when Docker Desktop is not running; worked around with an empty `DOCKER_CONFIG`
  (documented in testing.md and AGENTS.md; the user's global config was not changed).

### Implemented and verified

| Area | What | Evidence |
|---|---|---|
| Watchlist | owner-scoped entries (unique per listing), add/remove/list, bulk + single refresh, period change, sparkline, history download on add | `WatchlistTests` (6), `PriceHistoryTests` (7), e2e journey D incl. persistence across sign-in |
| Price history | `/stocks/{symbol}/history` 1M–5Y, adjusted/raw basis never mixed, gaps reported, 1D honestly unsupported | unit + integration tests; screenshots desktop/mobile |
| Stock page | `/stocks/:symbol`: price + provenance, chart, company news, watch/select actions | e2e journey D |
| Financial news | Yahoo company news (provider tags only), Fed/ECB/BoE/BoJ RSS, shared storage, URL dedup + syndication merge, keyword categories with basis, relevance points with reasons, filters (view, category, market, dates, publisher, search, watchlist, symbol), source status, images off by default | `NewsRulesTests`, `NewsTests`, `NewsProviderFailureTests`, live smoke (real Yahoo + 4 feeds parsed on 2026-10-15), e2e journey E |
| Event comparison | reference close before publication in exchange time, +1/+5/+20 trading days vs benchmark on identical dates, overlapping-news caveat, no causal claim | `EventPriceComparisonTests` (hand-built NY series), `NewsTests` |
| Reality check | concentration, risk shares, diversification ratio, stress correlation, in/out-of-sample, sensitivity re-solves, costs, labelled findings, next steps, limitations | `DiversificationMathTests` (closed forms), `RealityCheckTests` (volatility equals the run's to 1e-6, ownership), e2e journey F |
| Docs | watchlist, financial-news, portfolio-reality-check, roadmap, launch-readiness (incl. regulatory questions), decisions D-15…D-19, AGENTS working rules | this docs set |

### Journeys exercised (e2e stack, simulated data)

A registration/approval/sign-in, B stock selection and refresh, C optimization with interpretation, D watchlist
(add → history ranges incl. 1D message → company news → event comparison → sign-out/in → remove), E news (views,
official source, attribution, link target, watchlist empty state, source disclosure), F reality check, H admin overview
and "Admin Investor" label. Additional screenshot review at 1440 px and 390 px found and fixed: cramped price-history
header on phones, unlabeled date filters, "Stock selection" highlighted on stock pages, missing quote provenance on phone
watchlist rows, squeezed sensitivity table, "35.4 %" vs "35.4%" formatting.

### Data safety

No command in this pass wrote to the development database: all tests used per-class Testcontainers databases or the
disposable e2e stack. The two new migrations only add tables and indexes; they apply on the next `migrate`.

### Known gaps

Unofficial company-news source (personal use), sparse non-US news, no licensed disclosure feeds, unevaluated keyword
classifier, no notifications, no holiday calendars, reality-check sensitivity limited to four assumption changes,
out-of-sample section empty until 20 trading days after a run. See limitations.md and roadmap.md.

## Pass of 2026-10-14

Audit, fixes and remaining work of the 2026-10-14 "audit, repair, enhancement and QA" pass. The earlier platform
upgrade (PostgreSQL, authentication, ML, comparisons) is described in [audit.md](audit.md) (baseline before that
upgrade) and [final-report.md](final-report.md).

## 1. Baseline audit (2026-10-14, before this pass)

Runtime checked in the browser (Vite dev server + compose API/PostgreSQL), database inspected directly, providers
probed with the configured key.

**Working:** sign-in and administrator approval; owner-scoped portfolios, optimization runs, backtests, ML experiments
and comparisons; S&P 500 catalogue browsing with temporary selection; mean-variance optimizer with five objectives;
ML engine (four model families, purged chronological validation, explainability, walk-forward backtests); strategy
comparison on a shared simulator; 153 backend tests, 31 frontend unit tests, 9 e2e tests passing.

**Confirmed bugs (with reproduction):**

| # | Bug | Reproduction | Status |
|---|---|---|---|
| B1 | Test accounts in the administrator view | Run Playwright against the dev stack → Users shows `applicant-…@e2e.test` accounts (11 found in the dev DB, plus 64 audit events) | **Fixed**: purged; e2e isolated; `.test` accounts hidden and purgeable |
| B2 | Stock prices not loaded automatically | Sign in, open Stock discovery → only the 11 tracked stocks have prices; others "No prices yet" until a manual history download | **Fixed**: sign-in refresh cycle + page refresh |
| B3 | International symbols would break | `YahooFinanceProvider.ToYahooSymbol("SAP.DE")` → `SAP-DE` (replaced every dot) | **Fixed**: `SymbolMapper` |
| B4 | Alpha Vantage assumes USD | `AlphaVantageProvider` hard-coded `"USD"` for every series/quote; London quotes are in pence | **Fixed**: venue currency, pence conversion, unsupported markets reported |
| B5 | London prices in pence | Yahoo returns `GBp`; the asset would be rejected (currency mismatch) or shown 100× too high | **Fixed**: `CurrencyUnits` |
| B6 | No per-stock refresh | — | **Fixed**: refresh icon per row and in the detail panel |
| B7 | Yahoo batch occasionally rejected (HTTP 400) | Repeated sign-ins → one batch of 20 Tokyo stocks failed while the same request succeeded seconds later | **Fixed**: retry once, then per-symbol fallback (unit-tested) |
| B8 | ML experiment on a non-US selection failed | DAX stocks + default benchmark SPY → mixed currencies; with ^GDAXI → "benchmark has 0 stored prices" | **Fixed**: currency rule with automatic benchmark switch; missing history downloaded before the job |
| B9 | Coverage warning stale after "Fetch prices" | Fetch prices for new stocks → Optimize page still says "fewer than 61 prices" | **Fixed**: fetch waits for the job and refreshes all stock data |
| B10 | `purge-test-data` hung in a container on refusal | `podman compose run --rm migrate purge-test-data` (Production) → unhandled exception, container never exits | **Fixed**: exit code 1 with message |
| B11 | Mobile stock page unusable | 390 px wide: filters fill the first screen, price column off-screen | **Fixed**: collapsible filter, compact table |

**Missing functionality (now implemented):** DAX 40/DAX 30/CAC 40/FTSE 100/Nikkei 225 universes; market → index →
sector navigation; quote provenance (delayed/close/cached/stale/unavailable); strategy explanations and result
interpretations; glossary; personalized header ("Admin Investor"); admin overview with market-data health.

**Security / data-integrity concerns found:** test data in the real database (B1); e2e runs could target any stack;
nothing exposed secrets (verified: key never in responses, logs redact it).

## 2. Database cleanup performed

* Environment: Development compose stack, database `portfolio` (verified before deleting).
* Backup taken first: `~/Desktop/CODE/PortfolioOptimizer-backups/portfolio-before-test-purge-20261014-201528.dump`.
* Removed (one transaction, audited as `admin.test_data_purged`): 11 accounts `applicant-*@e2e.test` (they owned no
  data) and 64 audit events (54 performed by them, 10 approvals of them). A second dry run found 0.
* Kept as ambiguous/genuine: the administrator's runs from 13–14 Oct (1 optimization, 2 backtests, ML experiments
  including the evaluation runs cited in the final report, 1 comparison, 1 paper trade) and the administrator's
  sign-in audit events (some caused by test runs, indistinguishable from real sign-ins).
* Also removed: 2 duplicate instruments (`BRK.B`, `BF.B`) created minutes earlier by the first universe import, before
  the symbol convention was fixed (no prices, no references).

## 3. Definition of done

| Criterion | Status | Evidence |
|---|---|---|
| Prices load automatically after sign-in | Done | sign-in cycle refreshed 914/916 instruments in 29 s (2 delisted); integration test `SignIn_StartsOneSharedRefreshCycle…`; e2e `stock selection loads prices automatically` |
| Working refresh control per stock | Done | row icon + detail button; integration + e2e tests |
| US, DE, FR, UK, JP universes from maintainable sources | Done | 503 / 40 + 30 historical / 40 / 100 / 224; builder script, admin upload, override directory |
| Availability, timestamps, currencies, caching, provider limits shown correctly | Done | quote states; admin overview; [market-data.md](market-data.md) |
| Test artifacts removed and hidden from administrators | Done | section 2; admin list/overview exclude `.test` |
| Tests cannot pollute genuine data | Done | `scripts/e2e.sh` + environment guard (verified it refuses the dev stack); per-class integration databases |
| Explanations + interpretations for every mathematical strategy | Done | 5 guides; `interpretOptimization` |
| Explanations + validation + interpretation for every ML strategy | Done | 4 model guides + portfolio construction; `interpretMlExperiment` |
| Comparison explains math vs ML vs equal weight vs benchmark | Done | guide + `interpretComparison` |
| User name top-left; administrator "Admin Investor" | Done | header; unit + e2e tests |
| Browser review with fixes | Done | journeys below |
| Tests executed and recorded | Done | [testing.md](testing.md#results-2026-10-14) |
| Docs + `AGENTS.md` describe the real system | Done | this docs set |
| No secrets exposed | Done | key only server-side; admin shows "Key configured"; tests assert no secrets in overview |
| No unsupported claims | Done | quotes never "live"; ML results reported with t-statistics and baselines |

## 4. User journeys reviewed in the browser

Sign-in page → personalized header → stock selection loading cached prices then refreshing → market/index/sector
filtering (DAX 40 vs DAX 30 historical, Japan search "Toyota") → single refresh → detail panel → selection of six DAX
stocks → history download → optimization (EUR, max Sharpe) with interpretation → ML experiment on the same stocks
(benchmark switched to ^GDAXI, history downloaded first) with interpretation → strategy comparison (equal weight won;
interpretation states it) → strategy guide and glossary → admin overview → mobile layout (390 px). Issues found and
fixed: B7–B11, ticker-first sort putting Tokyo codes first (default now company name), inconsistent "All markets"
count, cut-off admin table, Vite failing on the symlinked `node_modules`.

## 5. Known limitations and open items

* No point-in-time index membership (survivorship bias); ISINs unavailable from the sources.
* Yahoo is unofficial; Alpha Vantage free tier cannot cover the catalogue (25 requests/day) and has no Tokyo data.
* No FX conversion: analyses are single-currency; paper portfolios need the stocks' currency.
* Sector mapping across classification schemes is approximate.
* No CI pipeline; e2e needs Podman/Docker locally.
* Backup/restore procedure documented but not rehearsed end to end.
* The repository sits in an iCloud-synced folder; see [testing.md](testing.md#macos-note-icloud-and-node_modules).
