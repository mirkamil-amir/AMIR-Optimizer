# Implementation status

## 2026-10-31: Forge stock list in the portfolio currency

* New portfolio (Forge): the portfolio currency is page state shared by the form and the stock list – a "Prices in"
  picker in the list changes the form's currency and vice versa. The list adds "Price (<currency>)" (shared
  `ConvertedPriceCell`, latest reference rates from `GET /fx/board` for the page's trading currencies, "—" without a
  rate) next to "Original price" (the quote with change, kind and time), scrolls under a fixed header and opens in
  Full screen with the same search, filters and pager. Choose stocks of Math/ML Optimization shows the same column in
  the portfolio's fixed currency (no picker).
* The stock list adds a "1 month" column – change and sparkline as on the watchlist, from `GET /stocks/trends` (one
  request per page, same calculation as the watchlist, integration-tested against it) – and fixed column widths that
  fit next to the two sidebars: tick box, ticker and both prices always; "1 month" from md; Company from xl (name under
  the ticker until then); Sector and Index from 2xl. Phones scroll the list sideways inside its box. Full screen has no
  sidebars: every column and the full company name (wrapping), plus three full-screen-only columns – "3 months" and
  "1 year" (same trend endpoint, loaded only while full screen is open) and "Price history" (stored days and span, marked
  "too short" below the 61 prices analyses need). Fits without sideways scrolling from 1280 px.

## 2026-10-31: holdings laid out like the trade proposal

* The positions list on the Overview (Core) uses the proposal's layout: `SearchableTable` with search, sector filter and
  "Show closed & target-only" (`toolbar`), about ten rows under a fixed header, Full screen; "Price (<currency>)" with
  only the rate underneath and "Original price" with its close date and stale flag – the same cells as Proposed trades.

## 2026-10-31: Codex guides as reading pages

* A strategy guide (`/learn/:id`) is one article instead of twelve folded boxes: the same 12 topics as headings in
  reading order (what, objective, theory, steps, worked example, mathematics, inputs, when to use, reading the
  portfolio, your own results, advantages and limitations, caveats), on a `.map-surface` panel at reading width, with
  family and reading time, the objective, takeaway and caveats as callouts, "Read next" and previous/next within the
  family. Wide screens: an "On this page" column follows the reader and marks the current section; phones: contents
  above the text. Checked with screenshots at 1440 and 390 px (API mocked, no account).
* Every guide has one or two interactive figures (`components/guide-figures.tsx`) under "The theory" and/or "Worked
  example": the two-stock curve with correlation, tangency line, target return or volatility limit; volatility by weight;
  1/N diversification; scores → weights with top K, rule and cap; walk-forward timeline; trading-cost drag; ridge
  shrinkage; the logistic curve; forest averaging; boosting with training vs. test error. Sliders change the example,
  "Play" animates it (not offered with reduced motion), charts are drawn at their real width so text stays readable on
  phones. All figures use sample data from `lib/guide-demos.ts` (fixed values or generated from a fixed seed, unit-tested,
  reproducing the worked examples) and say "Sample data – not market data"; they never read stored prices, the API or
  user data.

## 2026-10-29: long lists and the correlation matrix (D-48)

* Solver and Pattern: the equity universe, the allocation, the stock predictions and the stock eligibility are
  searchable, sortable, paged lists (`SearchableTable`, shared with Atlas; 25 rows per page, fixed header, first/last);
  each states what it counts ("228 stocks in the universe · 33 with a weight · 195 at 0 %"). Searching never submits the
  optimization form.
* The universe list now gets every stock's name and history beyond 200 stocks (requests of 200 symbols).
* Correlation matrix: scroll bars, pull with the mouse, row and column symbols pinned, Full screen view; only visible
  cells are drawn – opening a 228-stock matrix in full screen went from 5.9 s to 0.2 s (measured in Chromium on the
  development stack).
* Checked: Vitest 211 passed (new: SearchableTable, PannableArea, matrix window, heatmap), type check, lint, build;
  screenshots of a stored 228-stock run and a 24-stock ML Optimization at 1440 px and 390 px (no horizontal scroll).
  `./scripts/e2e.sh` not run for this change; `portfolios.spec.ts` was updated for the universe table.

