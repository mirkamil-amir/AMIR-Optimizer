# Financial news

`/news` (feed), the **Company news** section of every stock page, the watchlist filter and the **Event and
price-movement comparison**. Code: `Application/News` (rules, service, event comparison), `Infrastructure/News`
(providers), `Api/Controllers/NewsController.cs`.

## Sources (verified 2026-10-15; statistics, market feeds and SEC added 2026-11-08, AMIR-14)

| Provider | What | Coverage | Terms / caveats |
|---|---|---|---|
| `YahooNews` – Yahoo Finance search (`/v1/finance/search`, unofficial) | company news: headline, publisher, publish time, link, thumbnail, `relatedTickers` | good for US tickers; elsewhere searched by company name and only kept when Yahoo tagged the exact listing (e.g. `SIE.DE`) – sparse | unofficial endpoint, personal non-commercial use (like Yahoo prices); **no summaries** are provided; thumbnails belong to the publishers |
| `YahooNews` market feeds – same endpoint queried with 16 instruments (`YahooNewsProvider.MarketInstruments`: S&P 500, Nasdaq, Dow, EURO STOXX 50, DAX, FTSE 100, Nikkei 225, Hang Seng, US 10-year yield, EUR/USD, USD/JPY, GBP/USD, gold, WTI, Brent, VIX) | Market-wide news | global | same terms; one client, so company and market queries share Yahoo's 1 request/s. Yahoo tags many single-stock articles with an index too – those stay company news (see Taxonomy) |
| `OfficialRss` – Federal Reserve, ECB, Bank of England, Bank of Japan press RSS | official central-bank releases, speeches, statistics | US, euro area, UK, Japan | public institutions' own feeds; titles/links (and descriptions where the feed has them) with attribution |
| `OfficialStats` – U.S. Bureau of Economic Analysis (RSS), UK Office for National Statistics release calendar (RSS), Eurostat euro indicators (Atom, collection `CAT_PREREL`) | statistics releases: GDP, inflation, labour, trade, income, retail | US, UK, euro area | public institutions' own feeds. BEA dates are US Eastern (EST/EDT, accepted for this feed only); ONS items are kept only when the title names an economic indicator (`IsEconomicRelease`) – the calendar also lists health and population releases; BEA lists the latest release of every series back to 2013, older items fall outside the retention. Release figures are **not** extracted from the prose |
| `SecEdgar` – SEC EDGAR company filing list (`/cgi-bin/browse-edgar?…&output=atom`) | 8-K, 10-Q, 10-K, 20-F, 40-F, 6-K (and amendments) of US listings | US listings (no exchange suffix) | official and free; SEC requires the operator's name and e-mail in every request: **off until `News:SecEdgar:Contact` is set** (`NEWS_SEC_CONTACT`), never taken from a user account; 4 requests/s (SEC allows 10). Not yet checked against the live service (needs the contact) |
| `MockNews` | deterministic SIMULATED articles | tests and the e2e stack only (`News:Mock:Enabled`) | headlines start with "SIMULATED:" |

Not used: **Alpha Vantage `NEWS_SENTIMENT`** – a request with the configured key on 2026-10-15 answered "This is a
premium endpoint". **BLS** answers automated clients with HTTP 403 (bot protection, never bypassed); **Japan's
statistics bureau** has no release feed. Commercial, licensed news feeds (Reuters, Dow Jones, exchange disclosure
feeds) would be needed for a commercial product; see [launch-readiness.md](launch-readiness.md).

SEC filings get a headline built from the filing itself – company name, EDGAR's form title, 8-K item numbers and filing
date ("Apple Inc.: 8-K - Current report (items 2.02, 9.01), filed 2026-10-29") – because every 8-K has the same EDGAR
title and stories group by headline words. The publication time is EDGAR's acceptance time with its offset; an entry
without an offset is skipped.

Configuration (`appsettings` / environment): `News:Enabled`, `News:RefreshMinutes` (30), `News:FailureBackoffMinutes`
(10), `News:RetentionDays` (120), `News:ShowImages` (**false**), `News:WatchlistRefreshLimit` (5),
`News:Yahoo:Enabled`, `News:Yahoo:MarketFeeds` (true), `News:OfficialFeeds:Enabled`, `News:Statistics:Enabled`,
`News:SecEdgar:Contact` (empty = off), `News:Mock:Enabled`, and the schedule `News:Schedule:Enabled` (true),
`TickMinutes` (10), `InitialDelayMinutes` (2), `CompanyRefreshMinutes` (120), `CompaniesPerRun` (60),
`CuratedSymbols` (empty). Compose maps them to `NEWS_*` variables (`NEWS_STATISTICS_ENABLED`, `NEWS_YAHOO_MARKET_FEEDS`,
`NEWS_SEC_CONTACT`, `NEWS_SCHEDULE_ENABLED`, `NEWS_COMPANY_REFRESH_MINUTES`).

