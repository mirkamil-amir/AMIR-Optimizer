# Market data sources

Verified on 2026-10-12 from this development machine. Endpoint behaviour, terms and limits change – re-check before
relying on a provider.

| Provider | Status | Key | Adjusted close | Limits | Terms |
|---|---|---|---|---|---|
| **Yahoo Finance** chart API (`query1.finance.yahoo.com/v8/finance/chart/{symbol}`) – default | Working: 11 symbols × 5 years downloaded successfully | none | yes (`indicators.adjclose`) | unpublished; client self-limits to 1 request / 500 ms, retries 429/5xx with exponential backoff | **Unofficial, undocumented** endpoint. Yahoo's terms restrict use to personal, non-commercial purposes. May change or block clients without notice. |
| **Alpha Vantage** (`TIME_SERIES_DAILY`, `GLOBAL_QUOTE`) | Implemented; response format verified with the public `demo` key only (a real free-tier key was not available for testing) | required (`MarketData__AlphaVantage__ApiKey`) | only with the premium `TIME_SERIES_DAILY_ADJUSTED` (`UseAdjustedEndpoint=true`) | free tier ≈ 25 requests/day; throttling is reported in HTTP 200 bodies (`Note`/`Information`) and surfaced as transient errors | see Alpha Vantage terms of service |
| **Mock** (simulated) | Working, offline | none | yes (equal to close; no dividends) | none | Synthetic data. Rows are flagged `IsSimulated` and the UI shows a SIMULATED badge everywhere they are used. |
| Stooq CSV (`stooq.com/q/d/l/`) | **Not implemented** | – | – | – | As of 2026-10 the endpoint answers with a JavaScript proof-of-work bot challenge instead of CSV. Circumventing bot protection is not acceptable, so Stooq was dropped. |

## Ingestion pipeline

1. `POST /api/v1/market-data/sync` creates a job (202). The background worker processes assets sequentially.
2. Incremental download: only dates before the stored range and after `last stored date − 7 days` are requested
   (the overlap picks up late corrections). `forceFullRefresh` re-downloads the range.
3. **Normalization** (`PriceNormalizer`): drops rows outside the range or in the future, missing/NaN/non-positive
   closes, invalid adjusted closes, high < low; collapses duplicate dates (last wins); rounds raw prices to 4 and
   adjusted closes to 6 decimals; converts Yahoo session timestamps to exchange-local dates via `gmtoffset`;
   maps share-class symbols (`BRK.B` → `BRK-B` for Yahoo).
4. **Currency check:** the provider's currency must equal the asset's currency. No FX conversion is performed; a
   mismatch fails the asset with an explanatory message.
5. **Upsert:** unique (asset, source, date). If overlapping adjusted closes changed by more than 0.05% (a new
   dividend or split re-bases the back-adjusted series), the full stored history for that asset is re-downloaded.
6. Latest quote is stored separately. Per-asset logs record rows received/inserted/updated/rejected/duplicates.
7. Network failures, throttling and malformed responses are caught per asset; the job finishes as
   `PartiallySucceeded`/`Failed` with messages. **Failed live requests are never replaced with synthetic data.**

Resilience: `Microsoft.Extensions.Http.Resilience` standard pipeline (3 retries, exponential backoff with jitter,
honours `Retry-After`, per-attempt and total timeouts, circuit breaker) plus a per-provider throttling handler.

A scheduled refresh (default every 12 h, configurable, `MarketData:ScheduledRefreshEnabled`) enqueues an incremental
sync of all enabled assets. On first start with an empty database an initial 5-year download is queued.

Freshness: an asset is **Stale** when its last close is more than `StalePriceDays` business days old (default 5,
weekends excluded; exchange holidays are not modelled). The latest daily bar downloaded during market hours is
intraday until the next sync overwrites it.
