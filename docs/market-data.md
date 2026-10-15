# Market data

How stock universes, quotes and daily history get into the app, from which providers, with which limits. Verified
against the live providers on 2026-10-14 (smoke tests: `PO_LIVE_SMOKE=1 dotnet test --filter LiveProviderSmokeTests`).

## Supported markets and universes

| Market | Universe(s) | Members | Venue (MIC) | Currency | Canonical symbol | Benchmark |
|---|---|---|---|---|---|---|
| United States | S&P 500 | 503 | NYSE / NASDAQ / Cboe | USD | `AAPL`, share classes `BRK-B` | SPY (ETF) |
| Germany | DAX 40 (current, since 2021-09-20) | 40 | XETRA (XETR) | EUR | `SAP.DE` | ^GDAXI |
| Germany | DAX 30 (historical, as of 2021-09-06) | 30 | XETRA (XETR) | EUR | `SAP.DE` | ^GDAXI |
| France | CAC 40 | 40 (39 Paris + ArcelorMittal on Euronext Amsterdam `MT.AS`) | XPAR / XAMS | EUR | `MC.PA` | ^FCHI |
| United Kingdom | FTSE 100 | 100 | LSE (XLON) | GBP | `TSCO.L`, `BT-A.L` | ^FTSE |
| Japan | Nikkei 225 | 224 (the source lists NTT twice) | TSE (XTKS) | JPY | `7203.T` | ^N225 |

The DAX 30 list is a fixed historical composition (the 30 members before the expansion to 40), shown as
"DAX 30 (historical)" and never mixed up with the current DAX 40. Renamed tickers point to today's listing
(Daimler → `MBG.DE`, Deutsche Post → `DHL.DE`); members that no longer trade (Covestro `1COV.DE`, Hino `7205.T`) stay
in the list and show "No price available".

### Constituent sources and licence

All constituent lists come from pinned Wikipedia revisions (CC BY-SA 4.0), recorded per index in `indices.csv`
(source, URL with `oldid`, revision, licence, verification date) and per stock (`MetadataSource`):

| Index | Page | Revision |
|---|---|---|
| S&P 500 | en: "List of S&P 500 companies" | 1379247819 |
| DAX 40 | en: "DAX" | 1373844817 |
| DAX 30 (historical) | en: "DAX" | 1042710912 (2021-09-06) |
| CAC 40 | en: "CAC 40" | 1379195393 |
| FTSE 100 | en: "FTSE 100 Index" | 1377721311 |
| Nikkei 225 | de: "Nikkei 225" (the English article has no constituent table) | 271092994 |

No source provides ISINs, so the ISIN column is empty (never derived). Index providers' official lists are licensed
data; Wikipedia was chosen because its licence allows redistribution with attribution.

### Sectors

`sector` is an approximate GICS-style sector (11 groups) so that one sector filter works across markets; `industry`
keeps each source's own classification (GICS sub-industry for S&P 500 and CAC 40, Prime Standard sector for DAX, ICB
sector for FTSE, Nikkei industry translated from German). The mappings live in `tools/universe-builder/build_universes.py`
and are approximate where schemes differ (e.g. ICB "Household goods" spans two GICS sectors; company-level overrides
are listed in the script).

### Updating constituents (no code change)

Three ways, from most to least routine:

1. **Admin UI** – Admin settings → Instrument catalogue → *Update index constituents*: upload a CSV
   (`symbol,name,country,exchange,mic,currency,sector,industry,isin`, canonical symbols) with its source. The index's
   membership is replaced; former members stay in the catalogue with their prices. Audited. API:
   `POST /api/v1/admin/catalogue/universes/{code}`.
2. **Override directory** – set `Universes__Directory` to a folder with `indices.csv` and `<CODE>.csv` files (same
   format as the bundled ones); they take precedence over the bundled files on the next start.
3. **Rebuild the bundled files** – `python3 tools/universe-builder/build_universes.py --latest` fetches the current
   Wikipedia revisions, maps sectors and validates symbols; review the diff, commit, redeploy. The API re-imports an
   index whenever its file content changes (SHA-256 stored in `MarketIndex.ContentHash`).

After any update, run a quote refresh (Admin overview → *Refresh prices now*) and check the coverage table.

## Providers

| Provider | Role | Markets | Key | Limits (verified 2026-10-14) | Price kind |
|---|---|---|---|---|---|
| **Yahoo Finance** (unofficial chart + spark endpoints) | default for quotes and history | US, DE, FR, NL, GB, JP, index levels | none | unpublished; client throttles 1 request / 500 ms; spark accepts ≤ 20 symbols | delayed quotes during trading (typically 15+ min), session closes otherwise |
| **Alpha Vantage** | optional alternative (Admin settings → provider) | US, Xetra `.DEX`, Paris `.PAR`, Amsterdam `.AMS`, London `.LON`; **no Tokyo, no index levels** | `ALPHAVANTAGE_API_KEY` (server only) | free key: **25 requests/day**, ~1 request/s; adjusted daily series is premium | end of day (`GLOBAL_QUOTE` gives the last trading day only) |
| **Mock** | offline demos and all automated tests | every symbol | none | none | **SIMULATED**, flagged everywhere |

