# Architectural decisions

Short records of decisions that are not obvious from the code. Newest first. Format: context → decision → consequences.

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
