# Portfolio Optimizer – agent notes

Full-stack equity portfolio optimizer + paper trading. .NET 10 API (SQLite/EF Core) + React/Vite dashboard.
Local single-user, no auth, no real orders. Details live in `README.md` and `docs/` – read them only when needed.

## Environment (macOS dev machine)
- System `dotnet` is .NET 9. Use: `export PATH="$HOME/.dotnet:/opt/homebrew/opt/node@22/bin:$PATH" DOTNET_ROOT="$HOME/.dotnet"`
- Containers: Podman (`podman compose …`, uses docker-compose v2). Web http://localhost:8081, API http://localhost:5080/swagger
- Local dev ports: API 5080 (`dotnet run` in `src/PortfolioOptimizer.Api`), Vite 5173 (proxies `/api`).

## Layout
- `src/PortfolioOptimizer.Optimization` – pure math: metrics, covariance, `Solvers/AdmmQpSolver`, `Portfolio/MeanVarianceOptimizer`, `Backtesting/BacktestEngine`. No I/O.
- `src/PortfolioOptimizer.Domain` – entities; cash/holding accounting (`Portfolio.Buy/Sell`, weighted-average cost).
- `src/PortfolioOptimizer.Application` – services per area (MarketData, Portfolios, Optimization, Trading, Analytics, Backtesting), DTOs in `Dtos/`, `Common/Mapping.cs`.
- `src/PortfolioOptimizer.Infrastructure` – `Persistence/AppDbContext.cs` + `Migrations`, `DatabaseInitializer` (seed), `MarketData/*Provider.cs`, `Jobs/` (queue, worker, scheduler), `DependencyInjection.cs`.
- `src/PortfolioOptimizer.Api` – controllers, `Infrastructure/ApiExceptionHandler.cs`, `Program.cs`.
- `frontend/portfolio-dashboard/src` – `pages/*.tsx`, `components/{charts,common,data-table,proposal}.tsx`, `lib/{api,queries,types,format}.ts`.
- `tests/` – `UnitTests` (math/solver/accounting/providers), `IntegrationTests` (WebApplicationFactory + Mock provider, temp SQLite).

## Commands
- Backend: `dotnet build`, `dotnet test` (79 tests, ~15 s, no network).
- Frontend: `npm run build` (tsc + vite), `npm run lint` (oxlint) in `frontend/portfolio-dashboard`.
- Migration: `dotnet ef migrations add <Name> -p src/PortfolioOptimizer.Infrastructure -s src/PortfolioOptimizer.Api -o Persistence/Migrations`, then `dotnet build` again (tool builds before writing files).
- Containers: `podman compose up --build -d`; logs `podman compose logs api`.

## Conventions / invariants (don't break)
- Weights/rates are fractions; 252 trading days (`AnalyticsConventions`). Returns use adjusted close; trades/valuation use raw close.
- Never return arbitrary weights: optimizer results are verified against constraints; infeasible → explanation.
- Metrics return `MetricValue`/`MetricDto` (value or unavailable reason), never NaN.
- Never mix price sources; simulated (Mock) data is flagged `IsSimulated` and must show the SIMULATED badge.
- Money = `decimal`, math = `double`. Guid keys are `ValueGeneratedNever`. SQLite can't ORDER BY/aggregate decimals in SQL – do that client-side.
- Proposals never change holdings; paper execution needs `confirm: true` + `Idempotency-Key`; portfolio `Version` is the concurrency token.
- Errors: throw `ValidationException`/`NotFoundException`/`ConflictException`/`DomainException` → Problem Details.
- Frontend: shadcn/ui on **Base UI** (`render={<Link/>}` not `asChild`; Select needs `items` for labels). Mount forms only after data loads (use `defaultValues`) – otherwise Base UI controls go uncontrolled. Chart colours via `seriesColor(i)` / CSS vars `--series-N`, one y-axis, legend for ≥2 series, table view.
- Keep business logic out of controllers and React components.

## Gotchas
- `dotnet run --no-build` after adding a migration runs stale binaries → "no such table".
- A leftover local API on port 5080 blocks the container: `lsof -nP -iTCP:5080 -sTCP:LISTEN`.
- Yahoo endpoint is unofficial; Stooq is blocked by a bot challenge (don't try to bypass it). Alpha Vantage needs `MarketData__AlphaVantage__ApiKey`.

## Skills
- `/run-app` – start (Podman or local) and verify the stack.
- `/ship-change` – checklist after a code change (build, tests, migration, rebuild containers, docs).
