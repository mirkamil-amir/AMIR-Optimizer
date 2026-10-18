# Portfolios: several independent portfolios per user

Every signed-in user owns any number of **independent, private portfolios**. Each has its own cash, holdings, transaction
ledger, optimization runs, proposals and history. This document describes the model, the accounting and performance rules,
the API and its authorization, the user journey, the migration, and what is (not) implemented. Decision record: D-32 in
[decisions.md](decisions.md).

## Baseline (audit, 2026-10-17)

The backend was already multi-portfolio: `Portfolio` had an `OwnerId`, every query was owner-scoped (another user's id →
404), holdings used weighted-average cost, cash moved only through an immutable ledger, runs/proposals/orders carried a
`PortfolioId`, paper trades were idempotent and concurrency-checked (`Portfolio.Version`). The single-portfolio behaviour
was in the **frontend** and a few gaps:

| Found | Where | Change |
|---|---|---|
| One "selected portfolio" in browser storage, switched in the header; no URL contained a portfolio id; pages read it implicitly | `lib/portfolio-context.tsx`, `app-shell.tsx` | Portfolio workspace `/portfolios/:id/…`: the URL decides the portfolio; header dropdown removed |
| Overview showed only the selected portfolio | `pages/overview.tsx` | Overview = **My portfolios** (all of the user's portfolios); the old page is the workspace's Overview tab |
| A run of portfolio A opened while B was selected showed B's currency and B's run list | `pages/optimize.tsx` | Runs open inside their own portfolio (redirect if the URL names another); results use the run's currency |
| Portfolio creation only in Settings (name, currency, cash) | `pages/settings.tsx` | **Portfolio Creation** (`/stocks`): details + stock universe + review |
| No manual transactions (cash moved only through the initial deposit and confirmed proposals) | backend | Journal: buy, sell, deposit, withdrawal, dividend, fee, reversal |
| Delete was the only lifecycle action and removed all history | `PortfolioService.DeleteAsync` | Archive / restore; delete only after archiving |
| Deposits/withdrawals on a non-trading day were dropped from the performance replay | `PortfolioAnalyticsService` | Every cash entry applied on the next valued day |
| AI research used "the user's first portfolio" for its currency and saved runs | `ResearchAgent`, `SaveHypothesisTool` | Optional portfolio context per research session (owner-checked) |

## Domain model

| Concept | Entity / place | Notes |
|---|---|---|
| Portfolio | `Portfolio` | `OwnerId`, `Name`, `Description`, `BaseCurrency` (fixed), `CashBalance`, `Settings` (optimizer config incl. working universe), `ArchivedAtUtc` (status), `Version` (optimistic concurrency) |
| Current holdings | `Holding` (unique per portfolio + asset) | quantity, weighted-average cost (fees capitalized), realized P&L; same stock in two portfolios = two independent rows |
| Cash ledger | `CashLedgerEntry` | immutable, signed; types Deposit, Withdrawal, BuySettlement, SellSettlement, Fee, **Dividend** |
| Trade fills | `TradeExecution` | native price, FX rate/date/source, gross, fees, net cash, realized P&L |
| Manual transactions | **`PortfolioTransaction`** (journal) | the user's input with idempotency key, note, links to the execution / cash entry it created and to a reversal |
| Working universe | `PortfolioSettings.Universe` | which stocks the optimizer may use – not holdings |
| Temporary selection | browser memory (`lib/selection.tsx`) | never persisted; becomes a universe only on "Create portfolio" or an analysis |
| Target allocation | `OptimizationRun` + weights | a record of a calculation; never changes holdings |
| Proposed trades | `TradeProposal` | change holdings only when the user confirms the execution |
| History | derived from ledger + stored prices | no stored snapshots (see below) |

Derived values (total value, invested value, P&L, weights) are **not stored**: they are computed by
`PortfolioValuationService` from holdings, cash and the latest stored prices on each request, so they cannot drift.

## Transactions and accounting

Endpoint `POST /api/v1/portfolios/{id}/journal` (header `Idempotency-Key` required), service `PortfolioTransactionService`.

| Type | Input | Effect | Rules |
|---|---|---|---|
| Buy | stock, shares, price (stock currency), commission, date | holding +, cash − (price × shares × FX + commission) | positive values; enough cash (no margin); whole shares unless the portfolio allows fractions; foreign stock needs a dated reference rate |
| Sell | same | holding −, cash + (proceeds − commission); realized P&L = proceeds − commission − average cost × shares | cannot sell more than held (no short selling) |
| Deposit | amount (portfolio currency) | cash + | external flow |
| Withdrawal | amount | cash − | cannot exceed cash; external flow |
| Dividend | stock, net amount | cash + | stock must be or have been held; income (counts in return); no change to quantity or cost |
| Fee | amount | cash − | cannot exceed cash; reduces return |

* **Atomic:** the journal record, ledger entries, holding and cash change are saved in one database transaction; any
  validation error changes nothing (integration-tested). Concurrent changes fail with 409 (`Portfolio.Version`).
* **Idempotent:** repeating a request with the same key returns the original booking (`idempotentReplay: true`); the same
  key for a different transaction → 409; a unique index (portfolio, key) also catches races.
* **In time order:** a booking cannot be in the future or dated before the portfolio's latest booking, so the cash and
  quantity checks at booking time are valid for the effective date. Back-dated history (imports) is not supported.
* **Corrections, never edits:** deposits, withdrawals, dividends and fees can be reversed (`POST …/journal/{id}/reverse`):
  an opposite entry is appended, the original stays and is marked reversed; a reversal cannot be reversed or repeated.
  Trades are corrected by recording the opposite trade (reversing a fill would rewrite cost basis after later activity).
* **Money:** `decimal`, columns `numeric(18,6)`, amounts rounded to 6 decimals before they change cash so the ledger
  reconciles exactly (`CashBalance == Σ ledger`); FX rates `numeric(20,10)`.
* **Simulated:** recorded transactions are bookkeeping in a paper portfolio – no order is placed and no money moves.

## Valuation, P&L and performance

* **Total value** = cash + Σ quantity × latest stored price × FX rate of the price date. A position without a rate makes
  the total `null` ("cannot be valued"), never a partial sum.
* **Net contributions** = deposits − withdrawals. **Profit/loss** = total value − net contributions (includes realized and
  unrealized P&L, fees and recorded dividends). It is not a return: it ignores the timing of cash flows.
* **Performance (time-weighted):** daily value is rebuilt from the ledger and stored closes; `r_t = (V_t − F_t) / V_{t−1} − 1`
  where `F_t` are deposits and withdrawals of that day. Deposits are therefore never gains and withdrawals never losses;
  dividends and standalone fees are part of the return. A booking on a weekend/holiday takes effect on the next trading
  day. History starts at the first trade; nothing before the portfolio's own bookings is shown. Money-weighted return is not
  implemented.
* **No snapshots:** history is reconstructed deterministically from immutable inputs (ledger + stored prices), so a
  snapshot table would only duplicate it. Consequence: if stored prices are later corrected, history follows the
  corrected prices.
* **Backtests are not history:** backtests and in-sample run metrics are labelled as such and never mixed into the
  recorded performance of a portfolio.
* **Overview total:** each active portfolio's total converted with today's reference rate; shown only when every active
  portfolio can be valued and converted, otherwise "Not available" with the reason. No combined return is calculated.

## Optimizer integration

* The optimizer, holdings, transactions and performance of a portfolio live under `/portfolios/:id/…`; the id comes from
  the URL and every API call sends it, the server checks ownership on every request.
* Portfolio Creation stores the chosen stocks as the portfolio's universe (`CreatePortfolioRequest.Universe`, with a
  feasible default cap of at least 1/n per stock). The optimizer starts from it; "from=selection" can replace it explicitly.
