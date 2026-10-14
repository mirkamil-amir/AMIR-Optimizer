# Implementation status

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
