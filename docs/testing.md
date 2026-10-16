# Testing

All automated tests use deterministic data (the Mock provider or synthetic markets). None needs an API key, network
access or live market hours. Real-provider checks are separate and opt-in.

| Suite | Project / folder | What it covers | Command |
|---|---|---|---|
| Unit | `tests/PortfolioOptimizer.UnitTests` | optimizer math against closed forms, solver, metrics, backtest timing, accounting, provider parsing (Yahoo spark batches, pence conversion, retry/fallback, Alpha Vantage symbol mapping and daily quota), universe files, symbol mapping per market | `dotnet test tests/PortfolioOptimizer.UnitTests` |
| ML | `tests/PortfolioOptimizer.MachineLearning.Tests` | features and labels on hand-computed series, purged splits, leakage tests, model determinism, artifacts, architecture rules | `dotnet test tests/PortfolioOptimizer.MachineLearning.Tests` |
| Integration | `tests/PortfolioOptimizer.IntegrationTests` | the real API on real PostgreSQL: auth/approval/roles, owner isolation, stock discovery, universes, sign-in quote refresh, page/single refresh, currency rules, ML, comparisons, SQLite import, test-data purge, admin overview | `./scripts/test.sh` (all .NET suites) |
| Frontend unit | `frontend/portfolio-dashboard/src/**/*.test.ts(x)` (Vitest + Testing Library) | API client (antiforgery, retries), selection (memory only), route guards, header identity ("Admin Investor"), quote states, interpretations, worked examples, strategy-guide structure, components | `npm test` |
| End-to-end | `frontend/portfolio-dashboard/e2e` (Playwright) | sign-in, registration + approval, roles, personalized header, automatic prices + single refresh, markets/indices, selection → optimization with interpretation, strategy guide, glossary, admin overview, sign-out | `./scripts/e2e.sh` |
| Live smoke (opt-in) | `LiveProviderSmokeTests` in the unit project | one stock per market from real Yahoo; Alpha Vantage US + Xetra (2 of 25 daily requests) | `PO_LIVE_SMOKE=1 ALPHAVANTAGE_API_KEY=… dotnet test tests/PortfolioOptimizer.UnitTests --filter LiveProviderSmokeTests` |

Setup once per shell: `export PATH="$HOME/.dotnet:/opt/homebrew/opt/node@22/bin:$PATH" DOTNET_ROOT="$HOME/.dotnet"`.

## Integration tests and PostgreSQL

Each test class creates its own database (`po_it_<guid>`) and drops it afterwards. The server comes from:

* `PO_TEST_POSTGRES` – an admin connection string to an existing server (e.g. the compose database; the application
  database is never touched), or
* Testcontainers – a disposable `postgres:17-alpine` container. With Podman, `scripts/test.sh` points Testcontainers
  at the Podman socket; IDEs (Rider, VS Code) need `~/.testcontainers.properties`:

  ```properties
  docker.host=unix:///var/folders/…/podman/podman-machine-default-api.sock   # podman machine inspect --format '{{.ConnectionInfo.PodmanSocket.Path}}'
  ryuk.disabled=true
  ```

  Without either, every integration test fails with "Integration tests need PostgreSQL".

Integration tests disable the sign-in refresh cycle (`MarketData:Quotes:RefreshOnSignIn=false`) except in
`MarketDataRefreshTests`, which enables it to test it (`ApiFactory.CreateAsync(refreshOnSignIn: true)`).

## End-to-end tests: always on a disposable stack

`./scripts/e2e.sh` builds and starts a separate compose project (`po-e2e`: web 18081, API 15080, database 15432,
random passwords, `ASPNETCORE_ENVIRONMENT=E2E`, Mock provider, administrator `admin@e2e.test`), waits for readiness,
runs Playwright and removes the stack and its volumes (`KEEP_E2E_STACK=1` keeps it for debugging). Extra arguments go
to Playwright, e.g. `./scripts/e2e.sh e2e/journeys.spec.ts --headed`.