## 2026-10-28: quantitative audit, formula reference, maintenance tooling

* Backtests across markets with different holidays (audit Q-01/Q-01b, [quant audit](quant/audit-2026-10-28.md)): an
  execution day on which one exchange is closed no longer sells that market's positions into cash until the next
  rebalance – the rebalance waits up to 5 trading days, then trades around still-closed positions; a signal date on a
  holiday of one exchange keeps its stocks in the optimization (latest close ≤ signal). Changes results of multi-market
  backtests, comparisons and ML walk-forward backtests when they are re-run (stored results keep their numbers).
* The independent allocation validator applies the turnover limit under the same rule as the optimizer (only when an
  asset of the universe is held); before, such runs failed validation (Q-02).
* [Formula reference](quant/formula-reference.md): 65 entries mapped to code symbols, tests and skills, each with a
  verification status; [subsystem inventory](quant/subsystem-inventory.md); remaining-risk register in the audit.
* `scripts/quant/maintain.py` (validate, index, check-skills, review-changes, audit, test-quant, full-check) and the
  `quant-maintenance` GitHub Actions workflow (added, not yet run on GitHub). Seven subsystem skills added
  (`quant-formulas`, `optimization-backtesting`, `market-data-fx`, `ml-pipeline`, `portfolio-accounting`,
  `platform-architecture`, `maintain-knowledge`); `ship-change`, `ml-experiment`, `run-app` updated.

## 2026-10-26: MatrixView; per-portfolio currency view

* 2026-10-27: branded navigation (D-47) – sidebar Lens · Forge · Solver · Pattern · Trace · Rewind · Arena, portfolio tabs
  Core · Solver · Pattern · Trace · Rewind · Arena · Ledger, each with its plain name as tooltip and screen-reader
  description; Ledger's "Cash ledger" sub-tab is "Cash". Lens shows its title "My portfolios" again, with Forge a portfolio on
  the same row; the totals' currency sits in their heading line ("Across 3 active portfolios in USD"). Main buttons echo their tab: Forge portfolio, Solve, Learn patterns,
  Rewind, Face off; links between sections read Open Solver/Pattern/Trace/Rewind/Arena/Lens/Forge; money, deletion, data
  and account actions keep literal labels. Top bar: Matrix · AI Probe · Wire · Radar · Atlas · Codex · Lexicon · Settings
  with plain-name tips; links to these areas read Open Lexicon, Back to Codex, Open Codex, Back to Radar.
* 2026-10-27: navigation labels shortened – Math, ML, History (sidebar and portfolio tab, formerly Optimization history /
  Optimization runs), Backtesting, Comparison; the title repeating a tab (and "My portfolios" on View) is screen-reader only;
  the workspace header lost its "My portfolios" link (sidebar View); the app is just "MatrixView" (no tagline).
* Renamed: the app MaTrix → **MatrixView**; top bar Optimizer → **Viewer**; sidebar Portfolios Overview → **View**,
  Portfolio Creation → **New** (page heading "New portfolio"). Older entries below keep the names of their time.
