#!/usr/bin/env bash
#
# One-shot installer for the Raspberry Pi. Run it ON the Pi, from a checkout of this repo:
#
#     git clone https://github.com/rsoaresgouveia/holofan-studio.git
#     cd holofan-studio
#     sudo bash deploy/pi/install.sh
#
# It installs the compose stack under /opt/holofan and wires up systemd so the app
# auto-starts on boot and auto-updates every 15 minutes.
set -euo pipefail

APP_DIR=/opt/holofan
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if [[ $EUID -ne 0 ]]; then echo "Run with sudo."; exit 1; fi

command -v docker >/dev/null || { echo "Docker not found. Install it first: curl -fsSL https://get.docker.com | sh"; exit 1; }

echo "==> Installing to $APP_DIR"
mkdir -p "$APP_DIR"
install -m 0644 "$REPO_DIR/docker-compose.pi.yml" "$APP_DIR/docker-compose.yml"
install -m 0755 "$REPO_DIR/deploy/pi/holofan-update.sh" "$APP_DIR/holofan-update.sh"

echo "==> Installing systemd units"
install -m 0644 "$REPO_DIR/deploy/pi/holofan.service"        /etc/systemd/system/holofan.service
install -m 0644 "$REPO_DIR/deploy/pi/holofan-update.service" /etc/systemd/system/holofan-update.service
install -m 0644 "$REPO_DIR/deploy/pi/holofan-update.timer"   /etc/systemd/system/holofan-update.timer

systemctl daemon-reload
systemctl enable --now holofan.service        # start now + on every boot
systemctl enable --now holofan-update.timer   # update/watchdog every 15 min

echo
echo "==> Done. Status:"
systemctl --no-pager status holofan.service | head -5 || true
echo
echo "   UI:      http://$(hostname -I | awk '{print $1}'):8080"
echo "   Logs:    docker compose -f $APP_DIR/docker-compose.yml logs -f"
echo "   Timer:   systemctl list-timers holofan-update.timer"
echo
echo "Note: the GHCR image must be public (once), or 'docker login ghcr.io' on the Pi."
