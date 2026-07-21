#!/usr/bin/env bash
#
# gen_patterns.sh — generate the reverse-engineering test-pattern videos.
#
# Each clip is engineered so that the vendor encoder's .bin output becomes easy to
# decode by diffing pairs (see REVERSE_ENGINEERING.md). Feed every clip through the
# vendor Windows encoder (device 42-F2, "Fit" framing) and save the .bin next to it.
#
# ffmpeg is taken from the built docker image so nothing extra needs installing.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/patterns"
IMAGE="${HOLOFAN_IMAGE:-holofan-studio}"
S=480          # square canvas
C=240          # centre
R=240          # outer radius
DUR=2          # seconds

mkdir -p "$OUT"

ff() {
  # ff <output-name> <ffmpeg-args...>
  local name="$1"; shift
  docker run --rm --entrypoint ffmpeg -v "$OUT:/out" "$IMAGE" \
    -hide_banner -loglevel error -y "$@" -pix_fmt yuv420p "/out/$name.mp4"
  echo "  ✓ $name.mp4"
}

echo "Generating test patterns into $OUT (image: $IMAGE) …"

# --- Solids: file-size floor, channel order, bytes-per-pixel, gamma ---------------
ff black             -f lavfi -i "color=black:s=${S}x${S}:r=25"           -t $DUR
ff white             -f lavfi -i "color=white:s=${S}x${S}:r=25"           -t $DUR
ff solid_red         -f lavfi -i "color=0xFF0000:s=${S}x${S}:r=25"        -t $DUR
ff solid_green       -f lavfi -i "color=0x00FF00:s=${S}x${S}:r=25"        -t $DUR
ff solid_blue        -f lavfi -i "color=0x0000FF:s=${S}x${S}:r=25"        -t $DUR
ff half_bright_white -f lavfi -i "color=0x808080:s=${S}x${S}:r=25"        -t $DUR

# --- Geometry: pin the radial & angular axes -------------------------------------
# One bright pixel at the exact centre.
ff center_dot   -f lavfi -i "color=black:s=${S}x${S}:r=25" \
  -vf "drawbox=x=$((C-4)):y=$((C-4)):w=8:h=8:color=white:t=fill" -t $DUR

# A thin ring at r ≈ 0.5 (radial axis direction + scale).
ff ring_mid     -f lavfi -i "color=black:s=${S}x${S}:r=25" \
  -vf "format=gray,geq=lum='if(lt(abs(hypot(X-$C\,Y-$C)-120)\,2)\,255\,0)'" -t $DUR

# A single radial spoke pointing right = angle 0 (angular axis direction + start).
ff spoke_0deg   -f lavfi -i "color=black:s=${S}x${S}:r=25" \
  -vf "drawbox=x=$C:y=$((C-1)):w=$R:h=3:color=white:t=fill" -t $DUR

# Continuous gradients confirm the mappings are linear.
ff radial_gradient  -f lavfi -i "color=black:s=${S}x${S}:r=25" \
  -vf "format=gray,geq=lum='255*hypot(X-$C\,Y-$C)/$R'" -t $DUR
ff angular_gradient -f lavfi -i "color=black:s=${S}x${S}:r=25" \
  -vf "format=gray,geq=lum='255*(atan2(Y-$C\,X-$C)+3.14159265)/6.2831853'" -t $DUR

# --- Time axis: frame boundary, frame count, frame rate --------------------------
# Red for the first second, blue for the second → one clean colour change.
ff two_frames   -f lavfi -i "color=black:s=${S}x${S}:r=2" \
  -vf "format=rgba,geq=r='if(lt(T\,1)\,255\,0)':g='0':b='if(lt(T\,1)\,0\,255)'" -t $DUR

# Ten distinct frames (grey ramp) at 10 fps → frame-count / fps fields.
ff framecount_10 -f lavfi -i "color=black:s=${S}x${S}:r=10" \
  -vf "format=gray,geq=lum='mod(N\,10)*25'" -t 1

echo "Done. Now encode each with the vendor tool (device 42-F2) and save the .bin"
echo "into tools/re/captures/format/ with the SAME base name (e.g. solid_red.bin)."
