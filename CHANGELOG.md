# Changelog

All notable changes to HoloFan Studio. The version shown in the app footer (with its
commit hash) tells you exactly which build a device is running — handy for confirming a
Raspberry Pi has pulled the latest image.

The format is loosely [Keep a Changelog](https://keepachangelog.com/); versions follow
[SemVer](https://semver.org/). **Convention: every change that goes to git bumps the
`<Version>` in `src/HoloFan.Web/HoloFan.Web.csproj` and adds an entry here, in the same
commit.**

## [1.7.0] — 2026-07-23

### Added
- **Persistent clip library.** Every clip you render is now kept in a library on the data
  volume (`/data/library/`, survives reboots), and you can **import** existing `.bin` files
  (e.g. the factory demo clips). Full CRUD — rename, delete, download, and send any clip
  straight to the fan. This is the groundwork for managing what's on the fan without wiping
  everything: the app holds each clip's bytes, so it can re-upload survivors after a format
  (the fan itself has no per-file delete and can't send clips back — see REVERSE_ENGINEERING.md).
- New endpoints: `GET /api/library`, `POST /api/library/import`, `POST /api/library/{id}/rename`,
  `DELETE /api/library/{id}`, `GET /api/library/{id}/download`, `POST /api/library/{id}/send`.

## [1.6.0] — 2026-07-23

### Added
- **Version & changelog in the app.** The footer now shows the running version and short
  commit hash, and links to this changelog — so you can tell at a glance whether a device
  has updated. New `GET /api/version` endpoint exposes version, commit and build time.

## [1.5.0] — 2026-07-23

### Fixed
- **Colors on the fan were scrambled into rainbow rings.** The `.bin` bit-plane packer
  wrote each byte LSB-first, but the device reads MSB-first. Round-trip tests passed
  (pack and unpack agreed), yet the hardware unpacked the other way and turned every flat
  colour into concentric rainbow rings. Confirmed against the factory demo clips as ground
  truth and fixed in `BinFormat`. Colors now render correctly.

## [1.4.0] — 2026-07-23

### Fixed
- Power on/standby badge was inverted (status-tail byte 3 is a standby flag: 0 = on).

## [1.3.0] — 2026-07-22

### Added
- Play a specific clip by clicking it in the playlist (emulated via the device's
  current-index plus Next/Previous stepping).
- Fan state in the UI: playlist, power badge, and picture-duration control; modal errors
  now show inside the modal instead of behind it.

## Earlier (pre-versioning)

The foundation, before per-commit versioning began:

- **Reverse-engineered the fan end to end** — the proprietary `.bin` format (112 LEDs ×
  512 slices, RGB666 bit-planes) and the WiFi protocol (framing, playlist, commands),
  documented in `REVERSE_ENGINEERING.md`.
- **WiFi file upload** to the fan, validated on hardware (the filename must end in `.BIN`).
- **Admin passphrase** gating the destructive Format Disk / Clear Cache buttons
  (PBKDF2-hashed, never stored in plaintext).
- **Raspberry Pi deployment** — multi-arch image on GHCR, systemd auto-start, a 15-minute
  auto-update timer, and `holofan-studio.local` mDNS access.
- The core web app: drop a video, frame it to the fan's circle, convert to `.bin`.

[1.6.0]: https://github.com/rsoaresgouveia/holofan-studio/releases
[1.5.0]: https://github.com/rsoaresgouveia/holofan-studio/releases
[1.4.0]: https://github.com/rsoaresgouveia/holofan-studio/releases
[1.3.0]: https://github.com/rsoaresgouveia/holofan-studio/releases
