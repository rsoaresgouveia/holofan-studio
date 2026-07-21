# Reverse-engineering the fan — driving a 42-F2 directly from HoloFan Studio

**Goal:** make HoloFan Studio talk to the physical LED hologram fan **without any
vendor software** — generate the device's native `.bin` file ourselves and deliver
it both ways: copied to the **TF/SD card** *and* pushed over **WiFi** to the fan's
hotspot.

This is legitimate interoperability RE: you own the device, and we only observe its
own file format and its own network traffic to talk to *your* hardware.

---

## The device

From the vendor manual (V8.1, "3D Circle / 3D全息广告机") the unit in hand is:

| | |
|---|---|
| Encoder model to select | **42-F2** (42 cm ≈ 16.5 in) |
| LED beads (radial) | **224** |
| Vendor "resolution" | **2000 × 224** (angular × radial) |
| WiFi | fan boots as an **open AP** `3DCircle_…` / `3D_Cice_…` |
| Config password (to *change* AP name/pass) | factory `123456789` |
| On-device file | proprietary **`.bin`** (max 30 min/clip) |
| Delivery | copy `.bin` to TF card **or** upload over the AP (app / Windows exe) |

So there are exactly **two unknowns** to crack:

1. **The `.bin` container** — how a framed circular video becomes bytes the firmware
   plays. This is needed for *both* delivery routes.
2. **The WiFi upload protocol** — how the app/exe pushes a `.bin` to the fan.

We attack #1 with **controlled inputs** (black-box, but we choose the input), and #2
with a **packet capture**.

---

## ✅ THE `.BIN` FORMAT — SOLVED (2026-07-16)

Cracked by **static analysis** of the vendor encoder (never executed — read-only
disassembly with capstone) + validation against the factory demo clips. Decoding
`4Tiger.BIN` renders **an unmistakable tiger**, and `马里奥.BIN` renders **Mario** —
independent confirmation on two files.

### Container (model 42-F2 / 112-LED config block @ VA 0x403626)

```
file        = frame[]                       (no global header)
frame       = 129 024 B = 512 slices × 252 B
slice       = 252 B     = 6 bit-planes × 42 B
plane       = 42 B      = 336 bits = 1 bit per source byte
source slice= 336 B     = 112 LEDs × 3 channels, **BGR interleaved** (OpenCV order)
```

### Bit-plane mapping — the key

The encoder builds an **8-plane / 336-byte** buffer, one plane per source bit:

```
offset(bit) = 294 − 42 × bit
  bit 7 → 0 | bit 6 → 42 | bit 5 → 84  | bit 4 → 126
  bit 3 → 168 | bit 2 → 210 | bit 1 → 252 | bit 0 → 294
```

…but only the **first 252 bytes are written to the file**, so **bits 1 and 0 are
truncated**. The file therefore carries **bits 7..2 = the top 6 bits ⇒ RGB666**.
Planes appear in the file at offsets `0, 42, 84, 126, 168, 210` = bits `7, 6, 5, 4, 3, 2`.

Within a plane, packing is **LSB-first**: source byte `j` → plane byte `j >> 3`, bit `j & 7`.
(Read off the unrolled packer at VA 0x40741b: takes 8 consecutive source bytes,
`and X,1` / `and X,4` / `and X,8` … and ORs them into one output byte.)

### Geometry

- **112 LEDs per slice** — the advertised “224” is **both blades summed** (2 × 112);
  the `.bin` stores one blade.
- **512 angular slices** per revolution.
- **LED index 0 = outer edge, index 111 = centre** (confirmed: the tiger only renders
  right-side-up with the radius flipped).
- Source is `cv::Mat(rows=112, cols=512, CV_8UC3)` — the polar unwrap (VA 0x406401).

### Reference decoder

```python
SLICE, ANG, L, SRC = 252, 512, 112, 336
for s in range(ANG):
    sl = frame[s*SLICE:(s+1)*SLICE]
    v = [0]*SRC
    for j in range(SRC):
        val = 0
        for bit in range(2, 8):              # bits 7..2 survive
            off = 294 - 42*bit
            if off < SLICE:
                val |= ((sl[off + (j>>3)] >> (j & 7)) & 1) << bit
        v[j] = val
    # LED i, BGR interleaved:  B=v[i*3]  G=v[i*3+1]  R=v[i*3+2]
```

