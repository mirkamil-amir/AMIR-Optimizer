# Currency audit (2026-10-15, before the multi-currency refactor)

Baseline at `main` `df5a17d`: unit 212 passed / 7 skipped (live), ML 40, integration 89 – all green.

## How a portfolio's currency flowed before this change

| Stage | Where | Currency handling found |
|---|---|---|
| Catalogue | `Asset.Currency` (ISO), `Markets.Venues` (suffix → MIC, currency, time zone) | Correct: each listing has its venue currency; pence (GBp) normalized to GBP on import (`CurrencyUnits`). Identity is the provider symbol with exchange suffix (`SAP.DE` ≠ `SAP`). |
| Prices | `MarketDataService`, `QuoteRefreshService` | Correct and strict: provider currency must equal the listing currency, otherwise the bar/quote is rejected. Prices stored in native currency. |
| Selection | `lib/selection.tsx`, `stock-selection.tsx` | Warns when the selection mixes currencies. |
| Analyses | `UniverseCurrency.ResolveAsync` (optimizer, ML, strategy comparison) | **Rejects** any universe with more than one currency (no FX). Benchmarks in another currency are replaced. |
| Reality check | `RealityCheckService` | `currency = first asset's currency ?? "USD"` – a silent assumption. |
| Paper portfolio | `PortfolioService.CreateForOwnerAsync` | **Only the application default currency (USD) is allowed** – no EUR/GBP/JPY portfolio can be created. |
| Portfolio universe | `PortfolioService.ValidateUniverseAsync` | Rejects assets in another currency than the portfolio. |
| Optimization ↔ portfolio | `OptimizationService` | A run on EUR stocks for a USD portfolio is **allowed** with only a diagnostic note. |
| Trading | `TradingService.CreateFromRunAsync`, `RebalancePreviewAsync`, `TradeProposalCalculator` | **No currency check at all.** A proposal from such a run (or explicit target weights with foreign symbols) values EUR prices as if they were USD and spends USD cash on them – silent currency mixing. |
| Holdings / analytics | `PortfolioAnalyticsService`, frontend pages | Assume every price is in the portfolio currency (true only because of the restrictions above). |
| Research assistant | `Tools.cs` (`save_research_run`) | Picks the currency of the first asset in the universe. |
| FX | – | **No FX data, provider, table or service exists.** |

## Root causes

1. **Missing capability, not one bug:** there is no FX data source, no FX storage and no conversion service, so every
   layer had to forbid mixing currencies – the user cannot combine Apple (USD), SAP (EUR), Toyota (JPY) and AstraZeneca
   (GBP) in one analysis or portfolio, and cannot even create a non-USD portfolio.
2. **Inconsistent enforcement:** the restriction is enforced in analyses and portfolio settings but not in the path
   run → trade proposal → paper execution, where foreign prices are silently treated as base-currency amounts.
3. **Silent defaults:** `?? "USD"` and "currency of the first asset" in the reality check and the research save tool.
4. **Concepts not separated:** the portfolio's `BaseCurrency` is simultaneously the cash (settlement) currency and the
   reporting currency; there is no reporting conversion distinct from a (simulated) currency exchange.

## Plan (executed in this change; see implementation-status.md for what was verified)

1. FX data: `FxRate` observations (ECB euro reference rates, daily since 1999, official, no key; simulated provider for
   tests and the e2e stack), synchronised with the price sync, stored with source, retrieval time and simulation flag.
2. `FxService`: one convention (rate = target units per 1 source unit), inverse and cross rates via EUR, dated lookup
   with a documented carry-forward window for weekends/holidays, typed `FxRateUnavailableException`, never 1.0.
3. Analyses: price series converted to the analysis base currency with the FX rate of each price date; benchmark
   converted the same way; currency exposure reported; provenance in diagnostics.
4. Paper portfolio: any supported base currency; holdings keep their native currency; buying a foreign stock is a
   *simulated* currency exchange at the dated rate, recorded on the execution; valuation and reports in the base
   currency or any selected reporting currency without changing holdings.
5. Frontend: currency choice, native vs base values, FX rate and date, stale/unavailable states.
6. Tests for every item above with deterministic fixtures.
