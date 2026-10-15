# AMIR Optimizer – Portfolio Research Platform

An educational portfolio-research platform. An **AI research assistant** turns an investment objective into an
editable research brief, researches a universe with evidence, runs the real optimizer and risk review, explains the
results only with numbers it can show, and lets students refine and compare alternatives before deliberately saving
one (works without any language model; see [agentic architecture](docs/agentic-architecture.md)). Students browse ~900 stocks from five markets (S&P 500, DAX 40 and the
historical DAX 30, CAC 40, FTSE 100, Nikkei 225) with automatically refreshed, clearly labelled prices, build a
temporary selection, follow stocks on a personal **watchlist** with price history and company news, read
**financial news** from attributed sources (with an event and price-movement comparison that makes no causal claims),
optimize a selection with **classical mean-variance optimization** and challenge the result with the **Portfolio
Reality Check** (concentration, risk shares, stress correlation, sensitivity, costs), train and evaluate
**machine-learning** ranking models with strictly chronological validation, compare both approaches against equal
weight and a benchmark on **one shared walk-forward simulator**, and act on the results in a **simulated (paper)
portfolio**. Every strategy has a 12-part explanation in the built-in **strategy guide**, and every result comes with
an interpretation generated from its own numbers.

> Research and paper trading only. **No real orders are ever placed. Nothing here is investment advice**, and no
> result in this application demonstrates that a strategy is profitable. Backtests are hypothetical and subject to
> survivorship bias (see [limitations](docs/limitations.md)).

