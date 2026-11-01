#!/usr/bin/env bash
# Generates the tiny AAC-LC conformance fixtures used by Aibell.Audio.Aac.Tests.
#
# Each case is a short synthetic signal encoded to ADTS by FFmpeg's native AAC encoder (a development tool only; nothing from it is
# redistributed or linked) and decoded by FFmpeg's AAC decoder to 16-bit PCM as the reference. The tests decode the .aac with
# Aibell.Audio.Aac and compare against the .pcm. The signals contain no third-party recording.
set -euo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$root/aac"
rm -rf "$out"
mkdir -p "$out"
command -v ffmpeg >/dev/null || { echo "ffmpeg is required to generate AAC fixtures." >&2; exit 1; }

# name rate channels bitrate tool-flags source-expression
make() {
    local name="$1" rate="$2" channels="$3" bitrate="$4" flags="$5" source="$6" duration="${7:-0.2}"
    # shellcheck disable=SC2086
    ffmpeg -hide_banner -loglevel error -y -f lavfi -i "$source" -t "$duration" -ar "$rate" -ac "$channels" \
        -c:a aac -profile:a aac_low -b:a "$bitrate" $flags -f adts "$out/$name.aac"
    ffmpeg -hide_banner -loglevel error -y -c:a aac -i "$out/$name.aac" -f s16le -acodec pcm_s16le - | gzip -9n > "$out/$name.pcm.gz"
}

none="-aac_ms 0 -aac_is 0 -aac_pns 0 -aac_tns 0"
sweep_stereo="aevalsrc=0.5*sin(2*PI*(300+1500*t)*t)|0.4*sin(2*PI*(700+900*t)*t):s=48000"
noise_stereo="anoisesrc=color=pink:amplitude=0.3:seed=1:sample_rate=48000,aformat=channel_layouts=stereo"
click_stereo="aevalsrc=0.8*sin(2*PI*220*t)*exp(-30*mod(t\,0.1))+0.3*sin(2*PI*3000*t)*lt(mod(t\,0.1)\,0.004)|0.6*sin(2*PI*330*t)*exp(-30*mod(t\,0.1)):s=48000"
joint_stereo="aevalsrc=0.4*sin(2*PI*440*t)+0.2*sin(2*PI*1330*t)|0.4*sin(2*PI*440*t)+0.1*sin(2*PI*2000*t):s=48000"

make long_sweep_44k_stereo 44100 2 96k "$none" "$sweep_stereo"
make long_noise_48k_stereo 48000 2 128k "$none" "$noise_stereo"
make short_clicks_22k_stereo 22050 2 128k "$none" "$click_stereo" 0.4
make ms_22k_stereo 22050 2 96k "-aac_ms 1 -aac_is 0 -aac_pns 0 -aac_tns 0" "$joint_stereo"
make is_44k_stereo 44100 2 32k "-aac_ms 0 -aac_is 1 -aac_pns 0 -aac_tns 0" "$joint_stereo"
# Amplitude-modulated noise is what makes FFmpeg's encoder switch TNS on: 400 Hz gives TNS in long blocks, 40 Hz in short blocks.
make tns_long_44k_mono 44100 1 128k "-aac_ms 0 -aac_is 0 -aac_pns 0 -aac_tns 1" "anoisesrc=color=white:amplitude=0.5:seed=3:sample_rate=44100,aeval='val(0)*(0.5+0.5*sin(2*PI*400*t))':c=same" 0.5
make tns_short_44k_mono 44100 1 128k "-aac_ms 0 -aac_is 0 -aac_pns 0 -aac_tns 1" "anoisesrc=color=white:amplitude=0.5:seed=3:sample_rate=44100,aeval='val(0)*(0.5+0.5*sin(2*PI*40*t))':c=same" 0.3
make pns_22k_stereo 22050 2 48k "-aac_ms 0 -aac_is 0 -aac_pns 1 -aac_tns 0" "$noise_stereo"
for rate in 8000 11025 12000 16000 22050 24000 32000 44100 48000 64000 88200 96000; do
    make "rate_${rate}_mono" "$rate" 1 64k "$none" "aevalsrc=0.5*sin(2*PI*(200+1000*t)*t)+0.1*sin(2*PI*2400*t):s=48000" 0.25
done
# The same bytes in a real muxer's MPEG-TS packaging, to check the demuxer against something other than our own test muxer.
ffmpeg -hide_banner -loglevel error -y -i "$out/ms_22k_stereo.aac" -c:a copy -f mpegts "$out/ms_22k_stereo.ts"
# MPEG audio layer II with CRC protection: the same CRC-16 as ADTS, used to check the polynomial and preset against an independent encoder.
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:duration=0.1,aformat=channel_layouts=stereo" -c:a libtwolame -b:a 128k -ar 44100 -error_protection 1 -f mp2 "$out/crc_reference.mp2"
# Streams with coupling channel elements, which FFmpeg's encoder never writes. The .aac files are written by the tests' own bitstream writer
# (AIBELL_WRITE_SYNTHETIC=1 dotnet test tests/Aibell.Audio.Aac.Tests --filter Name=Writer_ExplicitGenerate); only the FFmpeg reference decode is made here.
for synthetic in "$root"/aac-synthetic/*.aac; do
    [ -e "$synthetic" ] || continue
    ffmpeg -hide_banner -loglevel error -y -c:a aac -i "$synthetic" -f s16le -acodec pcm_s16le - | gzip -9n > "${synthetic%.aac}.pcm.gz"
done
echo "Generated AAC fixtures in $out"
