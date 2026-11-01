#!/usr/bin/env python3
"""Writes the Layer II fixtures: synthetic signals encoded by FFmpeg's mp2 and libtwolame encoders (development tools only), random but valid Layer II streams built
straight from the syntax of ISO/IEC 11172-3 (2.4.1.6) and the allocation tables of scripts/mpeg-layer-ii-tables.txt, and a free-format variant; each with
FFmpeg's decode as a black-box yardstick.

usage: generate-mp3-layer2.py <output dir> <tables.txt> <ffmpeg>
"""
import gzip
import math
import os
import random
import struct
import subprocess
import sys
import tempfile

here = os.path.dirname(os.path.abspath(__file__))
BITRATES_MPEG1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384]
BITRATES_LSF = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]
GROUPED = {3: (2, 5), 5: (3, 7), 9: (4, 10)}
CLASSES = [3, 5, 7, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535]


class Bits:
    def __init__(self):
        self.value = 0
        self.count = 0

    def put(self, value, width):
        assert 0 <= value < (1 << width) or width == 0, (value, width)
        self.value = (self.value << width) | value
        self.count += width

    def to_bytes(self, length):
        assert self.count <= length * 8, (self.count, length * 8)
        self.value <<= length * 8 - self.count
        return self.value.to_bytes(length, "big")


def load_tables(path):
    # The generator script runs main() on import; reuse only its parser by executing the file with a fake argv.
    source = open(os.path.join(here, "generate-mp3-layer2-tables.py"), encoding="utf-8").read().replace("\nmain()\n", "\n")
    namespace = {}
    exec(compile(source, "tables", "exec"), namespace)
    return namespace["parse"](path)


def select_table(version_bits, rate, channels, bitrate):
    if version_bits != 3:
        return "B.1"
    per_channel = bitrate // channels
    if per_channel <= 48:
        return "3-B.2d" if rate == 32000 else "3-B.2c"
    if per_channel <= 80:
        return "3-B.2a"
    return "3-B.2a" if rate == 48000 else "3-B.2b"


def sample_bits(levels):
    return GROUPED[levels][0] if levels in GROUPED else (levels + 1).bit_length() - 1


def codeword_bits(levels):
    return GROUPED[levels][1] if levels in GROUPED else sample_bits(levels)