* My portfolios: the currency box converts every card, not only the total – value, invested, cash, net contributions and
  profit/loss (`PortfolioSummaryDto.InCurrency`). Today's value at the latest rates (Holdings reporting conversion); each
  deposit/withdrawal at the rate of its own date, so P&L includes the currency effect. The total is the sum of the cards
  (before: contributions at today's rate, which hid the currency effect).

## 2026-10-26: Holdings merged into the Overview

* The Holdings tab is gone; the Overview has its parts once each: reporting currency (whole page), cost basis (in the
  Unrealized P&L card), currency exposure, the positions table (search, sector, closed/target-only, side panel per stock)
  (`components/holdings-table.tsx`). `/portfolios/:id/holdings` and `/holdings` open the Overview; a simulated execution
  opens it too. The Overview's efficient frontier (latest Math Optimization) was removed – it stays on the run's page.
* Market data: the coverage table has a search box (symbol, name, currency), pages of 50 sorted rows (`DataTable pageSize`)
  and scrolls inside a fixed height (about 15 rows, header stays); its price chart was removed. Downloads show the MatrixView
  loader (`WorkingPanel`: progress, last stock, queued downloads) and a loader on each row still waiting for its prices.
* Market data has three pages – `/market-data` (Stocks & ETFs), `/market-data/indexes`, `/market-data/currencies` – with
  the same list: search, rows per page, first/previous/next/last (`RangePager`, shared with the stock chooser, inside
  `DataTable pageSize`), a box that scrolls under a fixed header; downloads, providers and jobs stay below on each page.
  Each page has its own sync (administrators): tracked stocks & ETFs or tracked indexes (`SyncRequest.AssetTypes`), or every
  exchange rate (`POST /fx/sync`; `MarketDataStatusDto.FxDownloadRunning` shows the MatrixView loader while rates arrive).
* Market data lists the whole catalogue (not only tracked instruments) in three boxes: Stocks & ETFs (search, pages;
  untracked ones are "Not tracked" with Download), Indexes, and Currencies (stored ECB rates per currency: 1 EUR = …,
  coverage, freshness; `MarketDataStatusDto.Currencies`). The stock chooser's market counts are distinct instruments
  (`MarketDto.Instruments`: listings plus members of its indices), so "All markets" equals "All sectors" – before it summed
  current index members and missed SPY (no index) and four stocks only in the historical DAX 30.
  Latest prices show each instrument's own trading currency (`AssetCoverageDto.Currency`; also `MlPredictionDto.Currency`
  on ML Optimization) – they were all labelled USD before.

## 2026-10-25 (4): large optimizations, faster downloads (D-43)

* Optimizer: exact maximum return (greedy LP), warm-started capped frontier, early polish, OSQP-style check interval.
  230 stocks via the API: ~2 min (504) → 10–11 s; benchmark frontier 154 s → 6.9 s. New test: exact max return with
  bounds and sector caps, frontier ending at it.
* Downloads: 8 requests/s, 8 in flight; DbContext released per symbol: 100 five-year symbols 33.6 s → 17.6 s.
* Stock list without the per-row Refresh button.
* Verified: `./scripts/test.sh` (after restarting the Podman machine, which had stopped with 5 GB free on the Mac;
  `fstrim` returned it to 17 GB), Vitest 187, Playwright 35; timings on the development and the e2e stack.

## 2026-10-25 (3): faster downloads, stock chooser for 35 markets (D-42)

* Sync jobs: start-spaced throttle with up to 4 Yahoo requests in flight, parallel download / sequential storage, quotes
  batched after the job; pre-listing periods are empty, not failures (5326.KL, AADI.JK now succeed). Measured: 100 stocks ×
  5 years 62 s → 36 s (≈1.4 s → 0.36 s per stock vs. the earlier jobs).
* Portfolio Creation and the optimizations' stock choice: markets grouped by region (folding, region as a filter, API
  `country=DE,FR`), filter chips with Clear all, "Selected only", pager with rows per page, position and first/last.
* Verified: `./scripts/test.sh` (unit incl. throttle and pre-listing tests, ML, integration incl. region filter), Vitest
  187, Playwright 35; downloads timed on the development stack; screenshot of the chooser.

## 2026-10-25 (2): twenty more markets (D-41)

* Norway, Finland, Belgium, Portugal, Ireland, Austria, Poland, Hungary, Greece, Iceland, Israel, South Korea, China,
  Malaysia, Thailand, Brazil, Indonesia, Mexico, South Africa, New Zealand: 791 stocks (20 index universes) and 14 index
  ETFs (10 markets); venues, time zones, sessions, benchmarks (18), foreign-currency listings, research-brief markets and
  names, map cities and frames. Builder: retries when Wikipedia throttles, tables whose class follows other attributes or
  that have a title row, ticker changes and delistings recorded in the script.
* Verified: every symbol and benchmark against the price provider (currency, at least a year of daily prices); on the
  development stack the 30 new universe files imported with 0 issues, the login quote refresh covered 2,188 symbols
  (2,130 updated; failures: four known provider quirks and stale quotes), a year of prices for one stock per new market
  and every benchmark downloaded, agorot and cents converted. `./scripts/test.sh`, Vitest 183, Playwright 35.
* Not done: point-in-time constituents (as before); holiday calendars; the Philippines (no provider data).

## 2026-10-25: MatrixView – UI polish, deletable optimizations, new brand (D-40)

