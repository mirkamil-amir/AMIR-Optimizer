#!/usr/bin/env bash
# Runs the .NET test suite. Integration tests need PostgreSQL: either PO_TEST_POSTGRES (admin connection string) or a
# Docker-compatible engine for Testcontainers. With Podman on macOS, the machine's API socket is used automatically.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
if [[ -z "${PO_TEST_POSTGRES:-}" && -z "${DOCKER_HOST:-}" ]] && command -v podman >/dev/null; then
  sock=$(podman machine inspect --format '{{.ConnectionInfo.PodmanSocket.Path}}' 2>/dev/null || true)
  if [[ -n "$sock" && -S "$sock" ]]; then
    export DOCKER_HOST="unix://$sock"
    # Ryuk (the reaper container) cannot mount the macOS-side socket path inside the Podman VM; the test fixture
    # disposes its container on process exit instead.
    export TESTCONTAINERS_RYUK_DISABLED=true
  fi
fi
dotnet test "$@"