Stooq was evaluated earlier and is blocked by a bot challenge; it is not supported (and must not be bypassed).

**Alpha Vantage key.** Configured in `.env` (`ALPHAVANTAGE_API_KEY`) and passed to the API container as
`MarketData__AlphaVantage__ApiKey`; locally use user-secrets. It never reaches the browser: the admin overview only
shows "Key configured" and the remaining daily requests; provider error messages redact the key. A free key cannot
keep 900+ quotes current (25 requests/day), so Yahoo stays the default and Alpha Vantage is limited by the client to
its daily quota (`MarketData__AlphaVantage__DailyRequestLimit`, default 25); sign-in cycles then refresh only tracked
instruments within half of the remaining budget.

## Symbol mapping

Canonical symbols follow Yahoo conventions (table above); `SymbolMapper` (`Application/MarketData/Markets.cs`)
translates per provider:

| Canonical | Yahoo | Alpha Vantage |
|---|---|---|
| `AAPL`, `BRK-B` | unchanged (`BRK.B` → `BRK-B`) | unchanged |
| `SAP.DE` | unchanged | `SAP.DEX` |
| `MC.PA` / `MT.AS` | unchanged | `MC.PAR` / `MT.AMS` |
| `TSCO.L`, `BT-A.L` | unchanged | `TSCO.LON`, `BT-A.LON` |
| `7203.T` | unchanged | not covered |
| `^GDAXI` etc. | unchanged | not covered |

**Currency units.** London prices are quoted in pence (Yahoo `GBp`, Alpha Vantage pence); `CurrencyUnits` converts
them to pounds before storage, so every stored price is in its ISO currency. The quote refresh also rejects a price
that differs from the last known one by more than a factor of 20 (unit errors) and any quote whose currency differs
from the listing currency.

## Quotes: automatic loading, cache, refresh

```
sign-in (approved user) ──► QuoteRefreshService.RequestCycleAsync ──► background job (one at a time, de-duplicated)
                                     │                                   ├─ quotes for all catalogue instruments, stale first,
                                     │                                   │  Yahoo spark batches of 20 (≈ 45 requests, ~30 s)
                                     │                                   └─ daily history of tracked instruments behind the last
                                     │                                      completed weekday? → incremental download (no full re-download)
stock page ──► cached quotes immediately ──► POST /stocks/quotes/refresh for visible rows that are missing or cached
row button ──► POST /stocks/{symbol}/quote/refresh (force, 30 s cooldown)
admin ──► POST /admin/catalogue/quote-refresh;  scheduler (every 12 h) ──► same cycle
```

* **De-duplication:** a sign-in starts a cycle only if none is running and the last one was requested more than
  `MarketData:Quotes:CycleMinIntervalMinutes` (15) ago; otherwise it reuses that cycle.
* **Cache window:** quotes retrieved within `FreshForMinutes` (15) are reused; the UI marks older ones "cached".
* **Rate limits:** page and single refreshes share a per-user limit (60/min); ≤ 100 symbols per page refresh; the
  provider throttles itself. A rejected Yahoo batch is retried once, then fetched symbol by symbol.
* **States shown to users:** *Delayed quote* (last trade within 30 minutes), *Last close* (session finished or stored
  daily close), *cached*, *stale* (older than `StalePriceDays` business days), *SIMULATED*, *No price available*.
  Each price shows when it applies (exchange time) and when it was retrieved. Nothing is called live or real-time.
* Quotes are stored per provider (`LatestQuotes`); analyses use only the active provider's data (no mixing).

## Daily history

Unchanged from earlier versions: incremental downloads with a 7-day overlap, normalization (`PriceNormalizer`),
adjusted closes when the provider supplies them (Yahoo yes, Alpha Vantage free tier no), full re-download when
adjustments change. Users fetch history for up to 50 selected stocks at once; ML experiments and comparisons queue
missing history (e.g. an index benchmark) automatically before they run.

## Analyses and currencies

Returns are computed in each stock's own currency without FX conversion, so an optimization, ML experiment or
comparison must use stocks of one currency (`UniverseCurrency`, mixed selections are rejected with an explanation). A
benchmark in another currency is replaced by the matching index (SPY → ^GDAXI for a DAX selection) with a note. Paper
trades need a portfolio in the stocks' currency.

## Known limitations

* Yahoo is unofficial (personal, non-commercial use) and may change or throttle without notice.
* No real-time data, no FX conversion, no corporate-action processing in paper holdings, no exchange holiday
  calendar (staleness counts weekdays).
* Constituent lists are snapshots: no point-in-time membership → survivorship bias in every backtest.
* Alpha Vantage free tier: 25 requests/day, no Tokyo, no index levels, no adjusted closes.

## Price history ranges and news

Chart ranges and period changes are described in [watchlist.md](watchlist.md); news providers, their terms and the event comparison in [financial-news.md](financial-news.md). Neither adds a new price source: both read the stored prices of the active provider.
