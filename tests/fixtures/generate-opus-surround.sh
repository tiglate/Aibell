#!/usr/bin/env bash
# Regenerates tests/fixtures/opus-surround: multichannel Ogg Opus files (mapping family 1: 4, 6 and 8 channels) encoded by FFmpeg's libopus (a development tool only;
# nothing of it is linked or shipped), plus <name>.f32.gz, the float samples FFmpeg gets from libopus's own decoder. Every channel carries its own tone, so a
# channel in the wrong place shows. The decoder is written from RFC 6716 and RFC 7845.
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$here/opus-surround"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

python3 -I - "$work" <<'PY'
import math, struct, sys
work = sys.argv[1]
def make(channels, seconds):
    rate = 48000
    n = int(rate * seconds)
    state = 515 + channels
    with open(f"{work}/ch{channels}.f32", "wb") as f:
        for i in range(n):
            t = i / rate
            for c in range(channels):
                state = (state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
                noise = ((state >> 33) / 0x7FFFFFFF) * 2 - 1
                v = 0.25 * math.sin(2 * math.pi * (250 * (c + 1)) * t) + 0.02 * noise
                f.write(struct.pack("<f", v))
for channels in (4, 6, 8):
    make(channels, 0.7)
PY

declare -A layouts=([4]=quad [6]=5.1 [8]=7.1)
for channels in 4 6 8; do
    ffmpeg -hide_banner -loglevel error -y -f f32le -ar 48000 -ch_layout "${layouts[$channels]}" -i "$work/ch$channels.f32" -c:a libopus -mapping_family 1 -b:a $((channels * 40))k -frame_duration 20 "$out/surround_${channels}ch.opus"
    ffmpeg -hide_banner -loglevel error -y -c:a libopus -i "$out/surround_${channels}ch.opus" -f f32le -c:a pcm_f32le - | gzip -9n > "$out/surround_${channels}ch.f32.gz"
done
echo "Generated Opus surround fixtures in $out"