Regular users see article publishers and the attribution/limitations notes; the news-source table (provider names,
terms, fetch status) and the names of failing sources are shown to administrators only.

## Fetching

**Scheduled (2026-11-08, `NewsScheduleWorker`):** every `TickMinutes` the server fetches what is due, so each feed has
its own supply before anyone opens Wire: all feeds (central banks, statistics offices, market instruments), then the
**scheduled company set** – every listing held in any portfolio, on any watchlist, or in `CuratedSymbols` – held first,
then watched, then curated, stalest first, at most `CompaniesPerRun` per run, each company again after
`CompanyRefreshMinutes`. A provider is asked only about listings it supports (`ICompanyNewsProvider.Supports`; SEC: US
listings only). Fetching is shared and needs no user; what each user sees stays scoped by the feed queries. Before this,
company news was fetched only for the viewer's own watchlist, so Latest, My Watchlist and Company News showed the same
stories (1,216 of 1,226 company stories were about watched stocks; audit of 2026-11-08 in the Wire rebuild plan).

**On page view** (unchanged), never in the browser:

* Each source/query has a `NewsFetchStates` row; it is fetched again only after `RefreshMinutes` (after a failure:
  `FailureBackoffMinutes`), shared by all users. One fetch per source at a time (single-flight lock). 20 s timeout plus
  the resilience pipeline of the market-data clients (throttling, retries on 429/5xx, circuit breaker).
* **Stored articles first (2026-10-19):** a source that has never been fetched is fetched in the request (there is
  nothing to show yet). A source that is merely *due* is refreshed in the background by `NewsRefreshWorker` while the
  stored articles are served; the response says `refreshing: true` and the page shows "Updating news sources…" and checks
  again every 3 s until the refresh is done. Concurrent viewers are merged into one refresh. Before, the first viewer
  after each refresh window waited for all due sources in sequence (measured: 10.1 s). All fetch states are read in one
  query. Nothing is lost on restart: due-ness lives in `NewsFetchStates`, so the next page view schedules it again. The
  worker does not use the shared job queue, which runs long backtests and ML experiments one at a time. See
  [performance.md](performance.md).
* Feed requests also refresh the stalest five watched companies of the caller.
* A failure is stored as `LastError` and shown on the page ("Some sources could not be reached; stored articles are
  shown"); it never produces substitute articles. Tested with a failing provider (`NewsProviderFailureTests`).

## Normalization and storage

* One shared row per article (`NewsArticles`): provider id, canonical URL, headline, publisher, publication time (UTC),
  retrieval time, provider summary shortened to ≤ 320 characters (Yahoo supplies none), thumbnail URL (https only),
  category + basis, scope, region, official flag, simulated flag. Full article text is never fetched or stored.
* **Deduplication:** `Fingerprint` = SHA-256 of the canonical URL (scheme-less, lower-case host, no fragment or tracking
  parameters) is unique. Syndicated copies (different URL, same headline words) share `HeadlineKey`; the feed shows the
  earliest copy once and lists the other publishers as "also reported by".
* **Entity matching:** an article is linked to a listing only when the provider tagged it with that canonical symbol
  (`NewsArticleAssets.MatchMethod = "YahooNews ticker tag"`). No fuzzy name matching, so untagged articles about a
  company may be missing, but false matches (e.g. the SAP ADR "SAP" vs `SAP.DE`) are avoided.
* Items without title, link or a parseable time are skipped; future timestamps and items older than the retention are
  rejected; RSS dates with ambiguous zone abbreviations (EST, CET) are rejected rather than guessed.

## Classification and "potential market relevance"

`NewsClassifier` (keyword rules on headline, then summary; whole-word matching; first rule wins; official feeds keep
their own category): central bank, earnings, corporate transaction, regulatory/legal, macroeconomic, geopolitical,
commodities/energy, currencies/bonds, market/index, else general. The matched term is shown as the basis. It is a
heuristic, not a trained model, and can be wrong (no accuracy evaluation exists yet).

Scope (rules version 2, `NewsTaxonomy.Scope`): **Company** when a catalogue equity is linked or any provider tag names a
security – even one outside the catalogue – other than a catalogue ETF or index; **Market** when every tag is a market
instrument or only index/ETF listings are linked; otherwise by category as before: **Macro** (central bank, macro,
geopolitics, commodities, currencies), else Market.

## Taxonomy (classification rules version 2, 2026-11-08)

`NewsTaxonomy` (pure, unit-tested in `WireSourcesTests`). Every article stores `RuleVersion` (1 = before 2026-11-08,
2 = these rules) and `ProcessedAtUtc`; publication and retrieval times are never replaced. A rule change means a new
version, so stored classifications stay attributable.

* **Instrument kind of a provider tag** (Yahoo symbol syntax): `^…` index (`^TNX`, `^TYX`, `^FVX`, `^IRX` → rates;
  `^VIX`, `^VXN`, `^VXD`, `^V2TX`, `^VSTOXX` → volatility); `…=X` and `DX-Y.NYB` currency; `…=F` futures (Treasury
  futures → rates, `VX=F` → volatility, others commodities); `TMBMK…` bond benchmarks → rates; anything else a security.
* **Market area** of a market article: the kind named most often among its tags; ties go to equities, rates,
  currencies, commodities, volatility in that order. Example: "Bank Stocks Lead European Markets Higher" (`^STOXX`,
  `EURUSD=X`) → equities.
* **Event type** (`EventType` + `EventTypeBasis`): the source's own structure when it has one – SEC form (10-Q, 10-K,
  20-F, 40-F → periodic report; 6-K → other filing) and 8-K item (1.03 bankruptcy, 2.01 merger or acquisition, 2.02
  results, 5.02 management change, 1.01 material agreement, 3.02/2.03 capital raise, else other filing). Otherwise
  keyword rules, first match wins: company articles → bankruptcy, merger or acquisition, guidance, results, dividend or
  buyback, management change, capital raise, legal or regulatory; macro articles → rate decision, inflation, labour,
  growth (GDP), activity, trade or fiscal release, policy statement; market articles → market news. Unmatched: official
  macro items → other release, everything else general. Keyword types are a heuristic (no accuracy evaluation yet).

