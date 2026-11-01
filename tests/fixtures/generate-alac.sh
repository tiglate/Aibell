#!/usr/bin/env bash
# Regenerates tests/fixtures/alac: Apple Lossless files in MP4 (.m4a) encoded by FFmpeg (a development tool only; nothing of it is linked or shipped) from
# synthetic signals made here. The signal itself is the reference: ALAC is lossless, so the decoder must reproduce it bit for bit (<name>.pcm.gz, interleaved
# little-endian 32-bit integers holding the samples shifted up to 32 bits, channels in WAVE order).
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$here/alac"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

python3 -I - "$work" <<'PY'
import math, struct, sys, gzip
work = sys.argv[1]
def lcg(seed):
    state = seed
    while True:
        state = (state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
        yield (state >> 33) & 0x7FFFFFFF
def make(name, rate, channels, bits, seconds, mode="tone"):
    n = int(rate * seconds)
    noise = lcg(len(name) * 7919 + channels)
    peak = (1 << (bits - 1)) - 1
    frames = []
    for i in range(n):
        row = []
        for c in range(channels):
            t = i / rate
            r = (next(noise) / 0x7FFFFFFF) * 2 - 1
            if mode == "noise":
                v = 0.98 * r
            elif mode == "silence":
                v = 0.0 if (i // 3000) % 2 == 0 else 0.3 * math.sin(2 * math.pi * 440 * t + c)
            else:
                v = 0.45 * math.sin(2 * math.pi * (220 * (c + 1)) * t) + 0.2 * math.sin(2 * math.pi * 3000.0 * t + c) + 0.05 * r
            s = max(-peak - 1, min(peak, int(round(v * peak))))
            row.append(s)
        frames.append(row)
    with open(f"{work}/{name}.s32", "wb") as f, gzip.GzipFile(f"{work}/{name}.pcm.gz", "wb", mtime=0) as g:
        for row in frames:
            for s in row:
                f.write(struct.pack("<i", s << (32 - bits)))
                g.write(struct.pack("<i", s << (32 - bits)))
make("s16_stereo_44k", 44100, 2, 16, 0.5)
make("s16_stereo_44k_deep", 44100, 2, 16, 0.5)
make("s16_stereo_noise", 44100, 2, 16, 0.3, "noise")
make("s16_stereo_silence", 44100, 2, 16, 0.5, "silence")
make("s16_mono_8k", 8000, 1, 16, 0.6)
make("s24_stereo_96k", 96000, 2, 24, 0.2)
make("s24_stereo_noise", 48000, 2, 24, 0.2, "noise")
make("s16_3ch_48k", 48000, 3, 16, 0.2)
make("s16_4ch_48k", 48000, 4, 16, 0.2)
make("s16_5ch_48k", 48000, 5, 16, 0.2)
make("s16_6ch_48k", 48000, 6, 16, 0.2)
make("s16_7ch_48k", 48000, 7, 16, 0.2)
make("s16_8ch_48k", 48000, 8, 16, 0.2)
PY

enc() { # name rate channels bits [extra ffmpeg args...]
    local name=$1 rate=$2 ch=$3 bits=$4; shift 4
    local fmt=s32 layout
    [ "$bits" = 16 ] && fmt=s16
    # The layouts ALAC can carry (FFmpeg names): the decoder must bring them back in the order the source had.
    case $ch in
        1) layout=mono ;; 2) layout=stereo ;; 3) layout=3.0 ;; 4) layout=4.0 ;; 5) layout='5.0(side)' ;; 6) layout='5.1(side)' ;; 7) layout=6.1 ;; 8) layout='7.1(wide)' ;;
    esac
    ffmpeg -hide_banner -loglevel error -y -f s32le -ar "$rate" -ch_layout "$layout" -i "$work/$name.s32" -af "aformat=sample_fmts=$fmt" -c:a alac "$@" "$out/$name.m4a"
    cp "$work/$name.pcm.gz" "$out/$name.pcm.gz"
}
enc s16_stereo_44k 44100 2 16
enc s16_stereo_44k_deep 44100 2 16 -min_prediction_order 30 -max_prediction_order 30
enc s16_stereo_noise 44100 2 16
enc s16_stereo_silence 44100 2 16
enc s16_mono_8k 8000 1 16 -compression_level 0
enc s24_stereo_96k 96000 2 24
enc s24_stereo_noise 48000 2 24
for ch in 3 4 5 6 7 8; do enc "s16_${ch}ch_48k" 48000 "$ch" 16; done
ls -l "$out"
