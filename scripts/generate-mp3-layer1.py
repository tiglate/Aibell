#!/usr/bin/env python3
"""Writes streams of random but valid MPEG Layer I frames (ISO/IEC 11172-3, 2.4.1.5) and free-format variants of existing Layer III fixtures, plus FFmpeg's decode
of each (a black-box yardstick). No Layer I encoder is used: the frames are built straight from the syntax in the standard.

usage: generate-mp3-layer1.py <layer1 output dir> <free-format output dir> <mp3 fixture dir> <ffmpeg>
"""
import gzip
import os
import random
import subprocess
import sys

BITRATES_MPEG1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448]
BITRATES_LSF = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256]


class Bits:
    def __init__(self):
        self.value = 0
        self.count = 0

    def put(self, value, width):
        assert 0 <= value < (1 << width) or width == 0
        self.value = (self.value << width) | value
        self.count += width

    def to_bytes(self, length):
        assert self.count <= length * 8, (self.count, length * 8)
        self.value <<= length * 8 - self.count
        return self.value.to_bytes(length, "big")


def make_frame(rng, version_bits, rate_index, rate, bitrate_index, bitrate, mode, mode_ext, padding, crc, scf_range, quiet):
    """version_bits: 3 MPEG-1, 2 MPEG-2, 0 MPEG-2.5."""
    slots = 12 * bitrate * 1000 // rate
    length = (slots + (1 if padding else 0)) * 4
    channels = 1 if mode == 3 else 2
    bound = 4 + 4 * mode_ext if mode == 1 else 32
    header = (0xFFE << 20) | (version_bits << 19) | (3 << 17) | ((0 if crc else 1) << 16) | (bitrate_index << 12) | (rate_index << 10) | ((1 if padding else 0) << 9) | (mode << 6) | (mode_ext << 4)
    out = Bits()
    out.put(header, 32)
    if crc:
        out.put(0, 16)  # filled in below
    # Draw allocations that fit in the frame.
    alloc = [[0] * 32 for _ in range(2)]
    budget = length * 8 - 32 - (16 if crc else 0)
    for sb in range(32):
        for ch in range(channels):
            if sb >= bound and ch == 1:
                alloc[1][sb] = alloc[0][sb]
            elif rng.random() < (0.35 if quiet else 0.15):
                alloc[ch][sb] = 0
            else:
                alloc[ch][sb] = rng.randint(1, 14) if rng.random() < 0.85 else rng.choice([1, 2, 14])
    while True:
        need = sum(4 for sb in range(32) for ch in range(channels) if not (sb >= bound and ch == 1))
        need += sum(6 for sb in range(32) for ch in range(channels) if alloc[ch][sb])
        need += 12 * sum(alloc[ch][sb] + 1 for sb in range(32) for ch in range(channels) if alloc[ch][sb] and not (sb >= bound and ch == 1))
        if need <= budget:
            break
        # Too many bits: drop an allocation and try again.
        sb = rng.randrange(32)
        for ch in range(channels):
            alloc[ch][sb] = 0
    for sb in range(32):
        for ch in range(channels):
            if sb >= bound and ch == 1:
                continue
            out.put(alloc[ch][sb], 4)
    for sb in range(32):
        for ch in range(channels):
            if alloc[ch][sb]:
                out.put(rng.randint(*scf_range), 6)
    for s in range(12):
        for sb in range(32):
            for ch in range(channels):
                a = alloc[ch][sb]
                if not a or (sb >= bound and ch == 1):
                    continue
                nb = a + 1
                raw = rng.randrange(0, (1 << nb) - 1)  # the all-ones code is left out
                out.put(raw, nb)
    body = out.to_bytes(length)
    if crc:
        # The protection word covers the last 16 header bits and the allocations; the decoder does not check it, so a plausible value is enough.
        body = body[:4] + b"\x00\x00" + body[6:]
    return body


