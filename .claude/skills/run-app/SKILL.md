---
name: run-app
description: Start the AMIR Optimizer stack (Podman containers by default, or local dotnet + Vite against the compose PostgreSQL) and verify it is healthy, including sign-in. Use when asked to run, start, restart, show or check the app.
---

# Run & verify the app

Credentials live in `.env` (gitignored). Never echo passwords into chat or logs – tell the user to read
`ADMIN_EMAIL` / `ADMIN_PASSWORD` from `.env`. If `.env` is missing: `cp .env.example .env` and ask the user to set
real passwords (or generate them with `openssl rand -base64 30` if they agree).

## Default: containers (Podman)

```bash
cd ~/Desktop/CODE/PortfolioOptimizer
podman machine list                                   # must show "Currently running"; else: podman machine start
lsof -nP -iTCP:5080 -iTCP:8081 -sTCP:LISTEN           # a local `dotnet run` on 5080 blocks the container API – stop it
podman compose up --build -d                          # db → migrate (exits 0) → api → web
```

Verify (all must pass before reporting success):

```bash
for i in $(seq 1 30); do curl -s localhost:5080/health/ready | grep -q Healthy && break; sleep 2; done
curl -s localhost:5080/health/ready                                          # Healthy
curl -s -o /dev/null -w '%{http_code}\n' localhost:8081                      # 200 (dashboard)
curl -s -o /dev/null -w '%{http_code}\n' localhost:8081/api/v1/auth/me       # 401 (nginx → API, not signed in)
podman compose ps -a --format '{{.Name}} {{.Status}}'                         # migrate: Exited (0)
podman compose logs api migrate 2>&1 | grep -iE "fail|error|exception" | tail
```

Sign-in check (optional, uses the cookie jar in the scratchpad; never print the password):

```bash
set -a; . ./.env; set +a; J=$(mktemp); B=http://localhost:5080/api/v1
T=$(curl -s -c $J -b $J $B/auth/antiforgery | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
python3 -c 'import json,os;print(json.dumps({"email":os.environ["ADMIN_EMAIL"],"password":os.environ["ADMIN_PASSWORD"]}))' \
  | curl -s -o /dev/null -w 'login %{http_code}\n' -c $J -b $J -H "X-XSRF-TOKEN: $T" -H 'Content-Type: application/json' --data @- $B/auth/login
rm -f $J
```

Sign-in is rate limited to 10 attempts/min per IP – don't loop it. Signing in starts a background quote refresh for
all markets (~30 s with Yahoo); check it with `curl -s -b $J $B/market-data/quote-refresh/latest` or the Admin overview.

For automated UI tests use `./scripts/e2e.sh` (separate disposable stack), never this stack.

URLs: dashboard http://localhost:8081 · Swagger http://localhost:5080/swagger (Development only).
If the user wants to *see* it, open http://localhost:8081 with the Chrome tools and take one screenshot.

## Alternative: local dev (hot reload)

```bash
export PATH="$HOME/.dotnet:/opt/homebrew/opt/node@22/bin:$PATH" DOTNET_ROOT="$HOME/.dotnet"
cd ~/Desktop/CODE/PortfolioOptimizer && set -a && . ./.env && set +a
podman compose up -d db
podman compose stop api web 2>/dev/null                # free :5080
dotnet build
(cd src/PortfolioOptimizer.Api && ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=$POSTGRES_DB;Username=$POSTGRES_OWNER_USER;Password=$POSTGRES_OWNER_PASSWORD" \
   Admin__Email="$ADMIN_EMAIL" Admin__Password="$ADMIN_PASSWORD" dotnet run --no-build --launch-profile http) &   # :5080, migrates in Development
(cd frontend/portfolio-dashboard && npm run dev) &                                                                # :5173
```

Run both as background commands. Verify `curl -s localhost:5080/health/ready` and `curl -s -o /dev/null -w '%{http_code}' localhost:5173`.
Stop with `podman compose down` (keeps data; `-v` deletes database **and** model artifacts) or by killing the jobs.

## Troubleshooting
- API exits "Connection string 'DefaultConnection' is not configured" → env var missing.
- API exits "The database schema is out of date" → `podman compose run --rm migrate` (containers) or rebuild + Development start (local).
- `migrate` fails with authentication errors → `.env` passwords differ from those the `pgdata` volume was initialized with.
- Node tooling hangs with idle CPU → iCloud offloaded `node_modules`: `rm -rf node_modules && npm ci`.
- Sync job failed (429) → Yahoo throttling; retry later. Offline: `MarketData__DefaultProvider=Mock` on a new database.
- "Model artifact unavailable" → artifacts directory differs between local (`data/model-artifacts`) and container (`model-artifacts` volume) runs.
