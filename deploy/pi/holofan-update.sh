#!/usr/bin/env bash
# Keep HoloFan Studio up to date and running (run by holofan-update.timer every 15 min).
#
# 1. If installed from a git clone, `git pull` to refresh the compose + deploy files.
# 2. `docker compose pull` to fetch a newer image from GHCR.
# 3. `docker compose up -d` — recreates only if something changed; also a watchdog
#    (brings the app back if it was down).
#
# The clone path is passed in as $1 by the systemd unit (empty if not a git install).
set -euo pipefail

APP_DIR=/opt/holofan
CLONE_DIR="${1:-}"

# 1. Refresh files from git, if this was a git install.
if [[ -n "$CLONE_DIR" && -d "$CLONE_DIR/.git" ]]; then
  if git -C "$CLONE_DIR" pull --ff-only --quiet 2>/dev/null; then
    # Re-sync the files install.sh placed under /opt/holofan.
    install -m 0644 "$CLONE_DIR/docker-compose.pi.yml" "$APP_DIR/docker-compose.yml"
    install -m 0755 "$CLONE_DIR/deploy/pi/holofan-update.sh" "$APP_DIR/holofan-update.sh"
  else
    echo "holofan-update: git pull skipped (offline or local changes)"
  fi
fi

cd "$APP_DIR"

# 2 + 3. Update the image and ensure the stack is running.
docker compose pull --quiet || echo "holofan-update: image pull failed (offline?), keeping current image"
docker compose up -d
docker image prune -f >/dev/null 2>&1 || true
