#!/usr/bin/env bash
# Regenerates tests/fixtures/mp3: MP3 files encoded by FFmpeg's libmp3lame (a development tool only; nothing of it is linked or shipped) from synthetic signals made
# here, plus <name>.f32.gz, the float samples FFmpeg's own MP3 decoder produces for each file. They are a black-box yardstick for the decoder (which is written from
# ISO/IEC 11172-3, ISO/IEC 13818-3 and the Fraunhofer description of Layer III): the two must agree to well within the 16-bit quantization step.
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$here/mp3"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

python3 -I - "$work" <<'PY'
import math, struct, sys
work = sys.argv[1]
def make(name, rate, channels, seconds, clicks):
    n = int(rate * seconds)
    state = 99 + channels * 31 + rate
    with open(f"{work}/{name}.f32", "wb") as f:
        for i in range(n):
            t = i / rate
            for c in range(channels):
                state = (state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
                noise = ((state >> 33) / 0x7FFFFFFF) * 2 - 1
                glide = math.sin(2 * math.pi * (200 + 80 * math.sin(2 * math.pi * 3 * t)) * (c + 1) * t)
                v = 0.30 * glide + 0.10 * math.sin(2 * math.pi * min(1800, rate * 0.2) * t + c) + 0.03 * noise
                if clicks and i % int(rate * 0.08) < 6:
                    v += 0.5 * (1 if (i // 2) % 2 == 0 else -1)
                f.write(struct.pack("<f", max(-1.0, min(1.0, v))))
for rate in (44100, 48000, 32000, 22050, 24000, 16000, 11025, 12000, 8000):
    make(f"stereo_{rate}", rate, 2, 0.7, True)
    make(f"mono_{rate}", rate, 1, 0.7, True)
PY

encode() { # source rate channels outname lame-options...
    local source="$1" rate="$2" channels="$3" name="$4"
    shift 4
    ffmpeg -hide_banner -loglevel error -y -f f32le -ar "$rate" -ac "$channels" -i "$work/$source.f32" -c:a libmp3lame "$@" "$out/$name.mp3"
    ffmpeg -hide_banner -loglevel error -y -i "$out/$name.mp3" -f f32le -c:a pcm_f32le - | gzip -9n > "$out/$name.f32.gz"
}
encode stereo_44100 44100 2 mpeg1_44k_stereo_128k -b:a 128k
encode stereo_44100 44100 2 mpeg1_44k_joint_96k -b:a 96k -joint_stereo 1
encode stereo_44100 44100 2 mpeg1_44k_320k -b:a 320k
encode stereo_44100 44100 2 mpeg1_44k_vbr_v2 -q:a 2
encode stereo_44100 44100 2 mpeg1_44k_vbr_v7 -q:a 7
encode stereo_48000 48000 2 mpeg1_48k_stereo_192k -b:a 192k
encode stereo_32000 32000 2 mpeg1_32k_stereo_64k -b:a 64k
encode mono_44100 44100 1 mpeg1_44k_mono_64k -b:a 64k
encode mono_32000 32000 1 mpeg1_32k_mono_48k -b:a 48k
encode stereo_22050 22050 2 mpeg2_22k_stereo_64k -b:a 64k
encode mono_22050 22050 1 mpeg2_22k_mono_32k -b:a 32k
encode stereo_24000 24000 2 mpeg2_24k_stereo_96k -b:a 96k
encode mono_16000 16000 1 mpeg2_16k_mono_24k -b:a 24k
encode mono_11025 11025 1 mpeg25_11k_mono_16k -b:a 16k
encode stereo_12000 12000 2 mpeg25_12k_stereo_32k -b:a 32k
encode mono_8000 8000 1 mpeg25_8k_mono_16k -b:a 16k
encode stereo_44100 44100 2 mpeg1_44k_no_tag -b:a 128k -write_xing 0
encode stereo_44100 44100 2 mpeg1_44k_crc_128k -b:a 128k -error_protection 1
encode stereo_44100 44100 2 mpeg1_44k_reservoir_off_128k -b:a 128k -reservoir 0
# A longer file for the seeking tests (no reference: they compare the decoder with itself).
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=14" -f lavfi -i "anoisesrc=color=pink:amplitude=0.1:sample_rate=44100:duration=14" \
    -filter_complex "[0][1]amix=inputs=2:normalize=0,pan=stereo|c0=c0|c1=0.7*c0" -c:a libmp3lame -b:a 128k "$out/long_44k.mp3"
echo "Generated MP3 fixtures in $out"