| Layer | Stack |
|---|---|
| Backend | .NET 10, ASP.NET Core Web API, EF Core 10 + **PostgreSQL 17** (Npgsql), ASP.NET Core Identity (cookie sessions), Swagger UI |
| Quant engine | Custom ADMM QP solver, Ledoit–Wolf covariance, efficient frontier, shared portfolio simulator |
| ML engine | Ridge and logistic regression (in-house, closed form / IRLS), random forest and gradient boosting (ML.NET FastForest / FastTree) |
| AI research | Deterministic orchestrator + typed tools; optional Claude (official Anthropic C# SDK) for validated brief/refinement/narrative proposals |
| Frontend | React 19 + TypeScript, Vite 8, Tailwind CSS v4, shadcn/ui on Base UI, TanStack Query, Recharts |
| Market data | Yahoo Finance (default; quotes in batches of 20), Alpha Vantage (optional, free key), Mock (simulated, tests) |
| Tests | xUnit: 195 unit + 40 ML + 89 integration (real PostgreSQL) · Vitest: 65 · Playwright: 21 on a disposable stack · opt-in live provider smoke tests |

Documentation: [AGENTS.md](AGENTS.md) (start here when changing code) · [architecture](docs/architecture.md) ·
[agentic research architecture](docs/agentic-architecture.md) · [threat model](docs/threat-model.md) ·
[agent evaluation](docs/agent-evaluation.md) ·
[database](docs/database.md) · [market data](docs/market-data.md) · [quantitative strategies](docs/quantitative-strategies.md) ·
[machine learning & strategy comparison](docs/machine-learning.md) · [security](docs/security.md) ·
[testing](docs/testing.md) · [deployment, backup & restore](docs/deployment.md) ·
[implementation status](docs/implementation-status.md) · [decisions](docs/decisions.md) ·
[limitations](docs/limitations.md) · [watchlist](docs/watchlist.md) · [financial news](docs/financial-news.md) ·
[portfolio reality check](docs/portfolio-reality-check.md) · [roadmap](docs/roadmap.md) ·
[launch readiness & regulatory questions](docs/launch-readiness.md) · [audit (first upgrade)](docs/audit.md) · [report (first upgrade)](docs/final-report.md)

## Quick start (containers)

Prerequisites: Podman (with `podman compose`) or Docker Compose v2.

```bash
cp .env.example .env          # then replace EVERY placeholder password in .env
podman compose up --build -d  # or: docker compose up --build -d
```

| URL | What |
|---|---|
| http://localhost:8081 | Dashboard (nginx serves the SPA and proxies `/api`) |
| http://localhost:5080/swagger | Swagger UI (Development environment only) |
| http://localhost:5080/health/ready | Readiness (database) |

Sign in with `ADMIN_EMAIL` / `ADMIN_PASSWORD` from `.env`. That administrator is created on first start only while
no administrator exists; later changes to these variables are ignored.

What starts: `db` (PostgreSQL, data in the `pgdata` volume) → `migrate` (applies EF Core migrations as the owner
role, then exits) → `api` (connects as the least-privilege app role; trained models in the `model-artifacts` volume)
→ `web`. First start seeds the 503-stock catalogue, the administrator, a demo portfolio, and queues a 5-year price
download for the 11 default symbols from Yahoo.

## Quick start (local development)

Prerequisites: .NET 10 SDK, Node.js 20.19+ or 22+, and a PostgreSQL 15+ server. The compose database works:

```bash
set -a; . ./.env; set +a
podman compose up -d db

# API – http://localhost:5080 (applies migrations on start in Development)
cd src/PortfolioOptimizer.Api
ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=$POSTGRES_DB;Username=$POSTGRES_OWNER_USER;Password=$POSTGRES_OWNER_PASSWORD" \
Admin__Email="$ADMIN_EMAIL" Admin__Password="$ADMIN_PASSWORD" dotnet run

# Frontend – http://localhost:5173 (proxies /api to :5080)
cd frontend/portfolio-dashboard && npm install && npm run dev
```

Instead of environment variables you can keep the connection string in user-secrets
(`dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<…>"` in `src/PortfolioOptimizer.Api`).
Local development uses the owner role so `Database:MigrateOnStartup` (true in Development) can create the schema.

Offline demo: start with `MarketData__DefaultProvider=Mock` on a new database, or switch the provider under
**Admin settings**. Simulated data is flagged and shows a **SIMULATED** badge everywhere it is used.

## Using the platform

0. **AI research** (`/research`, first in the navigation). Write an objective ("low-risk US stocks without energy,
   at most 20 % per stock"). The brief separates what you said from what was assumed (badges) and asks only for what
   blocks the research (the market). The shortlist shows the criterion and the evidence for every inclusion and
   exclusion. "Run analysis" calculates your method and an equal-weight baseline with the real optimizer, reviews them
   (concentration, sensitivity, in/out-of-sample) and explains them in seven sections. Questions ("why is MSFT so
   large?") are answered from the computed values; changes ("cap at 15 %", "exclude JPM") are shown as an exact proposal
   and only recalculated after you confirm. Compare any two hypotheses; "Save as optimization run" (with confirmation)
   creates a normal run you can reopen, trade on paper or check further. Every tool call is in the activity log.
1. **Sign in / apply.** New users apply on `/register`; their account stays *Pending* until an administrator
   approves it under **Users**. Pending, rejected and suspended accounts can sign in but only see their status page.
2. **Stock selection.** Navigate market → index → sector, search by company or ticker, sort. Every sign-in starts a
   shared background price refresh for all markets; the page shows cached prices immediately and refreshes the visible
   ones. Each price shows its currency, kind (*Delayed quote* / *Last close*, never "live"), when it applies, when it was
   retrieved and whether it is cached, stale or SIMULATED; a refresh icon updates a single stock. Select stocks across
   sectors – the selection lives **only in browser memory** (not saved, survives navigation, discarded on reload or
   sign-out). Fetch price history for selected stocks from the selection tray. Analyses need stocks of one currency.
3. **Optimize** (classical). The selection becomes the universe. Choose an objective and constraints, run, inspect
   allocation, frontier, risk contributions and diagnostics, then create a trade proposal for the paper portfolio.
4. **Machine learning.** Choose a model family, target (forward return, forward excess return, probability of
   outperformance, forward volatility), horizon and portfolio-construction rules. The experiment runs in the
   background: chronological train/validation/test split with purging, hyperparameter search on validation,
   one evaluation on the untouched test period with naive baselines, explainability, current predictions, an ML
   portfolio and a walk-forward backtest. Every model version is stored with a checksummed artifact.
5. **Strategy comparison.** Pick an optimization run and an ML experiment. Both are re-run walk-forward with their
   stored settings on identical dates, universe, capital, costs and rebalancing next to equal weight and the
   benchmark (optionally a hybrid). Saved comparisons are immutable.
6. **Paper portfolio.** Proposals never change holdings; executing requires an explicit confirmation. Holdings,
   ledger, analytics and backtests work as before, but every portfolio, run and backtest now belongs to its owner.
7. **Learn.** The strategy guide explains every optimization objective, every ML model, ML portfolio construction and
   strategy comparison in 12 sections (theory, mathematics, worked example, caveats …); a glossary covers the terms.
   Result pages show "What your result means", labelled as calculated facts, estimates, interpretations and cautions.
8. **Administration.** The admin overview shows accounts, analysis activity and market-data health (provider status,
   refresh cycles, coverage per index). Approve, reject, suspend and reactivate users, grant the administrator role,
   review the audit trail, change settings, import a catalogue or replace an index's constituents (CSV).

The header shows the signed-in user's name at the top left; administrators are labelled **Admin Investor**.

## Configuration

Settings come from `appsettings*.json`, environment variables (`Section__Key`) or user-secrets. Secrets never belong
in `appsettings*.json` or in git.

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | – (required) | PostgreSQL connection string. **Secret.** The API refuses to start without it. |
| `Database__MigrateOnStartup` | `false` (`true` in Development) | Apply migrations on API start. In production run `PortfolioOptimizer.Api migrate` with the owner role instead. |
| `Admin__Email`, `Admin__Password` | – | Bootstrap administrator, used only while no administrator exists. **Secret.** |
| `Authentication__SessionHours` | `8` | Sliding cookie session lifetime |
| `ModelArtifacts__RootPath` | `data/model-artifacts` (`/app/artifacts` in the image) | Directory for trained model files; back it up with the database |
| `ReverseProxy__TrustForwardedHeaders` | `false` | Trust `X-Forwarded-*` (only behind your own proxy, as in compose) |
| `MarketData__DefaultProvider` | `Yahoo` | Provider for a new database: `Yahoo`, `AlphaVantage`, `Mock` |
| `MarketData__AlphaVantage__ApiKey` | – | **Secret.** Only for the Alpha Vantage provider |
| `MarketData__ScheduledRefreshEnabled` / `…IntervalHours` | `true` / `12` | Periodic incremental price sync |
| `Seed__Assets`, `Seed__DemoPortfolio`, `Seed__SyncOnEmptyDatabase` | `true` | First-start seeding (set `Seed__DemoPortfolio=false` before a SQLite import) |
| `Cors__AllowedOrigins__0` | `http://localhost:5173` | Allowed browser origins |
| `RateLimiting__PermitPerMinute` / `…ExpensivePermitPerMinute` / `…AuthenticationPermitPerMinute` | `600` / `30` / `10` | Global per IP / expensive jobs per user / sign-in, registration and password change per IP |
| `Swagger__Enabled` | `true` in Development | Expose Swagger UI |
| `MarketData__Quotes__FreshForMinutes` / `…CycleMinIntervalMinutes` | `15` / `15` | Quote cache window / minimum time between sign-in refresh cycles |
| `MarketData__Quotes__RefreshOnSignIn` | `true` | Start a quote refresh cycle when an approved user signs in |
| `MarketData__AlphaVantage__DailyRequestLimit` | `25` | Client-side daily quota for Alpha Vantage (free tier) |
| `Universes__Directory` | – | Folder with `indices.csv` / `<CODE>.csv` overriding the bundled constituent files |
| `RateLimiting__QuotePermitPerMinute` | `60` | On-demand quote refreshes per user |
| `Agent__Llm__Provider` | `None` | Research assistant language model: `None` (deterministic rules only), `Anthropic`, `Mock` (tests) |
| `Agent__Llm__Model` / `…ApiKey` / `…TimeoutSeconds` / `…MaxRetries` / `…Effort` | `claude-opus-5-5` / – / `60` / `2` / `low` | Model settings; the key (**secret**, or `ANTHROPIC_API_KEY`) stays on the server |

## API overview (`/api/v1`)

Every endpoint except `auth/*` and `/health*` requires an approved account; `admin/*` and catalogue/settings writes
require the Administrator role. State-changing requests need the antiforgery header `X-XSRF-TOKEN` (the value of the
readable `XSRF-TOKEN` cookie; `GET /auth/antiforgery` issues one). Swagger UI sends it automatically.

| Area | Endpoints |
|---|---|
| Auth | `GET /auth/antiforgery`, `POST /auth/register`, `POST /auth/login`, `POST /auth/logout`, `GET /auth/me`, `POST /auth/change-password` |
| Administration | `GET /admin/overview`, `GET /admin/users?includeTestAccounts`, `GET /admin/users/{id}`, `PUT /admin/users/{id}/status`, `PUT /admin/users/{id}/roles`, `GET /admin/audit-events`, `POST /admin/catalogue/import`, `POST /admin/catalogue/universes/{code}`, `POST/GET /admin/catalogue/quote-refresh` |
| Stock selection | `GET /markets`, `GET /sectors?country&index`, `GET /stocks?country&index&sector&search&sort&withPrices&symbols&page`, `GET /stocks/{symbol}`, `…/quote`, `…/prices`, `POST /stocks/quotes/refresh`, `POST /stocks/{symbol}/quote/refresh`, `GET /market-data/quote-refresh/latest` |
| Machine learning | `GET /ml/models`, `POST/GET /ml/experiments`, `GET/DELETE /ml/experiments/{id}`, `…/metrics`, `…/backtest`, `…/models`, `GET/POST …/predictions` |
| Strategy comparison | `POST/GET /strategy-comparisons`, `GET/DELETE /strategy-comparisons/{id}` |
| AI research | `GET/POST /research`, `GET/DELETE /research/{id}`, `PUT …/brief`, `PUT …/universe`, `POST …/evaluate`, `POST/DELETE …/refinements`, `POST …/refinements/{proposalId}/apply`, `GET …/compare?a&b`, `POST …/save` (requires `confirm: true`), `GET …/companies/{symbol}` |
| Market data & assets | `GET/POST /assets`, `PATCH /assets/{symbol}`, `POST /market-data/sync`, `GET /market-data/jobs/{id}`, `GET /market-data/status` |
| Portfolios, optimization, trading, analytics, backtests | as before: `/portfolios/…`, `/optimization-runs/…`, `/trade-proposals/…`, `/backtests/…` (all owner-scoped) |
| Settings | `GET /settings` (all users), `PUT /settings` (administrators) |

Errors use RFC 9457 Problem Details (`400`, `401`, `403`, `404`, `409`, `422`, `429`, `502`).

## Testing

```bash
./scripts/test.sh                       # all .NET tests; disposable PostgreSQL via Testcontainers
cd frontend/portfolio-dashboard && npm test && npm run build && npm run lint
./scripts/e2e.sh                        # Playwright on a disposable, isolated stack (Mock data), removed afterwards
PO_LIVE_SMOKE=1 ALPHAVANTAGE_API_KEY=… dotnet test tests/PortfolioOptimizer.UnitTests --filter LiveProviderSmokeTests   # opt-in
```

Automated tests never touch the application database and need no API key. Setup for IDEs (Testcontainers with
Podman), what each suite covers and the latest results: [docs/testing.md](docs/testing.md).

## Operations

- **Update after code changes:** `podman compose up --build -d` – `migrate` applies new migrations before the API
  starts. Locally: `dotnet build`, restart `dotnet run` (Development migrates on start).
- **Add a migration:** `dotnet ef migrations add <Name> -p src/PortfolioOptimizer.Infrastructure -s src/PortfolioOptimizer.Api -o Persistence/Migrations`, then `dotnet build`.
- **Backup & restore, SQLite import, production hardening:** see [deployment](docs/deployment.md).
- **Reset everything (containers):** `podman compose down -v` deletes the database **and** the model artifacts.

## Troubleshooting

| Symptom | Fix |
|---|---|
| API exits: "Connection string 'DefaultConnection' is not configured" | Set the connection string (environment variable or user-secrets). |
| API exits: "The database schema is out of date" | Run `podman compose run --rm migrate` (or enable `Database__MigrateOnStartup` locally). |
| Sign-in fails with "Too Many Requests" | Sign-in rate limit (10/min per IP); wait a minute. Accounts lock for 15 min after 5 failed passwords. |
| `400 Antiforgery token validation failed` from scripts | Fetch `GET /api/v1/auth/antiforgery` with the session cookie and send `X-XSRF-TOKEN`. |
| Container API does not start, port 5080 busy | A local `dotnet run` is still listening: `lsof -nP -iTCP:5080 -sTCP:LISTEN`. |
| "Model artifact unavailable" | The artifact file is missing (e.g. the `model-artifacts` volume was not restored). Re-run the experiment. |
| ML experiment fails "not enough samples" | Select more stocks (≥ 5) with longer price history, or shorten the horizon. |
| Yahoo sync fails (429 / not JSON) | Throttling or endpoint change; retry later or use Mock / Alpha Vantage. |
| Node tooling is extremely slow on macOS | The repo is in an iCloud-synced folder and `node_modules` files were offloaded. Keep `node_modules` as a symlink to `node_modules.nosync` (docs/testing.md) or move the repo out of `~/Desktop`/`~/Documents`. |
| A stock shows "No price available" | The provider has no recent trade (e.g. delisted members of the historical DAX 30) or does not cover the market (Alpha Vantage: Tokyo). Nothing is filled in. |
| "The selection mixes currencies" | Analyses use one currency (no FX conversion); analyse each market separately. |
| Playwright refuses to run | It only runs against a disposable E2E stack – use `./scripts/e2e.sh`. |
