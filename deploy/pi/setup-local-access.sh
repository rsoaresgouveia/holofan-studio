#!/usr/bin/env bash
#
# Make HoloFan Studio reachable at http://holofan-studio.local — no IP, no port,
# the way Volumio does it. Run once on the Pi:
#
#     sudo bash deploy/pi/setup-local-access.sh            # -> holofan-studio.local
#     sudo bash deploy/pi/setup-local-access.sh myname     # -> myname.local
#
# It does three things:
#   1. mDNS  — installs Avahi and sets the hostname, so <name>.local resolves on the LAN.
#   2. port 80 — a compose override serves the app on :80 (Volumio-style, no :8080).
#   3. lets the non-root container bind :80 via a sysctl (keeps the app unprivileged).
set -euo pipefail

NAME="${1:-holofan-studio}"
APP_DIR=/opt/holofan

[[ $EUID -eq 0 ]] || { echo "Run with sudo."; exit 1; }
[[ -d "$APP_DIR" ]] || { echo "Run deploy/pi/install.sh first."; exit 1; }

echo "==> mDNS: Avahi + hostname '$NAME'"
if ! dpkg -l avahi-daemon 2>/dev/null | grep -q '^ii'; then
  apt-get update -qq && apt-get install -y -qq avahi-daemon
fi
hostnamectl set-hostname "$NAME"
# Keep /etc/hosts in sync so sudo/hostname resolution stays quiet.
if grep -q '^127.0.1.1' /etc/hosts; then
  sed -i "s/^127.0.1.1.*/127.0.1.1\t$NAME/" /etc/hosts
else
  echo -e "127.0.1.1\t$NAME" >> /etc/hosts
fi
systemctl restart avahi-daemon

echo "==> port 80: allow the non-root container to bind it"
echo "net.ipv4.ip_unprivileged_port_start=80" > /etc/sysctl.d/80-holofan.conf
sysctl -q -w net.ipv4.ip_unprivileged_port_start=80

echo "==> port 80: compose override"
cat > "$APP_DIR/docker-compose.override.yml" <<'YAML'
# Serves the app on :80 so http://<name>.local needs no port. Merged automatically
# with docker-compose.yml. Requires net.ipv4.ip_unprivileged_port_start<=80 (set above).
services:
  holofan:
    environment:
      ASPNETCORE_URLS: "http://+:80"
YAML

echo "==> restart"
cd "$APP_DIR"
docker compose up -d

echo
echo "Done. Open:  http://$NAME.local"
echo "(SSH too:    ssh $(logname 2>/dev/null || echo user)@$NAME.local )"
