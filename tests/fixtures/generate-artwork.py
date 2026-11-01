"""Generate original geometric PNG fixtures using only the Python standard library."""

from pathlib import Path
import struct
import zlib


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))


def image(path, accent):
    size = 192
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            radius = ((x - 96) ** 2 + (y - 96) ** 2) ** 0.5
            if 36 < radius < 67:
                color = accent
            elif radius < 10:
                color = (243, 235, 221)
            elif x < 12 or y < 12 or x >= 180 or y >= 180:
                color = accent
            else:
                color = (27, 32, 43)
            rows.extend(color)
    data = b"\x89PNG\r\n\x1a\n"
    data += chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0))
    data += chunk(b"IDAT", zlib.compress(rows, 9))
    data += chunk(b"IEND", b"")
    path.write_bytes(data)


root = Path(__file__).resolve().parent / "artwork"
root.mkdir(exist_ok=True)
image(root / "cover.png", (36, 193, 177))
image(root / "back.png", (218, 50, 135))
