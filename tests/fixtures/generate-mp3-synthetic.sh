#!/usr/bin/env bash
# Regenerates tests/fixtures/mp3-synthetic: streams of random but valid Layer III frames (mixed blocks, intensity stereo, scfsi, preflag, escape tables, all MPEG versions)
# that real encoders do not produce, plus FFmpeg's decode of each as a black-box yardstick. The frames are built from the specification text (the Fraunhofer
# description of Layer III), which is exported from the PDF the project owner supplied: set MP3_SPEC_PDF to its path.
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
pdf="${MP3_SPEC_PDF:-$HOME/Downloads/MPEG Layer3 Bitstream Syntax and Decoding.pdf}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
pdftotext -layout "$pdf" "$work/spec.txt"
python3 "$here/../../scripts/generate-mp3-synthetic.py" "$work/spec.txt" "$here/mp3-synthetic" ffmpeg
echo "Generated synthetic MP3 fixtures in $here/mp3-synthetic"
