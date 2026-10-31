# Database

PostgreSQL 17 via EF Core 10 + Npgsql. Context: `src/PortfolioOptimizer.Infrastructure/Persistence/AppDbContext.cs`
(also the ASP.NET Core Identity and data-protection store). Application services use `IApplicationDbContext`.

## Entities

| Area | Tables (entity) | Notes |
|---|---|---|
| Identity | `AspNetUsers` (`ApplicationUser`), `AspNetRoles`, `AspNetUserRoles`, claims/logins/tokens | status Pending/Approved/Rejected/Suspended, display name, status history |
| Security | `AuditEvents`, `DataProtectionKeys` | audit is append-only (except the test-data purge, which is itself audited) |
| Catalogue | `Assets` | symbol (unique, canonical), name, sector, industry, country, MIC, ISIN, currency, type (Equity/Etf/Index), tracked flag, metadata source |
| Universes | `MarketIndices`, `IndexMemberships` | index metadata with source/revision/licence/verified date/content hash; membership = (index, asset) |
| Prices | `HistoricalPrices`, `LatestQuotes` | daily bars unique per (asset, date, source) in the asset's own currency; latest quote per (asset, source) incl. previous close |
| Exchange rates | `FxRates` | units of a currency per 1 EUR per (source, currency, date); checks rate > 0 and currency ≠ EUR; shared, fetched on demand. See [currency.md](currency.md) |
| Data jobs | `SyncJobs` + `SyncLogs`, `QuoteRefreshRuns` | history downloads with per-asset logs; quote refresh cycles (trigger, counts, message) |
| Settings | `Settings` | single row: active provider, defaults, staleness threshold |
| Paper trading | `Portfolios`, `Holdings`, `CashLedger`, `Executions`, `TradeProposals` (+ lines), `PaperTradeBatches`, `Orders` | `Portfolio.Version` is the concurrency token; cash, cost basis and P&L in the portfolio currency; executions/orders/lines keep the native price with currency, FX rate (`numeric(20,10)`, > 0), rate date and source |
| Analyses | `OptimizationRuns` (+ weights), `Backtests`, `MlExperiments`, `MlModelVersions`, `MlPredictionSets`, `StrategyComparisons` | inputs and results stored as `jsonb`; immutable after completion; `Currency` and `FxSource` columns record the analysis currency |
| Watchlist | `WatchlistEntries` | (owner, asset) unique; `Position` = the owner's order (0 = top; migration `WatchlistOrder` filled it oldest-first); cascade-deleted with the user; asset FK restrict. See [watchlist.md](watchlist.md) |
| News | `NewsArticles`, `NewsArticleAssets`, `NewsFetchStates` | shared; `Fingerprint` (canonical-URL SHA-256) unique, `HeadlineKey` indexed for syndication; links (article, asset) with match method; fetch state per source/query. Retention `News:RetentionDays` (120). See [financial-news.md](financial-news.md) |

Money and quantities are `numeric` (`decimal`); keys of user-facing records are application-generated GUIDs.

## Ownership and isolation

* User-owned: `Portfolios` (and everything under them), `OptimizationRuns`, `Backtests`, `MlExperiments`,
  `StrategyComparisons`, `WatchlistEntries` – each has `OwnerId` (FK to `AspNetUsers`, restrict). Every service query filters by the
  current user; another user's id returns 404. `MlExperiments`, `Backtests` and `StrategyComparisons` also carry an optional
  `PortfolioId` (FK, set null on portfolio deletion; migration `StrategiesPerPortfolio`, additive). Administrators see counts and run metadata on the admin overview,
  never other users' results.
* Shared: catalogue, universes, prices, quotes, settings, data jobs, news articles and their fetch state (never copied per user).
* **Not persisted:** the stock selection (browser memory only) and anything a page merely displays. A run stores its
  universe as part of its inputs when it is submitted; New portfolio stores it as the new portfolio's universe.
