# Running HoloFan Studio on a Raspberry Pi

The fan is **AP-only**: it creates its own open `3DCircle_…` network and cannot join yours
(its "Wi-Fi config" only renames its own AP — see REVERSE_ENGINEERING.md). So to use it
without leaving your network, put a Pi in the middle:

```
   your LAN  ──eth0──►  [ Raspberry Pi ]  ──wlan0──►  fan's AP (192.168.4.1)
   you open http://holofan.local:8080        HoloFan Studio talks to the fan
```

You stay on your own WiFi; the Pi is the only thing joined to the fan.

---

## 0. Hardware note

| | Pi 3B+ (1 GB) | Pi 4 (4 GB) |
|---|---|---|
| Fan remote (play/pause, brightness…) | fine | fine |
| Encoding `.bin` | slow; keep clips short | comfortable |

The Pi 3B+ has **1 GB** — there is no 4 GB 3B+. Encoding runs ffmpeg **and** .NET together, so
on a 3B+ prefer short clips and add swap (step 3).

## 1. Flash Raspberry Pi OS **Lite (64-bit)**

64-bit is **required** — the image is `arm64`. Lite because RAM is precious.

Use [Raspberry Pi Imager](https://www.raspberrypi.com/software/):

1. **Choose OS** → Raspberry Pi OS (other) → **Raspberry Pi OS Lite (64-bit)**
2. Click the **gear** (pre-seed settings) and set:
   - hostname: `holofan`
   - **Enable SSH** (key-based is better than password)
   - username / password
   - locale + timezone
   - *Leave WiFi blank* — we want the Pi on **Ethernet** for your LAN, and its WiFi free for the fan.
3. Write, boot with the network cable plugged in.

```bash
ssh <user>@holofan.local
sudo apt update && sudo apt full-upgrade -y && sudo reboot
```

## 2. Install Docker

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker "$USER"
newgrp docker            # or just log out and back in
docker run --rm hello-world
```

## 3. (Pi 3B+ only) give it some swap

1 GB is tight once ffmpeg starts.

```bash
sudo dphys-swapfile swapoff
sudo sed -i 's/^CONF_SWAPSIZE=.*/CONF_SWAPSIZE=1024/' /etc/dphys-swapfile
sudo dphys-swapfile setup && sudo dphys-swapfile swapon
free -h
```

## 4. Join the fan's AP — **without breaking your internet**

This is the step that goes wrong if rushed. The fan's AP has **no internet and no useful
gateway**; if it becomes the default route, the Pi loses DNS and `apt`/`docker` break.

Bookworm uses NetworkManager, so tell that connection it may never be the default:

```bash
nmcli device wifi list                       # find the fan's SSID, e.g. 3DCircle_42cm_F254E600
sudo nmcli device wifi connect "3DCircle_42cm_F254E600"      # open network, no password

FAN="3DCircle_42cm_F254E600"
sudo nmcli connection modify "$FAN" ipv4.never-default yes
sudo nmcli connection modify "$FAN" ipv6.never-default yes
sudo nmcli connection modify "$FAN" ipv4.ignore-auto-dns yes
sudo nmcli connection modify "$FAN" connection.autoconnect yes
sudo nmcli connection up "$FAN"
```

Verify — **the default route must be `eth0`**:

```bash
ip route          # expect: default via <your router> dev eth0
                  #         192.168.4.0/24 dev wlan0
ping -c2 192.168.4.1        # the fan
ping -c2 1.1.1.1            # the internet, still alive
```

If `default` points at `wlan0`, the `never-default` line did not take — fix it before going on.

## 5. Ship the image (don't build on the Pi)

Your Mac is Apple Silicon, so it already builds **arm64** — the same architecture the Pi runs.
Build there and copy the image over; a `dotnet publish` on a 1 GB Pi is slow and can OOM.

On the **Mac**, from the repo root:

```bash
docker build -t holofan-studio .
docker save holofan-studio | gzip | ssh <user>@holofan.local "gunzip | docker load"
```

Then copy the Pi compose file over:

```bash
scp docker-compose.pi.yml <user>@holofan.local:~/
```

> If you ever *do* want to build on the Pi: `git clone` the repo there and
> `docker build -t holofan-studio .` — just expect it to take a while.

## 6. Run it

On the **Pi**:

```bash
docker compose -f docker-compose.pi.yml up -d
docker compose -f docker-compose.pi.yml logs -f
```

It uses `network_mode: host`, so the container shares the Pi's routing table and reaches
`192.168.4.1` over `wlan0` with no NAT in between.

From **any machine on your LAN**:

```
http://holofan.local:8080
```

Click **Connect** in the Fan remote panel — the buttons light up and you are driving the fan
from your own network.

## 6b. Auto-start on boot + auto-update every 15 min (recommended)

Instead of running compose by hand, install the systemd units — the app then starts on every
boot and a timer pulls the latest image and keeps it running every 15 minutes:

```bash
git clone https://github.com/rsoaresgouveia/holofan-studio.git
cd holofan-studio
sudo bash deploy/pi/install.sh
```

What it sets up:
- **`holofan.service`** — `docker compose up -d` on boot (auto-start when the Pi powers on).
- **`holofan-update.timer`** → **`holofan-update.service`** — every 15 min: `docker compose pull`
  then `up -d`. A no-op when nothing changed, so it both **auto-updates** and acts as a
  **watchdog** (brings the app back if it was down).

Check them:

```bash
systemctl list-timers holofan-update.timer
journalctl -u holofan-update.service -n 20
```

> The image is pulled from **GHCR**, built by CI — the Pi never compiles anything. Make the
> package **public** once (GitHub → your profile → Packages → holofan-studio → Package settings →
> Change visibility → Public), or run `docker login ghcr.io` on the Pi with a token.

## 7. Sanity checks

```bash
curl -s http://holofan.local:8080/api/health          # {"status":"ok"}
curl -s http://holofan.local:8080/api/fan/status      # connected:false, host 192.168.4.1
curl -s -X POST http://holofan.local:8080/api/fan/connect
```

## Gotchas

- **Port clashes with your other Pis.** Home Assistant is `8123`, Pi-hole takes `80` and `53`.
  We use `8080` — fine alongside them, but if you later put Pi-hole on *this* Pi, `network_mode:
  host` means no port isolation. Change `ASPNETCORE_URLS` if you need to.
- **The fan's AP is open.** Anyone in range can connect and send `Format Disk`. Renaming/securing
  it is on the roadmap (the vendor's Wi-Fi config flow).
- **`.local` needs mDNS.** If `holofan.local` does not resolve, use the Pi's LAN IP (`ip -4 addr
  show eth0`).
- **Encoding on a 3B+ is slow.** If it is painful, encode the `.bin` on your Mac and use the Pi
  purely as the remote.
