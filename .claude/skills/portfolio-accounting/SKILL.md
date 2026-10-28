---
name: portfolio-accounting
description: MatrixView's paper portfolios – ledger of holdings, cash and transactions (atomic, idempotent bookings, reversals, weighted-average cost, realized P&L), trade proposals from an optimization, simulated execution, current valuation in the base or a reporting currency, recorded performance (time-weighted return) and current-holdings risk. Use when changing bookings, proposals, executions, valuation, the Overview/Ledger numbers or anything that reads or changes holdings.
---

# Portfolio accounting, trading and recorded performance

Docs: `docs/portfolios.md` (fundamental rules), `docs/currency.md`. Formulas: F-TRD-001..004, F-PERF-011/012,
F-RISK-008 in `docs/quant/formula-reference.md`.

## Architecture and data flow
* Every portfolio operation resolves the portfolio through `PortfolioService.LoadAsync`/`EnsureExistsAsync` with the
  authenticated user's id (another user's id → 404; never a user id from the request); changes call
  `Portfolio.EnsureActive()` (archived = read-only).
* Domain (`src/PortfolioOptimizer.Domain/Portfolio.cs`, `PortfolioTransactions.cs`): holdings with weighted-average cost
  (fees capitalized on buys), realized P&L on sells, cash ledger entries, `TransactionCostModel`.
* Bookings: `PortfolioTransactionService.RecordAsync` / `ReverseAsync` – atomic, idempotent per
  `(PortfolioId, IdempotencyKey)`, in time order, never edited (corrections are reversing entries).
* Proposals and execution: `TradingService.CreateFromRunAsync` / `RebalancePreviewAsync` → `TradeProposalCalculator.Compute`
  (whole or 4-decimal shares, sells first, buys capped by cash after fees, drift threshold) → `TradingService.ExecuteAsync`
  (confirmed, optimistic concurrency; trades in another currency are simulated exchanges with rate/date/source).
* Valuation: `PortfolioValuationService.ValueAsync` (latest price × rate of its price date; cash at today's rate for a
  reporting currency). Performance and risk: `PortfolioAnalyticsService.GetPerformanceAsync` (replay of executions and
  cash entries → daily values → TWR with end-of-day external flows) and `GetRiskAsync` (historical simulation of current
  weights). Holdings view: `GetHoldingsAsync`.

## Rules (must not break)
* Keep five concepts apart: holdings (ledger), temporary selection (browser memory), working universe
  (`Settings.Universe`), target allocation (a run), transactions. Optimization never changes holdings.
* Performance starts at the first booking; deposits/withdrawals are flows, not returns; dividends the user records and
  standalone fees are returns; nothing is back-filled or fabricated.
* Missing rate → `null` totals with the problem listed; never a plausible number. The portfolio currency is fixed;
  reporting currencies change nothing stored.
* Money is `decimal`, rounded to the stored precision **before** it changes cash; math is `double`.
* Deleting a portfolio removes all its records (runs, ML Optimizations, backtests, comparisons, research), never prices.

## Edge cases
Bookings on weekends/holidays (take effect on the next valued day), sells larger than the holding (rejected), cash
overdraw (rejected), a position without any price (left out of market value with a warning – R-06), foreign position
without a rate on a day (day skipped in the performance series, flow carried forward), replay of the same idempotency key.

## Changing things safely
* New portfolio feature: route id → owner-scoped load → `EnsureActive()` → cross-user case in
  `tests/PortfolioOptimizer.IntegrationTests/MultiPortfolioTests.cs` → section under `/portfolios/:id/…` in the frontend.
* Accounting changes: unit tests in `tests/PortfolioOptimizer.UnitTests/Trading/TradingTests.cs` /
  `PortfolioCashTests.cs` with hand-computed cash and cost; reconcile the ledger (sum of entries = cash).
* Schema change: migration (see `/ship-change`) and a rehearsal on a restored copy of the development database – it
  holds the user's real portfolios (dry run, `pg_dump` backup, transaction, report).

## Tests
```bash
dotnet test tests/PortfolioOptimizer.UnitTests --filter "FullyQualifiedName~Trading"
./scripts/test.sh --filter "FullyQualifiedName~MultiPortfolioTests|FullyQualifiedName~MultiCurrencyTests|FullyQualifiedName~MissingFxTests"
./scripts/e2e.sh e2e/portfolios.spec.ts e2e/multi-currency.spec.ts                                     # disposable stack only
```

## Known limitations
No automatic dividends/splits in the paper portfolio (benchmark is total return – R-10); one cash balance per portfolio;
Overview "Total return" is on net contributions, not time-weighted (R-05); risk understated when holdings lack history.

## Related
`/optimization-backtesting` (runs that proposals come from), `/market-data-fx` (prices and rates),
`/quant-formulas` (TWR, metrics), `/platform-architecture` (auth, persistence).
