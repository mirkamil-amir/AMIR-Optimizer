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
| Switzerland | SMI | 20 | SIX Swiss Exchange (XSWX) | CHF | `NOVN.SW` | ^SSMI |
| Spain | IBEX 35 | 35 | Bolsa de Madrid (XMAD) | EUR | `ITX.MC` | ^IBEX |
| Italy | FTSE MIB | 40 | Borsa Italiana (XMIL) | EUR | `RACE.MI` | FTSEMIB.MI |
| Sweden | OMX Stockholm 30 | 30 | Nasdaq Stockholm (XSTO) | SEK | `ATCO-A.ST` | ^OMX |
| Denmark | OMX Copenhagen 25 | 25 | Nasdaq Copenhagen (XCSE) | DKK | `MAERSK-A.CO`, `NDA-DK.CO` | ^OMXC25 |
| Canada | S&P/TSX 60 | 60 | Toronto Stock Exchange (XTSE) | CAD | `RY.TO`, `RCI-B.TO`, `BIP-UN.TO` | ^GSPTSE (Composite) |
| Australia | S&P/ASX 50 | 48 (the source lists 49 rows incl. the fund VAS, removed) | ASX (XASX) | AUD | `BHP.AX` | ^AXJO (ASX 200) |
| Hong Kong | Hang Seng Index | 85 | HKEX (XHKG) | HKD | `0005.HK` (four digits) | ^HSI |
| India | NIFTY 50 | 50 | NSE (XNSE) | INR | `RELIANCE.NS`, `M&M.NS` | ^NSEI |
| Singapore | Straits Times Index | 30 (three trade in USD: `D01.SI`, `H78.SI`, `J36.SI`) | SGX (XSES) | SGD | `D05.SI` | ^STI |
| Norway | OBX | 24 | Oslo Børs (XOSL) | NOK | `EQNR.OL` | OSEBX.OL (Oslo Børs Benchmark) |
| Finland | OMX Helsinki 25 | 25 | Nasdaq Helsinki (XHEL) | EUR | `NOKIA.HE`, `NDA-FI.HE` | ^OMXH25 |
| Belgium | BEL 20 | 20 (19 Brussels + Aperam on Euronext Amsterdam `APAM.AS`) | Euronext Brussels (XBRU) | EUR | `ABI.BR` | ^BFX |
| Portugal | PSI | 17 | Euronext Lisbon (XLIS) | EUR | `GALP.LS` | PSI20.LS |
| Ireland | ISEQ 20 | 20 (Hammerson's Dublin line trades in GBP: `HMSO.IR`) | Euronext Dublin (XDUB) | EUR | `RYA.IR` | ^ISEQ (ISEQ All Share) |
| Austria | ATX | 20 | Wiener Börse (XWBO) | EUR | `EBS.VI` | ^ATX |
| Poland | WIG20 | 20 | Warsaw Stock Exchange (XWAR) | PLN | `PKO.WA` | ETFBW20TR.WA (ETF – no index history at the provider) |
| Hungary | BUX | 17 (Graphisoft Park trades in EUR: `GSPARK.BD`) | Budapest Stock Exchange (XBUD) | HUF | `OTP.BD` | none (no index history at the provider) |
| Greece | FTSE/Athex Large Cap | 24 | Athens Stock Exchange (ASEX) | EUR | `ALPHA.AT` | GD.AT (Athex Composite) |
| Iceland | OMX Iceland 15 | 15 | Nasdaq Iceland (XICE) | ISK | `ARION.IC` | none (no index history at the provider) |
| Israel | TA-35 | 35 | Tel Aviv Stock Exchange (XTAE) | ILS (provider quotes agorot, ÷100) | `TEVA.TA` | TA35.TA |
| South Korea | KOSPI 200 | 200 | Korea Exchange (XKRX) | KRW | `005930.KS` (six digits) | ^KS11 (KOSPI Composite) |
| China | SSE 50 | 50 | Shanghai Stock Exchange (XSHG) | CNY | `600519.SS` | 000001.SS (SSE Composite) |
| Malaysia | FTSE Bursa Malaysia KLCI | 30 | Bursa Malaysia (XKLS) | MYR | `1155.KL` (four digits) | ^KLSE |
| Thailand | SET50 | 51 | Stock Exchange of Thailand (XBKK) | THB | `PTT.BK` | TDEX.BK (ETF – no index history at the provider) |
| Brazil | Ibovespa | 57 (share classes separately: `PETR3.SA`, `PETR4.SA`) | B3 (BVMF) | BRL | `VALE3.SA`, `BPAC11.SA` | ^BVSP |
| Indonesia | LQ45 | 44 | Indonesia Stock Exchange (XIDX) | IDR | `BBCA.JK` | ^JKLQ45 |
| Mexico | S&P/BMV IPC | 33 | Bolsa Mexicana de Valores (XMEX) | MXN | `WALMEX.MX`, `GFNORTEO.MX`, `PE&OLES.MX` | ^MXX |
| South Africa | FTSE/JSE Top 40 | 39 | Johannesburg Stock Exchange (XJSE) | ZAR (provider quotes cents, ÷100) | `NPN.JO` | ^J200.JO |
| New Zealand | S&P/NZX 50 | 50 | New Zealand Exchange (XNZE) | NZD | `FPH.NZ` | ^NZ50 (gross) |

**Twenty markets added 2026-10-25.** Every member and benchmark symbol was checked against the price provider that day
(the instrument exists, trades in the recorded currency and has at least a year of daily prices); a sample download of a
year of prices per market and of every benchmark succeeded, with agorot and cents converted to ILS and ZAR. Wikipedia
tables lag corporate events, so the builder records what changed since the pinned revision:
renamed or re-listed members point to today's symbol (`TICKER_CHANGES`: Ryanair `RYA.IR`, Immofinanz → CPI Europe
`CPI.VI`, Santander Bank Polska → Erste Bank Polska `EBP.WA`, CCC → Modivo `MDV.WA`, Mytilineos → Metlen `MTLN.AT`,
OPAP → Allwyn `ALWN.AT`, Kojamo → Lumo Kodit `LUMO.HE`, Alfa → Sigma Foods `SIGMAFA.MX`, CCR → Motiva `MOTV3.SA`,
Eletrobras → Axia `AXIA3.SA`, Embraer `EMBJ3.SA`, Natura `NATU3.SA`, Marfrig/BRF → MBRF `MBRF3.SA`) and members no longer
listed are left out with the reason printed by the build (Golden Ocean, Sonae Capital, Terna Energy, Grupo Elektra,
MultiChoice, BRF, JBS, Eletrobras PNB, Azul PN, Gol, Cielo, Carrefour Brasil). The ATX, WIG20 and FTSE/JSE Top 40 tables
name no tickers, and the ISEQ 20, SSE 50 and LQ45 tables no sectors: those are assigned in the script (company →
ticker/sector, approximate sectors), each ticker checked with the provider. The **Philippines** (PSEi) was evaluated
and left out: the provider has no data for the Philippine Stock Exchange. Turkey, the Czech Republic and Romania have
no usable Wikipedia constituent table; Taiwan, Saudi Arabia and others have no ECB reference rate.

