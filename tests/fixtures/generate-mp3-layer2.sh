#!/usr/bin/env bash
# Regenerates tests/fixtures/mp3-layer2: Layer II files from FFmpeg's mp2 and libtwolame encoders, random valid Layer II streams, and a free-format file, each with
# FFmpeg's decode as a black-box yardstick (development tools only; nothing of them is linked or shipped).
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
python3 -I "$here/../../scripts/generate-mp3-layer2.py" "$here/mp3-layer2" "$here/../../scripts/mpeg-layer-ii-tables.txt" ffmpeg
echo "Generated Layer II fixtures in $here/mp3-layer2"