* A run is stored with the settings snapshot, universe, window, data hash and currency of its portfolio. It never changes
  holdings; a proposal shows the trades; only "Confirm simulated execution" changes holdings and cash.
* Switching portfolio remounts the workspace sections, so form state, chosen runs and proposals of one portfolio cannot
  appear under another.
* Archived portfolios refuse runs, proposals, executions and bookings (422); pending proposals are cancelled on archive.

## AI research context

`StartResearchRequest.PortfolioId` (optional, owner-checked) attaches a session to a portfolio: its currency is the analysis
currency and a saved run is attached to it by default (still with the user's confirmation). Only the id and currency are
used – no holdings, names or descriptions are sent to a language model. Without a context the research is independent.
Existing sessions stay independent after the migration (their portfolio cannot be inferred reliably).

## API and authorization

All endpoints require an approved account and antiforgery for unsafe methods. Every portfolio endpoint resolves the
portfolio with `PortfolioService.LoadAsync` / `EnsureExistsAsync` (`Id == id && OwnerId == current user`); another user's id
behaves exactly like an unknown id (404). The user id comes from the authenticated identity, never from the request.

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/portfolios` | the caller's portfolios (active and archived) with valuation |
| GET | `/api/v1/portfolios/overview?currency=EUR` | same plus totals over active portfolios |
| POST | `/api/v1/portfolios` | create (name, description, currency, cash, universe, max weight) |
| PUT | `/api/v1/portfolios/{id}/details` | rename / describe |
| POST | `/api/v1/portfolios/{id}/archive`, `/restore` | lifecycle |
| DELETE | `/api/v1/portfolios/{id}` | only archived portfolios (409 otherwise); removes the whole history |
| GET/POST | `/api/v1/portfolios/{id}/journal` | list / record manual transactions |
| POST | `/api/v1/portfolios/{id}/journal/{tid}/reverse` | reverse a cash event |
| GET | `…/holdings`, `…/transactions` (executions), `…/cash-ledger`, `…/orders`, `…/performance`, `…/risk`, `…/optimization-runs`, `…/trade-proposals` | unchanged, owner-scoped |

Cross-user tests: `MultiPortfolioTests.AnotherUser_CannotReadOrChangeAnyPartOfAPortfolio` (every endpoint above),
`AuthTests.Users_CannotSeeOrChangeEachOthersData`, e2e `portfolios.spec.ts` (second user, URL and API).

## User journey and routes

| Route | Page |
|---|---|
| `/` | **My portfolios** (overview): cards with value, cash, invested, holdings, P/L, last optimization, last activity; totals; archived list (restore, delete) |
| `/stocks` | **Portfolio Creation**: details (name, description, currency, starting cash, advanced cap) → choose stocks → review → Create |
| `/portfolios/:id` | workspace: header (name, currency, value, cash, holdings, valuation date, warnings, switcher, Record transaction, rename, archive, research) and tabs Overview · Optimize · Holdings · Transactions · Performance · Optimization runs |
| `/portfolios/:id/optimize[/:runId]`, `/holdings`, `/transactions`, `/performance` | sections of that portfolio |
| `/optimize`, `/optimize/:runId`, `/holdings`, `/trades`, `/analytics` | old URLs: redirect into the most recently opened portfolio, or into the run's own portfolio |

Unknown, deleted or foreign portfolio ids show "Portfolio not found" without revealing which case applies.

## Migration `20261017113808_MultiPortfolio`

Additive only: `Portfolios.Description` (nullable), `Portfolios.ArchivedAtUtc` (nullable → existing portfolios are
active), `ResearchSessions.PortfolioId` (nullable FK, SET NULL), new table `PortfolioTransactions` (FK cascade with its
portfolio, restrict to assets, unique (portfolio, idempotency key), unique filtered `ReversesId`, check constraints on FX
rate and fees). No existing row is rewritten; ownership already existed (`Portfolios.OwnerId` NOT NULL, FK to users), so
no data is reassigned.

Rehearsal on a copy of the development database (2026-10-17): backup with `pg_dump -Fc` into
`../PortfolioOptimizer-backups/`, restored into a scratch database, migration applied twice: all counts identical before
and after (1 portfolio, 12 holdings, 20 executions, 41 cash entries, 17 runs, 105 run weights, 7 proposals, 3 research
sessions, cash reconciles with the ledger); the second run applied nothing; the application role can write the new table
(default privileges). The scratch database was dropped.

**Rollback:** the migration is reversible (`dotnet ef database update MultiCurrency`), which drops the journal table and the
new columns – manual transactions, descriptions and archive states recorded after the upgrade are lost (their cash effects
stay in the ledger, which is consistent on its own). Prefer restoring the pre-upgrade backup (see
[deployment.md](deployment.md#restore)) and redeploying the previous image.

## Status

| Feature | Status |
|---|---|
| Multiple private portfolios, overview, workspace, URLs, switcher, archive/restore/delete | implemented, tested |
| Portfolio Creation with universe and review | implemented, tested |
| Manual buy/sell/deposit/withdrawal/dividend/fee, idempotency, reversal of cash events | implemented, tested |
| Time-weighted performance with external flows, cash series | implemented, tested |
| Portfolio-aware AI research (currency, save target) | implemented, tested |
| Trade reversal, back-dated/imported history with duplicate detection | not implemented (record the opposite trade; no imports) |
| Money-weighted return, combined multi-portfolio return | not implemented (overview shows value and P/L only) |
| Backtests, ML experiments and strategy comparisons attached to a portfolio | not implemented: they stay per user and take their defaults from the most recently opened portfolio |
| AI questions about a portfolio's holdings ("why so much tech?") | planned: needs a validated holdings tool for the agent |
| Shared portfolios | out of scope (private only) |