### Still to polish

**Channel separation.** Geometry is exact, but the three channels come out nearly
equal (all ≈44–56 average), so images render washed-out with rainbow fringing. Either
a phase/interleave detail in the 336-byte source layout is still off, or it is the
6-bit dither (`aa`/`55` are among the most frequent bytes). Does **not** block the
encoder — geometry and packing are known.

### Other models

`.text` holds **21 config blocks = the 21 device models** (LEDs 48…256). Rule:
`plane_bytes = LEDs / 8`; most models use **24 planes (RGB888)** — the 112-LED model is
the exception with **18** (`0x3dd8 = 14`, `252/14`). A second packing branch exists at
VA 0x4074d7, selected by the flag at `[esi+0x3dc9]` — almost certainly the manual's
decode modes (Colorido / Comum / Destaque).

---

## 📡 THE WiFi PROTOCOL — endpoint solved, framing pending (2026-07-16)

**No packet capture was needed.** The vendor app carries the whole client, so the same
static analysis that cracked the `.bin` answered this too.

### Confirmed

| | |
|---|---|
| Transport | **plain TCP** — the app imports only `socket / connect / send / recv / select / ioctlsocket / closesocket` from `WS2_32.dll` (by ordinal). **No HTTP, no TLS.** |
| Host | **`192.168.4.1`** — literal string at VA 0x40dc7c (classic ESP SoftAP gateway) |
| Port | **`20320`** (`0x4f60`) — written to both port members at VA 0x401a33 / 0x401a3d |
| Init site | VA 0x401a29: `mov [edi+0x100], 0x40dc7c` (ip) / `[edi+0x104] = [edi+0x108] = 0x4f60` |
| Handshake | 24 B at VA 0x40e598, sent immediately after connect (VA 0x408a5a): `C0EEB7C9BAA3C0EEBDF9E5B7` |
| Signature | 20 B at VA 0x40e238: `B2DDDDEDC0EEBDF9E5B7` |

Both magic strings are **ASCII hex of GBK bytes** and decode to Chinese names
(`C0EE`=李, `B7C9`=飞, `BAA3`=海, `BDF9`=金) — the developers signed their own protocol.

> **Neat cross-check:** `B2DDDDEDC0EEBDF9E5B7` is byte-for-byte the **20-byte trailer** found
> appended to `房子.BIN` and `马里奥.BIN`. The same signature doubles as a file marker and a
> protocol token — which is why those two clips are `n×129024 + 20`.

Connection wrappers live at VA 0x404db0 / 0x4050b0 / 0x408920, each taking `(char* ip, int port)`
and doing `WSAStartup → socket → htons/inet_addr → connect → send/recv`, non-blocking via
`ioctlsocket(FIONBIO)` + `select` with a 100 ms timeout (`0x186a0` µs).

### The packet framing — SOLVED

The command packer lives at **VA 0x408a80** (`this` in `ecx`, args `(void* payload, int len)`).
It builds, at `this+0x1118`:

```
"C0EEB7C9BAA3"                    12 B header   (VA 0x40dd34)
byte  len / 323                   ─┐
byte  0x63 + (len / 17) % 19       │ 3 B length, mixed radix (323 = 17 × 19)
byte  0x62 + len % 17             ─┘
<payload>                         len B
"C0EEBDF9E5B7"                    12 B trailer  (VA 0x40dd44)
```

…then `send(sock, buf, len + 27)` — matching the `lea eax, [esi+0x1b]` exactly. The 24-byte
handshake is simply header+trailer with an empty payload, sent raw.

Decoding the length back out: `len = 323*b0 + 17*((b1-0x63)) + (b2-0x62)`.

### The command set — EXTRACTED

Thirteen **one-byte commands**, each a tiny MFC handler that does `push 1; push <ptr>; call 0x408a80`:

