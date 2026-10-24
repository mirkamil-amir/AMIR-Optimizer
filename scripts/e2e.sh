#!/usr/bin/env bash
# Runs the Playwright end-to-end tests against a disposable, isolated stack:
#   * its own compose project (po-e2e), database volume and ports (web 18081, API 15080, db 15432),
#   * ASPNETCORE_ENVIRONMENT=E2E (the specs refuse to run against anything else),
#   * the deterministic Mock market-data and news providers (no network, no API keys),
#   * random throw-away passwords.
# The stack and its volumes are removed afterwards, so test accounts and runs never reach a real database.
# Usage: ./scripts/e2e.sh [extra playwright args]      KEEP_E2E_STACK=1 keeps it running for debugging.
set -euo pipefail
cd "$(dirname "$0")/.."

export COMPOSE_PROJECT_NAME=po-e2e
export POSTGRES_PORT=15432 API_PORT=15080 WEB_PORT=18081
export POSTGRES_DB=portfolio_e2e POSTGRES_OWNER_USER=e2e_owner APP_DB_USER=e2e_app
export POSTGRES_OWNER_PASSWORD="$(openssl rand -hex 16)" APP_DB_PASSWORD="$(openssl rand -hex 16)"
export ADMIN_EMAIL=admin@e2e.test ADMIN_PASSWORD="E2e-$(openssl rand -hex 8)-Aa1"
export MARKET_DATA_PROVIDER=Mock ALPHAVANTAGE_API_KEY= ASPNETCORE_ENVIRONMENT=E2E AUTH_RATE_LIMIT_PER_MINUTE=1000
# All browsers share one client IP; the global per-IP limit (600/min in production) would throttle the whole suite.
export RATE_LIMIT_PER_MINUTE=6000
# Simulated news only (no network); real news sources stay off on the disposable stack.
export NEWS_YAHOO_ENABLED=false NEWS_OFFICIAL_FEEDS_ENABLED=false NEWS_MOCK_ENABLED=true
export PROFILES_WIKIPEDIA_ENABLED=false PROFILES_MOCK_ENABLED=true
# Research assistant: deterministic rules only (no language model, no key).
export AGENT_LLM_PROVIDER=None ANTHROPIC_API_KEY=

COMPOSE="${COMPOSE:-podman compose}"
cleanup() {
  if [[ "${KEEP_E2E_STACK:-}" == "1" ]]; then
    echo "Keeping the e2e stack (web http://localhost:$WEB_PORT). Remove it with: COMPOSE_PROJECT_NAME=po-e2e $COMPOSE down -v"
  else
    $COMPOSE down -v >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

$COMPOSE down -v >/dev/null 2>&1 || true
$COMPOSE up --build -d
echo "Waiting for the e2e API…"
for _ in $(seq 1 90); do
  curl -sf "http://localhost:$API_PORT/health/ready" >/dev/null && break
  sleep 2
done
curl -sf "http://localhost:$API_PORT/health/ready" >/dev/null || { $COMPOSE logs api | tail -40; exit 1; }

cd frontend/portfolio-dashboard
E2E_BASE_URL="http://localhost:$WEB_PORT" E2E_ADMIN_EMAIL="$ADMIN_EMAIL" E2E_ADMIN_PASSWORD="$ADMIN_PASSWORD" \
  ./node_modules/.bin/playwright test "$@"
