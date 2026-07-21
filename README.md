<div align="center">

# 🌀 HoloFan Studio

**Turn any video into a clip your LED “hologram” fan can play — and drive the fan itself, with no vendor software.**

[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Docker](https://img.shields.io/badge/Docker-arm64%20%7C%20amd64-2496ED?logo=docker&logoColor=white)](./Dockerfile)
[![Tests](https://img.shields.io/badge/tests-54%20passing-3fb950)](./tests)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![AI-assisted](https://img.shields.io/badge/built%20with-Claude%20(AI--assisted)-8A63D2?logo=anthropic&logoColor=white)](#-how-this-was-built-ai-disclosure)

</div>

Those spinning “3D hologram” LED fans paint a round image in mid-air (persistence of vision).
They only play a **proprietary `.bin` format** produced by closed, Windows-only vendor software,
and they’re controlled over their own WiFi with an **undocumented protocol**.

HoloFan Studio replaces that whole toolchain with one self-hosted web app:

- **Convert** any video into the fan’s native `.bin` — crop to the circle, sample into the LED
  grid, bit-plane pack — straight to the SD card.
- **Control** the fan live over WiFi (play/pause, brightness, rotate, and more) in its own protocol.
- Runs anywhere Docker does, including a **Raspberry Pi** that bridges your LAN and the fan.

> **Interoperability project for hardware I own.** Nothing here redistributes the vendor’s code —
> only the file-format and wire-protocol facts needed to talk to my own device.

---

## 🤖 How this was built (AI disclosure)

**This project was built collaboratively with [Claude](https://www.anthropic.com/claude) (Anthropic),
using Claude Code, and I want that stated plainly rather than hidden.** I directed the work, made the
decisions, tested against the real hardware, and reviewed everything that landed; Claude did a large
share of the implementation, the reverse-engineering legwork, and the writing — as an AI pair working
under my direction.

The most interesting part — decoding the fan — is genuinely a joint effort worth being transparent about:

- The **`.bin` format** was cracked by **static analysis of the vendor’s encoder** (read-only x86
  disassembly — the binary was never executed) plus validation against the factory demo clips:
  decoding them renders the original artwork (a recognisable tiger, Mario…). See
  [`REVERSE_ENGINEERING.md`](./REVERSE_ENGINEERING.md).
- The **WiFi protocol** (host, port, framing, and the full command set) came out of the same
  disassembly — **no packet capture needed**.

If you’re a recruiter or engineer reading this: the value on show is the *direction, verification, and
judgement* around an AI collaborator, not a claim that a human typed every line. That’s how I work.

---

## What it does

### Convert
- **Visual framing** — an interactive canvas shows your frame with the square crop box *and* the
  inscribed circle the blades actually light. Drag to reposition, scroll to zoom.
- **Live fan preview** — a round preview renders exactly what the fan will show.
- **Fully configurable** — fan-size presets, output resolution, frame rate, playback speed, circular
  mask, background colour, trim, and brightness / contrast / saturation.
- **Two outputs** — a normal **MP4** (preview / any tool) or the fan’s **native `.bin`** (SD card).

### Control (over the fan’s WiFi)
Every button the vendor app has, reimplemented from the protocol we recovered:

`Play/Pause` · `Last/Next` · `List loop` / `Single loop` · `Brightness ± ` · `CW / CCW rotate` ·
`On/Off` · `Wi-Fi config` · `Clear cache` · `Format disk` — plus an undocumented **clock** mode
(needle colour, dial style: Digital / Symbol / Constellation / Zodiac) found in the binary.

Destructive commands (`Format disk`, `Clear cache`) are guarded and require explicit confirmation.

---

## Run it

### Docker (recommended)

```bash
docker compose up --build
# open http://localhost:8080
```

### Local development

Requires the **.NET 9 SDK** and **ffmpeg/ffprobe** on your `PATH`.

```bash
dotnet run --project src/HoloFan.Web
dotnet test        # 54 tests, no ffmpeg needed
```

### On a Raspberry Pi (bridge your LAN to the fan)

The fan is **AP-only** — it makes its own `3DCircle_…` network and can’t join yours. Put a Pi in the
middle (Ethernet to your LAN, WiFi to the fan) and reach the UI from your own network. Full steps,
including the routing gotcha, in [`docs/RASPBERRY_PI.md`](./docs/RASPBERRY_PI.md).

The image is multi-arch (`arm64` + `amd64`), so it runs on a Pi unchanged.

---

## How it works

```
video ─▶ ffprobe ─▶ crop to circle ─▶ scale ─▶ retime ─▶ colour ─▶ ┬─▶ H.264 MP4
                                                                     └─▶ polar sample ─▶ bit-plane pack ─▶ .bin
                                                                                                          └─▶ SD card / (WiFi upload, WIP)
fan ◀── TCP 192.168.4.1:20320 ◀── framed command protocol ◀── control panel
```

| Project | Role |
|---|---|
| **HoloFan.Core** | Pure, testable conversion logic — presets, validation, the ffmpeg filter-graph builder. |
| **HoloFan.Device** | The reverse-engineered device layer — `.bin` encoder + the WiFi command protocol. |
| **HoloFan.Web** | ASP.NET Core Minimal API + a vanilla-JS SPA (canvas configurator + fan remote). |
| **HoloFan.Tests** | 54 xUnit tests, including a round-trip against **real vendor `.bin` bytes**. |

### The fan format, in one paragraph

Model 42-F2: each frame is **129 024 B = 512 angular slices × 252 B**; a slice is **6 bit-planes ×
42 B** over a **112-LED × 3-channel BGR** source (⇒ RGB666, since the encoder truncates the low 2
bits). The full write-up — how it was found and every field — is in
[`REVERSE_ENGINEERING.md`](./REVERSE_ENGINEERING.md).

---

## Status

| Area | State |
|---|---|
| MP4 conversion | ✅ done |
| Native `.bin` (SD card) | ✅ done, validated against vendor bytes |
| Fan remote (WiFi commands) | ✅ done |
| Bulk `.bin` upload over WiFi | 🚧 endpoint known, framing still being transcribed |
| On-device colour mapping | 🚧 geometry exact; colour verified on the device |

---

## Legal & safety

- This is **interoperability reverse engineering of a device I own**, done by observing its own file
  format and network traffic. No vendor code is redistributed; the vendor binaries and demo clips are
  **not** in this repo.
- The fan’s AP is **open by default** — anyone in range can connect and issue commands. Treat it as
  untrusted and keep it off networks you care about.

## License

[MIT](./LICENSE).
