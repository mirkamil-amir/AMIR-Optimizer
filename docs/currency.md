# Currencies and exchange rates

Stocks from different markets – Apple (USD), SAP (EUR), Toyota (JPY), AstraZeneca (GBP) – can be combined in one portfolio
and one analysis. Each instrument keeps its own currency and prices; everything that combines instruments is computed in
one **base currency** with dated exchange rates. Root-cause analysis of the previous restriction: [currency-audit.md](currency-audit.md).

## Three currencies, never assumed equal

| Concept | Where | Meaning |
|---|---|---|
| **Trading currency** | `Asset.Currency` (ISO 4217, from the listing's venue: `.DE` → EUR, `.T` → JPY, `.L` → GBP, no suffix → USD) | The currency the instrument is quoted in. Prices are stored in it (pence normalized to GBP). Never changed by any conversion. |
| **Portfolio (account) currency** | `Portfolio.BaseCurrency`, fixed at creation | The paper account's cash, cost basis, realized P&L, fees and returns. Any currency with exchange rates can be chosen. |
| **Reporting currency** | `?currency=` on holdings and performance | How values are *shown*. A reporting conversion; nothing is exchanged and nothing stored changes. |

There is no brokerage integration: no real orders, settlement or account currency of a broker exist. Buying a foreign stock
in the paper portfolio is a **simulated currency exchange** of the portfolio cash, recorded with its rate on the execution.

## Convention

**rate = units of the target currency per one unit of the source currency**, so `converted = amount × rate`
(EUR→USD 1.10: €100 = $110; USD→EUR = 1 / 1.10). It is used everywhere in code, API (`/api/v1/fx/rate`) and UI ("1 USD = 0.9091 EUR").

Rates are stored as the provider's quote against the euro (`FxRates.UnitsPerEur`, ECB convention) and every pair is derived
from two observations of the same source and date: `rate(A→B, d) = unitsPerEur(B, d) / unitsPerEur(A, d)`. Direct, inverse
and cross rates are therefore exactly consistent (`rate(A→B) × rate(B→A) = 1`, `rate(A→B) × rate(B→C) = rate(A→C)`), and a
provider quoting the other direction must be normalized before storage (the ECB parser rejects any other denominator).

## Sources

| Source | Used with | Notes |
|---|---|---|
| **ECB** euro foreign exchange reference rates (`data-api.ecb.europa.eu`, dataset EXR, daily) | real prices (Yahoo, Alpha Vantage) | ~30 currencies incl. USD, GBP, JPY, CHF, SEK, NOK, DKK, CAD, AUD, CNY, INR; history since 1999; published ≈16:00 CET on TARGET days; free, no key; reference rates for information, not transaction rates |
| **MockFx** (deterministic simulated walk per currency) | simulated prices (Mock provider: tests, e2e, demos) | NOT real rates; labelled simulated |

Simulated prices are always converted with simulated rates and real prices with ECB rates – sources are never mixed.
Rates are fetched on demand for the needed currencies and dates, upserted into `FxRates` (source, retrieval time,
simulated flag) and reused; a range the source has no data for yet is not re-requested for an hour.

**Alternatives** (not integrated): central-bank feeds of other base currencies (Fed H.10 for USD, BoE for GBP), commercial
intraday providers (e.g. Refinitiv, Bloomberg, Open Exchange Rates paid tiers) for valuation at each market's close or for
real execution. A provider is added by implementing `IFxRateProvider` (normalize to units per EUR, typed failures) and
selecting it in `FxService.ProviderFor`; nothing else changes. No paid dependency was added.

## Dating policy

* A price on date *d* is converted with the rate of date *d*. Without an observation on *d* (weekend, holiday, before the
  afternoon publication) the latest earlier observation is used if it is **at most 4 calendar days old** (covers a weekend
  plus one holiday); otherwise the rate is **unavailable** – never 1.0, never today's rate for a historical date, never a
  later rate. A cross rate reports the older of its two legs.
* **Asynchronous closes:** ECB reference rates are fixed at 14:15 CET, the US close is 22:00 CET, Tokyo closes before
  Europe opens. Converting each market's daily close with the same day's reference rate is an approximation, not a
  simultaneous valuation; it is stated in every conversion note.
* **Historical series** (`CurrencyConverter.Convert`): `base_price[t] = native_price[t] × rate(native→base, t)`. Returns
  are computed from converted prices, so for an unhedged position without cash flows `1 + r_base = (1 + r_native) × (1 + r_fx)`
  (with r_fx measured in base units per native unit) – not the sum. A series already in the base currency is returned
  unchanged, which makes a second conversion impossible.
* Points without a usable rate are dropped and counted; a symbol losing more than 5 % of its points is **excluded with the
  reason** instead of being thinned silently. Different markets' holidays are handled by `ReturnMatrix` aligning on common
  dates (returns span the gap; nothing is interpolated).

## Where conversion happens (only through `FxService`)

| Feature | Analysis currency | Implementation |
|---|---|---|
| Optimization runs | portfolio base currency (research hypotheses: the user's first portfolio, else the market's currency) | `OptimizationService.EvaluateAsync` → `FxService.GetSeriesInCurrencyAsync`; benchmark converted the same way; `OptimizationRun.Currency`, `FxSource`; allocation shows each stock's trading currency; currency exposure |
| Reality check | the run's currency | same conversion as the run (data-change detection still works) |
| Backtests, ML experiments | `Currency` of the request (UI: portfolio currency); default: the selection's own currency, or the application default for mixed selections | converted series; ML volumes unchanged |
| Strategy comparison | the optimization run's currency | both strategies and the benchmark |
| Portfolio valuation | account or reporting currency | `PortfolioValuationService`: native price × rate of the price date; cash converted only for reporting |
| Trade proposals, simulated fills | portfolio currency | native price × rate (rounded to the stored 10 decimals so amounts reconcile exactly); execution stores currency, rate, rate date, source |
| Performance | account currency, optional reporting currency | daily value = cash + Σ qty × close × rate(d); days without a rate are left out and counted |
| Risk | account currency | weights from the valuation, converted return series |

A single-market selection analysed in its own currency keeps the old benchmark rule (SPY is replaced by `^GDAXI` for a DAX
selection). A mixed selection keeps the requested benchmark, converted.

## Missing or unreliable data

* Unsupported currency (no rates from the source): validation error naming the currency.
* No rate within the carry-forward window: typed `FxRateUnavailableException` (HTTP 422 on `/fx/rate`), symbol excluded from
  analyses with the reason, holdings show **"Cannot be valued"** and the portfolio's market value and total are `null`
  (no plausible partial total), proposals mark the line "No exchange rate" and keep the position unchanged, fills are
  rejected. Provider outages fall back to stored rates; beyond them the same rules apply.
* The domain refuses a foreign-currency trade without an explicit rate and source, and a rate other than 1 for a
  same-currency trade.

## Data model and migration `MultiCurrency`

* `FxRates (Source, Currency, Date, UnitsPerEur numeric(20,8), IsSimulated, RetrievedAtUtc)`, primary key
  (source, currency, date), checks `UnitsPerEur > 0` and `Currency <> 'EUR'`.
* `OptimizationRuns`, `Backtests`, `MlExperiments`, `StrategyComparisons`: `Currency`, `FxSource`.
* `Executions`: `Currency`, `FxRate numeric(20,10) > 0`, `FxRateDate`, `FxSource`; `Orders`, `TradeProposalLine`:
  `Currency`, `FxRate`.
* Backfill: before this change every analysis and trade used one currency, so existing rows get their instrument's
  currency and rate 1; runs get the currency of their stocks. Verified on a restored copy of the development database
  (5 runs, 9 executions, 19 proposal lines: no empty currency, all rates 1).

## API

`GET /api/v1/fx/currencies` (source, convention, supported currencies) · `GET /api/v1/fx/rate?from=&to=` (latest usable
rate with date and source, 422 with the reason when unavailable) · `GET /api/v1/fx/history?from=&to=&days=` (only days
with a published rate – nothing carried forward, for charts) · `GET /api/v1/fx/board?base=&quotes=` (latest published rate
per quote with 1-day and 1-month change; a quote without a recent rate shows its reason instead of a number) · `GET /portfolios/{id}/holdings?currency=` ·
`GET /portfolios/{id}/performance?currency=` · `currency` on backtest and ML requests · `currency`, `fxSource`,
`currencyExposure` on runs · `currency`, `fxRate`, `fxRateDate`, `fxSource` on transactions, orders and proposal lines.

## Currency converter (UI)

`components/currency-converter.tsx` on the overview page. Every currency the FX source publishes (ECB: 30 + EUR, incl. TRY) is
available in grouped drop-downs (`CurrencySelect` in `components/currency.tsx`: portfolio currency and majors first, then
"More currencies", each with its English name). The same drop-down is used for the portfolio currency (Portfolio Creation),
the currency of the overview totals and the reporting currency on holdings ("EUR (portfolio)" marks the base currency):

* **Rate board** against a selectable base: the majors (`majorCurrencies` in `lib/currency.ts`: USD, EUR, GBP, JPY, CHF, CAD,
  AUD, CNY, SEK, NOK, TRY) plus any currency added with "+ Add currency" (removable; kept for the visit only).
  `/fx/board` accepts up to 32 quotes, so all published currencies fit on one board.
* **Chart** of any pair (1M/3M/1Y/5Y): choose the currency in the chart's drop-down or click a board row.
* **Calculator** between any two supported currencies (amount × rate, cross rate via the board's base, own `/fx/board` request). Every number is labelled with the source and rate date; it states that reference rates are not exchange offers.

## Limitations

* Daily reference rates only: no intraday FX, no bid/ask spread, no FX transaction costs in the paper portfolio.
* One cash balance per portfolio (in its currency); no multi-currency cash accounts, no hedging, no currency forwards.
* The portfolio currency cannot be changed after creation (it would require converting cash and cost basis – a modelled
  exchange that is not implemented); use the reporting currency to view values in another currency.
* In a reporting currency other than the account currency, cost basis, fees and realized results are converted at today's
  rate (labelled); returns are always measured in the account currency.
* There is no Swiss (SIX) market in the catalogue yet: CHF portfolios work and convert all markets, but no CHF stocks can be
  selected (add the venue and an SMI constituent file as described in AGENTS.md).
