# Portfolio Optimizer

A full-stack equity portfolio optimization and **paper-trading** application: download historical prices, estimate
risk and return, solve constrained mean-variance problems, turn the result into a whole-share trade proposal,
execute it in a simulated portfolio, and track holdings, P&L, risk and benchmark-relative performance. Includes a
walk-forward backtester.

> Local, single-user analytics tool. **No real orders are ever placed. Not investment advice.**

| Layer | Stack |
|---|---|
| Backend | .NET 10, ASP.NET Core Web API (controllers), EF Core 10 + SQLite, Swagger UI (Swashbuckle), `Microsoft.Extensions.Http.Resilience`, MathNet.Numerics |
| Quant engine | Custom OSQP-style ADMM QP solver with active-set polishing, Ledoit–Wolf covariance, efficient frontier, backtester |
| Frontend | React 19 + TypeScript, Vite 8, Tailwind CSS v4, shadcn/ui (Base UI), TanStack Query, React Hook Form + Zod, Recharts, React Router |
| Tests | xUnit: 70 unit tests (math, solver, optimizer, backtest timing, accounting, providers) + 9 API integration tests |

Documentation: [architecture](docs/architecture.md) · [methodology & assumptions](docs/methodology.md) ·
[data sources](docs/data-sources.md) · [limitations & roadmap](docs/limitations.md)

## Quick start (local)

Prerequisites: **.NET 10 SDK** and **Node.js 20.19+ or 22+**.

```bash
# 1. Backend – http://localhost:5080 (Swagger UI at /swagger)
cd src/PortfolioOptimizer.Api
dotnet run
```

On first start the API applies EF Core migrations, creates `data/portfolio.db`, seeds an 11-asset universe
(AAPL, MSFT, NVDA, AMZN, GOOGL, META, JPM, JNJ, XOM, PG, SPY) and a **Demo Portfolio** with 100,000 USD of simulated
cash, and queues a 5-year price download from the active provider (Yahoo by default, no key needed).

```bash
# 2. Frontend – http://localhost:5173 (proxies /api to the backend)
cd frontend/portfolio-dashboard
npm install
npm run dev
```

### Offline / no-network demo

Use the deterministic simulated provider (clearly labelled **SIMULATED** throughout the UI):

```bash
MarketData__DefaultProvider=Mock dotnet run      # only affects a new database
```

