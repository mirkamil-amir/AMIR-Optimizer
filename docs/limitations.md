# Known limitations & future enhancements

## Scope and security
* Local, single-user application. **No authentication/authorization**; do not expose it publicly.
* Paper trading only. No broker connectivity; no real orders are ever placed. Not investment advice.

## Data
* Yahoo Finance is an unofficial endpoint (personal, non-commercial use; may break or throttle without notice).
* Stooq was evaluated but is blocked by a JavaScript bot challenge and is not supported.
* Alpha Vantage was verified with the `demo` key only; free tier lacks adjusted closes (dividends ignored) and has a
  low daily request cap, so a full 11-symbol sync with two requests per symbol exceeds the free daily quota.
* Single currency (USD). Assets in other currencies are rejected rather than converted.
* No exchange holiday calendar: staleness counts business days (Mon–Fri).
* The latest bar of a sync during market hours is intraday until the next sync.
* Corporate actions are only reflected through the provider's adjusted close; raw-close positions in the paper
  portfolio are not adjusted for splits after purchase (a split would distort valuation until handled manually).
* Dividends are not credited to the paper portfolio's cash.

## Quant
* Expected returns are historical means – noisy and backward-looking. No Black–Litterman, factor models or
  robust optimization yet.
* Max-holdings / minimum-position constraints use a greedy heuristic, not mixed-integer optimization.
* Short selling and leverage are not supported.
* Transaction costs are proportional with a minimum fee; no market-impact or bid/ask model. Costs are applied in
  proposals and backtests but are not a penalty term in the optimizer objective (turnover can be capped instead).
* Backtests use the current universe (survivorship bias), fractional units, fills at the next close, and cash at 0%.
* Dense solver: suitable for tens to a few hundred assets.

## Engineering
* Background jobs run in-process (one worker, sequential). A crash marks running jobs failed; queued jobs resume.
* SQLite is the only configured provider. The code avoids SQLite-specific SQL, but PostgreSQL needs its own
  provider registration (Npgsql) and a separate migrations assembly; this is not implemented yet.
* Docker Compose files are provided but could not be tested on the development machine (Docker daemon not running).
* No frontend unit tests yet (backend has unit + integration tests; the UI was verified manually in Chrome).

## Future enhancements
* Additional providers (official vendor APIs with commercial licences), FX conversion, holiday calendars.
* Dividend and split processing for the paper portfolio.
* Black–Litterman / shrinkage of expected returns, risk parity, CVaR optimization, MIQP for cardinality.
* Scheduled automatic rebalancing proposals and alerts on drift.
* Authentication (e.g. OpenID Connect) and per-user portfolios; PostgreSQL deployment profile.
* Playwright end-to-end tests and component tests for the dashboard.
