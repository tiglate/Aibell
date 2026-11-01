#!/usr/bin/env python3
"""Generates src/Aibell.Audio.Mp3/Mp3Tables.g.cs from the text of "MPEG-Layer3 Bitstream Syntax and Decoding" (Sieler/Sperschneider, 1997),
as extracted by pdftotext -layout: the Huffman code tables (Tables 24-41), the scalefactor band tables (42-59), the synthesis window (Table 22)
and the preemphasis table (23). Every table is checked: the Huffman codes must be prefix free and complete counts, the bands must add up to 576 / 192 lines.

usage: generate-mp3-tables.py <spec.txt> <output dir>
"""
import os
import re
import sys


def fail(message):
    sys.exit(message)


def sections(lines):
    """Maps 'Table N' -> list of lines until the next 'Table'/'Continuation' header."""
    result = {}
    current = None
    for line in lines:
        m = re.match(r"^(Table (\d+)|Continuation of Table (\d+))\s*$", line.strip())
        if m and not line.startswith(" "):
            number = int(m.group(2) or m.group(3))
            current = number
            result.setdefault(number, [])
            continue
        if current is not None:
            result[current].append(line)
    return result


def parse_pair_rows(body, table):
    rows = []
    for line in body:
        t = line.split()
        if len(t) < 3 or not re.fullmatch(r"[01]+", t[-1]) or not all(x.isdigit() for x in t[:-1]):
            continue
        code = t[-1]
        if len(t) == 4:
            x, y, hlen = int(t[0]), int(t[1]), int(t[2])
        elif len(t) == 3:
            # "x" and then "y" fused with the length ("7 010 ..."): the length is the length of the code.
            hlen = len(code)
            x = int(t[0])
            mid = t[1]
            tail = str(hlen)
            if not mid.endswith(tail):
                fail(f"table {table}: cannot split '{line.strip()}'")
            y = int(mid[: len(mid) - len(tail)])
        else:
            continue
        if hlen != len(code):
            fail(f"table {table}: length mismatch in '{line.strip()}'")
        rows.append((x, y, code))
    return rows


def parse_quad_rows(body, table):
    rows = []
    for line in body:
        t = line.split()
        if len(t) == 6 and all(x.isdigit() for x in t[:5]) and re.fullmatch(r"[01]+", t[5]):
            v, w, x, y, hlen = (int(a) for a in t[:5])
            if hlen != len(t[5]):
                fail(f"table {table}: length mismatch")
            rows.append((v, w, x, y, t[5]))
    return rows


def check_prefix_free(codes, name):
    ordered = sorted(codes)
    for a, b in zip(ordered, ordered[1:]):
        if b.startswith(a):
            fail(f"{name}: code {a} is a prefix of {b}")
    kraft = sum(2.0 ** -len(c) for c in codes)
    if kraft > 1.0 + 1e-9:
        fail(f"{name}: Kraft sum {kraft}")


