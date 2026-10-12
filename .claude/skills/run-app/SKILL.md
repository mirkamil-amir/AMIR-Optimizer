---
name: run-app
description: Start the Portfolio Optimizer stack (Podman containers by default, or local dotnet + Vite) and verify it is healthy. Use when asked to run, start, restart, show or check the app.
---

# Run & verify the app

## Default: containers (Podman)

```bash
cd ~/Desktop/CODE/PortfolioOptimizer
lsof -nP -iTCP:5080 -iTCP:8081 -sTCP:LISTEN   # free the ports first (kill stray local API/Vite)
podman machine list                             # must show "Currently running"; else: podman machine start
podman compose up --build -d
```

Verify (all must pass before reporting success):

```bash
for i in $(seq 1 30); do curl -s localhost:5080/health/ready | grep -q Healthy && break; sleep 2; done
curl -s localhost:5080/health/ready                                   # Healthy
curl -s -o /dev/null -w '%{http_code}\n' localhost:8081               # 200 (dashboard)
curl -s -o /dev/null -w '%{http_code}\n' localhost:8081/api/v1/portfolios   # 200 (nginx → API proxy)
podman logs portfoliooptimizer-api-1 2>&1 | grep -iE "fail|error|sync .* finished" | tail
```

URLs: dashboard http://localhost:8081 · Swagger http://localhost:5080/swagger.
If the user wants to *see* it, open http://localhost:8081 with the Chrome tools and take one screenshot.

## Alternative: local dev (hot reload)

```bash
export PATH="$HOME/.dotnet:/opt/homebrew/opt/node@22/bin:$PATH" DOTNET_ROOT="$HOME/.dotnet"
cd ~/Desktop/CODE/PortfolioOptimizer && dotnet build
(cd src/PortfolioOptimizer.Api && dotnet run --no-build --launch-profile http) &   # :5080
(cd frontend/portfolio-dashboard && npm install && npx vite --port 5173 --strictPort) &   # :5173
```

Run both as background commands. Stop with `podman compose down` (containers) or by killing the background jobs.

## Troubleshooting
- API exits with "no such table" → binaries older than a migration: `dotnet build`, rerun.
- Sync job failed (429) → Yahoo throttling; retry later from Market data page. Offline: `MarketData__DefaultProvider=Mock` (new DB only) or switch provider in Settings.
- Container DB is separate from the local `src/PortfolioOptimizer.Api/data/` DB.
