#!/usr/bin/env bash
# Builds the Aibell audio shim (a thin C layer over the vendored miniaudio) for the machine it runs on.
# Usage: scripts/build-native-audio.sh <output-directory>
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/native/aibell-audio/aibell_audio.c"
output_dir="${1:?Usage: build-native-audio.sh <output-directory>}"
compiler="${CC:-}"
if [[ -z "$compiler" ]]; then
    for candidate in cc gcc clang; do
        if command -v "$candidate" >/dev/null 2>&1; then compiler="$candidate"; break; fi
    done
fi
if [[ -z "$compiler" ]]; then
    echo "A C compiler (cc, gcc or clang) is required to build the Aibell audio library." >&2
    exit 1
fi

mkdir -p "$output_dir"
# miniaudio loads ALSA/PulseAudio/JACK with dlopen at run time, so nothing but libc, libm and libdl is linked.
"$compiler" -O2 -shared -fPIC -fvisibility=hidden -o "$output_dir/libaibell_audio.so" "$source_file" -ldl -lpthread -lm
