# Architectural decisions

Short records of decisions that are not obvious from the code. Newest first. Format: context → decision → consequences.

## D-39 Typography and readability over the map (2026-10-24)
**Context:** text placed directly on the background map (e.g. the market list of Portfolio Creation) became hard to read
where dots or highlighted countries ran through it, and the interface used one generic sans-serif. **Decision:** a
two-family type system served from the app (the CSP allows fonts from 'self' only): Sora (OFL, variable – a wide, confident
modern sans, chosen by the owner from seven rendered options after Cormorant Garamond and Source Serif 4 were rejected) as
display face for the wordmark (spaced capitals), page titles, portfolio names and section headings (`.heading-display`,
weight 600, −0.03 em); Geist stays for body text, labels, tables and all figures. Readability: the content column has a 70 % veil in the
page colour (no blur – it would be recomputed on every camera frame), text on it gets a soft halo, dense text next to the
map sits on a near-solid panel (`.map-surface`), cards stay 82 % opaque. **Consequences:** the map shows fully only behind
the header and at the page edges and faintly behind content; one more font file (Latin subset; other scripts load only when such characters appear).

## D-38 Background map: coloured countries and a camera that frames the selected markets (2026-10-24)
**Context:** the dotted map of D-37 looked washed out in the light theme and always showed the whole world, so selecting
stocks changed little. **Decision:** keep the hand-built SVG map (no map library, no tiles, no new dependency) but draw
Natural Earth 1:50m country shapes (simplified) as a fine dot grid without borders, with a cool palette per theme (map tokens in
`index.css`); the dot spacing follows the target zoom, so dots keep their size on screen and coastlines their detail. Markets come from the
canonical symbol (the server's `Markets.ForSymbol` convention, mirrored in `lib/markets.ts` and cached); unknown suffixes are
recorded, never guessed. The map shows the *listing exchange*, not the headquarters or revenue footprint (the catalogue does
not record those). The camera frames a fixed region per market (Europe for any European listing, the contiguous US, Japan,
…), the union for several markets, and the world for none; zoom 1–5, 10 % padding. It is one CSS transform with a native
transition (450–1400 ms by distance), applied imperatively after a 220 ms pause that combines rapid selections; a newer
target retargets a running move; reduced motion → no animation. Exchange markers are HTML elements moved with the same
transition, so they keep their size. Manual choices ("World view", one market) in the header control last until the
selection changes. Cards in the content area are 82 % opaque so the map and its moves show through (no backdrop blur – it would be
recomputed every frame). **Consequences:** a 269 KB (≈100 KB compressed) geometry chunk loaded after the page; the map has
one copy of the world (all supported
exchanges lie between −125° and 155°, so no frame crosses the antimeridian).

## D-37 Ten more markets; one Overview per portfolio; portfolio-scoped runs, backtests and comparisons; background map (2026-10-24)
**Context:** students asked for stocks and ETFs beyond the five markets; the portfolio workspace split its charts between
Overview and Performance (two cumulative-return charts and two allocation donuts), and "Optimization runs" left the
workspace for the global history. **Decision:** ten venues (CH, ES, IT, SE, DK, CA, AU, HK, IN, SG) chosen because
Wikipedia has a constituent table with tickers and the ECB publishes their currencies, so `FxService` converts them like
the earlier ones; one index benchmark each; small curated index-ETF universes per new market (checked against the price
provider, `type` column) instead of an unbounded ETF catalogue. Sectors where the source has none are assigned in the
builder and documented as approximate. Performance is merged into the Overview (each chart once, efficient frontier
directly under portfolio vs benchmark); Optimization runs, Backtest and Strategy comparison become tabs under
`/portfolios/:id/…` using the existing `portfolioId` list filters; inside a portfolio a custom backtest belongs to the
URL's portfolio (explicit, never the remembered one). A dotted world map (Natural Earth, public domain) is a decorative
background; it lights markets of the page and is hidden from assistive technology. **Consequences:** ~460 more
instruments (prices downloaded on demand as before); `/portfolios/:id/performance` and `/analytics` open the Overview;
the map is a 45 KB lazily loaded chunk.

## D-36 Optimizations belong to a portfolio; backtest and compare any of them (2026-10-24)
**Context:** the sidebar called the two optimization kinds "Optimize" and "Machine learning" in different groups;
backtests covered only hand-entered mean-variance settings, and a comparison was always one optimization run against one
ML experiment, without any portfolio relation (ML experiments were owner-scoped only). **Decision:** sidebar "Mathematical
Optimization" and "ML Optimization" next to each other before the history. An ML experiment records the portfolio it was
created for (explicit picker, its base currency); older experiments are linked explicitly by the owner, only to a
portfolio of the same currency – never inferred (two USD portfolios existed). A portfolio's optimizations are its
successful runs and linked experiments; a backtest can run any of them (`/backtests/from-strategy`; ML through
`BacktestEngine` with the ML decision rule), and a comparison takes any two of the same portfolio. Strategy settings are
frozen at submission. **Consequences:** additive nullable `PortfolioId` columns; the comparison request changed
(`portfolioId`, `strategyA`, `strategyB`); stored comparisons keep their old configuration and are read in the new shape;
custom backtests stay unlinked (no silent use of the remembered portfolio).

## D-35 Long FX histories: chunked background download; check rates only where they are used (2026-10-23)
**Context:** an ML experiment in USD with 8 years of prices excluded every EUR/GBP/JPY stock with 8 years of history
("749 of 2022 prices have no GBP→USD rate within four days"). Stored ECB rates started 2021-10-08; the API log showed the
one 8-year ECB request for GBP, JPY, USD timing out (10 s per attempt, 25 s per request) – the ECB answered that request in
17 s and single years in 2.6–20 s when measured directly. The fetch was cancelled with the request, so the history was never
stored, and the next run failed the same way. Independently, the experiment's benchmark (SPY) had prices only from
2021-10-13; the panel uses the benchmark's dates, so the 2018–2021 prices were never used, yet their missing rates excluded
the stocks. **Decision:** `FxDownloader` (singleton) downloads missing history in two-year chunks, newest-first backwards
and oldest-first forwards, storing each chunk as it arrives and only observations that continue the stored series (stored
rates of a currency stay one continuous range, so first/last date describe coverage); one download per provider, joined
by concurrent callers; it keeps running when a web request stops waiting (25 s), background jobs wait up to 5 min; ECB
attempt timeout 45 s. The ML loader restricts every series to the benchmark's stored span before conversion. The rule
itself is unchanged: rates at most 4 days old, never later rates, never filled; a stock with > 5 % unconvertible prices is
excluded – now with the cause (before the first stored rate / gaps / after the latest rate) and any download problem.
No Redis exists (D-33), so no cache could hide repaired rates. **Consequences:** a long history arrives with the first
analysis that needs it (or within minutes after an interactive request); experiments store a per-stock eligibility funnel
(`MlResultDocument.Eligibility`). Details in [currency.md](currency.md) and [machine-learning.md](machine-learning.md).

## D-34 Exchange rates: fetch the rate set, not single currencies; BGN history-only (2026-10-19)
**Context:** adding a currency in the converter took 6–7 s and up to 72 s in the development stack's logs: each added row
waited for a new board request that called the ECB inside the request (30 s attempt timeout, 3 retries); BGN (ECB series
ended 2025-12-31, Bulgaria in the euro since 2026-01-01) was "missing" forever and refetched with every other currency; a
range starting on a weekend was never "covered" and refetched hourly. **Decision:** the converter loads one board with every
currency per base; `FxService` fetches all published currencies for ranges ≤ 400 days in one request (0.2–1.2 s), per gap
otherwise, single-flight per provider, at most 25 s per request; coverage tolerates closures at the start; discontinued
currencies are provider metadata (`DiscontinuedCurrencies`) with history only, never a current rate, never EUR substituted.
Still no Redis (D-33): the bottleneck was provider calls inside requests, and PostgreSQL already stores the fetched rates.
**Consequences:** adding a currency makes no request (measured: row immediately; chart 6–21 ms, 0 ECB calls, vs. 2 ECB calls
per add before); the first board of a day costs one ECB request of ~1 s. Details in [currency.md](currency.md).

## D-33 Fix query shapes and blocking work before caching; no Redis (2026-10-19)
**Context:** a performance audit found every page waiting 2.4 s for the navigation's data status, news views blocked by
provider fetches (10 s) and per-stock queries (N+1). **Decision:** fix causes, not symptoms: one set-based query for
"instruments with prices" (`PriceQueryService.AssetIdsWithPricesAsync`; correlated `EXISTS` per instrument degraded to a
table scan per instrument), a compact `/market-data/summary` for the navigation, batched price queries with an
index-friendly LATERAL "latest N per asset" in Infrastructure (`IApplicationDbContext.LatestPricesPerAsset`), and
stored-articles-first news with a coalescing background refresher (`NewsRefreshWorker`, separate from the job queue so long
ML/backtest jobs cannot delay news). No Redis or other cache: one API instance, PostgreSQL already stores prices/news with
provenance, and every remaining read is 3–30 ms. A parallel efficient frontier was measured and reverted (median slower).
**Consequences:** measurements and how to repeat them in [performance.md](performance.md); revisit Redis together with
multi-instance deployment.

## D-32 Portfolios are independent workspaces addressed by URL (2026-10-17)
**Context:** the backend already kept portfolios per user, but the UI worked on one remembered portfolio, could show a run
of portfolio A under B, created portfolios only in Settings, had no manual transactions and deleted history in one step.
**Decision:** Overview lists all of the user's portfolios; each opens a workspace `/portfolios/:id/…` whose URL decides the
portfolio (remounted on switch); old URLs redirect. "Stock selection" became **Portfolio Creation** (details + universe +
review; nothing stored before Create). A typed, idempotent, append-only journal records buys, sells, deposits,
withdrawals, dividends and fees in time order; cash events are corrected by reversals, trades by opposite trades.
Portfolios are archived before they can be deleted. History is not snapshotted but rebuilt from ledger + stored prices
(deterministic, nothing redundant); deposits/withdrawals are external flows in the time-weighted return. Research sessions
may carry an owner-checked portfolio context. **Consequences:** additive migration only; no back-dated imports, no trade
reversal, no money-weighted or combined return; backtests/ML/comparisons stay per user. Details: [portfolios.md](portfolios.md).

## D-31 Primary areas in a top bar, Optimizer sections and administration in the sidebar (2026-10-17)
**Context:** the single sidebar mixed research tools, optimizer tools, data and settings. **Decision:** the header's second
row is the primary navigation, in this order: Optimizer, AI Research, Financial News, Watchlist, Market Data,
Strategy Guide, Glossary, Settings (the two learning pages moved up from the sidebar's Learn group the same day).
The left sidebar (a menu sheet below 1024 px) holds the Optimizer workspace – Workspace (overview, stock selection),
Optimize (optimize, history incl. reality checks), Test & compare (backtest, strategy comparison, machine learning),
Paper portfolio – and, for administrators only, the unchanged Administration group with Admin settings. One typed
model (`lib/navigation.ts`) drives both bars and the active state (most specific prefix wins). **Consequences:** no URL
changed; Optimizer links to the overview from other areas and does nothing inside its own workspace, so an open section
keeps its inputs; stock pages (`/stocks/:symbol`) count as Optimizer even when opened from the watchlist; the
administration area has no primary item. Visibility stays cosmetic – the API enforces the `Administrator` policy.

## D-30 Data-provider names are shown to administrators only (2026-10-16)
Users see what kind of price they look at (delayed quote, last close, adjusted/raw, as-of and retrieval time,
SIMULATED) but not which vendor delivered it; administrators keep the full provenance. Backend texts are worded
generically so no screen leaks a name; structured source fields stay in the API for reproducibility. Licence-bound
attribution (news publishers, Wikipedia CC BY-SA) is unaffected.

## D-29 Brand, themes and skins are presentation only (2026-10-16)
**Context:** a recognizable identity and user-selectable themes were requested. **Decision:** AMIR = Allocation · Modelling ·
Investment · Research; an SVG mark driven by CSS variables (v2: geometric A over ascending gold bars, serif wordmark);
light/dark/system mode plus ten skins (Navy & Gold default; Navy and Midnight with a dark sidebar) that change only the
brand/primary/sidebar colours (semantic gain/loss/warn and chart series keep their meaning); the choice is stored in the browser and
applied before the first paint. **Consequences:** no server state; a cleared browser falls back to Graphite/system.

## D-28 Independent allocation validation (2026-10-16)
**Context:** the optimizer verifies its own solution, but with the same code that built the problem. **Decision:**
`AllocationValidator` (pure, separate code) re-checks every result against the user's constraints (bounds, invested share,
sector caps, holdings, minimum position, turnover, exclusions, finite numbers) before a run is accepted; a violation fails the
run with the reason. Nothing is relaxed automatically. **Consequences:** one more guard; agent and UI never see an
unchecked allocation.

## D-27 Reporting currency is a view; the portfolio currency is fixed (2026-10-16)
**Context:** "changing the base currency" must not corrupt holdings. **Decision:** the portfolio (account) currency holds
cash, cost basis and P&L and is fixed at creation; `?currency=` converts values for display only (positions at the price
date's rate, account amounts at today's rate, labelled). **Alternatives:** converting the account (a modelled exchange with
costs – not implemented). **Consequences:** no hidden rewrites of history; returns are measured in the account currency.

## D-26 FX via ECB reference rates, one convention, dated conversion, explicit gaps (2026-10-16)
**Context:** no FX existed; mixed-market portfolios were impossible and one path mixed currencies silently
([currency-audit.md](currency-audit.md)). **Decision:** daily ECB euro reference rates (official, free, since 1999) stored
as units per EUR; one internal convention (target units per one source unit); each price converted with the rate of its
date (≤ 4 days carry-forward); simulated prices with simulated rates; missing rates are errors, exclusions or null totals.
**Alternatives:** Yahoo FX tickers (unofficial), commercial intraday feeds (paid). **Consequences:** correct historical
returns including FX; daily granularity and asynchronous closes are documented approximations.

## D-25 Gemini as a second provider over plain REST (2026-10-15)
**Context:** the user's key is a Google Gemini key. **Decision:** `GeminiLanguageModel` calls `generateContent` with
`responseJsonSchema` through `IHttpClientFactory` (no SDK dependency), with the same failure mapping, redaction and
validation as the Anthropic provider; the validator now allows negated cautions ("does not guarantee") and rejects
leaked JSON field names. **Consequences:** both providers are interchangeable by configuration; the request shape is
covered by unit tests from a recorded response and by an opt-in live test.

## D-24 The model cannot change user data; saving is a separate confirmed request (2026-10-15)
**Context:** an assistant that "does things" invites prompt injection into persistent actions. **Decision:** the only
writing tool (`save_research_run`) is `UserDataChange`; `ToolRunner` denies it unless the request carries
`Confirm = true`, which the UI sends only from a confirmation dialog. Model output can never trigger it.
**Consequences:** one extra click; denied attempts are visible in the activity log; no high-impact tools exist.

## D-23 Explanations are templates first; model narratives must pass a numeric validator (2026-10-15)
**Context:** explanations that contradict the numbers are worse than none. **Decision:** every hypothesis gets a
deterministic seven-section explanation from its computed values; a model narrative replaces it only if every number is
within 0.05 of a value in the evidence (or its rounding), every ticker belongs to the hypothesis and no forbidden promise
appears. Otherwise the template is shown with the reason. **Alternatives:** trusting the model; asking the model to
self-check (not verifiable). **Consequences:** the product is fully usable without a model; model text is
labelled; some correct but differently-rounded narratives are rejected (conservative).

## D-22 Hypotheses are unsaved evaluations; only a confirmed save creates a run (2026-10-15)
**Context:** research explores many alternatives; persisting each as an optimization run would flood the history and
blur what the user chose. **Decision:** `OptimizationService.EvaluateAsync` and `RealityCheckService.AnalyzeAsync`
compute without saving; hypotheses live in the research session with their full inputs and a config hash (idempotency).
Saving re-evaluates with the stored inputs and links the run to the session. **Consequences:** reproducible, no
duplicates; the existing optimizer and reality-check pages work unchanged on saved runs.

## D-21 One deterministic orchestrator with bounded model calls, model off by default (2026-10-15)
**Context:** the challenge asked for an agentic, explainable research platform; no model key is available and numbers
must never come from a model. **Decision:** a state machine (`ResearchAgent`) with five typed tools; a language model
is used for three schema-bound proposals (brief fields, unclear refinements, narratives) via `ILanguageModel`
(Anthropic official SDK, `claude-opus-5-5`), with deterministic fallbacks; `Agent:Llm:Provider = None` by default.
**Alternatives:** an autonomous tool-calling loop (non-deterministic, harder to bound and test); multi-agent designs
(no task needs them). **Consequences:** testable with a scripted mock; predictable cost; the "agent" cannot pursue
goals on its own – deliberate.

## D-20 Company descriptions from the catalogue's Wikipedia links (2026-10-15)
**Context:** the stock panel's description was always empty because nothing ever wrote one; Alpha Vantage `OVERVIEW` is
premium for the configured key and Yahoo profiles sit behind an anti-scraping crumb. **Decision:** record the company
article linked in the pinned constituent tables (`wiki` column) and fetch its lead section from the Wikimedia REST API
(CC BY-SA, attributed), cached per listing for 30 days. **Alternatives:** name search on Wikipedia/Wikidata (false
matches, e.g. "Apple"), licensed fundamentals vendors (not available). **Consequences:** reliable identity, no API key,
works for all five markets; encyclopedia text rather than issuer-written profiles; ~29 listings have no article.

## D-19 Reality check on the run's own window, re-solving instead of resampling (2026-10-15)
**Context:** users need to see how fragile an optimized allocation is. **Decision:** analyse the saved weights on the
run's stored window and price source; measure sensitivity by re-solving the same problem with one assumption changed
(covariance estimator, window halves, shrunk means, equal weight) and report turnover against the saved weights; label
every statement fact/estimate/interpretation/caution with documented thresholds. **Alternatives:** resampled
efficiency / bootstrap (more complete, slower, harder to explain – roadmap). **Consequences:** deterministic,
reproducible, testable; covers four assumption changes, not all estimation error.

## D-18 News linked to stocks only by provider tags (2026-10-15)
**Context:** ticker collisions (e.g. SAP ADR vs SAP.DE) and company names inside unrelated headlines make text matching
unreliable. **Decision:** link an article to a listing only when the provider tagged that exact canonical symbol.
**Consequences:** no false matches in watchlist news; recall is lower (untagged articles, sparse non-US coverage).

## D-17 News sources: Yahoo search + official central-bank RSS; images off by default (2026-10-15)
**Context:** Alpha Vantage `NEWS_SENTIMENT` is premium for the configured key (verified); licensed feeds are not
available. **Decision:** Yahoo Finance search (unofficial, same terms as Yahoo prices) for company news, official RSS of
the Fed, ECB, BoE and BoJ for macro news, a simulated provider for tests; store metadata and short provider summaries
only; show thumbnails only when `News:ShowImages` is enabled after rights are clarified. **Consequences:** real,
attributable news for a personal/educational deployment; a commercial launch needs licensed news (launch-readiness.md).

## D-16 On-demand news fetching with shared per-source state (2026-10-15)
**Context:** no reliable scheduler per user; providers have unpublished limits. **Decision:** fetch when a page needs a
source and the shared `NewsFetchStates` entry is older than 30 min (10 min after failures), single-flight per source,
at most five watched companies per request. **Consequences:** bounded outbound traffic regardless of users; the first
request after the window waits for the fetch (20 s timeout); no push notifications.

## D-15 Watchlist keyed by catalogue listing (2026-10-15)
**Context:** the same company trades on several venues. **Decision:** `WatchlistEntries (OwnerId, AssetId)` unique,
shared prices/news never copied. **Consequences:** duplicates prevented by the database; a delisted or renamed listing
keeps its row and shows "no price" until the catalogue is updated.

## D-14 Constituent lists from pinned Wikipedia revisions (2026-10-14)
**Context:** S&P 500, DAX (current and historical 30), CAC 40, FTSE 100 and Nikkei 225 constituents were needed with
sector and venue data; official index-provider files are licensed and not redistributable. **Decision:** build CSVs
from pinned Wikipedia revisions (CC BY-SA 4.0) with `tools/universe-builder`, record source/revision/licence per index
and stock, validate every symbol against Yahoo, never invent ISINs. **Consequences:** reproducible, attributable,
updatable without code (admin upload, override directory, re-run builder); lists are snapshots (survivorship bias);
quality depends on the Wikipedia tables (e.g. NTT listed twice → 224 Nikkei members).

## D-13 Canonical symbols = Yahoo symbols (2026-10-14)
**Context:** tickers collide across exchanges and providers use different suffixes. **Decision:** store one canonical
symbol per listing in Yahoo form (`SAP.DE`, `TSCO.L`, `7203.T`, `BRK-B`) and translate per provider
(`SymbolMapper`). **Consequences:** unique keys and readable symbols; Alpha Vantage needs `.DEX/.PAR/.LON` mapping
and cannot serve Tokyo; an earlier mapping bug that turned `SAP.DE` into `SAP-DE` is gone.

## D-12 Prices stored in ISO major units (2026-10-14)
**Context:** London prices arrive in pence. **Decision:** convert at the provider boundary (`CurrencyUnits`) and reject
implausible jumps (> 20×) on quote refresh. **Consequences:** all stored and displayed prices are in GBP/EUR/JPY/USD;
analyses never see mixed units.

## D-11 One currency per analysis, no FX (2026-10-14)
**Context:** the universe now spans USD, EUR, GBP and JPY; there is no FX data. **Decision:** reject mixed-currency
optimizations, ML experiments and comparisons with an explanation; switch the benchmark to the universe's index
benchmark when needed (with a visible note). **Consequences:** results are interpretable; cross-market portfolios are
out of scope until FX data is added.

## D-10 Shared server-side quote refresh, triggered by sign-in (2026-10-14)
**Context:** prices had to appear automatically after sign-in without every browser calling providers and without
re-downloading history. **Decision:** a de-duplicated background cycle (`QuoteRefreshService`) per sign-in / schedule /
admin request, Yahoo spark batches of 20, cached quotes reused for 15 minutes, synchronous page and single-stock
refreshes with a cooldown, incremental history check for tracked instruments only. **Consequences:** ~45 provider
requests refresh ~920 instruments in ~30 s; quota-limited providers (Alpha Vantage) refresh only tracked instruments
within half of the remaining daily budget.

## D-09 Yahoo stays the default provider; Alpha Vantage optional (2026-10-14)
**Context:** the configured Alpha Vantage key is a free key (25 requests/day, ~1/s, no Tokyo, no adjusted series).
**Decision:** keep Yahoo as default for quotes and history, support Alpha Vantage as an alternative with a client-side
daily quota. **Consequences:** coverage of all five markets; dependence on an unofficial endpoint (documented).

## D-08 Test data isolated by environment and reserved domain (2026-10-14)
**Context:** Playwright runs against the development stack had left 11 test accounts in the admin view.
**Decision:** e2e only on a disposable `po-e2e` stack (`scripts/e2e.sh`) enforced by `/api/v1/system/test-environment`;
test accounts under the reserved `.test` domain are hidden, excluded from admin metrics, refused in Production and
removable with `purge-test-data`. Rejected: an `IsTestData` column on every table (spoofable via the API and invasive).
**Consequences:** no test pollution of real databases; purge is auditable and safe by default (dry run, Production
refusal).

## D-07 Result interpretations generated in the frontend from stored numbers (2026-10-14)
**Context:** students need explanations of their actual results; explanations must never invent reasons.
**Decision:** pure functions (`lib/interpretation.ts`) derive labelled statements (Calculated / Estimate / What it means
/ Caution) from the run's stored outputs; strategy guides live in `lib/strategy-guide.ts` with computed worked
examples. **Consequences:** testable without a backend; no new API; content must be kept in sync with the code
(tests check structure and the worked-example math).

## D-06 Shared portfolio simulator (2026-10-13)
Classical backtests, ML backtests and strategy comparisons all run through `PortfolioSimulator`, verified
bit-identical to the previous backtester on nine configurations, so comparisons are fair by construction.

## D-05 ML engine separate from the optimizer (2026-10-13)
`PortfolioOptimizer.MachineLearning` has no I/O and must not use the mean-variance optimizer (architecture test).
ML portfolios use transparent construction rules; only the labelled hybrid strategy combines both.

## D-04 ML.NET trees + in-house linear models (2026-10-13)
LightGBM has no macOS arm64 binaries; ML.NET FastForest/FastTree are portable. Ridge and logistic regression are
implemented directly (closed form / IRLS) for exact, explainable contributions.

## D-03 Cookie sessions with administrator approval (2026-10-13)
ASP.NET Core Identity cookies (HttpOnly, SameSite=Strict) + antiforgery header; status checked on every request so
suspension is immediate. Rejected: JWT (no server-side revocation without extra state).

## D-02 PostgreSQL with separate owner and application roles (2026-10-13)
Migrations run as the owner in a one-shot `migrate` service; the API uses a DML-only role and refuses an outdated
schema. SQLite data can be imported with `tools/PortfolioOptimizer.DataMigration`.

## D-01 Stock selection is client memory only (2026-10-13)
Selecting stocks never writes to the server or browser storage; the universe is stored only as part of a submitted
run. Sign-out reloads the page so nothing carries over to the next user.
