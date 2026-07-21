#!/usr/bin/env python3
"""
binspect.py — inspect, diff and render the fan's proprietary .bin files.

Pure standard library (struct + zlib), so it runs anywhere with Python 3 and needs
no `pip install`. See REVERSE_ENGINEERING.md for the workflow.

Subcommands
-----------
  analyze FILE [--radial 224]
      Guess the container layout: header size, bytes-per-pixel, and how the payload
      factors into frames × angular × radial. Prints magic bytes and entropy.

  diff A B
      Byte-level difference of two encodes that vary in ONE known way (e.g. red vs
      green) — isolates exactly which bytes carry that field.

  render FILE --angular N [--radial 224] [--header H] [--bpp 3] [--order RGB]
             [--frame 0] [--polar] [--out out.png]
      Render one frame as a rectangular angular×radial image (and optionally a polar
      projection). Iterate --order / --header / direction until a known pattern
      renders correctly — that's the format cracked.
"""
from __future__ import annotations

import argparse
import math
import struct
import sys
import zlib
from collections import Counter
from pathlib import Path


# --------------------------------------------------------------------------- io
def load(path: str) -> bytes:
    return Path(path).read_bytes()


def entropy(data: bytes) -> float:
    if not data:
        return 0.0
    counts = Counter(data)
    n = len(data)
    return -sum((c / n) * math.log2(c / n) for c in counts.values())


def hexrow(data: bytes) -> str:
    return " ".join(f"{b:02x}" for b in data)


# ----------------------------------------------------------------------- png out
def write_png(path: str, width: int, height: int, rgb: bytes) -> None:
    """Minimal RGB8 PNG writer (no dependencies)."""
    assert len(rgb) == width * height * 3, "rgb buffer size mismatch"
    raw = bytearray()
    stride = width * 3
    for y in range(height):
        raw.append(0)  # filter type 0 (None)
        raw.extend(rgb[y * stride:(y + 1) * stride])

    def chunk(tag: bytes, body: bytes) -> bytes:
        return (struct.pack(">I", len(body)) + tag + body
                + struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)  # 8-bit, colour type 2 (RGB)
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", ihdr)
           + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
           + chunk(b"IEND", b""))
    Path(path).write_bytes(png)


# ------------------------------------------------------------------- subcommands
def cmd_analyze(args) -> None:
    data = load(args.file)
    size = len(data)
    print(f"file        : {args.file}")
    print(f"size        : {size} bytes")
    print(f"entropy     : {entropy(data):.3f} bits/byte "
          f"({'looks compressed/encrypted' if entropy(data) > 7.5 else 'looks raw/structured'})")
    print(f"first 32B   : {hexrow(data[:32])}")
    print(f"last 16B    : {hexrow(data[-16:])}")

    # Printable magic?
    magic = bytes(b for b in data[:8] if 32 <= b < 127)
    if len(magic) >= 3:
        print(f"ascii head  : {magic!r}")

    radial = args.radial
    print(f"\nLayout candidates (assuming {radial} radial LEDs):")
    print(f"{'header':>7} {'bpp':>4} {'angular_total':>13} {'hint':>28}")
    found = False
    for bpp in (3, 4, 2):
        for header in (0, 4, 8, 12, 16, 24, 32, 48, 64, 96, 128, 256, 512, 1024):
            rem = size - header
            if rem <= 0 or rem % (radial * bpp):
                continue
            cols = rem // (radial * bpp)          # total angular columns across all frames
            hint = _factor_hint(cols)
            print(f"{header:>7} {bpp:>4} {cols:>13} {hint:>28}")
            found = True
    if not found:
        print("  (no clean split — try a different --radial, or the payload is compressed)")
        print("  divisors of size near 224:", _divisors_near(size, radial))


def _factor_hint(cols: int) -> str:
    """Suggest frames×angular splits for a total column count."""
    for ang in (2000, 1000, 720, 512, 480, 400, 360, 256, 200):
        if cols % ang == 0:
            return f"{cols // ang} frames × {ang}"
    return "prime-ish / unknown split"


def _divisors_near(size: int, radial: int) -> list[int]:
    out = []
    for h in range(0, 2049):
        rem = size - h
        for d in range(radial - 4, radial + 5):
            if rem > 0 and rem % d == 0:
                out.append(d)
    return sorted(set(out))


