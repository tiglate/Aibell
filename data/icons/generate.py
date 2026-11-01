#!/usr/bin/env python3
"""Draws the Aibell icon: a vinyl record with a pink label.

Run `python3 data/icons/generate.py` from the repository root; it needs Pillow and rewrites the files next to it and the
two icon assets under src/Aibell.Desktop/Assets. The artwork is original to this project and released under the project's MIT
license. The now-playing placeholder cover (Assets/default-cover.png) is a separate, hand-made image and is not generated here.

Every size is drawn from scratch with its own level of detail (small sizes drop the fine grooves and thicken the label), and the
.ico holds one hand-rendered frame per size instead of one frame that Windows has to shrink.
"""
import io
import struct
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
ASSETS = ROOT / "src" / "Aibell.Desktop" / "Assets"
TOP, BOTTOM = (240, 64, 130), (120, 24, 110)  # label gradient: pink to plum, the application accent
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def label_gradient(size):
    image = Image.new("RGBA", (size, size))
    pixels = image.load()
    for y in range(size):
        t = y / max(1, size - 1)
        row = tuple(round(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3)) + (255,)
        for x in range(size):
            pixels[x, y] = row
    return image


def draw_icon(size):
    s = 8 if size <= 64 else 4  # supersampling
    big = size * s
    c = big / 2
    radius = big * 0.485
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)

    # disc body with a slightly lighter rim so it separates from dark taskbars
    d.ellipse((c - radius, c - radius, c + radius, c + radius), fill=(70, 72, 78, 255))
    body = radius * 0.965
    d.ellipse((c - body, c - body, c + body, c + body), fill=(20, 21, 24, 255))

    # grooves: fewer and bolder when small
    grooves = 2 if size <= 24 else 4 if size <= 48 else 9
    inner, outer = 0.46, 0.93
    for i in range(grooves):
        r = radius * (inner + (outer - inner) * (i + 0.5) / grooves)
        shade = 44 if i % 2 == 0 else 34
        d.ellipse((c - r, c - r, c + r, c + r), outline=(shade, shade, shade + 4, 255), width=max(1, int(big * (0.012 if size <= 48 else 0.004))))

    # soft sheen: two opposite light wedges across the grooves
    sheen = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    sd = ImageDraw.Draw(sheen)
    for start in (205, 25):
        sd.pieslice((c - body, c - body, c + body, c + body), start, start + 38, fill=(255, 255, 255, 30 if size > 24 else 22))
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).ellipse((c - body, c - body, c + body, c + body), fill=255)
    hole = radius * 0.46
    ImageDraw.Draw(mask).ellipse((c - hole, c - hole, c + hole, c + hole), fill=0)
    sheen.putalpha(Image.composite(sheen.split()[3], Image.new("L", (big, big), 0), mask))
    image = Image.alpha_composite(image, sheen.filter(ImageFilter.GaussianBlur(big * 0.01)))
    d = ImageDraw.Draw(image)

    # label
    label = radius * (0.40 if size > 24 else 0.46)
    side = int(label * 2)
    gradient = label_gradient(side)
    round_mask = Image.new("L", (side, side), 0)
    ImageDraw.Draw(round_mask).ellipse((0, 0, side - 1, side - 1), fill=255)
    image.paste(gradient, (int(c - side / 2), int(c - side / 2)), round_mask)
    d = ImageDraw.Draw(image)
    if size >= 32:
        ring = label * 0.82
        d.ellipse((c - ring, c - ring, c + ring, c + ring), outline=(255, 255, 255, 70), width=max(1, int(big * 0.004)))

    # spindle hole
    spindle = radius * (0.075 if size > 24 else 0.11)
    d.ellipse((c - spindle, c - spindle, c + spindle, c + spindle), fill=(14, 15, 17, 255))
    return image.resize((size, size), Image.LANCZOS)


def write_ico(path, frames):
    """Writes PNG-compressed ICO frames (supported since Windows Vista)."""
    blobs = []
    for frame in frames:
        buffer = io.BytesIO()
        frame.save(buffer, "PNG", optimize=True)
        blobs.append(buffer.getvalue())
    offset = 6 + 16 * len(frames)
    header = struct.pack("<HHH", 0, 1, len(frames))
    entries = b""
    for frame, blob in zip(frames, blobs):
        w = frame.width % 256
        entries += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    path.write_bytes(header + entries + b"".join(blobs))


def svg():
    grooves = "".join(
        f'<circle cx="500" cy="500" r="{r}" fill="none" stroke="#2c2d31" stroke-width="5"/>' for r in range(250, 470, 26))
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1000" width="512" height="512">
  <title>Aibell</title>
  <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f04082"/><stop offset="1" stop-color="#78186e"/></linearGradient></defs>
  <circle cx="500" cy="500" r="485" fill="#46484e"/>
  <circle cx="500" cy="500" r="468" fill="#141518"/>
  {grooves}
  <circle cx="500" cy="500" r="200" fill="url(#g)"/>
  <circle cx="500" cy="500" r="164" fill="none" stroke="#fff" stroke-opacity=".27" stroke-width="4"/>
  <circle cx="500" cy="500" r="36" fill="#0e0f11"/>
</svg>
"""


def main():
    HERE.mkdir(exist_ok=True)
    frames = {size: draw_icon(size) for size in (*ICO_SIZES, 512)}
    for size, image in frames.items():
        image.save(HERE / f"aibell-{size}.png")
    ico = [frames[size] for size in ICO_SIZES]
    write_ico(HERE / "aibell.ico", ico)
    write_ico(ASSETS / "aibell.ico", ico)
    frames[256].save(ASSETS / "aibell.png")
    (HERE / "aibell.svg").write_text(svg(), encoding="utf-8")


if __name__ == "__main__":
    main()
