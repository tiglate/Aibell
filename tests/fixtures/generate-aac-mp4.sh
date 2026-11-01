#!/usr/bin/env bash
# Generates the tiny AAC-in-MP4 fixtures used by Aibell.Audio.Aac.Tests and Aibell.Playback.Miniaudio.Tests.
#
# Each case is a short synthetic signal encoded by FFmpeg's native AAC encoder into an MP4 (a development tool only; nothing from it is
# redistributed or linked) and decoded by FFmpeg to 16-bit PCM in WAVE channel order as the reference. FFmpeg's MP4 muxer writes an edit
# list that skips the encoder delay, and its decoder honours it, so the references are exactly the audio a player should produce.
set -euo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$root/aac-mp4"
rm -rf "$out"
mkdir -p "$out"
command -v ffmpeg >/dev/null || { echo "ffmpeg is required to generate AAC fixtures." >&2; exit 1; }

# name rate channels bitrate mux-flags source-expression duration
make() {
    local name="$1" rate="$2" channels="$3" bitrate="$4" flags="$5" source="$6" duration="${7:-0.5}"
    # shellcheck disable=SC2086
    ffmpeg -hide_banner -loglevel error -y -f lavfi -i "$source" -t "$duration" -ar "$rate" -ac "$channels" \
        -c:a aac -profile:a aac_low -b:a "$bitrate" -aac_ms 0 -aac_is 0 -aac_pns 0 -aac_tns 0 $flags "$out/$name.m4a"
    ffmpeg -hide_banner -loglevel error -y -i "$out/$name.m4a" -f s16le -acodec pcm_s16le - | gzip -9n > "$out/$name.pcm.gz"
}

sweep_stereo="aevalsrc=0.5*sin(2*PI*(300+1500*t)*t)|0.4*sin(2*PI*(700+900*t)*t):s=48000"
sweep_mono="aevalsrc=0.5*sin(2*PI*(200+1000*t)*t)+0.1*sin(2*PI*2400*t):s=48000"
# Six different tones, so a wrong channel order cannot go unnoticed: FL FR FC LFE BL BR.
surround="aevalsrc=0.4*sin(2*PI*300*t)|0.4*sin(2*PI*400*t)|0.4*sin(2*PI*500*t)|0.4*sin(2*PI*60*t)|0.4*sin(2*PI*700*t)|0.4*sin(2*PI*800*t):s=48000:c=5.1"

make stereo_44k 44100 2 96k "" "$sweep_stereo"
make mono_48k 48000 1 64k "" "$sweep_mono"
make surround_48k 48000 6 192k "" "$surround" 0.3
# moov before mdat (what iTunes and most taggers write), and no edit list (the encoder delay is then part of the audio).
make faststart_stereo_44k 44100 2 96k "-movflags +faststart" "$sweep_stereo"
make noedit_stereo_44k 44100 2 96k "-use_editlist 0" "$sweep_stereo"
# A fragmented file, which has no samples in the movie box: the player must refuse it with a clear message.
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "$sweep_stereo" -t 0.3 -ar 44100 -ac 2 -c:a aac -b:a 96k -movflags frag_keyframe+empty_moov "$out/fragmented.m4a"
