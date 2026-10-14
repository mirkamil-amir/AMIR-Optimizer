# Deployment, backup & restore

## Container stack

`docker-compose.yml` (works with `podman compose` and `docker compose`):

| Service | Image / build | Purpose |
|---|---|---|
| `db` | `postgres:17-alpine` | Data in volume `pgdata`; port bound to `127.0.0.1` only. `deploy/postgres/init/01-app-role.sh` creates the least-privilege application role on first initialization. |
| `migrate` | API image, command `migrate` | Applies EF Core migrations as the **owner** role, then exits. |
| `api` | `src/PortfolioOptimizer.Api/Dockerfile` | Runs as non-root user `app`, connects as the **application** role, `Database__MigrateOnStartup=false`. Model artifacts in volume `model-artifacts` (`/app/artifacts`). |
| `web` | `frontend/portfolio-dashboard/Dockerfile` | nginx serving the SPA with security headers and CSP; proxies `/api`, `/health`, `/swagger` to the API. |

```bash
cp .env.example .env    # replace every placeholder; use long random passwords
podman compose up --build -d
podman compose logs -f api migrate
curl -s localhost:5080/health/ready     # Healthy
```

Ports are configurable (`API_PORT`, `WEB_PORT`, `POSTGRES_PORT`; defaults 5080/8081/5432), as is the sign-in rate
limit (`AUTH_RATE_LIMIT_PER_MINUTE`, default 10). `scripts/e2e.sh` uses these to run a second, isolated stack.

`.env` variables: `POSTGRES_DB`, `POSTGRES_OWNER_USER`, `POSTGRES_OWNER_PASSWORD`, `APP_DB_USER`, `APP_DB_PASSWORD`,
`POSTGRES_PORT`, `ADMIN_EMAIL`, `ADMIN_PASSWORD`, `MARKET_DATA_PROVIDER`, `ALPHAVANTAGE_API_KEY`,
`ASPNETCORE_ENVIRONMENT`. Generate passwords with e.g. `openssl rand -base64 30`.

The init script only runs when `pgdata` is empty. Changing `APP_DB_PASSWORD` later requires
`ALTER ROLE portfolio_app PASSWORD '…'` in the database as well.

## Market universes and quotes in operation