or switch the provider at any time in **Settings** (each provider's data is stored separately and never mixed).

### Docker Compose

```bash
cp .env.example .env        # optionally set ALPHAVANTAGE_API_KEY
docker compose up --build
```

Dashboard: http://localhost:8081 · API/Swagger: http://localhost:5080/swagger · data persists in the
`portfolio-data` volume. (The compose setup was written but not run on the development machine – see limitations.)

## Configuration

Settings come from `appsettings.json`, environment variables (`Section__Key`) or user-secrets.

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings__Default` | `Data Source=data/portfolio.db` | SQLite database file |
| `MarketData__DefaultProvider` | `Yahoo` | Provider for a new database: `Yahoo`, `AlphaVantage`, `Mock` |
| `MarketData__AlphaVantage__ApiKey` | – | **Secret.** Use an env var or `dotnet user-secrets set "MarketData:AlphaVantage:ApiKey" "<key>"` in `src/PortfolioOptimizer.Api`. Never commit it. |
| `MarketData__AlphaVantage__UseAdjustedEndpoint` | `false` | Use the premium adjusted daily series |
| `MarketData__ScheduledRefreshEnabled` / `…IntervalHours` | `true` / `12` | Periodic incremental sync |
| `MarketData__Yahoo__MinRequestIntervalMs` | `500` | Politeness throttle |
| `Seed__Assets`, `Seed__DemoPortfolio`, `Seed__SyncOnEmptyDatabase` | `true` | First-start seeding |
| `Cors__AllowedOrigins__0` | `http://localhost:5173` | Allowed browser origins |
| `RateLimiting__PermitPerMinute` / `…ExpensivePermitPerMinute` | `600` / `30` | Per-IP limits (optimization, sync, backtest are "expensive") |
| `Swagger__Enabled` | `true` in Development | Expose Swagger UI |

## Walkthrough (definition of done)

1. **Overview** – portfolio value, cash, returns, ex-ante risk, recent runs/transactions, data freshness.
2. **Market data** – coverage per asset (observations, date range, adjusted-close availability, freshness), sync
   jobs with data-quality counts, add/disable assets, price chart (raw vs adjusted close).
3. **Settings → Portfolios** – create a portfolio with any initial investment.
4. **Optimize** – select universe, lookback, objective (min variance, max Sharpe, target return, max return for a
   volatility limit, equal weight) and constraints (weight bounds, max holdings, min position, cash reserve,
   turnover, sector caps, costs, fractional shares) → **Run optimization**.
5. Inspect allocation, efficient frontier, correlation heatmap, risk contributions, comparison with equal weight and
   the benchmark, in-sample curve, and the full inputs/diagnostics of the stored run. Infeasible settings produce an
   explanation instead of weights.
6. **Create trade proposal** – target vs. current weights, whole-share quantities, prices and dates, costs, cash after
   trades, and why weights deviate.
7. **Review & execute** → confirmation dialog → simulated fills; holdings and the immutable ledger update
   (**Holdings**, **Trades & ledger**).
8. **Analytics** – performance vs benchmark, drawdown, monthly returns, VaR/CVaR, beta, tracking error, risk
   contribution, allocation history. (Realized history grows with each daily close after the first trade.)
9. **Backtest** – monthly/quarterly re-optimization with costs vs buy-and-hold, equal weight and benchmark.
10. **Swagger UI** at http://localhost:5080/swagger documents and exercises every endpoint.
11. Restart the API – everything is persisted in SQLite.

## API overview (`/api/v1`)

| Area | Endpoints |
|---|---|
| Assets | `GET/POST /assets`, `PATCH /assets/{symbol}`, `GET /assets/{symbol}/prices?from&to&page&pageSize` |
| Market data | `POST /market-data/sync` (202), `GET /market-data/jobs/{id}`, `GET /market-data/status` |
| Settings | `GET/PUT /settings` |
| Portfolios | `GET/POST /portfolios`, `GET/PUT/DELETE /portfolios/{id}` |
| Optimization | `POST/GET /portfolios/{id}/optimization-runs`, `GET /optimization-runs/{runId}`, `…/allocation`, `GET …/trade-proposal`, `POST …/trade-proposals` |
| Trading | `GET /portfolios/{id}/holdings`, `/transactions`, `/orders`, `/cash-ledger`, `/trade-proposals`, `POST /portfolios/{id}/rebalance-preview`, `POST /portfolios/{id}/paper-trades` (requires `Idempotency-Key` header and `confirm: true`), `GET /trade-proposals/{id}` |
| Analytics | `GET /portfolios/{id}/performance?from&to`, `GET /portfolios/{id}/risk?lookbackDays&confidence` |
| Backtests | `POST /backtests` (202), `GET /backtests`, `GET /backtests/{id}` |
| Ops | `GET /health` (liveness), `GET /health/ready` (database) |

Errors use RFC 9457 Problem Details (`400` validation with field errors, `404`, `409` conflicts such as executing a
non-pending proposal, `422` business-rule violations, `502` provider failures, `429` rate limit).

## Updating

### After changing code (or pulling new commits)

**Containers (Podman or Docker)** – rebuild and restart; the database volume is kept and new EF Core migrations are
applied automatically on API start:

```bash
podman compose up --build -d          # or: docker compose up --build -d
podman compose logs -f api            # watch start-up / migrations / sync jobs (Ctrl+C to stop)
curl -s localhost:5080/health/ready   # → Healthy
```

Rebuild only one side: `podman compose up --build -d api` or `… web`.

**Local development** – stop `dotnet run` / `npm run dev`, then:

```bash
dotnet build && dotnet test                         # repo root
cd src/PortfolioOptimizer.Api && dotnet run          # migrations apply on start
cd frontend/portfolio-dashboard && npm install && npm run dev
```

### Changing the database schema

1. Edit the entity in `src/PortfolioOptimizer.Domain` and its mapping in `Infrastructure/Persistence/AppDbContext.cs`.
2. Create a migration (from the repo root), **then build again** – the EF tool builds before writing the files:
   ```bash
   dotnet tool restore
   dotnet ef migrations add <Name> -p src/PortfolioOptimizer.Infrastructure -s src/PortfolioOptimizer.Api -o Persistence/Migrations
   dotnet build
   ```
3. Restart the API (or rebuild the `api` container). Migrations run automatically.

### Updating dependencies

```bash
dotnet list package --outdated                       # then: dotnet add <project> package <Name> -v <version>
cd frontend/portfolio-dashboard && npm outdated && npm update
npx shadcn@latest add <component>                    # add/refresh UI components (Base UI flavour)
```

Run `dotnet test` and `npm run build` afterwards.

### Resetting data

| What | Command |
|---|---|
| Container database (all portfolios, prices) | `podman compose down -v` then `podman compose up -d` |
| Local database | stop the API, delete `src/PortfolioOptimizer.Api/data/` |
| Re-download prices only | Market data page → *Sync all enabled* (or per-asset *Refresh*) |

### Stopping

`podman compose down` (keeps data) · `podman compose down -v` (deletes data).

## Testing

```bash
dotnet test                                   # unit + integration tests (integration uses the mock provider, no network)
cd frontend/portfolio-dashboard && npm run build   # type-check + production build
```

Unit tests verify results against closed-form solutions (two-asset and interior GMV, tangency portfolio), metric
definitions on hand-computed series, infeasibility detection, constraint satisfaction, look-ahead-free backtest
timing, weighted-average-cost accounting, whole-share/fee/cash arithmetic, data normalization and provider parsing.

## Database & migrations

Migrations live in `src/PortfolioOptimizer.Infrastructure/Persistence/Migrations` and are applied automatically on
start. To add one:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/PortfolioOptimizer.Infrastructure -s src/PortfolioOptimizer.Api -o Persistence/Migrations
```

Reset the local database by stopping the API and deleting `src/PortfolioOptimizer.Api/data/`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Dashboard shows "The API is unreachable" | Start the backend on port 5080 (or set `VITE_API_PROXY_TARGET`). |
| Sync job fails with HTTP 429 / "Rate limited" | Yahoo throttling – wait and click *Sync all enabled* again (downloads are incremental). Increase `MarketData__Yahoo__MinRequestIntervalMs`. |
| Yahoo returns "not valid JSON" | The unofficial endpoint changed or served a bot check; switch to `Mock` or Alpha Vantage. |
| Alpha Vantage greyed out | Set `MarketData__AlphaVantage__ApiKey` on the server and restart. |
| Optimization "Infeasible" | Read the explanation – typically max weight × assets < 100 % − cash, sector caps, or a target above the achievable return. |
| "No portfolio … beats the risk-free rate" | Max Sharpe is undefined; lower the risk-free rate or choose minimum variance. |
| Performance charts empty | Realized history begins after the first execution and grows daily; use Backtest for history. |
| `dotnet` reports a missing SDK | Install the .NET 10 SDK (`global.json` accepts any 10.0.x feature band). |