* Brand: renamed **MatrixView** (page title "MatrixView - Portfolio Viewer"); logo = 4×4 correlation matrix with a gold diagonal
  (chosen from nine concepts), also the favicon. Navigation (top bar and sidebar) in Sora (chosen from nine fonts); entries
  magnify near the pointer like a dock (`lib/nav-zoom.ts`), the selected one stays enlarged. Sidebar without headings.
* No decorative icons anywhere; explanations open from their label (no ⓘ, no underline). Header shows the name only.
* Portfolio tabs: Overview · Math Optimization · ML Optimization · Optimization runs · Backtest · Strategy
  comparison · Transactions. Optimization runs: scrolling lists with search and filters; Math and ML Optimizations can be
  deleted (bin + confirmation; `DELETE /api/v1/optimization-runs/{id}`, integration-tested incl. cross-user and archived).
* Math/ML Optimization: one "What your result means" box (paragraphs by kind, inputs & methodology, data notes); ML feature
  guide in plain language, checked against `FeatureCatalog.cs`. Backtest: inline choice of any optimization, no custom
  strategy form, one "Data notes & methodology" box.
* Currency converter: one calculator line; list and chart follow From (chart = To per From, drawn without a click); To
  never equals From; the rate and its date in the result's tooltip.
* Verified: `./scripts/test.sh` (unit, ML, integration) earlier in this change set; Vitest 183; Playwright 35 on the
  disposable stack; screenshots of sidebar, header, converter, backtest and overview.

## 2026-10-24 (8): typography and contrast (D-39)

* Sora for the wordmark, page titles, portfolio names and section headings (chosen from seven rendered options); Geist
  for everything else.
* Readability over the map: veil behind the content column, halo for text directly on it, near-solid panel for the
  Portfolio Creation market/sector list; buttons and badges excluded from the halo. Checked in light and dark screenshots.

## 2026-10-24 (7): background map – cool palette and selection-aware camera (D-38)

* Map: dotted Natural Earth 1:50m countries (incl. Hong Kong and Singapore) on an ice-blue ocean (navy/slate/cyan in dark
  mode); dot spacing follows the zoom so coastlines stay precise; no country borders; selected markets with teal dots,
  the focused market (last selected or opened stock) with larger, stronger dots, a bold label and a double-ring marker.
  Exchange cities as markers; labels only for highlighted markets. Content cards are 82 % opaque so the map shows through.
* Camera: frames the highlighted markets plus the focused one – one market → its region, several → their combined extent,
  none → world; smooth CSS transition timed by distance, rapid selections combined (220 ms), resizes re-framed instantly,
  reduced motion respected. Header globe control: markets in view as text, "Fit selected markets", "World view", one market.
* Portfolio workspace frames its holdings (its universe while it holds nothing); stock selection, opened stocks and the
  portfolio list feed the same state. Camera positions never enter React state.
* Verified: Vitest (camera framing, zoom limits, rapid selections, interruption, manual view, resize, reduced motion,
  cleanup), browser screenshots in light and dark at 1440, 820 and 390 px (no console errors, no horizontal overflow).
  Frame rate was not measured.

## 2026-10-24 (6): ten more markets, one Overview, portfolio tabs, world map (D-37)

* Markets: Switzerland (SMI), Spain (IBEX 35), Italy (FTSE MIB), Sweden (OMX Stockholm 30), Denmark (OMX Copenhagen 25),
  Canada (S&P/TSX 60), Australia (S&P/ASX 50), Hong Kong (Hang Seng), India (NIFTY 50), Singapore (Straits Times) – 423
  stocks from pinned Wikipedia revisions plus 18 index ETFs (curated); all 441 symbols and their currencies were checked
  against the price provider on 2026-10-24 (three SGX listings trade in USD and are recorded so). Venues, sessions, index
  benchmarks, Alpha Vantage `.TRT` for Toronto, research-brief markets; symbols may contain `&` (`M&M.NS`).
* Portfolio workspace: tabs Overview · Math Optimization · ML Optimization · Optimization runs ·
  Backtest · Strategy comparison · Transactions. Performance merged into the Overview (date range, efficient frontier right under portfolio
  vs benchmark, recorded performance, risk, allocation over time – duplicates removed). Optimization runs (Math and ML),
  Backtest and Strategy comparison stay inside the portfolio and list only its work; the portfolio is fixed there.