def cmd_diff(args) -> None:
    a, b = load(args.a), load(args.b)
    print(f"A {args.a}: {len(a)} bytes")
    print(f"B {args.b}: {len(b)} bytes")
    if len(a) != len(b):
        print(f"⚠ sizes differ by {abs(len(a) - len(b))} bytes "
              "(frame count / header differs — informative on its own)")
    n = min(len(a), len(b))
    diffs = [i for i in range(n) if a[i] != b[i]]
    print(f"differing bytes in common region: {len(diffs)} / {n} "
          f"({100 * len(diffs) / n:.2f}%)")
    if not diffs:
        print("identical in common region.")
        return
    print(f"first diff at offset {diffs[0]} (0x{diffs[0]:x})")

    # Collapse into contiguous runs to reveal the changed field(s).
    runs = []
    start = prev = diffs[0]
    for i in diffs[1:]:
        if i == prev + 1:
            prev = i
        else:
            runs.append((start, prev))
            start = prev = i
    runs.append((start, prev))
    print(f"changed regions ({len(runs)}):")
    for s, e in runs[:20]:
        print(f"  [{s:>8}..{e:<8}] len {e - s + 1:<6} "
              f"A={hexrow(a[s:min(e + 1, s + 6)])}  B={hexrow(b[s:min(e + 1, s + 6)])}")
    if len(runs) > 20:
        print(f"  … and {len(runs) - 20} more runs")


_ORDER = {"RGB": (0, 1, 2), "RBG": (0, 2, 1), "GRB": (1, 0, 2),
          "GBR": (2, 0, 1), "BRG": (1, 2, 0), "BGR": (2, 1, 0)}


def cmd_render(args) -> None:
    data = load(args.file)
    bpp, radial, angular = args.bpp, args.radial, args.angular
    frame_bytes = angular * radial * bpp
    off = args.header + args.frame * frame_bytes
    block = data[off:off + frame_bytes]
    if len(block) < frame_bytes:
        sys.exit(f"frame {args.frame} out of range: need {frame_bytes} bytes at "
                 f"offset {off}, file has {len(data)}")

    ri, gi, bi = _ORDER[args.order.upper()]
    # Rectangular map: width = angular (columns / angle), height = radial (LED index).
    rgb = bytearray(angular * radial * 3)
    for col in range(angular):
        for row in range(radial):
            p = (col * radial + row) * bpp        # assumes column-major (angle outer)
            src = block[p:p + bpp]
            o = (row * angular + col) * 3
            rgb[o] = src[ri]; rgb[o + 1] = src[gi]; rgb[o + 2] = src[bi]
    out = args.out or f"{Path(args.file).stem}_f{args.frame}.png"
    write_png(out, angular, radial, bytes(rgb))
    print(f"wrote rectangular map: {out}  ({angular}×{radial}, order {args.order})")

    if args.polar:
        _render_polar(bytes(rgb), angular, radial, out.replace(".png", "_polar.png"))


def _render_polar(rect_rgb: bytes, angular: int, radial: int, out: str) -> None:
    """Project the angular×radial map onto a disc for a human-readable preview."""
    d = radial * 2
    disc = bytearray(d * d * 3)
    for y in range(d):
        for x in range(d):
            dx, dy = x - radial, y - radial
            r = math.hypot(dx, dy)
            if r >= radial:
                continue
            ang = (math.atan2(dy, dx) + math.pi) / (2 * math.pi)  # 0..1
            col = int(ang * angular) % angular
            row = min(radial - 1, int(r))
            s = (row * angular + col) * 3
            o = (y * d + x) * 3
            disc[o:o + 3] = rect_rgb[s:s + 3]
    write_png(out, d, d, bytes(disc))
    print(f"wrote polar preview  : {out}  ({d}×{d})")


# ------------------------------------------------------------------------- main
def main() -> None:
    p = argparse.ArgumentParser(description="Inspect/diff/render fan .bin files.")
    sub = p.add_subparsers(dest="cmd", required=True)

    a = sub.add_parser("analyze"); a.add_argument("file"); a.add_argument("--radial", type=int, default=224)
    a.set_defaults(func=cmd_analyze)

    d = sub.add_parser("diff"); d.add_argument("a"); d.add_argument("b")
    d.set_defaults(func=cmd_diff)

    r = sub.add_parser("render")
    r.add_argument("file")
    r.add_argument("--angular", type=int, required=True)
    r.add_argument("--radial", type=int, default=224)
    r.add_argument("--header", type=int, default=0)
    r.add_argument("--bpp", type=int, default=3)
    r.add_argument("--order", default="RGB")
    r.add_argument("--frame", type=int, default=0)
    r.add_argument("--polar", action="store_true")
    r.add_argument("--out")
    r.set_defaults(func=cmd_render)

    args = p.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