**Index ETFs.** Twenty markets also have a small universe "<Country> index ETFs" (`ETFCH` … `ETFSG`, checked
2026-10-24; `ETFKR`, `ETFCN`, `ETFBR`, `ETFMX`, `ETFZA`, `ETFNZ`, `ETFPL`, `ETFTH`, `ETFMY`, `ETFID`, checked 2026-10-25;
1–2 funds each, `AssetType.Etf`): exchange-traded funds on the local index, e.g. `CSSMI.SW`, `XIU.TO`, `STW.AX`,
`2800.HK`, `NIFTYBEES.NS`, `ES3.SI`, `069500.KS`, `BOVA11.SA`, `NAFTRACISHRS.MX`, `STX40.JO`. They are a curated list in
`build_universes.py` (not Wikipedia, not index constituents); symbol, name and currency were checked against the price
provider (which labels some of them as equities). The other new markets have no local index ETF with prices at the provider. `SPIC25KL.CO` is a Danish
listed index fund (investeringsforening) rather than a UCITS ETF. Constituent files may carry an optional `type` column
(`Equity` default, `Etf`).

All currencies of the newer markets (CHF, SEK, DKK, CAD, AUD, HKD, INR, SGD, NOK, PLN, HUF, ISK, ILS, KRW, CNY, MYR, THB,
BRL, IDR, MXN, ZAR, NZD) have ECB reference rates, so prices,
analyses, portfolio values and backtests convert exactly like the earlier markets (`FxService`, dated rates).

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
| SMI | en: "Swiss Market Index" | 1380694390 |
| IBEX 35 | en: "IBEX 35" | 1369000118 |
| FTSE MIB | en: "FTSE MIB" | 1381720123 |
| OMX Stockholm 30 | en: "OMX Stockholm 30" | 1356511453 |
| OMX Copenhagen 25 | en: "OMX Copenhagen 25" | 1381674834 |
| S&P/TSX 60 | en: "S&P/TSX 60" | 1363917062 |
| S&P/ASX 50 | en: "S&P/ASX 50" | 1368805713 |
| Hang Seng Index | en: "Hang Seng Index" | 1377881667 |
| NIFTY 50 | en: "NIFTY 50" | 1379683379 |
| Straits Times Index | en: "Straits Times Index" | 1373209403 |
| OBX | en: "OBX Index" | 1356511451 |
| OMX Helsinki 25 | en: "OMX Helsinki 25" | 1356511455 |
| BEL 20 | en: "BEL 20" | 1380911122 |
| PSI | en: "PSI-20" | 1381076092 |
| ISEQ 20 | en: "ISEQ 20" | 1362357822 |
| ATX | en: "Austrian Traded Index" | 1356511363 |
| WIG20 | de: "WIG20" (the English article has no constituent table) | 270643862 |
| BUX | en: "BUX" | 1356511092 |
| FTSE/Athex Large Cap | en: "FTSE/Athex Large Cap" | 1366728259 |
| OMX Iceland 15 | en: "OMX Iceland 15" | 1337461510 |
| TA-35 | en: "TA-35 Index" | 1362844669 |
| KOSPI 200 | en: "KOSPI 200" | 1379902632 |
| SSE 50 | en: "SSE 50 Index" | 1356509131 |
| FTSE Bursa Malaysia KLCI | en: "FTSE Bursa Malaysia KLCI" | 1356927985 |
| SET50 | en: "SET50 Index and SET100 Index" | 1358079040 |
| Ibovespa | pt: "Lista de companhias citadas no Ibovespa" | 70162555 |
| LQ45 | en: "LQ45" | 1370624740 |
| S&P/BMV IPC | en: "Indice de Precios y Cotizaciones" | 1364117964 |
| FTSE/JSE Top 40 | en: "FTSE/JSE Top 40 Index" | 1343081092 |
| S&P/NZX 50 | en: "S&P/NZX 50" | 1381776750 |