* Constituent updates: Admin settings → Instrument catalogue, or mount files and set `Universes__Directory`
  ([market-data.md](market-data.md#updating-constituents-no-code-change)).
* Quote refresh cycles start at sign-in and every `MarketData__ScheduledRefreshIntervalHours`; health and manual
  refresh on the Admin overview. Tuning: `MarketData__Quotes__FreshForMinutes` (15), `…__CycleMinIntervalMinutes` (15).
* Test data: `podman compose run --rm -e ASPNETCORE_ENVIRONMENT=Development migrate purge-test-data [--apply]`
  ([database.md](database.md#test-data-isolation)).

## Upgrading

```bash
git pull
podman compose up --build -d     # migrate runs before the new API starts; the API refuses an outdated schema
```

Back up first (below). Migrations are forward-only; rolling back code to a version with fewer migrations needs the
backup taken before the upgrade.

## Production checklist

- `ASPNETCORE_ENVIRONMENT=Production` (disables Swagger UI and detailed errors).
- Terminate TLS in a reverse proxy in front of `web` (Caddy, Traefik, nginx). Forward `X-Forwarded-For/Proto`;
  the bundled nginx passes them on and the API trusts them only because `ReverseProxy__TrustForwardedHeaders=true`.
  Cookies become `Secure` automatically over HTTPS. Add HSTS at the TLS proxy.
- Do not publish the API port (`5080`) or the database port; only the proxy should be reachable.
- Set `Cors__AllowedOrigins__0` to the public origin if the SPA is served from a different origin.
- Change the bootstrap administrator's password after first sign-in (Settings → Account) and remove
  `ADMIN_PASSWORD` from the environment.
- Keep `Seed__SyncOnEmptyDatabase` and the scheduled refresh in mind: Yahoo is an unofficial endpoint for personal use.
- Schedule backups and test a restore.

## Backup

Two things hold state: the **PostgreSQL database** and the **model-artifact directory** (database rows reference
artifact files by path and SHA-256). Back them up together.

```bash
set -a; . ./.env; set +a
ts=$(date +%Y%m%d-%H%M%S)

# 1. Database (custom format, compressed, consistent snapshot)
podman compose exec -T db pg_dump -U "$POSTGRES_OWNER_USER" -d "$POSTGRES_DB" -Fc > "backup-$ts.dump"

# 2. Model artifacts
podman compose exec -T api tar -C /app/artifacts -czf - . > "artifacts-$ts.tar.gz"
```

Locally (no containers): `pg_dump -h localhost -U portfolio_owner -d portfolio -Fc > backup.dump` and archive
`src/PortfolioOptimizer.Api/data/model-artifacts` (or your `ModelArtifacts__RootPath`).

Backups contain password hashes, e-mail addresses and audit data: store them encrypted and access-controlled.

## Restore

```bash
set -a; . ./.env; set +a
podman compose stop api web

# 1. Database: recreate it empty, then restore as the owner
podman compose exec -T db psql -U "$POSTGRES_OWNER_USER" -d postgres \
  -c "DROP DATABASE IF EXISTS \"$POSTGRES_DB\" WITH (FORCE);" -c "CREATE DATABASE \"$POSTGRES_DB\" OWNER \"$POSTGRES_OWNER_USER\";"
podman compose exec -T db pg_restore -U "$POSTGRES_OWNER_USER" -d "$POSTGRES_DB" --no-owner --role="$POSTGRES_OWNER_USER" < backup-YYYYMMDD-HHMMSS.dump

# 2. Re-grant the application role (the init script does not run again)
podman compose exec -T db psql -U "$POSTGRES_OWNER_USER" -d "$POSTGRES_DB" <<SQL
GRANT CONNECT, TEMPORARY ON DATABASE "$POSTGRES_DB" TO "$APP_DB_USER";
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO "$APP_DB_USER";
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO "$APP_DB_USER";
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO "$APP_DB_USER";
ALTER DEFAULT PRIVILEGES FOR ROLE "$POSTGRES_OWNER_USER" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "$APP_DB_USER";
ALTER DEFAULT PRIVILEGES FOR ROLE "$POSTGRES_OWNER_USER" IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO "$APP_DB_USER";
SQL

# 3. Model artifacts
podman compose run --rm --no-deps --entrypoint sh -v "$PWD:/backup:ro" api \
  -c "rm -rf /app/artifacts/* && tar -C /app/artifacts -xzf /backup/artifacts-YYYYMMDD-HHMMSS.tar.gz"

# 4. Apply migrations newer than the backup, then start
podman compose run --rm migrate
podman compose up -d
```

If artifacts are missing after a restore, experiments still display their stored metrics and backtests, but
"Predict on latest data" reports "Model artifact unavailable" until the experiment is re-run.

The backup and restore commands above were written for this stack but have not been run end to end on the development
machine; rehearse them on a copy before relying on them.

## Importing the legacy SQLite database

Earlier versions stored everything in `src/PortfolioOptimizer.Api/data/portfolio.db` without users. The importer
copies assets, prices, quotes, settings, portfolios, holdings, ledgers, proposals, orders, optimization runs and
backtests into PostgreSQL and assigns all portfolios and runs to one existing user.

1. Start the new stack once with `Seed__DemoPortfolio=false` so the target user exists without a conflicting demo
   portfolio (the importer stops if the target already contains portfolios).
2. Dry run (reads everything, checks balances, writes nothing):
   ```bash
   PO_IMPORT_POSTGRES='Host=localhost;Port=5432;Database=portfolio;Username=portfolio_owner;Password=…' \
     dotnet run --project tools/PortfolioOptimizer.DataMigration -- \
     --sqlite src/PortfolioOptimizer.Api/data/portfolio.db --owner-email admin@example.com --dry-run
   ```
3. Run again without `--dry-run`. The import is one transaction: on any error nothing is written. It verifies row
   counts and that each portfolio's cash equals its ledger balance.

The dry run against the development database (17 tables) passed its checks; the committing import was exercised by
the integration test `SqliteImportTests` against a fixture with the legacy schema.