| cmd | handler | control ID |   | cmd | handler | control ID |
|---|---|---|---|---|---|---|
| `d` | 0x404c30 | 1008 | | `r` | 0x4093b0 | 1057 |
| `c` | 0x404c20 | 1009 | | `a` | 0x404c10 | 1066 |
| `k` | 0x404cc0 | 1037 | | `p` | 0x404c90 | 1069 |
| `j` | 0x404cb0 | 1038 | | `q` | 0x404ca0 | 1070 |
| `e` | 0x404c40 | 1051 | | `g` | 0x404c70 | 1079 |
| `h` | 0x404c50 | 1052 | | `b`+1 | 0x409590 | 1128 |
| `l` | 0x404c60 | 1053 | | `b`+0 | 0x4095f0 | 1129 |
| `m` | 0x404c80 | 1054 | | | | |

Plus parametrised forms: `'A' + byte` (VA 0x4090af), `'C' + byte` (VA 0x40951f), and a 5-byte
`'b' + <4 bytes>` family with sub-codes 1–4 (VA 0x4095bc…0x40985c).

### The command map — COMPLETE

Parsing the `RT_DIALOG` (DIALOGEX id=102) of both exes gives every control ID a caption, in
English and Chinese. Cross-referencing with the message map above:

| cmd | ID | Button (EN) | 中文 |
|---|---|---|---|
| `d` | 1008 | Last one | 上一个 |
| `c` | 1009 | Next one | 下一个 |
| `e` | 1051 | Play/Pause | 播放/暂停 |
| `h` | 1052 | List loop | 列表循环 |
| `g` | 1079 | Single loop | 单曲播放 |
| `m` | 1054 | Brightness+ | 增加亮度 |
| `l` | 1053 | Brightness− | 降低亮度 |
| `p` | 1069 | CW adjust | 顺时针调节 |
| `q` | 1070 | CCW adjust | 逆时针调节 |
| `a` | 1066 | On/Off | 运行/休眠 |
| `r` | 1057 | Wi-Fi config | 修改Wi-Fi信息 |
| `k` | 1037 | ⚠️ **Clear Cache** | 垃圾清理 |
| `j` | 1038 | ☠️ **Format Disk** | 格式化 |

> **Correction.** An earlier note in this file claimed `r` was the picture-duration command,
> inferred from the `5-30(S)` string sitting next to its payload in `.rdata`. That was wrong —
> `r` is **Wi-Fi config**; the string merely happened to be adjacent. Read the resources, don't
> infer from proximity.

**`j` erases the card.** Now that it is identified, it can be given a guard rather than avoided.

Non-command buttons on the same dialog: `1000` Decode video (this is our `BinEncoder`),
`1014` "Uplode file(s)" [sic], `1021` Disconnect, `1001` Unconnected (status), `1085`/`1087`/`1089`
the picture-duration box ("How long the picture play" / "Input 5-30(S)"), and dialog `140`
"Revise WIFI information" (new SSID / password, old password check).

### The parametrised `b` family — a whole clock feature

The 5-byte `'b'` command is `'b', value, ?, ?, subcode`, and the dialog reveals it drives a
**clock mode the manual never mentions**:

| subcode | control | values |
|---|---|---|
| 2 | Clock On/Off (1128/1129) | 1 = On, 0 = Off |
| 3 | Needle colour (1130/1131) | White / Black |
| 4 | Dial style (1132–1135) | 0 Digital, 1 Symbol, 2 Constellation, 3 Zodiac |

Plus "Set time" H/M/S and "Set the Power-on display time".

---

## FINDINGS — from the factory SD-card backup (2026-07-16)

The factory card shipped **12 demo `.BIN` clips** plus the vendor toolchain. Analysing
the real bytes (read-only; nothing executed) cracked most of the container.

### Confirmed

