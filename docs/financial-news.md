# Financial news

`/news` (feed), the **Company news** section of every stock page, the watchlist filter and the **Event and
price-movement comparison**. Code: `Application/News` (rules, service, event comparison), `Infrastructure/News`
(providers), `Api/Controllers/NewsController.cs`.

## Sources (verified 2026-10-15)

| Provider | What | Coverage | Terms / caveats |
|---|---|---|---|
| `YahooNews` – Yahoo Finance search (`/v1/finance/search`, unofficial) | company news: headline, publisher, publish time, link, thumbnail, `relatedTickers` | good for US tickers; elsewhere searched by company name and only kept when Yahoo tagged the exact listing (e.g. `SIE.DE`) – sparse | unofficial endpoint, personal non-commercial use (like Yahoo prices); **no summaries** are provided; thumbnails belong to the publishers |
| `OfficialRss` – Federal Reserve, ECB, Bank of England, Bank of Japan press RSS | official central-bank releases, speeches, statistics | US, euro area, UK, Japan | public institutions' own feeds; titles/links (and descriptions where the feed has them) with attribution |
| `MockNews` | deterministic SIMULATED articles | tests and the e2e stack only (`News:Mock:Enabled`) | headlines start with "SIMULATED:" |

Not used: **Alpha Vantage `NEWS_SENTIMENT`** – a request with the configured key on 2026-10-15 answered "This is a
premium endpoint". Commercial, licensed news feeds (Reuters, Dow Jones, exchange disclosure feeds) would be needed for
a commercial product; see [launch-readiness.md](launch-readiness.md).

Configuration (`appsettings` / environment): `News:Enabled`, `News:RefreshMinutes` (30), `News:FailureBackoffMinutes`
(10), `News:RetentionDays` (120), `News:ShowImages` (**false**), `News:WatchlistRefreshLimit` (5),
`News:Yahoo:Enabled`, `News:OfficialFeeds:Enabled`, `News:Mock:Enabled`. Compose maps them to `NEWS_*` variables.

## Fetching

Fetches happen on demand when a feed or stock page is requested, never in the browser:

* Each source/query has a `NewsFetchStates` row; it is fetched again only after `RefreshMinutes` (after a failure:
  `FailureBackoffMinutes`), shared by all users. One fetch per source at a time (single-flight lock; concurrent requests
  serve stored articles). 20 s timeout plus the resilience pipeline of the market-data clients (throttling, retries on
  429/5xx, circuit breaker).
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

Scope: **Company** (linked to an equity), **Market** (linked only to an index/ETF, or market-type without links),
**Macro** (central bank, macro, geopolitics, commodities, currencies without company links).

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
verified. With it off, a neutral icon is shown (never a stand-in photo). With it on, the provider's thumbnail for that
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
watchlist filter, event comparison end-to-end, auth), `NewsProviderFailureTests`, Vitest `news.test.ts`, e2e journey E.