`NewsRelevance` – points, each with a stated reason: provider-tagged company +2; material event type (earnings,
corporate transaction, regulatory/legal, central bank) +2, other market types +1; official issuer +1; published within
24 h +1; reported by ≥ 2 publishers +1. High ≥ 5, Medium 3–4, Low ≤ 2. **It does not predict the direction or size of
any price move and has not been validated against returns.** No sentiment scores are shown.

## Feed filters

Views: Latest, My watchlist, Company, Market-wide, Macroeconomic. Filters: category, market/region (US, EU, DE, FR, NL,
GB, JP), date range, publisher (click a publisher), headline/ticker/company search, `symbol` (from a stock page).
"Trending"/"most discussed" labels are deliberately not used.

## Images

`News:ShowImages` is off by default because the display rights of publisher thumbnails delivered by Yahoo were not
verified. With it off, no image is shown (never a stand-in photo). With it on, the provider's thumbnail for that
article is shown with a credit line, lazy-loaded with `referrerpolicy=no-referrer`; a broken image falls back to the
placeholder.

## Event and price-movement comparison

`GET /api/v1/news/{id}/price-movement?symbol=…` (only for stocks the provider tagged). `EventPriceComparison` (pure,
unit-tested on a hand-built New York series):

1. Publication time is converted to the exchange time zone; timing is classified as before the session, during it,
   after the close, or outside trading days. Regular session hours per venue (`Markets.Session`, Tokyo 15:30 close
   since 5 Nov 2024); no holiday calendar – a missing stored bar is treated as a non-trading day.
2. **Reference** = the last stored close whose session ended before publication (cannot contain a reaction).
3. Changes after **1, 5 and 20 stored trading days** of the stock, adjusted closes when available.
4. The benchmark of the stock's current index (same currency) is read on **exactly the same dates**; a missing
   benchmark date yields no comparison instead of a substitute. Difference = stock change − benchmark change (a simple
   market adjustment, not a model-based abnormal return).
5. Limitations listed with every result: daily data only; publication during the session; other stored articles about
   the stock within ±3 days (listed); missing history (a download is queued).

Labelled "Event and Price-Movement Comparison" with the disclaimer that it is **not evidence of causation**.

## Notifications

Not implemented: the application has no e-mail delivery or push infrastructure that could be tested and operated.
Extension point: a scheduled job over `NewsArticleAssets` × `WatchlistEntries` with per-user preferences and a
deduplicated delivery log.

## Tests

`NewsRulesTests` (normalizer, classifier incl. word boundaries, relevance points, Yahoo/RSS parsing incl. malformed
input and time zones, event comparison timing/benchmark gaps), `NewsTests` (dedup, provider-tag linking, filters,
watchlist filter, event comparison end-to-end, auth, scheduled run over another user's watchlist with rules version 2),
`NewsProviderFailureTests`, `WireSourcesTests` (taxonomy, schedule selection, BEA/ONS/Eurostat/EDGAR/Yahoo-market parsing
on payloads in the live shape), live smoke tests (`PO_LIVE_SMOKE=1`; SEC also needs `PO_SEC_CONTACT`), Vitest
`news.test.ts`, e2e journey E.
