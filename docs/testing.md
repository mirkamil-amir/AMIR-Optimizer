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