def make_frame(rng, tables, version_bits, rate_index, rate, bitrate_index, bitrate, mode, mode_ext, padding, crc):
    channels = 1 if mode == 3 else 2
    length = 144 * bitrate * 1000 // rate + (1 if padding else 0)
    # The table follows from the rate per channel: the total rate divided by the channels.
    name = select_table(version_bits, rate, channels, bitrate)
    table = tables[name]
    sblimit = max(sb + 1 for sb in range(32) if table[sb][0] > 0)
    bound = min(4 + 4 * mode_ext, sblimit) if mode == 1 else sblimit
    header = (0xFFE << 20) | (version_bits << 19) | (2 << 17) | ((0 if crc else 1) << 16) | (bitrate_index << 12) | (rate_index << 10) | ((1 if padding else 0) << 9) | (mode << 6) | (mode_ext << 4)

    def levels_of(sb, code):
        return 0 if code == 0 else table[sb][1][code]

    alloc = [[0] * 32 for _ in range(2)]
    for sb in range(sblimit):
        nbal = table[sb][0]
        for ch in range(channels):
            if sb >= bound and ch == 1:
                alloc[1][sb] = alloc[0][sb]
            elif rng.random() < 0.25:
                alloc[ch][sb] = 0
            else:
                # Mostly modest precision so that the frame fits; the rare high codes exercise the large classes.
                top = (1 << nbal) - 1
                alloc[ch][sb] = rng.randint(1, min(top, 7)) if rng.random() < 0.85 else rng.randint(1, top)
    budget = length * 8 - 32 - (16 if crc else 0)
    scfsi = [[rng.randrange(4) for _ in range(32)] for _ in range(2)]

    def needed():
        total = 0
        for sb in range(sblimit):
            for ch in range(channels):
                if not (sb >= bound and ch == 1):
                    total += table[sb][0]
        for sb in range(sblimit):
            for ch in range(channels):
                if alloc[ch][sb]:
                    total += 2 + 6 * (3 if scfsi[ch][sb] == 0 else 1 if scfsi[ch][sb] == 2 else 2)
        for sb in range(sblimit):
            for ch in range(channels):
                if alloc[ch][sb] and not (sb >= bound and ch == 1):
                    levels = levels_of(sb, alloc[ch][sb])
                    total += 12 * (codeword_bits(levels) if levels in GROUPED else 3 * sample_bits(levels))
        return total

    while needed() > budget:
        sb = rng.randrange(sblimit)
        for ch in range(channels):
            alloc[ch][sb] = 0
    out = Bits()
    out.put(header, 32)
    if crc:
        out.put(0, 16)
    for sb in range(sblimit):
        for ch in range(channels):
            if sb >= bound and ch == 1:
                continue
            out.put(alloc[ch][sb], table[sb][0])
    for sb in range(sblimit):
        for ch in range(channels):
            if alloc[ch][sb]:
                out.put(scfsi[ch][sb], 2)
    for sb in range(sblimit):
        for ch in range(channels):
            if not alloc[ch][sb]:
                continue
            count = 3 if scfsi[ch][sb] == 0 else 1 if scfsi[ch][sb] == 2 else 2
            for _ in range(count):
                out.put(rng.randint(10, 36), 6)
    for gr in range(12):
        for sb in range(sblimit):
            for ch in range(channels):
                if not alloc[ch][sb] or (sb >= bound and ch == 1):
                    continue
                levels = levels_of(sb, alloc[ch][sb])
                if levels in GROUPED:
                    out.put(rng.randrange(levels ** 3), codeword_bits(levels))
                else:
                    for _ in range(3):
                        out.put(rng.randrange(levels), sample_bits(levels))
    return out.to_bytes(length)


def ffmpeg_decode(ffmpeg, path, target):
    raw = subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-i", path, "-f", "f32le", "-c:a", "pcm_f32le", "-"], check=True, capture_output=True).stdout
    with open(target, "wb") as f:
        f.write(gzip.compress(raw, 9, mtime=0))


