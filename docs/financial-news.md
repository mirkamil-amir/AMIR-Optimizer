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
* **Entities other than companies** (2026-11-10, AMIR-19, D-54): see [Entities](#entities).
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

## Taxonomy (classification rules version 3, 2026-11-10)

`NewsTaxonomy` (pure, unit-tested in `WireSourcesTests`, `ArticleClassificationTests`, `CentralBankDecisionTests`). Every
article stores `RuleVersion` (1 = before 2026-11-08, 2 = from 2026-11-08, 3 = these rules, adding central-bank decision
titles) and `ProcessedAtUtc`; publication and retrieval times are never replaced. A rule change means a new
version, so stored classifications stay attributable.

* **Instrument kind of a provider tag** (Yahoo symbol syntax): `^…` index (`^TNX`, `^TYX`, `^FVX`, `^IRX` → rates;
  `^VIX`, `^VXN`, `^VXD`, `^V2TX`, `^VSTOXX` → volatility); `…=X` and `DX-Y.NYB` currency; `…=F` futures (Treasury
  futures → rates, `VX=F` → volatility, others commodities); `TMBMK…` bond benchmarks → rates; anything else a security.
* **Market area** of a market article: the kind named most often among its tags; ties go to equities, rates,
  currencies, commodities, volatility in that order. Example: "Bank Stocks Lead European Markets Higher" (`^STOXX`,
  `EURUSD=X`) → equities.
* **Event type** (`EventType` + `EventTypeBasis`): the source's own structure when it has one – SEC form (10-Q, 10-K,
  20-F, 40-F → periodic report; 6-K → other filing) and 8-K item (1.03 bankruptcy, 2.01 merger or acquisition, 2.02
  results, 5.02 management change, 1.01 material agreement, 3.02/2.03 capital raise, else other filing) and, since rules
  version 3 (AMIR-20, D-55), the title of a central bank's rate-decision release, as it appears in its own press feed:
  Federal Reserve "Federal Reserve issues FOMC statement", ECB "Monetary policy decisions", Bank of England "Bank Rate
  maintained / increased / reduced / cut … at|to N%", Bank of Japan "Statement on Monetary Policy" (whole titles; only
  for official items whose source states the central-bank category and whose region is that bank's; basis "<bank>
  decision-release title"). Under version 2 none of the real decision releases was a rate decision (keywords matched only
  made-up headlines such as "decides on key interest rates"), so they never reached My Watchlist or portfolio news. Otherwise
  keyword rules, first match wins: company articles → bankruptcy, merger or acquisition, guidance, results, dividend or
  buyback, management change, capital raise, legal or regulatory; macro articles → rate decision, inflation, labour,
  growth (GDP), activity, trade or fiscal release, policy statement; market articles → market news. Unmatched: official
  macro items → other release, everything else general. Keyword types are a heuristic (no accuracy evaluation yet).

## Classification history (2026-11-10, AMIR-18, D-53)

Every classification an article has had is kept in `NewsArticleClassifications`, one append-only row per article and
rules version: category with its basis, scope, event type with its basis, market area, processing time, what triggered it
(`Ingest` when stored, `Reclassification`, `Backfill`) and a note on its limits. The article row carries the current one,
so the feed queries are unchanged.

* **One function for both paths:** `NewsTaxonomy.ClassifyArticle` (pure) classifies from headline, summary, provider
  tags, linked listings (equities vs. index/ETF listings) and what the source itself stated. What the source stated is
  stored per article since this change (`SourceCategory`, `SourceEventType`, `SourceEventTypeBasis`; for articles stored
  before, the migration recovered it: official feeds by their category basis, SEC filings by provider).
* **Reclassification:** every scheduled run classifies up to 10 × 500 articles of an older rules version again
  (`NewsService.ReclassifyPendingAsync`) and adds a history row; earlier rows stay. Articles stored before 2026-11-08 have
  no stored provider tags, so their scope and market area follow from the linked listings and the category only – noted
  on the row (only for articles retrieved before tags were stored, 2026-11-08 21:50 UTC; later articles without tags,
  such as official releases, simply had none). Re-running changes nothing.
* **Stories** containing a reclassified report take their lead (earliest) report's scope, category, event type, market
  area and base attention points again; their reports are never regrouped and join times stay.
* **Backfill:** the migration `NewsClassificationHistory` copied every stored article's classification into the history
  (rules version 1 rows with the retrieval time, since version 1 stored no processing time).
