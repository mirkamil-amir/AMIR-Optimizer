# Roadmap

Prioritized follow-up work after the 2026-10-15 pass. Order reflects dependencies and risk, not effort.

## 1. Licensed, point-in-time data (blocks any commercial use and any credible backtest claim)
* Licensed end-of-day prices with redistribution rights; point-in-time index constituents incl. delisted securities
  (removes survivorship bias from backtests, ML evaluation and the reality check).
* Stable identifiers (ISIN/FIGI) per listing from the licensed source; corporate actions feed.
* Depends on: vendor selection and budget; legal review (launch-readiness.md).

## 2. Licensed news and disclosure feeds
* Replace the unofficial Yahoo search with a licensed news API that permits display of headlines, summaries and images;
  add exchange/issuer disclosure feeds (ad-hoc announcements, regulatory news).
* Then evaluate the keyword classifier against a hand-labelled sample (precision/recall per category) and only then
  consider an ML classifier compared with this baseline.

## 3. Continuous integration
* Run `scripts/test.sh` (Testcontainers), Vitest, lint, build and `scripts/e2e.sh` on every change; publish reports.
  The e2e script currently needs a working local Docker/Podman credential setup (see testing.md).

## 4. Robustness of the quantitative core
* Bootstrap / resampled-efficiency sensitivity in the reality check; confidence intervals for strategy metrics (block
  bootstrap); multiple-testing awareness across a user's experiments.
* Holiday calendars per exchange (event comparison, period changes, staleness); FX conversion for multi-currency
  analyses; spread/market-impact cost models.

## 5. Product
* Notifications for watched stocks (needs e-mail/push infrastructure, preferences and a delivery log).
* Guided first-run flow (select → optimize → reality check → backtest) and saved views of the news feed.
* Accessibility audit with assistive technology (only automated/role-based checks so far).

## Unresolved dependencies
* Legal opinion on investment-advice regulation (BaFin/ESMA/FCA) for any personalized output.
* Data-licence terms for prices, constituents (CC BY-SA attribution is in place) and news.
* Hosting, backup rehearsal and monitoring for a pilot.