def make_signal(path, rate, channels, seconds):
    n = int(rate * seconds)
    state = 77 + channels * 13 + rate
    with open(path, "wb") as f:
        for i in range(n):
            t = i / rate
            for c in range(channels):
                state = (state * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
                noise = ((state >> 33) / 0x7FFFFFFF) * 2 - 1
                glide = math.sin(2 * math.pi * (300 + 90 * math.sin(2 * math.pi * 2 * t)) * (c + 1) * t)
                v = 0.22 * glide + 0.08 * math.sin(2 * math.pi * min(2500, rate * 0.2) * t + c) + 0.02 * noise
                f.write(struct.pack("<f", v))


def main():
    out, tables_path, ffmpeg = sys.argv[1:4]
    os.makedirs(out, exist_ok=True)
    tables = load_tables(tables_path)
    work = tempfile.mkdtemp()
    encoded = [
        # name, codec, rate, channels, bitrate, extra options
        ("enc_44k_stereo_128k", "mp2", 44100, 2, "128k", []),
        ("enc_48k_stereo_192k", "mp2", 48000, 2, "192k", []),
        ("enc_32k_stereo_256k", "mp2", 32000, 2, "256k", []),
        ("enc_44k_stereo_64k", "mp2", 44100, 2, "64k", []),
        ("enc_32k_stereo_64k", "mp2", 32000, 2, "64k", []),
        ("enc_44k_mono_128k", "mp2", 44100, 1, "128k", []),
        ("enc_48k_mono_64k", "mp2", 48000, 1, "64k", []),
        ("enc_22k_stereo_64k", "mp2", 22050, 2, "64k", []),
        ("enc_24k_mono_32k", "mp2", 24000, 1, "32k", []),
        ("enc_16k_stereo_48k", "mp2", 16000, 2, "48k", []),
        ("enc_44k_stereo_256k", "mp2", 44100, 2, "256k", []),
        ("twolame_44k_joint_128k", "libtwolame", 44100, 2, "128k", ["-mode", "joint_stereo"]),
        ("twolame_48k_joint_96k", "libtwolame", 48000, 2, "96k", ["-mode", "joint_stereo"]),
    ]
    for name, codec, rate, channels, bitrate, extra in encoded:
        signal = os.path.join(work, f"{name}.f32")
        make_signal(signal, rate, channels, 1.4)
        path = os.path.join(out, name + ".mp2")
        subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "f32le", "-ar", str(rate), "-ac", str(channels), "-i", signal, "-c:a", codec, "-b:a", bitrate, *extra, path], check=True)
        ffmpeg_decode(ffmpeg, path, os.path.join(out, name + ".f32.gz"))

    rng = random.Random(11)
    cases = [
        # name, version_bits, rate_index, rate, mode, mode_ext, bitrate_index
        ("random_mpeg1_44k_stereo_128k", 3, 0, 44100, 0, 0, 8),
        ("random_mpeg1_48k_joint_ext3_192k", 3, 1, 48000, 1, 3, 10),
        ("random_mpeg1_44k_joint_ext1_256k", 3, 0, 44100, 1, 1, 12),
        ("random_mpeg1_32k_joint_ext3_64k", 3, 2, 32000, 1, 3, 4),
        ("random_mpeg1_44k_joint_ext2_64k", 3, 0, 44100, 1, 2, 4),
        ("random_mpeg1_32k_joint_ext0_192k", 3, 2, 32000, 1, 0, 10),
        ("random_mpeg1_44k_mono_96k", 3, 0, 44100, 3, 0, 6),
        ("random_mpeg1_48k_mono_48k", 3, 1, 48000, 3, 0, 2),
        ("random_mpeg1_48k_dual_320k", 3, 1, 48000, 2, 0, 13),
        ("random_mpeg2_22k_stereo_64k", 2, 0, 22050, 0, 0, 8),
        ("random_mpeg2_24k_joint_ext2_96k", 2, 1, 24000, 1, 2, 10),
        ("random_mpeg2_16k_mono_32k", 2, 2, 16000, 3, 0, 4),
    ]
    for name, vb, ri, rate, mode, ext, bi in cases:
        bitrate = (BITRATES_MPEG1 if vb == 3 else BITRATES_LSF)[bi]
        frames = []
        for n in range(24):
            padding = rate in (44100, 22050) and n % 3 == 1
            frames.append(make_frame(rng, tables, vb, ri, rate, bi, bitrate, mode, ext, padding, crc=(n % 5 == 0)))
        path = os.path.join(out, name + ".mp2")
        with open(path, "wb") as f:
            f.write(b"".join(frames))
        ffmpeg_decode(ffmpeg, path, os.path.join(out, name + ".f32.gz"))

    # Free format: the 256 kbit/s stereo file (table 3-B.2b, the one free format uses at 44.1 kHz) with the bit rate index set to 0. FFmpeg cannot read free-format
    # streams but decodes the original, which is the same audio.
    source = os.path.join(out, "enc_44k_stereo_256k.mp2")
    data = bytearray(open(source, "rb").read())
    pos = 0
    while pos + 4 <= len(data):
        h = int.from_bytes(data[pos:pos + 4], "big")
        if h >> 21 != 0x7FF:
            break
        length = 144 * 256000 // 44100 + ((h >> 9) & 1)
        data[pos + 2] &= 0x0F
        pos += length
    with open(os.path.join(out, "free_44k_stereo_256k.mp2"), "wb") as f:
        f.write(data[:pos])
    ffmpeg_decode(ffmpeg, source, os.path.join(out, "free_44k_stereo_256k.f32.gz"))


main()
