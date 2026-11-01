#!/usr/bin/env bash
# Regenerates tests/fixtures/flac: FLAC files encoded by FFmpeg (a development tool only; nothing of it is linked or shipped) from synthetic
# signals made here. The signal itself is the reference: FLAC is lossless, so the decoder must reproduce it bit for bit (<name>.pcm.gz, interleaved
# little-endian 32-bit integers holding the samples shifted up to 32 bits).
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$here/flac"
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
def make(name, rate, channels, bits, seconds, wasted=0, silence=False):
    n = int(rate * seconds)
    noise = lcg(len(name) * 7919 + channels)
    peak = (1 << (bits - 1)) - 1
    frames = []
    for i in range(n):
        row = []
        for c in range(channels):
            t = i / rate
            v = 0.45 * math.sin(2 * math.pi * (220 * (c + 1)) * t) + 0.2 * math.sin(2 * math.pi * 3000.0 * t + c) + 0.05 * ((next(noise) / 0x7FFFFFFF) * 2 - 1)
            if silence and (i // 2000) % 3 == 1:
                v = 0.0
            s = int(round(v * peak))
            s = max(-peak - 1, min(peak, s))
            s = (s >> wasted) << wasted
            row.append(s)
        frames.append(row)
    # raw source for the encoder (as 32-bit left-justified) and the reference for the tests
    with open(f"{work}/{name}.s32", "wb") as f:
        for row in frames:
            for s in row:
                f.write(struct.pack("<i", s << (32 - bits)))
    with gzip.GzipFile(f"{work}/{name}.pcm.gz", "wb", mtime=0) as g:
        for row in frames:
            for s in row:
                g.write(struct.pack("<i", s << (32 - bits)))
make("s16_stereo_44k", 44100, 2, 16, 0.4)
make("s16_stereo_44k_silence", 44100, 2, 16, 0.5, silence=True)
make("s16_mono_8k", 8000, 1, 16, 0.5)
make("s24_stereo_96k", 96000, 2, 24, 0.2)
make("s16_surround_48k", 48000, 6, 16, 0.2)
make("s16_wasted_44k", 44100, 2, 16, 0.3, wasted=3)
PY

enc() { # name rate channels bits [extra ffmpeg args...]
    local name=$1 rate=$2 ch=$3 bits=$4; shift 4
    local fmt=s32
    ffmpeg -hide_banner -loglevel error -y -f s32le -ar "$rate" -ac "$ch" -i "$work/$name.s32" -sample_fmt s32 "$@" "$out/$name.flac" 2>/dev/null || \
        ffmpeg -hide_banner -loglevel error -y -f s32le -ar "$rate" -ac "$ch" -i "$work/$name.s32" "$@" "$out/$name.flac"
}
enc s16_stereo_44k 44100 2 16 -sample_fmt s16 -compression_level 5
cp "$work/s16_stereo_44k.pcm.gz" "$out/"
for mode in left_side right_side mid_side indep; do
    ffmpeg -hide_banner -loglevel error -y -f s32le -ar 44100 -ac 2 -i "$work/s16_stereo_44k.s32" -sample_fmt s16 -ch_mode $mode -compression_level 8 "$out/s16_stereo_44k_$mode.flac"
done
for level in 0 3 12; do
    ffmpeg -hide_banner -loglevel error -y -f s32le -ar 44100 -ac 2 -i "$work/s16_stereo_44k.s32" -sample_fmt s16 -compression_level $level "$out/s16_stereo_44k_level$level.flac"
done
ffmpeg -hide_banner -loglevel error -y -f s32le -ar 44100 -ac 2 -i "$work/s16_stereo_44k.s32" -sample_fmt s16 -frame_size 1000 "$out/s16_stereo_44k_block1000.flac"
ffmpeg -hide_banner -loglevel error -y -f s32le -ar 44100 -ac 2 -i "$work/s16_stereo_44k_silence.s32" -sample_fmt s16 "$out/s16_stereo_44k_silence.flac"
cp "$work/s16_stereo_44k_silence.pcm.gz" "$out/"
ffmpeg -hide_banner -loglevel error -y -f s32le -ar 8000 -ac 1 -i "$work/s16_mono_8k.s32" -sample_fmt s16 "$out/s16_mono_8k.flac"
cp "$work/s16_mono_8k.pcm.gz" "$out/"
ffmpeg -hide_banner -loglevel error -y -f s32le -ar 96000 -ac 2 -i "$work/s24_stereo_96k.s32" -sample_fmt s32 "$out/s24_stereo_96k.flac"
cp "$work/s24_stereo_96k.pcm.gz" "$out/"
ffmpeg -hide_banner -loglevel error -y -f s32le -ar 48000 -ac 6 -i "$work/s16_surround_48k.s32" -sample_fmt s16 "$out/s16_surround_48k.flac"
cp "$work/s16_surround_48k.pcm.gz" "$out/"
ffmpeg -hide_banner -loglevel error -y -f s32le -ar 44100 -ac 2 -i "$work/s16_wasted_44k.s32" -sample_fmt s16 "$out/s16_wasted_44k.flac"
cp "$work/s16_wasted_44k.pcm.gz" "$out/"
echo "Generated FLAC fixtures in $out"
