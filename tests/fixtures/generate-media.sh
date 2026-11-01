#!/usr/bin/env bash

set -euo pipefail

fixture_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output_dir="$fixture_root/generated"

if ! command -v ffmpeg >/dev/null 2>&1; then
    echo "ffmpeg is required to generate media fixtures." >&2
    exit 1
fi

mkdir -p "$output_dir"

common_args=(
    -hide_banner
    -loglevel error
    -y
    -f lavfi
    -i "sine=frequency=440:sample_rate=48000:duration=1"
    -metadata title="Fixture Tone"
    -metadata artist="Aibell Test Runner"
    -metadata album="Synthetic Fixtures"
    -metadata track="1"
    -metadata date="2026"
)

ffmpeg "${common_args[@]}" -c:a pcm_s16le "$output_dir/fixture.wav"
ffmpeg "${common_args[@]}" -c:a flac "$output_dir/fixture.flac"
ffmpeg "${common_args[@]}" -c:a libmp3lame -q:a 5 "$output_dir/fixture.mp3"
ffmpeg "${common_args[@]}" -c:a libvorbis -q:a 4 "$output_dir/fixture.ogg"
ffmpeg "${common_args[@]}" -c:a libopus -b:a 96k "$output_dir/fixture.opus"
ffmpeg "${common_args[@]}" -c:a aac -b:a 128k "$output_dir/fixture.m4a"

echo "Generated media fixtures in $output_dir"