Only the FTSE MIB table lists ISINs (kept); for all other indices the ISIN column is empty (never derived). Index providers' official lists are licensed
data; Wikipedia was chosen because its licence allows redistribution with attribution.

### Sectors

`sector` is an approximate GICS-style sector (11 groups) so that one sector filter works across markets; `industry`
keeps each source's own classification (GICS sub-industry for S&P 500 and CAC 40, Prime Standard sector for DAX, ICB
sector for FTSE, Nikkei industry translated from German). The mappings live in `tools/universe-builder/build_universes.py`
and are approximate where schemes differ (e.g. ICB "Household goods" spans two GICS sectors; company-level overrides
are listed in the script). Two sources have no usable sector: the Straits Times Index table lists none, and the Hang
Seng sub-index "Commerce & Industry" spans every non-financial sector – for those members the sector is assigned in the
script from each company's main business (approximate; the Hang Seng sub-index is kept as `industry`).

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
| **Yahoo Finance** (unofficial chart + spark endpoints) | default for quotes and history | all 36 venues, index levels (not the Philippines; no BUX, OMX Iceland 15, WIG20 or SET history) | none | unpublished; client starts at most one request per 125 ms with ≤ 8 in flight (≤ 8/s, `MarketData:Yahoo`); spark accepts ≤ 20 symbols | delayed quotes during trading (typically 15+ min), session closes otherwise |
| **Alpha Vantage** | optional alternative (Admin settings → provider) | US, Xetra `.DEX`, Paris `.PAR`, Amsterdam `.AMS`, London `.LON`; **no Tokyo, no index levels** | `ALPHAVANTAGE_API_KEY` (server only) | free key: **25 requests/day**, ~1 request/s; adjusted daily series is premium | end of day (`GLOBAL_QUOTE` gives the last trading day only) |
| **Mock** | offline demos and all automated tests | every symbol | none | none | **SIMULATED**, flagged everywhere |

Stooq was evaluated earlier and is blocked by a bot challenge; it is not supported (and must not be bypassed).

