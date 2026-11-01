#!/usr/bin/env bash
# Regenerates tests/fixtures/vorbis: Ogg Vorbis files encoded by FFmpeg's libvorbis (a development tool only; nothing of it is linked or shipped) from synthetic
# signals made here, plus <name>.f32.gz, the float samples FFmpeg's own Vorbis decoder produces for each file. They are a black-box yardstick for the
# decoder (which is written from the Vorbis I specification): the two must agree to well within the 16-bit quantization step.
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$here/vorbis"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

python3 -I - "$work" <<'PY'
import math, struct, sys
work = sys.argv[1]
def make(name, rate, channels, seconds, clicks):
    n = int(rate * seconds)
    state = 12345 + channels * 77 + rate
    rows = []
    for i in range(n):
        t = i / rate
        row = []
        for c in range(channels):
            state = (state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
            noise = ((state >> 33) / 0x7FFFFFFF) * 2 - 1
            v = 0.35 * math.sin(2 * math.pi * 330 * (c + 1) * t) + 0.15 * math.sin(2 * math.pi * 2500 * t + c) + 0.04 * noise
            if clicks and i % int(rate * 0.07) < 6:
                v += 0.6 * (1 if (i // 2) % 2 == 0 else -1)
            row.append(max(-1.0, min(1.0, v)))
        rows.append(row)
    with open(f"{work}/{name}.f32", "wb") as f:
        for row in rows:
            for v in row:
                f.write(struct.pack("<f", v))
make("stereo_44k", 44100, 2, 0.5, False)
make("stereo_44k_clicks", 44100, 2, 0.5, True)
make("mono_22k", 22050, 1, 0.5, True)
make("surround_48k", 48000, 6, 0.3, True)
make("stereo_8k", 8000, 2, 0.5, True)
make("stereo_96k", 96000, 2, 0.3, False)
PY

encode() { # name rate channels quality
    ffmpeg -hide_banner -loglevel error -y -f f32le -ar "$2" -ac "$3" -i "$work/$1.f32" -c:a libvorbis -q:a "$4" "$out/$5.ogg"
    ffmpeg -hide_banner -loglevel error -y -i "$out/$5.ogg" -f f32le -c:a pcm_f32le - | gzip -9n > "$out/$5.f32.gz"
}
encode stereo_44k 44100 2 3 stereo_44k_q3
encode stereo_44k_clicks 44100 2 5 stereo_44k_clicks_q5
encode stereo_44k_clicks 44100 2 0 stereo_44k_clicks_q0
encode mono_22k 22050 1 4 mono_22k_q4
encode surround_48k 48000 6 4 surround_48k_q4
encode stereo_8k 8000 2 2 stereo_8k_q2
encode stereo_96k 96000 2 6 stereo_96k_q6
# A longer file for the seeking tests (no reference: they compare the decoder with itself).
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=14" -f lavfi -i "anoisesrc=color=pink:amplitude=0.1:sample_rate=44100:duration=14" \
    -filter_complex "[0][1]amix=inputs=2:normalize=0,pan=stereo|c0=c0|c1=0.7*c0" -c:a libvorbis -q:a 1 "$out/long_44k_q1.ogg"
echo "Generated Vorbis fixtures in $out"
