#!/usr/bin/env bash
# Regenerates tests/fixtures/rar: RAR 3/4 and RAR 5 archives made by the RAR command-line tool (a development tool only; nothing of it is linked or
# shipped) from small synthetic files made here, plus two .rsn sets of synthetic SPC files. The files in rar/source are the reference: the decoder must
# reproduce them byte for byte. Writing the RAR 3/4 format needs RAR 6.x or older (RAR 7 dropped -ma4): set RAR to that binary, for example
#   RAR=/opt/rar/rar tests/fixtures/generate-rar.sh
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
out="$here/rar"
rar="${RAR:-rar}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
rm -rf "$out"
mkdir -p "$out/source/sub" "$work/spc"

python3 -I - "$out/source" "$work/spc" <<'PY'
import math, struct, sys
source, spc = sys.argv[1], sys.argv[2]

def lcg(seed):
    state = seed
    while True:
        state = (state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
        yield (state >> 33) & 0x7FFFFFFF

words = "the of and a to in is you that it he was for on are as with his they at be this from I have or by one had not but what all were when we there can an your which their said if do will each about how up out them then she many some so these would other into has more her two like him see time could no make than first been its who now people my made over did down only way find use may water long little very after words called just where most know".split()
rng = lcg(1)
text = []
size = 0
while size < 6000:
    line = " ".join(words[next(rng) % len(words)] for _ in range(8 + next(rng) % 8)) + ".\n"
    text.append(line)
    size += len(line)
open(f"{source}/text.txt", "w").write("".join(text))
open(f"{source}/sub/nested.txt", "w").write("Nested file inside a directory.\n" * 15)
open(f"{source}/empty.txt", "w").close()

rng = lcg(2)
binary = bytearray()
while len(binary) < 8000:
    kind = next(rng) % 3
    if kind == 0:
        binary += bytes(next(rng) & 0xFF for _ in range(40 + next(rng) % 80))
    elif kind == 1:
        binary += bytes([next(rng) & 0xFF]) * (20 + next(rng) % 200)
    else:
        piece = bytes(next(rng) & 0xFF for _ in range(16))
        binary += piece * (2 + next(rng) % 20)
open(f"{source}/binary.bin", "wb").write(bytes(binary[:8000]))

rng = lcg(3)
samples = bytearray()
for i in range(6000):
    v = 9000 * math.sin(2 * math.pi * 440 * i / 8000) + 3000 * math.sin(2 * math.pi * 1250 * i / 8000) + (next(rng) % 400 - 200)
    samples += struct.pack("<h", int(v))
open(f"{source}/tone.wav", "wb").write(bytes(samples))

rng = lcg(4)
code = bytearray()
while len(code) < 8000:
    kind = next(rng) % 5
    if kind == 0:
        code += b"\xE8" + struct.pack("<i", (next(rng) % 4000) - 2000)
    elif kind == 1:
        code += b"\xE9" + struct.pack("<i", (next(rng) % 60000) - 1000)
    else:
        code += bytes(next(rng) & 0xFF for _ in range(1 + next(rng) % 6))
open(f"{source}/code.bin", "wb").write(bytes(code[:8000]))

# Synthetic SPC files: a program that keys on one voice playing a looped BRR square wave, with a text tag.
def make_spc(title, pitch):
    f = bytearray(0x10200)
    f[:33] = b"SNES-SPC700 Sound File Data v0.30"
    f[0x21] = f[0x22] = 26
    f[0x23] = 26
    f[0x24] = 30
    f[0x25:0x27] = struct.pack("<H", 0x200)
    f[0x2B] = 0xEF
    f[0x2E:0x2E + len(title)] = title.encode()
    game = b"Test Game"
    f[0x4E:0x4E + len(game)] = game
    f[0xA9:0xAC] = b"10\0"
    f[0xAC:0xB1] = b"1000\0"
    ram = 0x100
    block = bytearray(9)
    block[0] = (12 << 4) | 3
    nibbles = [7] * 8 + [9] * 8
    for i, n in enumerate(nibbles):
        block[1 + i // 2] |= (n & 0xF) << (4 if i % 2 == 0 else 0)
    f[ram + 0x1000:ram + 0x1009] = block
    f[ram + 0x0F00:ram + 0x0F04] = bytes([0x00, 0x10, 0x00, 0x10])
    f[ram + 0x200:ram + 0x208] = bytes([0x8F, 0x4C, 0xF2, 0x8F, 0x01, 0xF3, 0x2F, 0xFE])
    dsp = 0x10100
    f[dsp:dsp + 8] = bytes([0x7F, 0x7F, pitch & 0xFF, pitch >> 8, 0x00, 0x00, 0x00, 0x7F])
    f[dsp + 0x0C] = 0x7F
    f[dsp + 0x1C] = 0x7F
    f[dsp + 0x5D] = 0x0F
    f[dsp + 0x6C] = 0x20
    return bytes(f)

for index, pitch in enumerate((0x1000, 0x0C00, 0x0800), start=1):
    open(f"{spc}/song_{index:02d}.spc", "wb").write(make_spc(f"Song {index}", pitch))
PY

cd "$out/source"
make() {
    local name="$1"; shift
    "$rar" a -idq -r -ep1 -ts- "$@" "$out/$name.rar" . > /dev/null
}
make r4_store   -ma4 -m0
make r4_fastest -ma4 -m1
make r4_normal  -ma4 -m3
make r4_best    -ma4 -m5
make r4_solid   -ma4 -m3 -s
make r4_ppm     -ma4 -m5 -mct+ -s
make r4_e8      -ma4 -m5 -mce+
make r4_delta   -ma4 -m3 -mc2d+
make r4_audio   -ma4 -m3 -mc1a+
make r4_rgb     -ma4 -m3 -mcc+
make r5_store   -ma5 -m0
make r5_normal  -ma5 -m3
make r5_best    -ma5 -m5
make r5_solid   -ma5 -m3 -s
make r5_e8      -ma5 -m5 -mce+
make r5_delta   -ma5 -m3 -mc2d+

make r4_encrypted -ma4 -m3 -ptest
make r5_encrypted_headers -ma5 -m3 -hptest

cd "$work/spc"
"$rar" a -idq -ep1 -ma4 -m5 -s -ts- "$out/songs4.rsn" song_01.spc song_02.spc song_03.spc > /dev/null
"$rar" a -idq -ep1 -ma5 -m3 -s -ts- "$out/songs5.rsn" song_01.spc song_02.spc song_03.spc > /dev/null
ls -l "$out"
