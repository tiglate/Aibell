# Aibell audio shim

A small C library (`aibell_audio.c`) that gives the managed built-in player (`Aibell.Playback.Miniaudio`) a flat C API over
[miniaudio](https://miniaud.io/): one stereo float output device, and decoders for WAV, FLAC, MP3 and Ogg Vorbis that read through
caller-supplied read/seek callbacks (so files and http streams share one path).

## Vendored sources

`vendor/` holds upstream files from [mackron/miniaudio](https://github.com/mackron/miniaudio) at tag **0.11.25**; all are unmodified except `stb_vorbis.c` (see below):

| File | Upstream path | SHA-256 |
| --- | --- | --- |
| `miniaudio.h` | `miniaudio.h` | `ac7af4de748b7e26b777f37e01cee313a308a7296a3eb080e2906b320cc55c89` |
| `stb_vorbis.c` | `extras/stb_vorbis.c` | upstream `4c7cb2ff1f7011e9d67950446b7eb9ca044f2e464d76bfbb0b84dd2e23e65636`; **patched locally**, now `fa0fb0e654fb6eda542a785c8bb8c743041ebd6b1fb027fb357f19d468ef3878` |
| `LICENSE` | `LICENSE` | `457f1b500e0adf6bc059edddfa78a2f62012e7c3bb43476c20e0bd23b25ba0eb` |

Licenses: miniaudio is "public domain (Unlicense) or MIT-0"; stb_vorbis is "public domain or MIT". Aibell uses both under their
no-attribution terms. Neither is GPL-family. Update both files together and refresh the table.

### Local patch to stb_vorbis

`vendor/stb_vorbis.aibell.patch` (apply with `patch -p1` to a pristine `stb_vorbis.c`, then refresh the hash above) fixes a crash found
on a real file. miniaudio opens Vorbis in push mode: it hands stb_vorbis 4 KB, retries with 4 KB more while the answer is "need more
data", and stb_vorbis parses the comment header with no end-of-packet check. When that header continues on an Ogg page that has not arrived
yet (any file with a comment header over about 8 KB, for example a long AcoustID fingerprint), the length reads as -16843009, the
allocation fails and the cleanup frees uninitialized pointers: the process died with no message. The patch rejects negative or absurd
vendor and comment lengths and counts (the retry then works), zeroes the comment list so a half-read list can be freed, and stores the
count only after the list exists. The caps are 64 MiB per string and about a million comments, far above any real file. It was found
with AddressSanitizer; a sweep over every truncation of the real file and 4,000 random corruptions of its header found no further memory
errors. Fuzzed inputs still leak `setup_temp_malloc` memory on failed opens (an upstream error-path leak, about 75 KB per rejected file).
I did not compare against current upstream stb, so check whether a newer version already contains this fix before refreshing.

## Building

`Aibell.Playback.Miniaudio.csproj` builds the shim before compiling (`scripts/build-native-audio.sh` on Linux,
`scripts/build-native-audio.ps1` with the Visual C++ compiler on Windows) into `artifacts/native-audio/<rid>/` and copies it next
to the application and into the publish folder. It is compiled for the machine that builds, so build each package on its own
platform. Linux needs only a C compiler: miniaudio loads ALSA, PulseAudio and JACK with `dlopen` at run time.
