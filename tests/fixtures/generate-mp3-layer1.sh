#!/usr/bin/env bash
# Regenerates tests/fixtures/mp3-layer1 (random valid Layer I streams) and tests/fixtures/mp3-freeformat (the CBR Layer III fixtures with the bit rate index set to
# free format), each with FFmpeg's decode as a black-box yardstick. FFmpeg is a development tool only; nothing of it is linked or shipped.
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
python3 -I "$here/../../scripts/generate-mp3-layer1.py" "$here/mp3-layer1" "$here/mp3-freeformat" "$here/mp3" ffmpeg
echo "Generated Layer I and free-format fixtures in $here"
