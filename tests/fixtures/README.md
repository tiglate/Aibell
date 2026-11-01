# Migration Fixtures

Fixtures in this directory are synthetic and safe to redistribute. Do not commit personal music libraries or copied user databases.

- `generated/` contains committed one-second tone files in common codecs so tests do not depend on FFmpeg. `generate-media.sh` reproducibly refreshes them.
- `generated/variants/` holds tag and format variants (Unicode ID3v2.4, ID3v2.3 + ID3v1, lyrics, MP4 chapters, two FLAC pictures, repeated Vorbis fields, 8/24-bit and float WAV, 24-bit FLAC). Refresh with `generate-variants.sh`; `MetadataVariantTests` characterizes how each is read.
- `rar/` holds RAR 3/4 and RAR 5 archives of small synthetic files (`rar/source`) and two `.rsn` sets of synthetic SPC files, made with the RAR command-line tool by
  `generate-rar.sh` (writing the 3/4 format needs RAR 6.x or older; set `RAR`). They test `Aibell.Archive.Rar` and the RSN track handling.
- `fixture.m3u` and `podcast.xml` exercise playlist and podcast parsing.
- `corrupt.mp3` is intentionally invalid and must produce a per-item import error.
- `artwork/` contains original geometric PNG covers. Regenerate them with
  `python3 tests/fixtures/generate-artwork.py`. Artwork tests embed them only in
  disposable media copies; application artwork reading never writes audio tags.
- `AtlMetadataWritingSafetyTests` edits disposable audio copies, preserving the
  committed fixtures. Optional `AIBELL_METADATA_WRITE_TESTS=1` cases use FFmpeg
  to compare decoded PCM hashes before and after each metadata/artwork save.

Generate the media corpus with `tests/fixtures/generate-media.sh`. The synthesized tone contains no third-party recording.

The original PNG artwork fixture also has a JPEG variant for embedded-image
round-trip coverage. Regenerate it with:
`ffmpeg -i artwork/cover.png -frames:v 1 -q:v 3 -y artwork/cover.jpg`.