**Who sees provider names.** Only administrators: the admin overview and settings, the provider cards, active source
and job sources on the Market data page, the sidebar data status and the news-source table. Regular users see neutral
labels ("Stored prices", "Historical estimate", "reference rates", "Market data") and every backend message they can
receive (quote descriptions, refresh results, sync logs, optimizer/ML diagnostics, FX notes and errors) is worded
without a provider name. Simulated data is still labelled **SIMULATED** everywhere. The JSON fields (`priceSource`,
`fxSource`, `source`) are kept for reproducibility of stored runs; they are just not rendered for users. Article
publishers and Wikipedia attribution stay visible (licence requirement).

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
| `RCI-B.TO` | unchanged | `RCI-B.TRT` |
| `NOVN.SW`, `ITX.MC`, `RACE.MI`, `ATCO-A.ST`, `MAERSK-A.CO`, `BHP.AX`, `0005.HK`, `M&M.NS`, `D05.SI` | unchanged | not covered |
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
* **History downloads (sync jobs):** the stored date range of every symbol of a job is read in one query, the provider
  downloads run ahead in parallel (`IMarketDataProvider.HistoryParallelism`: Yahoo 8, Alpha Vantage 1) while the
  database work stays sequential and in job order, and the latest quotes are fetched afterwards in batches (20 per spark
  request) instead of one request per symbol. A period with no trading day (before a recent listing: Yahoo answers HTTP
  400 "Data doesn't exist") is stored as "no prices in this period", not as a failure. Measured on the development stack
  (2026-10-25, 100 stocks without stored prices, five years each): 1.4 s per symbol before, 0.36 s with 4 requests/s,
  0.18 s with 8/s once stored prices are released from the DbContext after each symbol (the time per symbol had grown
  from 0.14 to 0.31 s during a job). History cannot be downloaded 20 symbols per request: Yahoo's multi-symbol endpoint
  (spark) returns plain closes only – no adjusted closes (dividends), no open/high/low/volume and no currency.
* **Rate limits:** page and single refreshes share a per-user limit (60/min); ≤ 100 symbols per page refresh; the
  provider throttles itself. A rejected Yahoo batch is retried once, then fetched symbol by symbol.
* **States shown to users:** *Delayed quote* (last trade within 30 minutes), *Last close* (session finished or stored
  daily close), *cached*, *stale* (older than `StalePriceDays` business days), *SIMULATED*, *No price available*.
  Each price shows when it applies (exchange time) and when it was retrieved. Nothing is called live or real-time.
* Quotes are stored per provider (`LatestQuotes`); analyses use only the active provider's data (no mixing).

## Market Data pages