The Playwright setup step first calls `/api/v1/system/test-environment` and **refuses to run** unless the target is an
E2E stack. This prevents the problem found in the 2026-10-14 audit, where e2e runs against the development stack left
11 `@e2e.test` accounts in the administrator's view. (`E2E_ALLOW_NON_TEST_TARGET=1` overrides the check – don't.)

Test accounts use the reserved `.test` domain; see [database.md](database.md#test-data-isolation) for how they are
hidden and purged.

## Writing tests

* Financial math: closed-form or hand-computed expectations (unit tests). Never assert on live data.
* ML: anything that uses time must prove it cannot see the future (change data after *t*, assert nothing at *t* moves).
* API behaviour: `IntegrationTests` with `ApiFactory`; use `CreateApprovedUserAsync` for a second user and assert
  that other users' ids return 404.
* UI logic belongs in `src/lib/*.ts` (pure, Vitest-tested); components get light Testing Library tests; user journeys
  get Playwright specs with role-based locators.
* Frontend fixtures: build typed objects; cast partial fixtures with `as unknown as T` only in tests.

## Results (2026-10-16, currency converter, brand and themes)

| Suite | Result |
|---|---|
| Unit | 241 passed, 8 skipped (live smoke) – adds `ObservedSeries` |
| ML / Integration | 40 / 107 passed – adds `FxHistoryAndBoard_ShowPublishedRates_WithChanges` |
| Frontend unit (Vitest) | 78 passed – adds `currency-converter.test.tsx` (3), `theme.test.tsx` (3), currency helpers (3) |
| End-to-end | 25 passed – adds `converter-themes.spec.ts` (converter on both pages, calculator, chart range; title, logo, dark mode + skin persisting across reload) |
| Bug found and fixed | gross amounts of foreign trades were stored rounded to 6 decimals but cash was reduced by the unrounded amount (≤ 0.000001 ledger drift); amounts are now rounded before they change cash |

## Results (2026-10-16, multi-currency portfolios)

| Suite | Result |
|---|---|
| Unit | 240 passed, 7 skipped (live smoke) – adds `Fx/FxTests.cs` (FX table, converter, ECB parser, mock: 19) and `Optimization/AllocationValidatorTests.cs` (9) |
| Live smoke | `PO_LIVE_SMOKE=1 --filter LiveEcbSmokeTests`: 1 passed – real ECB rates for USD, GBP, JPY, CHF parsed as units per EUR |
| ML | 40 passed |
| Integration | 106 passed – adds `MultiCurrencyTests` (12: EUR/CHF/JPY portfolios, mixed optimization, USD vs EUR results, simulated exchange reconciliation, reporting currency, performance with daily rates, dated historical conversion identity, backtest/ML currency, FX endpoints, listing currency) and `MissingFxTests` (4: exclusion with reason, null totals, proposal "no exchange rate", 422, domain rejection) |
| Frontend unit (Vitest) | 69 passed (adds `lib/currency.test.ts`: 4) |
| End-to-end | 22 passed incl. `multi-currency.spec.ts` (EUR portfolio with USD/EUR/JPY/GBP stocks: create → optimize → proposal → simulated execution → holdings in EUR and USD); desktop screenshots reviewed with a throwaway spec (deleted) |
| Migration | applied to a restored copy of the development database (5 runs, 9 executions, 19 proposal lines preserved and backfilled) |
| Lint / build | 0 errors / OK |

## Results (2026-10-15, AI research assistant)

| Suite | Result |
|---|---|
| Unit | 195 passed, 5 skipped (live smoke) – adds `Research/ResearchRulesTests.cs`: 32 |
| ML | 40 passed |
| Integration | 89 passed – adds `ResearchAgentTests`: 16 (journey + evaluation cases, see [agent-evaluation.md](agent-evaluation.md)) |
| Frontend unit (Vitest) | 65 passed (adds `lib/research.test.ts`: 3) |
| End-to-end | 23 passed incl. `research-agent.spec.ts` (2) and two throwaway screenshot tests (desktop 1440 / phone 390, no horizontal scroll; spec deleted afterwards) |
| Lint / build | 0 errors / OK |
| Live language model | **not run** – no API key available; provider covered only with the scripted mock |

Note: `scripts/e2e.sh` failed three times in a row while the container build context was archived (`read …: operation
canceled` on varying files of the iCloud-synced worktree); a later rerun succeeded. Rerun when this happens.

## Results (2026-10-15, stock-detail panel)

| Suite | Result |
|---|---|
| Unit | 163 passed, 5 skipped (live smoke) |
| Live smoke (`PO_LIVE_SMOKE=1 --filter LiveProfileSmokeTests`) | 1 passed: real Wikipedia descriptions for AAPL, SAP, Toyota (de→en) |
| ML | 40 passed |
| Integration | 73 passed (adds `CompanyProfileTests`: 4) |
| Frontend unit (Vitest) | 62 passed (adds `stock-detail-dialog.test.tsx`: 8) |
| End-to-end | 19 passed (adds `stock-detail.spec.ts`: 2) |
| Lint / build | 0 errors / OK |

## Results (2026-10-15)

| Suite | Result |
|---|---|
| Unit | 148 passed, 4 skipped (live smoke) – adds price-history ranges, news rules/parsers, event comparison, diversification math |
| Live smoke (`PO_LIVE_SMOKE=1`, news only) | 2 passed: Yahoo news for AAPL (tagged, dated) and all four central-bank RSS feeds parsed |
| ML | 40 passed |
| Integration (Testcontainers PostgreSQL) | 69 passed – adds `WatchlistTests` (6), `NewsTests` (4), `NewsProviderFailureTests` (1), `RealityCheckTests` (3) |
| Frontend unit (Vitest) | 54 passed |
| End-to-end (`scripts/e2e.sh`) | 17 passed (adds journey D watchlist, journey E news, journey F reality check) |
| Lint (oxlint) | 0 errors (warnings: existing `only-export-components` pattern) |
| Builds | `dotnet build` 0 errors; `npm run build` OK; a clean checkout builds after the ignore-rule fix |

**Container builds with Docker Desktop's CLI config.** If `~/.docker/config.json` contains `"credsStore": "desktop"`
while Docker Desktop is not running, `podman compose up --build` hangs in `docker-credential-desktop list`. Run with an
empty config: `mkdir -p /tmp/dcfg && echo '{"auths":{}}' > /tmp/dcfg/config.json && DOCKER_CONFIG=/tmp/dcfg ./scripts/e2e.sh`.

## Results (2026-10-14)

| Suite | Result |
|---|---|
| Unit | 104 passed, 2 skipped (live smoke) |
| Live smoke (`PO_LIVE_SMOKE=1`) | 2 passed (Yahoo all markets, Alpha Vantage US + Xetra) |
| ML | 40 passed |
| Integration (Testcontainers PostgreSQL) | 55 passed |
| Frontend unit (Vitest) | 50 passed |
| End-to-end (`scripts/e2e.sh`) | 15 passed (1 setup incl. environment guard + 14 specs) |
| Lint (oxlint) | 0 errors |

## macOS note: iCloud and node_modules

If the repository lives in an iCloud-synced folder (`~/Desktop`, `~/Documents`), iCloud offloads `node_modules` files
and Node tools hang. The frontend keeps its packages in `node_modules.nosync` (never synced) with a `node_modules`
symlink; Vite is configured with `preserveSymlinks` and ignores the folder in its watcher. After `npm ci` (which
replaces the symlink with a folder) restore it:

```bash
cd frontend/portfolio-dashboard
rm -rf node_modules.nosync && mv node_modules node_modules.nosync && ln -s node_modules.nosync node_modules
```

Better: keep the repository outside synced folders.
