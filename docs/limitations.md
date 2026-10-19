# Known limitations & future enhancements

## Scope
- Research and paper trading only. No broker connectivity; no real orders. Not investment advice.
- **No result in this application shows that a strategy is profitable.** All real-data ML experiments run during
  development (10 US large caps; 6 DAX stocks) had test-period ICs indistinguishable from zero (|t| < 1) and negative
  out-of-sample R²; in the comparisons, equal weight had the best Sharpe ratio. See
  [final-report.md](final-report.md) and [implementation-status.md](implementation-status.md).

## Portfolios
- Transactions are recorded in time order only: no back-dating before the latest booking and no import of historical
  transactions; trades cannot be reversed (record the opposite trade).
- Performance is time-weighted from the first booking; no money-weighted return and no combined return across portfolios
  (the overview shows value and P/L against net contributions only). Dividends count only when recorded.
- Backtests, ML experiments and strategy comparisons belong to the user, not to a portfolio.
- The AI assistant uses a portfolio's currency and save target, not its holdings.

## Data
- Yahoo Finance is an unofficial endpoint (personal, non-commercial use; may break or throttle without notice).
  Stooq is blocked by a bot challenge and not supported. Alpha Vantage was verified with the configured free key
  (US, Xetra, Paris, London quotes; 2026-10-14).
- The universes (S&P 500, DAX 40, DAX 30 historical, CAC 40, FTSE 100, Nikkei 225) are snapshots of Wikipedia
  constituent tables (CC BY-SA). There are **no point-in-time constituents** (the DAX 30 list is one fixed historical
  composition), so every backtest, ML evaluation and comparison is subject to **survivorship bias**. ISINs are not
  provided by the sources. Cross-market sectors are an approximate mapping onto GICS-style groups.
- Alpha Vantage (configured free key): 25 requests/day, no Tokyo, no index levels – Yahoo remains the default.
- No FX conversion: each analysis uses stocks of one currency; paper portfolios need the stocks' currency.
- Quotes for all ~920 catalogue instruments are refreshed at sign-in; daily *history* is downloaded on demand (and
  automatically before ML experiments/comparisons), so most stocks have a current price but no history until used.
- Quotes are end-of-day or delayed, never real-time.
- No exchange holiday calendar, dividends not credited to the paper portfolio, splits after
  purchase not applied to paper holdings.
- Adjusted closes reflect adjustments known at download time (data vintage), not as originally published.

## Company descriptions
- From Wikipedia (CC BY-SA 4.0, attributed): encyclopedia text, not an issuer profile; it can lag corporate changes
  and is refreshed at most every 30 days. 28 Nikkei 225 members and Insulet (PODD) have no linked article and show
  "currently unavailable". The stock-detail panel navigates within the current page of the stock list (50 rows).

## News
- Company news comes from Yahoo's unofficial search (personal, non-commercial use) and is linked to stocks only via
  Yahoo's own ticker tags: non-US coverage is sparse and untagged articles are missing. No summaries from that source.
- Official feeds cover the Fed, ECB, Bank of England and Bank of Japan only; no exchange/issuer disclosure feeds,
  statistics offices or licensed newswires. Alpha Vantage news is premium for the configured key.
- Categories are keyword rules (unevaluated); the relevance indicator is unvalidated and never a price prediction.
- Images are off by default (rights unverified). No notifications.
- Event comparisons use daily closes, regular session hours and no holiday calendar; they cannot show causation.

## Currencies
* Daily ECB reference rates (≈16:00 CET); market closes are not simultaneous with them; no intraday FX, spreads or FX costs.
* One cash balance per portfolio in its currency; no multi-currency cash, hedging or forwards; the portfolio currency is
  fixed after creation (a reporting currency can be chosen per view).
* Rates older than 4 days are not used: around long market closures values can be "not available" until new rates arrive.
* BGN is history-only (ECB rates until 2025-12-31; Bulgaria uses the euro since 2026-01-01): no current BGN conversions or BGN portfolios.
* A provider failure or timeout blocks re-fetching the same range for an hour; until then stored rates are used.
* No Swiss market in the catalogue yet (CHF portfolios work; CHF stocks cannot be selected).
* Monitoring (scheduled reviews, alerts) is not implemented; review and rebalance are user-triggered.

## AI research assistant
* The Anthropic provider has **not been exercised against the live API** (no key available); it compiles against SDK
  12.57.0 and the workflow is tested with a scripted mock. The server-side fallback option in the request is unverified.
* The Gemini provider was verified live with `gemini-3.8-flash` on a free-tier key: answers take 9–36 s and the free tier
  rate-limits bursts (HTTP 429), so during an evaluation some explanations fall back to the template. A paid tier or a
  lighter model (`AGENT_LLM_MODEL=gemini-3.5-flash-lite`) reduces this; untested here.
* Rule parsers cover common English phrasings; other languages and unusual wording reach the model (if configured) or
  get "not understood" with examples.
* Only the mean-variance strategies and equal weight are available to hypotheses; ML signals, backtests and the strategy
  comparison are not part of the workflow (answered honestly when asked).
* The universe screen uses only stored history; members without enough history are excluded (with a reason), so a fresh
  database yields small shortlists until prices are downloaded. There is no tool to download history from the workflow.
* Requests run synchronously (seconds); there is no background continuation or monitoring/alerting step yet.
* Explanation quality was not evaluated with users.

## Quant & ML
- The Portfolio Reality Check tests four assumption changes; it is not a full resampling/bootstrap analysis, and its
  out-of-sample section appears only once ≥ 20 trading days exist after the run's window.
- Expected returns for mean-variance are historical means. No Black–Litterman, factor models or robust optimization.
- Max-holdings / minimum-position constraints use a greedy heuristic, not mixed-integer optimization. No shorting
  or leverage. Proportional costs only (no market impact, no bid/ask model).
- ML features are price/volume based only; there are no fundamentals, earnings, news or alternative data.
- Test periods are short (20% of the history) and universes small, so metric uncertainty is large. The IC t-statistic
  uses non-overlapping dates, but nothing corrects for **multiple testing** across experiments a user may run.
- Hyperparameter grids are small and fixed per family; no nested cross-validation.
- Tree explanations (occlusion, split gain) are approximations; correlated features share or mask importance.
- Models are trained in-process and sequentially; large universes (hundreds of stocks × long histories) make
  experiments slow. Dense covariance math suits tens to a few hundred assets.

## Engineering
- One in-process background worker: jobs run sequentially; a crash marks running jobs failed (queued ones resume).
  Multiple API instances would each run a worker; a shared queue is not implemented.
- Model artifacts are stored on a local volume, not object storage.
- No e-mail confirmation, password reset by e-mail or multi-factor authentication.
- The backup/restore procedure in [deployment](deployment.md) is documented but was not rehearsed end to end.
- Playwright tests run against an already running stack and are not wired into a CI pipeline (none exists in this
  repository).
- macOS: when the repository lives in an iCloud-synced folder (`~/Desktop`, `~/Documents`), iCloud can offload
  `node_modules` files, making Node tooling extremely slow or time out. Keep the repository outside synced folders.

## Future enhancements
- Point-in-time constituent data and delisted securities from a licensed vendor; fundamentals.
- Nested/rolling cross-validation, multiple-testing adjustments, confidence intervals for strategy metrics
  (block bootstrap).
- Dividends and splits in the paper portfolio; FX conversion; holiday calendars.
- Shared job queue and object storage for horizontal scaling; OpenID Connect and MFA.
- CI pipeline running unit, integration (Testcontainers) and Playwright tests on every change.
