#!/usr/bin/env bash
# Pull the latest image and ensure the stack is running.
# - If a newer image exists, `up -d` recreates the container.
# - If nothing changed, `up -d` is a no-op (no needless restart).
# - If the app was down, this brings it back (watchdog).
set -euo pipefail
cd /opt/holofan

# Pull quietly; don't fail the timer if the network/registry is briefly unavailable.
docker compose pull --quiet || echo "holofan-update: pull failed (offline?), continuing with current image"
docker compose up -d
docker image prune -f >/dev/null 2>&1 || true
