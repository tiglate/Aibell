#!/usr/bin/env python3
"""Wraps the packets written by alac-apple-driver (cookie, then frame count, byte count and bytes per packet) in a minimal MP4 / M4A file.
usage: alac-mux.py in.alacpk out.m4a channels bits rate [faststart]   (a development tool for generate-alac-apple.sh)"""
import struct, sys

def box(kind, payload=b""):
    return struct.pack(">I4s", 8 + len(payload), kind) + payload

def full(kind, payload, version=0, flags=0):
    return box(kind, struct.pack(">I", version << 24 | flags) + payload)

src, dst, channels, bits, rate = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])
faststart = len(sys.argv) > 6 and sys.argv[6] == "faststart"
data = open(src, "rb").read()
(cookie_size,) = struct.unpack_from("<I", data, 0)
cookie = data[4:4 + cookie_size]
pos = 4 + cookie_size
frames, packets = [], []
while pos < len(data):
    n, length = struct.unpack_from("<II", data, pos)
    pos += 8
    frames.append(n)
    packets.append(data[pos:pos + length])
    pos += length

def moov(mdat_offset):
    entry = (b"\0" * 6 + struct.pack(">H", 1) + struct.pack(">HHI", 0, 0, 0) + struct.pack(">HHHH", channels, bits, 0, 0) + struct.pack(">I", min(rate, 65535) << 16)
             + full(b"alac", cookie))
    stsd = full(b"stsd", struct.pack(">I", 1) + box(b"alac", entry))
    runs = []
    for n in frames:
        if runs and runs[-1][1] == n:
            runs[-1][0] += 1
        else:
            runs.append([1, n])
    stts = full(b"stts", struct.pack(">I", len(runs)) + b"".join(struct.pack(">II", c, d) for c, d in runs))
    stsc = full(b"stsc", struct.pack(">IIII", 1, 1, len(packets), 1))
    stsz = full(b"stsz", struct.pack(">II", 0, len(packets)) + b"".join(struct.pack(">I", len(p)) for p in packets))
    stco = full(b"stco", struct.pack(">II", 1, mdat_offset))
    stbl = box(b"stbl", stsd + stts + stsc + stsz + stco)
    minf = box(b"minf", full(b"smhd", b"\0\0\0\0") + box(b"dinf", full(b"dref", struct.pack(">I", 1) + full(b"url ", b"", 0, 1))) + stbl)
    total = sum(frames)
    mdhd = full(b"mdhd", struct.pack(">IIII", 0, 0, rate, total) + struct.pack(">HH", 0x55C4, 0))
    hdlr = full(b"hdlr", struct.pack(">I4s", 0, b"soun") + b"\0" * 12 + b"Sound\0")
    mdia = box(b"mdia", mdhd + hdlr + minf)
    tkhd = full(b"tkhd", struct.pack(">IIIII", 0, 0, 1, 0, total) + b"\0" * 52, 0, 7)
    mvhd = full(b"mvhd", struct.pack(">IIII", 0, 0, rate, total) + b"\0" * 80)
    return box(b"moov", mvhd + box(b"trak", tkhd + mdia))

ftyp = box(b"ftyp", b"M4A " + struct.pack(">I", 0) + b"M4A mp42isom")
mdat_payload = b"".join(packets)
if faststart:
    size = len(moov(0))
    out = ftyp + moov(len(ftyp) + size + 8) + box(b"mdat", mdat_payload)
else:
    out = ftyp + box(b"mdat", mdat_payload) + moov(len(ftyp) + 8)
open(dst, "wb").write(out)