def ffmpeg_decode(ffmpeg, path, target):
    raw = subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-i", path, "-f", "f32le", "-c:a", "pcm_f32le", "-"], check=True, capture_output=True).stdout
    with open(target, "wb") as f:
        f.write(gzip.compress(raw, 9, mtime=0))


def main():
    out1, out_free, mp3dir, ffmpeg = sys.argv[1:5]
    os.makedirs(out1, exist_ok=True)
    os.makedirs(out_free, exist_ok=True)
    rng = random.Random(7)
    cases = [
        # name, version_bits, rate_index, rate, mode, mode_ext, bitrate_index
        ("layer1_mpeg1_44k_stereo", 3, 0, 44100, 0, 0, 10),
        ("layer1_mpeg1_48k_joint4", 3, 1, 48000, 1, 0, 12),
        ("layer1_mpeg1_32k_joint16", 3, 2, 32000, 1, 3, 9),
        ("layer1_mpeg1_44k_mono", 3, 0, 44100, 3, 0, 6),
        ("layer1_mpeg1_48k_dual", 3, 1, 48000, 2, 0, 14),
        ("layer1_mpeg2_22k_stereo", 2, 0, 22050, 0, 0, 8),
        ("layer1_mpeg2_24k_joint8", 2, 1, 24000, 1, 1, 10),
        ("layer1_mpeg25_11k_mono", 0, 0, 11025, 3, 0, 5),
    ]
    for name, vb, ri, rate, mode, ext, bi in cases:
        table = BITRATES_MPEG1 if vb == 3 else BITRATES_LSF
        frames = []
        for n in range(150):
            padding = rate == 44100 and (n % 3 == 1) or (rate == 22050 and n % 3 == 1) or (rate == 11025 and n % 3 == 1)
            frames.append(make_frame(rng, vb, ri, rate, bi, table[bi], mode, ext, padding, crc=(n % 5 == 0), scf_range=(20, 50), quiet=True))
        path = os.path.join(out1, name + ".mp1")
        with open(path, "wb") as f:
            f.write(b"".join(frames))
        ffmpeg_decode(ffmpeg, path, os.path.join(out1, name + ".f32.gz"))
    # Free format: take CBR Layer III files and set the bit rate index to 0; the frames keep their length.
    for name in ("mpeg1_44k_stereo_128k", "mpeg1_48k_stereo_192k", "mpeg2_22k_stereo_64k", "mpeg1_44k_mono_64k"):
        data = bytearray(open(os.path.join(mp3dir, name + ".mp3"), "rb").read())
        original = bytes(data)
        # Skip an ID3v2 tag, then every frame.
        pos = 0
        if data[:3] == b"ID3":
            pos = 10 + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9])
        start = pos
        skipped = False
        table1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
        table2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]
        rates = [44100, 48000, 32000]
        while pos + 4 <= len(data):
            h = int.from_bytes(data[pos:pos + 4], "big")
            if h >> 21 != 0x7FF:
                break
            mpeg1 = ((h >> 19) & 3) == 3
            rate = rates[(h >> 10) & 3] // (1 if mpeg1 else 2)
            bitrate = (table1 if mpeg1 else table2)[(h >> 12) & 15] * 1000
            length = (144 if mpeg1 else 72) * bitrate // rate + ((h >> 9) & 1)
            data[pos + 2] &= 0x0F  # bit rate index 0
            if not skipped:
                skipped = True
                start += length  # the Xing/Info frame is left out: FFmpeg's free-format detection trips over it
            pos += length
        target = os.path.join(out_free, "free_" + name + ".mp3")
        with open(target, "wb") as f:
            f.write(data[start:pos])
        # FFmpeg's MP3 demuxer cannot read free-format streams, but the audio is the same as in the constant bit rate original, which it decodes.
        twin = target[:-4] + ".cbr.mp3"
        with open(twin, "wb") as f:
            f.write(original[start:pos])
        ffmpeg_decode(ffmpeg, twin, os.path.join(out_free, "free_" + name + ".f32.gz"))
        os.remove(twin)


main()
