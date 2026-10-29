---
name: market-data-fx
description: How MatrixView ingests, normalizes and stores prices (Yahoo, Alpha Vantage, Mock providers; quotes vs. daily bars; adjusted vs. raw closes) and converts them between currencies with dated ECB rates (FxTable, CurrencyConverter, FxService, FxDownloader, missing-rate handling). Use when changing providers, sync jobs, price normalization, markets/currencies, FX conversion or anything that decides which prices an analysis may use; or when stocks are excluded for missing history or rates.
---

# Market data and currencies

Canonical docs: `docs/market-data.md`, `docs/currency.md` (policy), `docs/currency-audit.md`. Formulas: F-FX-001..003,
F-MD-001..003, F-RET-004 in `docs/quant/formula-reference.md`.

## Architecture and data flow
* Providers implement `IMarketDataProvider` (`src/PortfolioOptimizer.Application/Abstractions/IMarketDataProvider.cs`):
  `src/PortfolioOptimizer.Infrastructure/MarketData/YahooFinanceProvider.cs` (spark batches ≤ 20 symbols, retry/fallback
  on HTTP 400), `AlphaVantageProvider.cs` (free tier: 25 requests/day, no adjusted closes), `MockMarketDataProvider.cs`
  (deterministic, flagged simulated). HTTP goes through `ThrottlingHandler.cs`. Registration:
  `src/PortfolioOptimizer.Infrastructure/DependencyInjection.cs`.
* Sync: `MarketDataService.EnqueueSyncAsync` → job → `ExecuteSyncJobAsync` → plan (incremental ranges with overlap) →
  fetch → `PriceNormalizer.Normalize` → upsert by `(AssetId, Source, Date)` (unique index) → `DataSyncLog`. A changed
  adjusted close (new dividend/split) re-downloads the full history. Quotes: `QuoteRefreshService`.
* Reads: `PriceQueryService` (batched; `AssetIdsWithPricesAsync`, `LatestPricesPerAsset` – never a per-row
  `HistoricalPrices.Any(...)` or a query per asset, see `docs/performance.md`).
* FX: `src/PortfolioOptimizer.Infrastructure/Fx/FxProviders.cs` (ECB, MockFx) → `FxDownloader` (2-year chunks,
  background, contiguous per currency, D-35) → `FxRates` (units per EUR) → `FxService.GetTableAsync` → `FxTable` (pure)
  → `CurrencyConverter.Convert` / `FxService.GetSeriesInCurrencyAsync`. **No other code converts currencies.**
* Markets: `src/PortfolioOptimizer.Application/MarketData/Markets.cs` (`Markets.Venues`, sessions, `CurrencyUnits` –
  pence/agorot/cents to major units) mirrored in `frontend/portfolio-dashboard/src/lib/markets.ts`; universes in
  `src/PortfolioOptimizer.Infrastructure/Seed/universes/*.csv` (built by `tools/universe-builder`).

## Rules (must not break)
* Never fabricate, zero-fill or interpolate prices; label price kind, as-of and retrieval time, cached/stale/SIMULATED;
  never say "live". Store ISO major units. Never mix sources in one analysis (active provider only).
* Rate convention: target units per source unit; dated rate of each price date; ≤ 4 days carry-forward
  (`FxTable.DefaultMaxCarryForwardDays`); never 1.0 or today's rate for history; simulated prices use simulated rates.
* Missing rates → explicit exclusion with the cause (`FxMissing`: before first rate / in gaps / after last rate) or
  `null` totals; a symbol with > 5 % of points without a rate is excluded (`CurrencyConverter.MaxDroppedShare`).
* Provider currency must equal the listing currency, otherwise bars are rejected (wrong exchange suffix).
* Licensing: metadata, links and provider teasers only; provider names visible to administrators only (D-30).
* **Instrument counts are split, never one unexplained total** (owner's requirement, 2026-10-29). The catalogue holds
  stocks, ETFs and indexes, each tracked (`Asset.IsEnabled` – what a sync downloads) or not tracked (never downloaded).
  A total that mixes them cannot be compared with a download job: the Atlas tab once showed "2,158 Stocks & ETFs" (incl.
  270 not tracked) next to a "1,918 stocks" job (1,863 stocks + 25 ETFs + 30 indexes, all tracked). So every count shown
  to users says per type how many are tracked and how many are not (`describeCatalogue`), tab badges show the tracked
  count ("1,888 tracked"), and a job's size names its types (`describeSymbols`: "1,863 stocks, 25 ETFs, 30 indexes") –
  never call a mixed job "stocks". Use the helpers in `frontend/portfolio-dashboard/src/lib/instrument-counts.ts`
  (tested in `instrument-counts.test.ts`) instead of `rows.length`. The scheduled refresh
  (`ScheduledMarketDataRefresh`) and the seed sync take **every tracked instrument of every type**; the per-page syncs
  take only their types (`SyncRequest.AssetTypes`). Before reporting a count, check it against the database
  (`Assets` grouped by `AssetType`, `IsEnabled`).

## Failure modes and diagnosis
Yahoo 429/400 (throttling; retry later), Alpha Vantage quota, a listing whose prices stopped (delisted – offered again
by the missing-data check), FX download still running (web requests wait 25 s, jobs 5 min:
`FxFetchThrottle.ProviderWait`/`BackgroundProviderWait`), a failed FX chunk blocked for an hour (`RetryInterval`).
Look at the run's diagnostics/excluded reasons, `DataSyncLog`, `FxRates` coverage (Admin → Data), API logs.

## Changing things safely
* New market or index: follow "Add a market or index" in `AGENTS.md` (venue, session, `SymbolMapper` tests, frontend
  mirror, universe CSV validated against the provider, ECB currency).
* New provider: implement the interface incl. `Supports`, `QuoteBatchSize`, `RemainingDailyRequests`, currency
  normalization; unit-test parsing from recorded responses (`tests/PortfolioOptimizer.UnitTests/MarketData/ProviderTests.cs`);
  opt-in live smoke test.
* FX logic: keep `FxTable`/`CurrencyConverter` pure; add identities to `tests/PortfolioOptimizer.UnitTests/Fx/FxTests.cs`
  (inverse, cross, dated rate, dropped points) and an integration case to `MultiCurrencyTests`/`MissingFxTests`.

## Tests
```bash
dotnet test tests/PortfolioOptimizer.UnitTests --filter "FullyQualifiedName~Fx|FullyQualifiedName~MarketData"
./scripts/test.sh --filter "FullyQualifiedName~MultiCurrencyTests|FullyQualifiedName~MissingFxTests|FullyQualifiedName~FxFetchTests|FullyQualifiedName~MarketDataRefreshTests"
PO_LIVE_SMOKE=1 dotnet test tests/PortfolioOptimizer.UnitTests --filter LiveEcbSmokeTests   # opt-in, network
```

## Known limitations
ECB fixing ~16:00 CET vs. market closes (R-09), no intraday FX/spreads, no holiday calendars, BGN history only,
adjusted closes stored with 6 decimals (R-11), survivorship bias in today's constituent lists. See `docs/limitations.md`.

## Related
`/quant-formulas`, `/optimization-backtesting`, `/ml-pipeline`, `/portfolio-accounting`, `/platform-architecture`.