* Portfolio children: `Holdings`, `CashLedger`, `Executions`, `PortfolioTransactions` (manual journal; unique
  (portfolio, idempotency key)), `Orders`, `PaperTradeBatches`, `TradeProposals`, `OptimizationRuns` – all keyed by
  `PortfolioId` and removed when the portfolio is deleted (active or archived). `MlExperiments`, `BacktestRuns`,
  `StrategyComparisons` and `ResearchSessions` carry an optional `PortfolioId` (SET NULL in the schema);
  `PortfolioService.DeleteAsync` removes the linked rows (and ML artifacts) explicitly. Shared tables – assets, prices,
  quotes, FX – are never touched by a portfolio deletion. `Portfolios.ArchivedAtUtc` marks archived (read-only) portfolios. See [portfolios.md](portfolios.md).

## Migrations

`src/PortfolioOptimizer.Infrastructure/Persistence/Migrations`: `InitialPostgres`, `StockCatalogueMetadata`,
`MachineLearningExperiments`, `StrategyComparisons`, `MarketUniversesAndQuoteRefresh`, `UniverseContentHash`, `Watchlist`,
`FinancialNews` (the last two only add tables and indexes; no existing column changes), …, `MultiPortfolio` (additive:
portfolio description and archive date, research portfolio link, `PortfolioTransactions`).

```bash
set -a; . ./.env; set +a
ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=$POSTGRES_DB;Username=$POSTGRES_OWNER_USER;Password=$POSTGRES_OWNER_PASSWORD" \
  dotnet ef migrations add <Name> -p src/PortfolioOptimizer.Infrastructure -s src/PortfolioOptimizer.Api -o Persistence/Migrations
dotnet build   # the EF tool writes files after its own build
```

Applied by the `migrate` compose service (owner role) or, in Development, on API start. The API refuses to start on
an outdated schema. Roles: owner (DDL, migrations) vs. application role (DML only) – see [security.md](security.md).

## Seed data

On start (`DatabaseInitializer`, idempotent): settings row; the 11 default tracked instruments (empty database only);
the index universes from `Seed/universes/*.csv` (re-imported when a file's SHA-256 changes, see
[market-data.md](market-data.md)) and benchmark instruments; the bootstrap administrator (while none exists); a
"Demo Portfolio" for that administrator (`Seed:DemoPortfolio`); an initial history download for tracked instruments
when no prices exist.

## Test data isolation

| Kind | Where it lives | Protection |
|---|---|---|
| Integration tests | a fresh `po_it_*` database per test class on a disposable PostgreSQL (Testcontainers) or `PO_TEST_POSTGRES`, dropped afterwards | never touches the application database |
| End-to-end tests | a separate compose project `po-e2e` (own volumes and ports, `ASPNETCORE_ENVIRONMENT=E2E`, Mock data), removed afterwards by `scripts/e2e.sh` | Playwright refuses to run unless `/api/v1/system/test-environment` reports an E2E stack |
| Test accounts | e-mail under the reserved `.test` domain (RFC 6761), see `TestDataPolicy` | hidden from the admin user list by default, excluded from admin counts, refused at registration in Production, removable with `purge-test-data` |
| Demo data | the administrator's "Demo Portfolio" | created only for the bootstrap administrator; disable with `Seed__DemoPortfolio=false` |

`purge-test-data` (API command, dry run by default):

```bash
# Development stack (the container defaults to Production, where the command refuses unless --allow-production is given)
podman compose run --rm -e ASPNETCORE_ENVIRONMENT=Development migrate purge-test-data           # report only (dry run)
podman compose run --rm -e ASPNETCORE_ENVIRONMENT=Development migrate purge-test-data --apply   # delete in one transaction (audited)
```

Back up first ([deployment.md](deployment.md#backup)). The command exits with code 1 when it refuses.

It deletes `.test` accounts, everything they own (portfolios, runs, backtests, ML experiments with artifacts,
comparisons) and audit events they performed or that target them. Genuine accounts and their data are never touched.

## Backup and restore

See [deployment.md](deployment.md#backup). Back up the database **and** the model-artifact volume together.
