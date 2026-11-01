#!/usr/bin/env bash
# Regenerates tests/fixtures/alac-apple: ALAC streams made by Apple's own encoder (https://github.com/macosforge/alac, Apache-2.0; fetched and built here as a development tool,
# nothing of it is linked or shipped) for what FFmpeg's encoder never writes: 20 and 32 bit audio, other frame sizes, a partial last frame, the 31-coefficient mode,
# matrixed stereo and shifted-out low bytes. alac-mux.py wraps the packets in MP4. <name>.pcm.gz is the source signal (see generate-alac.sh for its layout).
# Needs git, g++, python3 and network access.
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$here/alac-apple"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

git clone --quiet --depth 1 https://github.com/macosforge/alac.git "$work/alac"
codec="$work/alac/codec"
g++ -O1 -w -I"$codec" -o "$work/driver" "$here/alac-apple-driver.cpp" "$codec"/ALACEncoder.cpp "$codec"/ALACBitUtilities.c "$codec"/EndianPortable.c \
    "$codec"/ag_enc.c "$codec"/ag_dec.c "$codec"/dp_enc.c "$codec"/dp_dec.c "$codec"/matrix_enc.c "$codec"/matrix_dec.c

python3 -I - "$work" <<'PY'
import math, struct, sys, gzip
work = sys.argv[1]
def lcg(seed):
    state = seed
    while True:
        state = (state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
        yield (state >> 33) & 0x7FFFFFFF
def make(name, rate, channels, bits, frames_count, mode="tone"):
    noise = lcg(len(name) * 104729 + channels)
    peak = (1 << (bits - 1)) - 1
    with open(f"{work}/{name}.s32", "wb") as f, gzip.GzipFile(f"{work}/{name}.pcm.gz", "wb", mtime=0) as g:
        for i in range(frames_count):
            t = i / rate
            for c in range(channels):
                r = (next(noise) / 0x7FFFFFFF) * 2 - 1
                if mode == "noise":
                    v = 0.97 * r
                elif mode == "lowbits":   # the low byte of every sample is zero: the encoder shifts it out
                    v = 0.4 * math.sin(2 * math.pi * (300 * (c + 1)) * t) + 0.01 * r
                else:
                    v = 0.4 * math.sin(2 * math.pi * (180 * (c + 1)) * t) + 0.15 * math.sin(2 * math.pi * 2500.0 * t + c) + 0.03 * r
                s = max(-peak - 1, min(peak, int(round(v * peak))))
                if mode == "lowbits":
                    s = (s >> 8) << 8
                packed = struct.pack("<i", s << (32 - bits))
                f.write(packed)
                g.write(packed)
make("a20_stereo_44k", 44100, 2, 20, 11000)
make("a20_mono_22k", 22050, 1, 20, 9000)
make("a24_stereo_44k_lowbits", 44100, 2, 24, 10000, "lowbits")
make("a32_stereo_48k", 48000, 2, 32, 10000)
make("a32_stereo_lowbits", 48000, 2, 32, 9000, "lowbits")
make("a32_mono_noise", 48000, 1, 32, 6000, "noise")
make("a16_stereo_frame1000", 44100, 2, 16, 5300)
make("a16_stereo_frame16384", 44100, 2, 16, 40000)
make("a24_6ch_44k", 44100, 6, 24, 9000)
PY

enc() { # name rate channels bits [frame size] [faststart]
    local name=$1 rate=$2 ch=$3 bits=$4 frame=${5:-4096} fast=${6:-}
    "$work/driver" "$work/$name.s32" "$work/$name.alacpk" "$ch" "$bits" "$rate" "$frame"
    python3 -I "$here/alac-mux.py" "$work/$name.alacpk" "$out/$name.m4a" "$ch" "$bits" "$rate" $fast
    cp "$work/$name.pcm.gz" "$out/$name.pcm.gz"
}
enc a20_stereo_44k 44100 2 20
enc a20_mono_22k 22050 1 20 4096 faststart
enc a24_stereo_44k_lowbits 44100 2 24
enc a32_stereo_48k 48000 2 32
enc a32_stereo_lowbits 48000 2 32
enc a32_mono_noise 48000 1 32
enc a16_stereo_frame1000 44100 2 16 1000
enc a16_stereo_frame16384 44100 2 16 16384
enc a24_6ch_44k 44100 6 24
ls -l "$out"