* Background world map: dotted Natural Earth land, exchange cities; lit for the open portfolio's holdings and universe,
  all cities on "My portfolios", the selection's markets; a selected stock's city ripples, an opened stock's city blinks
  (reduced motion respected).
* Not done: prices of the new markets are not downloaded in advance (on demand, as for every market); the development
  database receives the new universes on the next API start.

## 2026-10-24 (5): foldable reference boxes; backtest monthly line chart and currency

* Shared `Collapsible` (button with `aria-expanded`, closed by default): comparison – "Strategies and methodology",
  "Universe and data notes", "What the comparison shows" (Holdings overlap now next to Risk vs return, Turnover full width);
  backtest – "Rebalance log" (new "Invested" column: sum of target weights, or "held" when the previous holdings were kept)
  and "Data notes"; Math and ML Optimization – "What your result means" (the "How this strategy works" button stays in the
  title row), "Inputs & methodology", ML data notes.
* Backtest monthly returns: line chart of strategy and benchmark. Fixed: backtest amounts were always labelled USD; they now
  use the backtest's analysis currency.

## 2026-10-24 (4): Strategy comparison layout

* Drawdown, average allocation by stock and average sector exposure use the full page width (sector names no longer
  shortened); risk vs return and turnover side by side; monthly returns as one line chart of all strategies (table view
  lists every month); "Universe and data notes" folded by default and complete when opened (`Warnings collapsible`);
  "What the comparison shows" with "How this strategy works" at the bottom. `optimizations.spec.ts` checks the order and
  the folded notes.

## 2026-10-24 (3): Math and ML Optimization side by side in each portfolio

* Renamed "Mathematical Optimization" → "Math Optimization". ML Optimization moved into the portfolio workspace
  (`/portfolios/:id/ml-optimize[/:experimentId]`, tab next to Math Optimization); `/ml-optimize` and the earlier
  `/machine-learning[/:id]` URLs redirect. Both pages share one layout: Optimization history, then the form (equity universe
  list with Browse stocks → Choose stocks page, "Use stock selection", Clear; Run optimization / Run ML optimization) on
  the left; header with badges, currency and date on the right, five headline metrics starting with Investable value, every result section visible at once (`ResultSection`, no tabs), the trade proposal card
  (Create trade proposal / Recalculate; `TradeProposalCard`, its title folds the proposal away to a one-line summary of orders, buys and sells); Math Optimization's allocation shows the donut, table and weight by sector (full width), the risk-contribution chart scrolls inside a fixed height, and the reference portfolios appear as squares in the efficient frontier and as curves in the in-sample charts, and "What your result means" with "How this strategy works" last.
* Experiments without a portfolio are no longer listed on the ML Optimization page (card removed 2026-10-25, owner's
  request); one opened by its link (`/machine-learning/:id` opens it in the current portfolio) can still be linked there
  (`ExperimentPortfolio`, same currency only), and `GET /ml/experiments?unlinked=true` remains. ML trade proposals always
  target the portfolio in the URL. Selection tray: "Math Optimization in …" and "ML Optimization in …".
* Tests: Playwright `optimizations.spec.ts` checks both pages (no tabs, named sections visible, interpretation below the
  proposal) and a backtest window with two Math runs and one ML Optimization; other specs updated for names and URLs.

## 2026-10-24 (2): Mathematical / ML Optimization, backtest and compare any optimization of a portfolio (D-36)

* Sidebar: Mathematical (since renamed Math) Optimization, ML Optimization, Optimization history; Test & compare: Backtest, Strategy
  comparison. Workspace tab and page titles renamed accordingly.
* ML experiments record their portfolio (form picker; `PUT /ml/experiments/{id}/portfolio` links older ones of the same
  currency). `GET /portfolios/{id}/strategies`; `POST /backtests/from-strategy` (window on the Backtest page; ML strategies
  run walk-forward through `BacktestEngine`); comparisons of any two optimizations of one portfolio (math/math, ML/ML,
  math/ML; hybrid only for math + ML). Migration `StrategiesPerPortfolio` (nullable `PortfolioId` on three tables).
