# Watchlist

A personal list of stocks a user follows without holding them. Watching a stock is **not a purchase**: it never creates
orders, never changes a portfolio, optimization run or the temporary stock selection.

## Behaviour

| Action | Where | What happens |
|---|---|---|
| Add | `/watchlist` search box (name or ticker), Watch button on the stock page and in the stock browser's detail panel | `POST /api/v1/watchlist {symbol}`. 404 for an unknown symbol, 409 if already watched or the list has 100 entries. If fewer than 20 daily prices from the last five years are stored, a history download is queued (same on-demand download as the stock browser, max 50 symbols per user request) and the response says so. |
| List | `/watchlist` | `GET /api/v1/watchlist?range=1M|3M|6M|1Y|5Y`: each row has the stock's metadata (incl. sector and index membership, shown as "Industrials · DAX 40"), latest quote with provenance (kind, as-of, provider, retrieved, cached/stale/SIMULATED), the change over the range and a 60-point sparkline. |
| Refresh one | Refresh button per row / on the stock page | `POST /api/v1/stocks/{symbol}/quote/refresh` (30 s cooldown per stock). |
| Stock list trend | New portfolio / Choose stocks, column "1 month" | `GET /api/v1/stocks/trends?symbols=…&range=1M`: the same change and 60-point sparkline (`PriceHistory.Slice` + `PriceHistory.Downsample`) for one page of the stock list, up to 200 symbols in two batched queries; unknown symbols are skipped, stocks without enough history get a null change with the reason. Not owned data; any approved account. |
| Refresh all | "Refresh prices" | `POST /api/v1/watchlist/quotes/refresh`: the caller's symbols only; quotes retrieved within the cache window (15 min) are reused, the rest are batched through the provider. Rate limited (`quotes` policy). |
| Reorder | drag handle on each row (mouse, pen, touch), or arrow keys / Home / End on the focused handle | `PUT /api/v1/watchlist/order {symbols}` with every watched symbol exactly once; positions are stored per owner and the list is returned in that order (new entries go to the end). A list that misses or adds a symbol (changed in another tab) → 409, duplicates → 400; the page shows the new order at once and rolls back on failure. The list scrolls in its own box and is paged (25 rows by default, 25/50/100/200); dragging works within the visible page. |
| Remove | trash icon | `DELETE /api/v1/watchlist/{symbol}`; shared prices and news are kept. |
| Details | click anywhere on a row (or the symbol link; the row's buttons keep their own function) | `/stocks/{symbol}`: price and provenance, history chart, company news, event comparison per article. When the stock is on the watchlist, Previous / Next buttons with "n of m" step through the watchlist in its order (disabled at the ends). |

There is no background refresh per watchlist: quotes are refreshed by the existing sign-in/scheduled cycle and by the
explicit actions above, so opening the page never triggers uncontrolled provider requests.

## Price history and period change

`GET /api/v1/stocks/{symbol}/history?range=…` returns stored daily bars of the active source (`PriceHistory.Slice`,
unit-tested):

* The range ends at the **last stored bar** (a stale series is not stretched to today).
* The base is the **last observation on or before** `end − range` (calendar months/years; weekends and holidays fall back
  to the previous session). Change = end / base − 1.
* The series is **adjusted closes** (splits and dividends, i.e. total return) when every bar in the slice has one,
  otherwise raw closes for the whole slice – never mixed – and the response says which (`basis`).
* If the stored history starts after the range start, `change` is null and `limitation` names the first stored date;
  nothing is extrapolated. Non-positive prices are ignored.
* `1D` is accepted but always reported as unsupported: no configured source stores intraday bars.

## Persistence and security

* Table `WatchlistEntries (Id, OwnerId, AssetId, AddedAtUtc)`, unique index on `(OwnerId, AssetId)`; the asset is the
  catalogue listing (symbol incl. venue suffix), so the same company on two exchanges is two entries and a ticker is never
  assumed to be globally unique. Migration `Watchlist` only adds the table.
* Every query filters by the caller (`ICurrentUser`); another user's entry is 404. Deleting a user cascades to their
  entries; the test-data purge reports them.
* Quotes, prices and news stay in the shared tables – nothing is copied per user.

Tests: `WatchlistTests` (add/duplicate/404, persistence across sign-out, cross-user isolation, bulk refresh scope,
history ranges and gaps, queued download), `PriceHistoryTests`, e2e `research.spec.ts` (journey D).
