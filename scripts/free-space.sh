#!/usr/bin/env bash
# Frees disk space taken by container builds – safe to run without asking (owner's rule, 2026-10-30).
# Every image build grows the Podman VM disk file (~/.local/share/containers/podman/machine), which never shrinks by
# itself; two builds at once filled the Mac on 2026-10-29 (120 MB left, VM unreachable).
#
# Removes ONLY:   untagged images (build leftovers), images/stopped containers/volumes of the disposable e2e project
#                 (po-e2e*), then returns the VM's free blocks to the Mac (fstrim).
# Never touches:  any other volume (the development database lives in portfoliooptimizer_pgdata), containers or images of
#                 other projects, images a container uses, package caches, files outside the VM.
#
# Usage: ./scripts/free-space.sh                 prints free space before/after and what was removed
#        ./scripts/free-space.sh --restart-vm    first hard-restarts an unreachable VM (only when the disk is full and
#                                                podman cannot connect); restart the stack in order afterwards
#        FREE_SPACE_MIN_GB=10 ./scripts/free-space.sh --check   exit 1 if the Mac has less than that free (no changes)
set -euo pipefail

min_gb=${FREE_SPACE_MIN_GB:-10}
free_gb() { df -k "$HOME" | awk 'NR==2 { printf "%d", $4 / 1024 / 1024 }'; }

if [[ "${1:-}" == "--check" ]]; then
  gb=$(free_gb)
  echo "Mac free: ${gb} GB (minimum ${min_gb} GB)"
  [[ "$gb" -ge "$min_gb" ]]
  exit
fi

echo "Mac free before: $(free_gb) GB"

if ! podman info >/dev/null 2>&1; then
  if [[ "${1:-}" == "--restart-vm" ]]; then
    echo "Podman VM unreachable – restarting it (PostgreSQL runs crash recovery on its next start)."
    podman machine stop >/dev/null 2>&1 || true
    podman machine start >/dev/null
  else
    echo "Podman VM unreachable. If the disk is full, rerun with --restart-vm." >&2
    exit 1
  fi
fi

before=$(podman system df --format '{{.Type}} {{.Size}}' 2>/dev/null | awk '$1=="Images" { print $2 }')

# 1. Untagged images: intermediate layers and images replaced by newer builds.
dangling=$(podman images -q --filter dangling=true | sort -u | wc -l | tr -d ' ')
podman image prune -f >/dev/null
echo "Removed ${dangling} untagged image(s)."

# 2. The disposable e2e project (scripts/e2e.sh rebuilds it each run) – only when no e2e container is running.
if [[ -z "$(podman ps -q --filter name=po-e2e)" ]]; then
  for c in $(podman ps -aq --filter name=po-e2e); do podman rm -f "$c" >/dev/null; done
  for v in $(podman volume ls -q | grep '^po-e2e_' || true); do podman volume rm "$v" >/dev/null && echo "Removed e2e volume $v."; done
  for i in $(podman images --format '{{.Repository}}:{{.Tag}}' | grep -E '(^|/)po-e2e-' || true); do
    podman rmi "$i" >/dev/null 2>&1 && echo "Removed e2e image $i."
  done
else
  echo "An e2e stack is running – its images were kept."
fi

# 3. Give the freed blocks back to the Mac (deletes nothing).
podman machine ssh sudo fstrim -av 2>/dev/null | tail -1 || echo "fstrim failed (VM not reachable over ssh)."

after=$(podman system df --format '{{.Type}} {{.Size}}' 2>/dev/null | awk '$1=="Images" { print $2 }')
echo "Images: ${before:-?} -> ${after:-?}"
echo "Mac free after: $(free_gb) GB"