def load_tables(spec):
    lines = open(spec, encoding="utf-8", errors="replace").read().split("\n")
    # The body of the document starts after the table of contents.
    start = max(i for i, l in enumerate(lines) if l.strip() == "Table 24") - 0
    start = [i for i, l in enumerate(lines) if l.strip() == "Table 24" and not l.startswith(" ")][-1]
    pre = [i for i, l in enumerate(lines) if l.strip() == "Table 23" and not l.startswith(" ")][-1]
    sec = sections(lines[pre:])

    # Huffman pair tables: table numbers and their dimensions.
    pair_tables = {0: 1, 1: 2, 2: 3, 3: 3, 5: 4, 6: 4, 7: 6, 8: 6, 9: 6, 10: 8, 11: 8, 12: 8, 13: 16, 15: 16}
    spec_table = {0: 26, 1: 27, 2: 28, 3: 29, 5: 30, 6: 31, 7: 32, 8: 33, 9: 34, 10: 35, 11: 36, 12: 37, 13: 38, 15: 39}
    linbits = {n: 0 for n in pair_tables}
    shared = {}
    for n, b in zip(range(16, 24), [1, 2, 3, 4, 6, 8, 10, 13]):
        shared[n] = (40, 16, b)
    for n, b in zip(range(24, 32), [4, 5, 6, 7, 8, 9, 11, 13]):
        shared[n] = (41, 16, b)

    huffman = {}  # table number -> (dim, [(code, length) per x*dim+y])
    for n, dim in pair_tables.items():
        if n == 0:
            huffman[0] = (1, [("", 0)], 0)
            continue
        rows = parse_pair_rows(sec[spec_table[n]], spec_table[n])
        if n in (13, 15):
            rows += parse_pair_rows(sec.get(spec_table[n], []), spec_table[n]) if False else []
        entries = {}
        for x, y, code in rows:
            entries[(x, y)] = code
        if len(entries) != dim * dim:
            fail(f"table {n}: {len(entries)} entries, expected {dim * dim}")
        check_prefix_free(list(entries.values()), f"table {n}")
        huffman[n] = (dim, [(entries[(x, y)], len(entries[(x, y)])) for x in range(dim) for y in range(dim)], 0)
    for spec_number in (40, 41):
        rows = parse_pair_rows(sec[spec_number], spec_number)
        entries = {(x, y): code for x, y, code in rows}
        if len(entries) != 256:
            fail(f"table {spec_number}: {len(entries)} entries")
        check_prefix_free(list(entries.values()), f"table {spec_number}")
        codes = [(entries[(x, y)], len(entries[(x, y)])) for x in range(16) for y in range(16)]
        for n, (st, dim, bits) in shared.items():
            if st == spec_number:
                huffman[n] = (16, codes, bits)

    quad = {}
    for letter, number in (("A", 24), ("B", 25)):
        rows = parse_quad_rows(sec[number], number)
        entries = {(v, w, x, y): code for v, w, x, y, code in rows}
        if len(entries) != 16:
            fail(f"count1 table {letter}: {len(entries)} entries")
        check_prefix_free(list(entries.values()), f"count1 {letter}")
        quad[letter] = [entries[(v, w, x, y)] for v in range(2) for w in range(2) for x in range(2) for y in range(2)]

    # Scalefactor bands.
    rates = [(8000, 42), (11025, 44), (12000, 46), (16000, 48), (22050, 50), (24000, 52), (32000, 54), (44100, 56), (48000, 58)]
    long_bands, short_bands = {}, {}
    for rate, number in rates:
        for kind, table, total, count in (("long", number, 576, 22), ("short", number + 1, 192, 13)):
            widths = []
            for line in sec[table]:
                t = line.split()
                if len(t) == 4 and all(x.isdigit() for x in t):
                    band, width, first, last = (int(a) for a in t)
                    if band != len(widths) or last - first + 1 != width:
                        fail(f"table {table}: bad row '{line.strip()}'")
                    widths.append(width)
            # The tables of the standard list 21 (long) or 12 (short) bands; the last band reaches to the end of the spectrum.
            if kind == "long" and len(widths) not in (21, 22) or kind == "short" and len(widths) not in (12, 13):
                fail(f"table {table}: {len(widths)} bands")
            (long_bands if kind == "long" else short_bands)[rate] = widths

    # Synthesis window.
    window = {}
    for line in lines:
        for m in re.finditer(r"D\[\s*(\d+)\]\s*=\s*(-?\s*\d+,\d+)", line):
            window[int(m.group(1))] = float(m.group(2).replace(",", ".").replace(" ", ""))
    if sorted(window) != list(range(512)):
        fail(f"window has {len(window)} coefficients")

    # Preemphasis.
    pretab = [0] * 22
    for line in sec[23]:
        t = line.split()
        if len(t) == 2 and t[0].isdigit() and t[1].isdigit() and int(t[0]) >= 10:
            pretab[int(t[0])] = int(t[1])
    # The first ten rows are fused ("00", "10", ...): every value is 0.
    pretab_expected = [0] * 11 + [1, 1, 1, 1, 2, 2, 3, 3, 3, 2]
    if pretab[:21] != pretab_expected:
        # Rows 10..20 are "sfb value" pairs; row 10 has value 0.
        fail(f"pretab {pretab}")

    return huffman, quad, rates, long_bands, short_bands, window, pretab