| Fact | Evidence |
|---|---|
| **Frame = 129 024 bytes** | 10 of 12 demo files are *exact* multiples (103, 119, 121, 139, 144, 167, 174, 181, 218, 362 frames). |
| **No global header** (most files) | Files begin with zeroed pixel data; no magic bytes. |
| **A 20-byte extra chunk exists on some files** | `房子.BIN` and `马里奥.BIN` are `n×129024 + 20`. Different encoder version. |
| **Row/slice stride = 252 bytes** | Autocorrelation minimum by a wide margin (32.09 vs ~39 for neighbours). |
| **512 slices per frame** | 129024 / 252 = 512 exactly. |
| **252 B = 224 LEDs × 9 bits** | Exact, zero padding. ⇒ **3-bit RGB (512 colours)**, matching the 224-bead spec. |
| **Data is bit-planar, not packed-per-pixel** | Plane densities rise monotonically per triple (0.175→0.296→0.430) and agreement clusters as {0,3,6}/{1,4,7}/{2,5,8} = the MSB/mid/LSB tiers across R,G,B. |
| **Best layout so far: 2 arms** | Slice = 2 × 126 B; each arm = 9 planes × 14 B = 112 LEDs. Scored best on image smoothness (1.67 vs 2.41 for a flat 224-LED read). Matches the hardware: **F2 = 2 blades, 224 = 2 × 112**. |

### Still open

The **exact bit→channel mapping and the angular/radial mapping**. Decoded frames show
coherent large-scale structure but retain a systematic *arc/precession* artifact, so
one mapping (plane order, LED direction, or the arm↔angle relationship) is still off.
Guessing permutations has hit diminishing returns — **the test patterns settle this in
one shot** (`solid_red` fixes channels; `center_dot`/`ring_mid` fix the radial axis;
`spoke_0deg` fixes the angular axis).

### About the vendor toolchain

- App is **MFC, 32-bit x86**, built on **OpenCV 3.1.0** + **FreeImage** (they shipped
  `FreeImage.h`, `.lib` and the OpenCV cmake files by mistake).
- It **bundles `ffmpeg.exe` (55 MB)** and shells out to it — `Creat_Mp3_File.bat`
  contains a literal `ffmpeg -i "…" -f mp3 -y -vn "…"`. So video decode is ffmpeg;
  only the frame→`.bin` step is theirs.
- Static string analysis yielded little (`The file to be uploaded(*.BIN)|*.BIN|…`,
  `ffmpeg -i "`); the binary looks packed and builds command lines dynamically.

### The V13.0 zip that would not extract — solved

`电脑软件V13.0(Windows App V13.0).zip` is **not corrupt** (`unzip -t` → *no errors*).
Its entry names are **GBK/CP936-encoded without the UTF-8 flag**, which Windows
Explorer's built-in ZIP handler cannot decode — hence the failure. Extract it with a
codepage-aware tool (7-Zip → set codepage 936) or the GBK-decoding snippet used here.
It is now extracted to `backup files from factory/Windows App V13.0 (extracted)/`
with correct names and **no execute permission**.

> ⚠ Treat every file under `backup files from factory/` as **untrusted**. It is
> gitignored. Nothing from it is ever executed by this project.

---

## What I need from you (the capture kit)

Everything lands in `tools/re/captures/`. Nothing here is committed (it is
`.gitignore`d) — these are raw research artifacts.

### A. Format samples — the high-value one

I generate a set of **test-pattern videos** (`tools/re/gen_patterns.sh`) engineered so
that the encoder's output bytes become trivially decodable when we diff them. For
**each** pattern MP4 you:

1. Open the vendor Windows encoder, select device **42-F2**.
2. "Decodificar / Convert Video" → pick the pattern MP4.
3. When it shows the **red framing ring**, use **"Fit / center, max zoom-out"** so the
   whole square maps to the circle the same way every time (consistency matters more
   than perfection).
4. Name the output exactly like the input (e.g. `solid_red.bin`) and drop it in
   `tools/re/captures/format/`.

The patterns and *why each one exists* are listed below — encoding all of them takes
~15 min and pins down almost the entire format.

### B. Existing SD samples

Copy whatever `.bin` files already shipped on the TF card into
`tools/re/captures/sd/`, plus a recursive listing:

```bash
# from the SD card root
find . -type f -exec ls -l {} \; > tools/re/captures/sd_listing.txt
```

Also list the vendor **software** folder (the SD `.exe` + DLLs) into
`tools/re/captures/software_listing.txt` — filenames, sizes, dates. Do **not** run or
share the `.exe` itself; the listing is enough to understand structure.

### C. WiFi capture (for the upload protocol)

