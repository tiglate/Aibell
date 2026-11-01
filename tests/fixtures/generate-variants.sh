#!/usr/bin/env bash
# Generates the tag/format variant corpus used by MetadataVariantTests. Every file is a short synthetic tone with
# made-up metadata (no third-party recording). Run it only to refresh the committed files; tests never need ffmpeg.
set -euo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$root/generated/variants"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

tone=(-hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=0.5")

# Unicode text in an ID3v2.4 (UTF-8) tag.
ffmpeg "${tone[@]}" -c:a libmp3lame -q:a 6 -id3v2_version 4 \
  -metadata title="Zażółć gęślą jaźń — 日本語 — Привет" -metadata artist="Björk & Sigur Rós" \
  -metadata album="Ünïcödé Álbum" -metadata genre="Électronique" -metadata date="1999" -metadata track="3/12" \
  "$out/unicode-id3v24.mp3"

# ID3v2.3 and an ID3v1 footer in the same file (two tag versions).
ffmpeg "${tone[@]}" -c:a libmp3lame -q:a 6 -id3v2_version 3 -write_id3v1 1 \
  -metadata title="Both Tags" -metadata artist="Tag Duo" -metadata album="Two Versions" -metadata date="2001" \
  "$out/id3v23-and-v1.mp3"

# Lyrics (USLT) and a comment.
ffmpeg "${tone[@]}" -c:a libmp3lame -q:a 6 -id3v2_version 4 \
  -metadata title="With Lyrics" -metadata artist="Lyric Band" -metadata lyrics="First line
Second line" -metadata comment="A short comment" \
  "$out/lyrics.mp3"

# Chapters in an MP4 audiobook-style file.
cat > "$work/chapters.ffmeta" <<'META'
;FFMETADATA1
title=Chaptered Book
artist=Narrator
album=Book
[CHAPTER]
TIMEBASE=1/1000
START=0
END=200
title=Opening
[CHAPTER]
TIMEBASE=1/1000
START=200
END=500
title=Second Part
META
ffmpeg "${tone[@]}" -i "$work/chapters.ffmeta" -map_metadata 1 -map_chapters 1 -c:a aac -b:a 64k "$out/chapters.m4b"

# FLAC with front and back cover pictures.
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=0.5" \
  -i "$root/artwork/cover.png" -i "$root/artwork/back.png" -map 0:a -map 1:v -map 2:v -c:a flac -c:v png \
  -disposition:v:0 attached_pic -disposition:v:1 attached_pic \
  -metadata title="Two Pictures" -metadata artist="Cover Band" -metadata:s:v:0 title="Front" -metadata:s:v:1 title="Back" \
  "$out/two-pictures.flac" || echo "two-pictures.flac could not be created" >&2

# WAV profiles beyond 16-bit stereo.
ffmpeg "${tone[@]}" -ac 1 -ar 8000 -c:a pcm_u8 -metadata title="Eight Bit Mono" "$out/pcm-u8-mono-8k.wav"
ffmpeg "${tone[@]}" -t 0.2 -ac 2 -ar 48000 -c:a pcm_s24le -metadata title="Twenty Four Bit" -metadata artist="Hi Res" "$out/pcm-s24-stereo-48k.wav"
ffmpeg "${tone[@]}" -t 0.2 -ac 2 -ar 44100 -c:a pcm_f32le -metadata title="Float" "$out/pcm-f32-stereo-44k.wav"

# Other containers with several tag fields and unusual rates.
ffmpeg "${tone[@]}" -ac 1 -ar 22050 -c:a libvorbis -q:a 2 -metadata title="Mono Vorbis" -metadata artist="One" -metadata ARTIST="Two" "$out/mono-22k.ogg"
ffmpeg "${tone[@]}" -c:a flac -sample_fmt s32 -metadata title="Deep FLAC" -metadata albumartist="Album Artist" -metadata discnumber="2/3" -metadata composer="A. Composer" "$out/flac-24bit-tags.flac"

ls -l "$out"