* Tests: `StrategyComparisonTests` rewritten (math vs ML, math vs math, ML vs ML, same-portfolio and cross-user 404s,
  linking with currency check, backtests of a run and an experiment incl. the in-sample warning, frozen settings after
  deletion); Vitest `strategies.test.ts`; Playwright `optimizations.spec.ts` (sidebar order, backtest window, math vs math
  comparison). Note: with simulated prices a Maximum Sharpe run can be infeasible and is then not offered (only successful
  optimizations are listed).

## 2026-10-24: ask before an analysis when prices or exchange rates are missing

* `DataReadinessService` + `POST /market-data/check` / `download-missing`; dialog (`useMissingDataGuard`) in the ML
  experiment, backtest and optimization forms: download and continue / continue without / cancel. Details in
  [market-data.md](market-data.md#daily-history).
* Tests: `DataReadinessTests` (missing older prices, benchmark without prices and USD rates reported, downloaded on request
  as an approved user, then complete; second download does nothing; anonymous 401; validation), Vitest `data-check.test.ts`,
  Playwright `missing-data.spec.ts` (dialog shown for 15 years of history; cancel starts nothing; download and continue
  starts the experiment).

## 2026-10-23: long FX histories and ML stock eligibility (D-35)

* **Reported:** an 8-year ML experiment in USD (19 stocks) excluded 7 EUR/GBP/JPY stocks ("749 of 2022 prices have no
  GBP→USD rate within 4 days").
* **Root cause 1 – history never downloaded:** stored ECB rates began 2021-10-08; every missing date was in one leading run
  2018-10-23 → 2021-10-07 (no interior gaps, no weekend/holiday pattern, quote direction and date keys correct). The API log
  shows the single 8-year ECB request for GBP, JPY, USD hitting the 10 s attempt timeout twice and the 25 s request bound
  ("FX provider ECB did not answer within 25 s"); measured directly, the ECB needed 17 s for it (2.6–20 s for one year). The
  fetch was cancelled with the request, so the next run failed the same way. No Redis exists (D-33): no cache was involved.
* **Root cause 2 – rates checked on unused dates:** the benchmark SPY had prices only from 2021-10-13; the ML panel uses the
  benchmark's dates, so the stocks' 2018–2021 prices were never used, yet their missing rates excluded the stocks
  (within SPY's span all 15 non-USD stocks had 0 prices without a rate).
* **Fixes:** `FxDownloader` (two-year chunks, stored as they arrive, contiguous per currency, single flight, continues after
  the request stops waiting; background jobs wait up to 5 min; ECB attempt timeout 45 s); `GapsAsync` returns head and tail
  gaps instead of refetching the stored middle; exclusion reasons name the cause (`FxMissing`) and download problems;
  `MlDataLoader` converts only the benchmark's span; per-stock eligibility funnel stored with each experiment and shown in the
  "Setup" tab; non-finite ML scores are reported instead of silently dropped. The 4-day carry-forward and the 5 % exclusion
  rule are unchanged.
* **Measured on the development database (2026-10-23):** before – USD/GBP/JPY from 2021-10-08 (1,292 rates each); 720–758
  prices without a rate for 6952.T, 7751.T, ABF.L, ANTO.L, BAYN.DE, BEI.DE, BVI.PA. After deploying and one history request
  per pair (6.9 s and 6.7 s, 4 ECB chunk requests of 2.3–4.3 s) – USD/GBP/JPY from 2018-10-08 (2,060 each, largest step
  5 days); 0 prices without a rate for all 15 non-USD stocks of the experiment. The experiment itself was not re-run on the
  development stack (runs are created only on the e2e stack); the selection audit of the stored run found the bottleneck
  in data eligibility: 12 of 19 stocks eligible, top 10 selected at 10 % each, fully invested – not the model or caps.
* **Tests:** unit (chunk plan, contiguity filter, exact/beyond 4-day age, no later rate, cause classification, reported case),
  ML (invalid scores), integration `FxHistoryTests` (chunked download kept on failure and resumed; download continues after
  the request stops waiting and is visible at once; ML experiment waits for rates and checks only the benchmark span –
  verified to fail with either fix removed), eligibility funnel in `MachineLearningTests`.

## 2026-10-19 (2): currency converter speed, layout and BGN (D-34)

* Root causes (from the API logs): every added currency waited for a new `/fx/board` request that called the ECB inside the
  request (6–7 s; 20–72 s with ECB timeouts and retries); BGN's ended series was refetched with every addition; ranges starting
  on a weekend never counted as stored. No Redis exists (D-33) – it was not involved.
* Fixes: one board per base with every currency (adding/removing is local state), full rate set per ECB request for ranges
  ≤ 400 days, single-flight fetch, 25 s bound, closure-tolerant coverage, BGN history-only with an explanation.
* Chart follows the list height (capped list scrolls, with a hint), stays visible while updating, error/retry state, day labels on
  short ranges; currency drop-downs open as plain lists (no off-screen options near the page bottom).
* Measured (real ECB, empty database, replaying each version's requests): per added currency 2 ECB calls → 0; chart of an
  added currency 24–789 ms → 6–21 ms; e2e at 1440/1100/768/390 px: 1 board request per page, no overflow, no console errors.

## 2026-10-19: performance (D-33)

* Navigation data status and Portfolio Creation's markets/sectors: 2.4 s → 11–25 ms (set-based query instead of a per-instrument
  `EXISTS`); compact `/market-data/summary` for the sidebar; pages settle in 0.6–1.1 s instead of 3.1–4.1 s.
* News: stored articles first, due sources refreshed in the background (first view after the refresh window 10.1 s → 0.2 s).
* Batched price queries (risk 45 → 24 SQL commands, holdings 30 → 16, reality check 30 → 12, optimization 26 → 17).
* No Redis (decision matrix in [performance.md](performance.md)); parallel efficient frontier tried and reverted.
* Fix found by the e2e run: "Record transaction" sent the browser's date by default; with the Podman VM clock ~3 h behind
  the host, deposits were refused as "in the future". The form now sends a date only when the user picks one (Vitest).
* Verified: unit 251 (+8 skipped), ML 40, integration 122 (incl. `DataAccessEfficiencyTests`: batched = per-asset reference,
  summary = status, unchanged coverage counts, background news refresh once for concurrent viewers, failures recorded not served),
  Vitest 138, e2e 32; before/after on the development stack (read-only).
* Open: optimization analytics (efficient frontier) is ~95 % of a run.
* Test stalls of ~16 min (one integration run, two e2e runs) were the Mac entering "Maintenance Sleep" (pmset log: 990–1046 s);
  unattended runs now use `caffeinate -ims` (see testing.md).

## 2026-10-17 (2): multi-portfolio workspaces (D-32)

* Overview = My portfolios (all of the user's portfolios, totals only when every one can be valued); Portfolio Creation
  replaces Stock selection; per-portfolio workspace `/portfolios/:id/…` with header, switcher and sections; old URLs redirect.
* Manual transactions (journal): buy, sell, deposit, withdrawal, dividend, fee – atomic, idempotent, in time order, with
  reversal of cash events; archive/restore, delete only when archived; research sessions with a portfolio context.
* Fixes: deposits/withdrawals on non-trading days are no longer dropped from performance; a run opened under another
  portfolio's URL now opens in its own portfolio (it showed the other portfolio's currency and run list).
* Verified: backend unit 251 (+8 skipped), ML 40, integration 117 (incl. `MultiPortfolioTests`, 10); Vitest 136; e2e 32 on the
  disposable stack (incl. `portfolios.spec.ts`); migration rehearsed twice on a copy of the development database.
* Open: no trade reversal or imports, no money-weighted/combined return, backtests/ML/comparisons not portfolio-bound,
  AI questions about holdings – see [portfolios.md](portfolios.md#status).

## 2026-10-17: primary top navigation (D-31)

* Top bar: Optimizer · AI Research · Financial News · Watchlist · Market Data · Strategy Guide · Glossary · Settings
  (Strategy Guide and Glossary moved up in a follow-up; single strategy pages keep Strategy Guide active); the sidebar
  holds the Optimizer sections (Workspace, Optimize, Test & compare, Paper portfolio) and, unchanged, Administration with Admin settings.
  One model in `lib/navigation.ts`; no route changed; active state follows the URL (stock pages, reality checks and
  strategy guides now highlight their section). Narrow screens: the bar scrolls horizontally, the sidebar is a menu sheet.
* Fix: the theme pre-paint script moved from inline to `public/theme-init.js` – the container CSP (`script-src 'self'`)
  had blocked it, so saved themes were applied only after React started.
* Verified: Vitest 117 (incl. `navigation.test.ts`, `app-shell.test.tsx`), e2e 30 on the disposable stack (incl.
  `navigation.spec.ts`: order, every area, sidebar sections, back/forward, reloads, 1440/820/390 px, keyboard, regular
  user without admin area and HTTP 403 from admin APIs, no console errors), screenshots at 1440/1024/820/390 px.
* Limitation: a stock page opened from the watchlist shows Optimizer as the active area.

## 2026-10-16 (3): brand v2, ten skins, converter base selection

* Converter only on the overview; its base ("1 USD =") is selectable among all published currencies – board, chart and the
  calculator default (base → portfolio currency) follow it.
* New mark and serif wordmark; ten skins with themed sidebars (Navy & Gold default); old "forest" choice maps to Emerald.
* Verified: Vitest 80, e2e 25 (incl. base selection, dark Burgundy persisting), screenshots of Navy light/dark, Midnight,
  Burgundy, Emerald dark, sign-in and the appearance picker.

## 2026-10-16 (2): currency converter, AMIR brand and themes

* Converter card (rates board, pair chart, calculator) on the overview and stock-selection pages, backed by
  `/fx/history` and `/fx/board`; labelled reference rates with dates, never "live".
* Brand: MatrixView (formerly AMIR Optimizer); SVG mark (a correlation matrix with a gold diagonal) in the sidebar, sign-in pages, favicon, tab title.
* Light / dark / system mode and five colour skins; header toggle and Settings → Appearance; persisted per browser.
* Verified by unit, integration, Vitest and e2e tests and by screenshots (light, dark Ocean, Violet).

## 2026-10-16: multi-currency portfolios (FX service, dated conversion, independent validation)

* **Root causes** ([currency-audit.md](currency-audit.md)): no FX capability existed, so analyses rejected mixed selections
  and only default-currency portfolios could be created; the run → proposal → execution path and backtests had no currency
  check at all (EUR prices could be spent as USD cash); silent `?? "USD"` / first-asset defaults; the admin "add asset"
  dialog hard-coded USD; native prices were formatted with the portfolio currency in holdings, proposals and trades.
* **Implemented and tested:** `FxRates` + ECB and simulated providers; `FxService` (one convention, inverse/cross, 4-day
  carry-forward, typed unavailability, upsert, throttle); conversion in optimizer, reality check, backtests, ML, strategy
  comparison and research hypotheses; `PortfolioValuationService`; portfolios in any supported currency; simulated exchange
  on trades with stored rate/date/source; reporting currency for holdings and performance; FX API; listing currency from
  the venue; `AllocationValidator` before any run is accepted; UI for all of it; migration with backfill.
* **Verified:** see [testing.md](testing.md#results-2026-10-16-multi-currency-portfolios); migration on a copy of the real
  database; live ECB call. The user's existing data has no mixed-currency trades (read-only check: 9 USD executions in a
  USD portfolio; one EUR run on the USD portfolio without trades).
* **Mission phases 4–5 mapped to existing components** (no new agents): orchestrator = `ResearchAgent` (state machine,
  budgets, bounded steps); research = `search_investable_universe` / company context; risk = reality-check review + flags;
  optimization = deterministic `OptimizationService`; validation = `AllocationValidator` + reality check (sensitivity,
  out-of-sample) + `NarrativeValidator`; reporting = `InterpretationService`. Infeasible results are not retried by relaxing
  constraints; the user confirms suggested changes. Execution stays paper-only behind confirmation, idempotency keys and
  audit; there is no broker integration.
* **Not implemented:** scheduled portfolio reviews / alerts (monitoring), multi-currency cash, hedging, FX costs, changing a
  portfolio's currency, a Swiss market in the catalogue, live ECB use in the running app (verified only by the live test).

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
| Working refresh control per stock | Done | row Refresh button + detail button; integration + e2e tests |
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