`/market-data` (Stocks & ETFs), `/market-data/indexes` and `/market-data/currencies` list the **whole catalogue**, not
only tracked instruments: an instrument nobody has downloaded yet is "Not tracked" with a Download button (a download makes
it tracked). Each page has the same list – search, rows per page, first/previous/next/last (`RangePager`, shared with the
stock chooser) inside a box that scrolls under a fixed header – and its own sync for administrators: tracked stocks & ETFs
or tracked indexes (`SyncRequest.AssetTypes`), or every exchange rate of the FX source paired with the active price source
(`POST /api/v1/fx/sync`, gaps only). Latest prices show each instrument's own trading currency, never converted.
Counts are split by type and tracking (`lib/instrument-counts.ts`): a tab badge shows the tracked instruments ("1,888
tracked"), the line above the list each type's tracked and not tracked instruments, the catalogue total and how many have
prices ("Stocks: 1,863 tracked · 262 not tracked · ETFs: 25 tracked · 8 not tracked · 2,158 in the catalogue, 1,888 with
prices"). Downloads show the MatrixView loader with progress and the job's types ("… of 1,918 tracked instruments (1,863
stocks, 25 ETFs, 30 indexes)" – the scheduled refresh takes every tracked instrument, indexes included, so its total is
not the Stocks & ETFs count), the last symbol and how many downloads wait (jobs run one at a time,
so a manual download can wait behind the scheduled refresh); exchange-rate downloads use the same panel
(`MarketDataStatusDto.FxDownloadRunning`). The stock chooser's market counts are distinct instruments
(`MarketDto.Instruments`), so "All markets" equals "All sectors"; the navigation's stale badge counts tracked
instruments only.

## Daily history

Unchanged from earlier versions: incremental downloads with a 7-day overlap, normalization (`PriceNormalizer`),
adjusted closes when the provider supplies them (Yahoo yes, Alpha Vantage free tier no), full re-download when
adjustments change (more than 0.05 % on overlapping dates – a new dividend or split back-adjusts all earlier adjusted
closes; the refresh keeps the series consistent). This is routine: the Market data table shows such a sync as "OK" with
the note "History refreshed … after a dividend or split adjustment" in its tooltip. Users fetch history for up to 50 selected stocks at once; ML experiments and comparisons queue
missing history (e.g. an index benchmark) automatically before they run.

**Asking before an analysis (missing-data dialog).** The ML experiment, backtest and optimization forms first call
`POST /market-data/check` with the stocks, benchmark, period (ML: the history years; backtest/optimization: the start minus
the lookback, as the backtest loads it) and analysis currency (`DataReadinessService`). It lists, per stock, prices that start
later than needed (more than 7 days) or end more than the stale threshold before the end, and per currency the exchange-rate
ranges not stored (`FxService.MissingRatesAsync`). When something can be downloaded the dialog asks: **Download and continue**
(`POST /market-data/download-missing` queues price jobs of ≤ 50 stocks – the incremental sync adds older history before the
stored range – and starts the background FX download; the dialog polls the check and starts the analysis once complete),
**Continue without** (the analysis excludes or shortens as it explains), or **Cancel**. Older prices already requested from
the source without result are marked as unavailable (younger listing) and not offered again. Any approved user may use both
endpoints (the same 50-symbol rule as manual syncs; data-provider names are not exposed).

## Analyses and currencies

Stocks of different currencies can be combined: prices are converted into the analysis currency with the dated ECB rate
(`FxService`, rules in [currency.md](currency.md)). In a single-market analysis in that market's own currency a foreign
benchmark is replaced by the market's index (SPY → ^GDAXI for DAX stocks in EUR) with a note.

## Known limitations

* Yahoo is unofficial (personal, non-commercial use) and may change or throttle without notice.
* No real-time data, no FX conversion, no corporate-action processing in paper holdings, no exchange holiday
  calendar (staleness counts weekdays).
* Constituent lists are snapshots: no point-in-time membership → survivorship bias in every backtest.
* Alpha Vantage free tier: 25 requests/day, no Tokyo, no index levels, no adjusted closes.

## Price history ranges and news

Chart ranges and period changes are described in [watchlist.md](watchlist.md); news providers, their terms and the event comparison in [financial-news.md](financial-news.md). Neither adds a new price source: both read the stored prices of the active provider.

## Company descriptions

Shown in the stock-detail panel and on `/stocks/{symbol}` (`CompanyOverview`), served by
`GET /api/v1/stocks/{symbol}/profile` (`CompanyProfileService`).

* **Source:** the lead section of the company's Wikipedia article via the Wikimedia REST API
  (`/api/rest_v1/page/summary/{title}`), text licensed **CC BY-SA 4.0** and always shown with source link, licence and
  retrieval date. The article is the one linked in the pinned constituent table the catalogue was built from
  (`wiki` column, stored as `Asset.ReferenceArticle`, e.g. `en:Apple Inc.`), so no name search and no ticker guessing is
  involved. References into another language edition (Nikkei 225 comes from German Wikipedia) are resolved to the English
  article through the interlanguage link when one exists. Coverage of the seed files: S&P 500 502/503, DAX 40/30, CAC 40
  and FTSE 100 all members, Nikkei 225 196/224.
* **Not usable (verified 2026-10-15):** Alpha Vantage `OVERVIEW` answers "premium endpoint" for the configured key;
  Yahoo `quoteSummary/assetProfile` requires a session crumb (an anti-scraping mechanism) and returned HTTP 429 – using it
  would mean working around bot protection, which this project does not do.
* **Cache:** table `CompanyProfiles` (one row per listing, separate from quotes). A description is reused for 30 days, a
  "not found" for 7 days, an error is retried after 15 minutes; one fetch per listing at a time; a failed refresh keeps
  the earlier text and marks it stale. A changed catalogue reference or a different source (e.g. simulated → Wikipedia)
  invalidates the entry.
* **Statuses:** `Available`, `NotFound` (missing page, disambiguation page, extract shorter than 40 characters),
  `NoReference` (no article in the catalogue, or an index/ETF), `Error` (source unreachable, HTTP error, rate limit,
  malformed JSON, timeout 20 s), `NotConfigured`. The UI shows "Company description is currently unavailable." with the
  reason; it never shows invented text or the ticker as a description.
* **Configuration:** `MarketData:Profiles:Wikipedia:Enabled` (default true; compose `PROFILES_WIKIPEDIA_ENABLED`),
  `MarketData:Profiles:Mock:Enabled` (simulated text for tests/e2e only; compose `PROFILES_MOCK_ENABLED`). Requests send a
  descriptive User-Agent as Wikimedia asks and are throttled to one per 200 ms.
* `Asset.Description` is a legacy column that was never populated; it is no longer exposed.