* **Where it is shown:** on the Wire page, a story's reports → "Classification history" (`GET
  /api/v1/news/stories/{id}/classifications`; the story must be visible to the caller like in the feeds, otherwise 404;
  it never fetches news). Each report lists its rules versions, newest first, with result, basis, time and trigger.

## Entities

Since 2026-11-10 (AMIR-19, D-54) stories link to things other than companies from a reviewed list in code (`NewsEntities`, version 1): equity indexes
(S&P 500, Nasdaq Composite and 100, Dow, PHLX Semiconductor, S&P/TSX, EURO STOXX 50, STOXX Europe 600, DAX, CAC 40,
IBEX 35, FTSE 100, Nikkei 225, Hang Seng), US Treasury yields (13-week, 5-, 10-, 30-year), currency pairs (EUR/USD and
USD/EUR – Yahoo's `EUR=X` is euros per dollar –, GBP/USD, USD/GBP, USD/JPY, US Dollar Index), commodities (WTI and Brent
crude, natural gas, gold, silver, platinum, copper, aluminium), the VIX, the four central banks and the three statistics
offices. Every market instrument Wire fetches news for is on it (unit test).

* **Links only from the source, never from headline text:** a provider tag on the list ("provider tag ^GSPC"); an official
  item whose source states the central-bank category → the central bank of its region ("official central-bank feed
  (EU)"); an official release of a statistics office → that office. Tags not on the list stay unlinked (`CL-LCO1=R`,
  `XAUUSD=X` – Yahoo writes gold spot like a currency pair, which the taxonomy reads as a currency). Entity kinds agree with
  the instrument kind the taxonomy reads from the same tag (unit test). **A simulated item is never linked to a real
  institution** (tag links only), so simulated rate decisions no longer reach My Watchlist or portfolio news.
* **Storage:** `NewsArticleEntities` (article, entity key, match method) and `NewsStoryEntities` (the union of a story's
  reports, indexed by entity). Each article records the list version it was linked with (`EntityRuleVersion`); the
  scheduled run links articles of an older version again (10 × 500 per run) from their stored tags and source, removes
  links the list no longer gives and recomputes their stories' union. Articles stored before 2026-11-08 have no stored
  tags, so they get source links only. The migration `NewsEntityLinks` links stored official central-bank items at once
  (the rate-decision feeds depend on them); everything else follows with the first scheduled run.
* **Rate decisions** in My Watchlist and portfolio news are found through the central-bank link (before: the region of an
  official story); "Why you see this" names the bank.
* **API and page:** stories return `entities` (key, name, kind, region, currency); an index that is also a catalogue
  listing (e.g. `^FTSE`) shows only as that listing. Market-wide and Macroeconomic take `entity=<key>` (unknown key → 400).
  On the Wire page each entity is a text label ("S&P 500 · index") linking to the feed that lists its stories
  (`/news?view=markets&entity=^GSPC`, central banks and statistics offices in Macroeconomic).

`NewsRelevance` – points, each with a stated reason: provider-tagged company +2; material event type (earnings,
corporate transaction, regulatory/legal, central bank) +2, other market types +1; official issuer +1; published within
24 h +1; reported by ≥ 2 publishers +1. High ≥ 5, Medium 3–4, Low ≤ 2. **It does not predict the direction or size of
any price move and has not been validated against returns.** No sentiment scores are shown.

## Stories and attention (2026-11-08, AMIR-15)

Articles are grouped into **stories** (`NewsStories`, `NewsStoryArticles`, `NewsStoryAssets`; `StoryRules`, version 1):
an article joins a story when it was published within **36 hours** of the story's reports and either has the same
headline words as one of them (a syndicated copy) or has the same scope and event type, concerns the same thing (a shared
listing; without listings, the same region) and shares at least **half of its headline words** (Jaccard index of words
of three or more letters, common stop words removed). Otherwise it starts a new story. The earliest report leads
(headline, link, publisher); every report stays listed with publisher and time, and each membership records when it
joined. Grouping runs right after each fetch and in every scheduled run (which also grouped the articles stored before
the story layer). A story leaves with its last report at the retention limit.

**Attention** (`StoryRules.Assess`): material event type +2 (results, guidance, merger or acquisition, bankruptcy,
management change, rate decision, inflation, labour or growth release; for articles from before event types, the
earnings, corporate-transaction, regulatory/legal and central-bank categories), official source +1, two or more
publishers +1, a market story tagged with two or more market instruments +1 (rules version 2, owner's decision
2026-11-08: such stories concern the market as a whole and reach Latest while fresh; the provider's tags are stored per
article as `ProviderTags` since then, so older market stories cannot earn this point), latest report within 24 hours
+1. **High ≥ 4, Medium 2–3, Low ≤ 1.** The time-independent part is stored
(`BasePoints`) so feeds filter in SQL; every reason is returned. It is not a prediction and has not been validated
against returns.

## Wire feeds (story endpoints)

Five endpoints, each its own query with written rules; a story is in two feeds only when it meets both
(`WireFeedService`, contract tests in `WireFeedTests`). Shared filters: `hours` (window), `region`, `attention`
(minimum), `search`, `eventType` where it applies, `cursor` (keyset on latest report time and id), `pageSize` (1–50).

| Feed | Endpoint | Includes | Excludes | Order | Default window |
|---|---|---|---|---|---|
| Latest | `GET /api/v1/news/latest` | every visible story with Medium or High attention | Low attention; a third story about the same company on the same page | newest first | 48 h |
| My Watchlist | `GET /api/v1/news/watchlist` | stories linked to a listing the caller watches (High, "X is on your watchlist"); rate decisions linked to the central bank of a currency a watched listing trades in (Federal Reserve → USD, ECB → EUR, Bank of England → GBP, Bank of Japan → JPY; see Entities) | general market, sector or country news | newest first | 7 days |
| Company News | `GET /api/v1/news/companies` | companies (curated, held or watched by the caller) with company stories, each with its 3 newest | companies only other users follow | newest company first | 7 days |
| Market-wide | `GET /api/v1/news/markets` | market-level stories by area: Equities, Rates, Currencies, Commodities, Volatility (5 each; `area` → one area, paged) | single-company stories | newest first per area | 72 h |
| Macroeconomic | `GET /api/v1/news/macro` | central-bank, statistics and other economy-wide stories | company and market stories | newest first | 30 days |

**On request** (2026-11-08, AMIR-16) each feed does what the article feed did: sources never fetched are fetched now,
due feeds and the viewer's stalest watched companies are refreshed in the background, and the response says
`refreshing: true` until then (the page checks again every 5 s – within the 60 requests per minute limit).

**The Wire page** (`/news`, AMIR-16) shows the five feeds as tabs with search, time window (default per feed, shown as
"Default: …"), market or region and attention filters, all in the URL (`view`, `hours`, `region`, `attention`, `q`,
`cursor`, `area`; old `view=Company|Market|Macro` and `symbol=` links still work). Each story is one dense row: latest
report time in UTC and relative, headline linking to the earliest report, linked listings (stock page), event type with
its basis, market area, region, the publishers ("A and 2 more · 3 reports", expands to every report with its time and
link), official and SIMULATED labels, "Why you see this" in My watchlist, the attention label with every reason, and
the event comparison for linked listings. Company news shows one block per company; Market-wide one section per area
with a link to the paged area. Side panels: freshness per source group and five benchmarks with their stored last close,
change, kind and date (`useStocksBySymbols`; never called live). Text only, no icons; dense lists on `.map-surface`.

**News about your holdings** (portfolio Overview, `GET /api/v1/portfolios/{id}/news`, AMIR-17): the portfolio is loaded
owner-scoped (another user's portfolio → 404). Stories linked to a listing the portfolio holds, and official rate
decisions of the central bank of a currency it holds, newest first (default 7 days; 30 and 90 on the page). Each story
carries its **exposure** as facts from `PortfolioValuationService` – the holding's weight (market value ÷ total value
including cash, the Overview's basis, with the price date) or the portfolio's share in the currency (positions and cash
in it ÷ total value) – and a story linked to a holding is High attention. Missing prices or rates leave the weight
`null` and list the valuation problems; nothing is estimated. No predicted effect is shown; Compare opens the event and
price-movement comparison with its "not evidence of causation" note. The section appears once the portfolio holds a
position; it requests the held companies' due news like the feeds.

**Visibility (privacy):** with `News:Feeds:HideOthersInterests` (default true) a story is shown only when it has no
listing, or one of its listings is curated, an index or benchmark, or held or watched by the caller – otherwise stories
fetched because another user watches a stock would reveal what they follow. Watchlist and holdings are read for the
authenticated user on every request; nothing is cached (there is no cache layer). The article feed `/api/v1/news` remains
only for the Company news section of a stock page (articles tagged with that listing).

**Source freshness** is returned with every feed per group – central banks, statistics offices, company filings, company
news, market news – as Fresh, Delayed (no success for twice the group's refresh interval) or Unavailable, without
provider names (D-30).

**Measured** (`WireBenchmarkTests`, `PERF_BENCH=1`, 100,000 stories, in-process, 2026-11-08): median 26 ms Latest
(also page 5), 9 ms My Watchlist, 41 ms Company News, 21 ms Market-wide, 11 ms Macroeconomic.

## Feed filters (article feed)

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
`NewsProviderFailureTests`, `StoryRulesTests` (grouping, attention thresholds incl. the SQL filter for every combination,
cursor), `WireFeedTests` (the five feed contracts on one data set for two users, privacy, paging, validation, auth),
`NewsEntitiesTests` (list and link rules), `NewsEntityLinkingTests` (linking stored articles, stale links, story union),
`WireFeedTests` (entities on stories, entity filter), `NewsClassificationHistoryTests` (history row on ingest, reclassification of version 1 articles with their stories,
idempotence, endpoint visibility), `ArticleClassificationTests` (the shared classification function),
`WireBenchmarkTests` (`PERF_BENCH=1`), `WireSourcesTests` (taxonomy, schedule selection, BEA/ONS/Eurostat/EDGAR/Yahoo-market parsing
on payloads in the live shape), live smoke tests (`PO_LIVE_SMOKE=1`; SEC also needs `PO_SEC_CONTACT`), Vitest
`news.test.ts`, e2e journey E.