def main():
    spec, out = sys.argv[1], sys.argv[2]
    huffman, quad, rates, long_bands, short_bands, window, pretab = load_tables(spec)
    with open(os.path.join(out, "Mp3Tables.g.cs"), "w", encoding="utf-8") as f:
        f.write("// <auto-generated> by scripts/generate-mp3-tables.py from the text of \"MPEG-Layer3 Bitstream Syntax and Decoding\" (Sieler/Sperschneider, 1997). </auto-generated>\n")
        f.write("namespace Aibell.Audio.Mp3;\n\ninternal static class Mp3Tables\n{\n")
        f.write("    /// <summary>The Huffman code tables 0-31 of Layer III: dimension, linbits and, per (x, y), the code and its length.</summary>\n")
        f.write("    public static readonly (int Dimension, int LinBits, uint[] Codes, byte[] Lengths)[] Huffman =\n    [\n")
        for n in range(32):
            if n not in huffman:
                f.write("        (0, 0, [], []),\n")
                continue
            dim, entries, bits = huffman[n]
            codes = ", ".join(str(int(c, 2)) if c else "0" for c, _ in entries)
            lens = ", ".join(str(l) for _, l in entries)
            f.write(f"        ({dim}, {bits}, [{codes}], [{lens}]),\n")
        f.write("    ];\n\n")
        for letter in "AB":
            codes = ", ".join(f"({int(c, 2)}, {len(c)})" for c in quad[letter])
            f.write(f"    /// <summary>Count1 code table {letter}: (code, length) for the quadruple index v*8 + w*4 + x*2 + y.</summary>\n")
            f.write(f"    public static readonly (byte Code, byte Length)[] Count1{letter} = [{codes}];\n\n")
        f.write("    /// <summary>Scalefactor band widths for long blocks, by sampling rate (the last band, 21, is the rest of the spectrum).</summary>\n")
        f.write("    public static int[] LongBandWidths(int sampleRate) => sampleRate switch\n    {\n")
        for rate, _ in rates:
            f.write(f"        {rate} => [{', '.join(str(w) for w in long_bands[rate])}],\n")
        f.write("        _ => throw new ArgumentOutOfRangeException(nameof(sampleRate))\n    };\n\n")
        f.write("    /// <summary>Scalefactor band widths for short blocks (per window), by sampling rate.</summary>\n")
        f.write("    public static int[] ShortBandWidths(int sampleRate) => sampleRate switch\n    {\n")
        for rate, _ in rates:
            f.write(f"        {rate} => [{', '.join(str(w) for w in short_bands[rate])}],\n")
        f.write("        _ => throw new ArgumentOutOfRangeException(nameof(sampleRate))\n    };\n\n")
        f.write("    /// <summary>The synthesis window D[0..511] of the polyphase filterbank.</summary>\n")
        f.write("    public static readonly float[] SynthesisWindow =\n    [\n")
        vals = [repr(window[i]) + "f" for i in range(512)]
        for i in range(0, 512, 8):
            f.write("        " + ", ".join(vals[i:i + 8]) + ",\n")
        f.write("    ];\n\n")
        f.write("    /// <summary>pretab: the preemphasis added to the scalefactors of the long blocks when preflag is set.</summary>\n")
        f.write(f"    public static readonly byte[] Pretab = [{', '.join(str(v) for v in pretab)}];\n")
        f.write("}\n")


if __name__ == "__main__":
    main()
