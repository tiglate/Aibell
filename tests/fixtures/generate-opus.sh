#!/usr/bin/env bash
# Regenerates tests/fixtures/opus: Ogg Opus files encoded by FFmpeg's libopus (a development tool only; nothing of it is linked or shipped) from synthetic signals
# made here, plus <name>.f32.gz, the float samples FFmpeg gets from libopus's own decoder for each file. They are a black-box yardstick for the decoder (which is
# written from RFC 6716 and RFC 7845): the two must agree to well within the 16-bit quantization step. The names say which Opus mode the encoder was steered to.
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$here/opus"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

python3 -I - "$work" <<'PY'
import math, struct, sys
work = sys.argv[1]
def make(name, channels, seconds, clicks):
    rate = 48000
    n = int(rate * seconds)
    state = 4242 + channels * 77
    with open(f"{work}/{name}.f32", "wb") as f:
        for i in range(n):
            t = i / rate
            for c in range(channels):
                state = (state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
                noise = ((state >> 33) / 0x7FFFFFFF) * 2 - 1
                glide = math.sin(2 * math.pi * (180 + 90 * math.sin(2 * math.pi * 3 * t)) * (c + 1) * t)
                v = 0.30 * glide + 0.12 * math.sin(2 * math.pi * 1200 * t + c) + 0.03 * noise
                if clicks and i % int(rate * 0.09) < 8:
                    v += 0.5 * (1 if (i // 2) % 2 == 0 else -1)
                f.write(struct.pack("<f", max(-1.0, min(1.0, v))))
make("stereo", 2, 0.6, False)
make("stereo_clicks", 2, 0.6, True)
make("mono", 1, 0.6, True)
PY

encode() { # source channels outname libopus-options...
    local source="$1" channels="$2" name="$3"
    shift 3
    ffmpeg -hide_banner -loglevel error -y -f f32le -ar 48000 -ac "$channels" -i "$work/$source.f32" -c:a libopus "$@" "$out/$name.opus"
    ffmpeg -hide_banner -loglevel error -y -c:a libopus -i "$out/$name.opus" -f f32le -c:a pcm_f32le - | gzip -9n > "$out/$name.f32.gz"
}
encode stereo_clicks 2 celt_stereo_20ms -application audio -b:a 96k -frame_duration 20
encode stereo_clicks 2 celt_stereo_10ms -application audio -b:a 128k -frame_duration 10
encode stereo_clicks 2 celt_stereo_2_5ms -application lowdelay -b:a 128k -frame_duration 2.5
encode mono 1 celt_mono_wb -application lowdelay -b:a 48k -cutoff 8000 -frame_duration 20
encode mono 1 silk_mono_nb -application voip -b:a 10k -cutoff 4000 -frame_duration 20
encode mono 1 silk_mono_wb_60ms -application voip -b:a 20k -cutoff 8000 -frame_duration 60
encode stereo 2 silk_stereo_mb -application voip -b:a 24k -cutoff 6000 -frame_duration 40
encode stereo_clicks 2 hybrid_stereo_swb -application voip -b:a 40k -cutoff 12000 -frame_duration 20
encode mono 1 hybrid_mono_fb -application voip -b:a 32k -cutoff 20000 -frame_duration 10
# A longer file for the seeking tests (no reference: they compare the decoder with itself).
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=14" -f lavfi -i "anoisesrc=color=pink:amplitude=0.1:sample_rate=48000:duration=14" \
    -filter_complex "[0][1]amix=inputs=2:normalize=0,pan=stereo|c0=c0|c1=0.7*c0" -c:a libopus -b:a 64k "$out/long_48k.opus"
echo "Generated Opus fixtures in $out"