1. Connect your computer to the fan's AP (`3DCircle_…`, open, no password).
2. Find the fan's IP: after joining, run `ipconfig` / `ifconfig` and note your address
   and gateway (the fan is usually the gateway or a fixed `192.168.x.1`).
3. Start a capture on that WiFi interface:
   - Wireshark → select the interface, or
   - `sudo tcpdump -i <iface> -w tools/re/captures/upload.pcap`
4. Upload **one small, known** `.bin` (e.g. `solid_red.bin`) via the vendor app **and**
   again via the Windows exe (do both — they may differ). Note the wall-clock time you
   hit "upload" for each.
5. Stop the capture. Save `upload.pcap` (+ a note of the fan IP/port you saw) in
   `tools/re/captures/`.

> If you can, also grab a capture of the app **listing/playing** files — it reveals the
> command channel, not just bulk upload.

---

## The test patterns and what each reveals

`tools/re/gen_patterns.sh` writes these to `tools/re/patterns/` (short, 1–2 s each):

| Pattern | Reveals |
|---|---|
| `black`, `white` | File size floor, header size, and whether "off" is `0x00`. Byte count → frames × angular × radial × bpp. |
| `solid_red/green/blue` | **Channel order** (RGB vs GRB/BRG — POV strips are often GRB) and bytes-per-pixel. |
| `half_bright_white` | **Gamma / brightness curve** (is 50 % grey `0x80`, or gamma-corrected?). |
| `center_dot` | Where the **center** maps in the byte stream. |
| `ring_mid` (one circle at r≈0.5) | **Radial axis** direction & scale (which byte index = which LED). |
| `spoke_0deg` (one radial line) | **Angular axis** direction, start angle, and how many angular slices are stored. |
| `radial_gradient` | Confirms radius mapping continuously. |
| `angular_gradient` | Confirms angle mapping continuously. |
| `two_frames` (red then blue) | **Frame boundary & frame count** encoding; whether there's a per-frame header. |
| `framecount_10` (10 distinct frames) | Frame-rate / frame-count fields in the header. |

Diffing e.g. `solid_red` vs `solid_green` isolates the color bytes; `black` vs
`center_dot` isolates one pixel's offset; `two_frames` reveals the frame stride.

---

## Tools in this folder

```
tools/re/
├── gen_patterns.sh   # generate the test-pattern MP4s (uses ffmpeg from the docker image)
├── binspect.py       # inspect / diff / render .bin files (pure stdlib, no pip installs)
├── patterns/         # generated inputs           (gitignored)
├── captures/         # YOUR artifacts land here    (gitignored)
└── out/              # analysis output (PNGs)      (gitignored)
```

### binspect.py

```bash
# 1) Structure guess: factor the payload, propose (header, frames, angular, radial, bpp)
python3 tools/re/binspect.py analyze tools/re/captures/format/solid_red.bin --radial 224

# 2) Byte diff two files that differ in one known way (→ isolates the changed field)
python3 tools/re/binspect.py diff  black.bin solid_red.bin

# 3) Render a frame as a rectangular angular×radial image, and as a circular projection,
#    once we have candidate params (iterate order/direction until the picture is right)
python3 tools/re/binspect.py render solid_red.bin --header 0 --angular 400 --radial 224 \
        --order GRB --frame 0 --out tools/re/out/red.png
```

`analyze` and `diff` need **no** parameters — run them the moment the first `.bin`
arrives and they already narrow the format hard. `render` is the iterative step where
we lock direction/order/gamma until the known pattern renders correctly.

---

## Roadmap once artifacts arrive

1. **Decode the format** (`binspect analyze/diff/render`) → write it up as
   `src/HoloFan.Device/BinFormat` and prove it round-trips a pattern.
2. **Build the encoder** — extend `HoloFan.Core`'s pipeline so that instead of (or in
   addition to) an MP4 we emit a `.bin` for a chosen `DeviceModel`.
3. **Decode the protocol** (from `upload.pcap`) → implement `FanClient` (connect to AP,
   push `.bin`, plus list/play/brightness/rotate commands seen in the capture).
4. **Wire into the web app** — a "Send to fan" button (WiFi) and a "Download .bin"
   button (SD card) next to the existing "Download MP4".

`src/HoloFan.Device/` already holds the skeleton for steps 2–3.
