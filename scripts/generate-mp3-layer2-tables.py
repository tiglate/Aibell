#!/usr/bin/env python3
"""Generates src/Aibell.Audio.Mp3/Mp3Layer2Tables.g.cs from scripts/mpeg-layer-ii-tables.txt: the Layer II bit allocation tables (ISO/IEC 11172-3
Table 3-B.2a-d and ISO/IEC 13818-3 Table B.1) as the quantization class of every (subband, allocation code). The text was reconstructed for this project from
independent implementations (the standard's Annex B was not available); everything that can be checked is: the row widths, the sums of nbal, the sblimit, the match
with the authentic 13818-3 Table B.1 reference values, and the constants C and D of Table 3-B.4 against the closed forms they follow.

usage: generate-mp3-layer2-tables.py <tables.txt> <output dir>
"""
import os
import re
import sys

CLASSES = [3, 5, 7, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535]
GROUPED = {3: (2, 5), 5: (3, 7), 9: (4, 10)}  # nlevels -> (bits per sample code, bits per codeword)
NAMES = ["3-B.2a", "3-B.2b", "3-B.2c", "3-B.2d", "B.1"]
EXPECTED = {"3-B.2a": (27, 88), "3-B.2b": (30, 94), "3-B.2c": (8, 26), "3-B.2d": (12, 38), "B.1": (30, 75)}


def fail(message):
    sys.exit("error: " + message)


def parse(path):
    lines = open(path, encoding="utf-8").read().splitlines()
    tables = {}
    current = None
    for line in lines:
        m = re.match(r"^TABLE (3-B\.2[a-d]|B\.1)\b", line)
        if m:
            current = m.group(1)
            tables[current] = {}
            continue
        if re.match(r"^\d+\. ", line):
            current = None
        if current is None:
            continue
        m = re.match(r"^\s+(\d+)\s+(\d)\s+\|(.*)$", line)
        if m:
            sb, nbal = int(m.group(1)), int(m.group(2))
            cells = m.group(3).split()
            row = [None if c == "-" else int(c) for c in cells]
            if len(row) != 16:
                fail(f"{current} subband {sb}: {len(row)} cells")
            tables[current][sb] = (nbal, row)
    return tables


def check(tables):
    for name in NAMES:
        if name not in tables:
            fail(f"table {name} missing")
        t = tables[name]
        if sorted(t) != list(range(32)):
            fail(f"{name}: subbands {sorted(t)}")
        sblimit = 0
        total = 0
        for sb in range(32):
            nbal, row = t[sb]
            if nbal == 0:
                if row[0] != 0 or any(c is not None for c in row[1:]):
                    fail(f"{name} sb {sb}: nbal 0 row")
                continue
            sblimit = sb + 1
            total += nbal
            if row[0] != 0:
                fail(f"{name} sb {sb}: code 0 is not 0")
            for code in range(16):
                present = row[code] is not None
                if present != (code < (1 << nbal)):
                    fail(f"{name} sb {sb}: row width does not match nbal {nbal}")
                if present and code > 0 and row[code] not in CLASSES:
                    fail(f"{name} sb {sb}: {row[code]} is not a quantization class")
            # The levels of a row increase with the code.
            levels = [c for c in row if c]
            if levels != sorted(levels) or len(set(levels)) != len(levels):
                fail(f"{name} sb {sb}: levels not increasing")
        if (sblimit, total) != EXPECTED[name]:
            fail(f"{name}: sblimit/sum {(sblimit, total)}")
    # ISO/IEC 13818-3 Table B.1, as read from the standard's own text.
    low = tables["B.1"]
    for sb in range(30):
        nbal, row = low[sb]
        want_nbal = 4 if sb < 4 else 3 if sb < 11 else 2
        if nbal != want_nbal:
            fail(f"B.1 sb {sb}: nbal {nbal}")
        want = [0, 3, 5, 7, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383] if sb < 4 else \
               [0, 3, 5, 9, 15, 31, 63, 127] if sb < 11 else [0, 3, 5, 9]
        if [c for c in row if c is not None] != want:
            fail(f"B.1 sb {sb}: levels {row}")


def constants_from_text(path):
    result = {}
    for line in open(path, encoding="utf-8").read().splitlines():
        m = re.match(r"^\s+(\d+)\s+(yes|no)\s+(\d+)\s+(\d+)\s+(\d\.\d+)\s+(\d\.\d+)\s*$", line)
        if m:
            result[int(m.group(1))] = (m.group(2) == "yes", int(m.group(3)), int(m.group(4)), float(m.group(5)), float(m.group(6)))
    return result


def check_classes(constants):
    if sorted(constants) != CLASSES:
        fail("quantization classes missing")
    for level, (grouped, bits, samples, c, d) in constants.items():
        m, codeword = GROUPED.get(level, (None, None))
        if level in GROUPED:
            if not grouped or bits != codeword or samples != 3:
                fail(f"class {level}: grouping")
            sample_bits = m
        else:
            sample_bits = (level + 1).bit_length() - 1
            if (1 << sample_bits) != level + 1 or grouped or bits != sample_bits or samples != 1:
                fail(f"class {level}: sample width")
        # The reconstruction s'' = C * (s''' + D) of a code q with s''' = q / 2^(m-1) - 1 is symmetric about zero: C = 2^m / L, D = 1 - (L - 1) / 2^m.
        if abs(c - (1 << sample_bits) / level) > 2e-11 or abs(d - (1 - (level - 1) / (1 << sample_bits))) > 2e-10:
            fail(f"class {level}: C/D {c} {d}")


def main():
    source, out = sys.argv[1:3]
    tables = parse(source)
    check(tables)
    check_classes(constants_from_text(source))
    path = os.path.join(out, "Mp3Layer2Tables.g.cs")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("// <auto-generated> by scripts/generate-mp3-layer2-tables.py from scripts/mpeg-layer-ii-tables.txt; do not edit. </auto-generated>\n")
        f.write("namespace Aibell.Audio.Mp3;\n\n")
        f.write("internal static class Mp3Layer2Tables\n{\n")
        f.write("    /// <summary>The number of levels of each quantization class (Table 3-B.4); class 0 is \"no allocation\".</summary>\n")
        f.write("    public static readonly int[] ClassLevels = [0, " + ", ".join(str(c) for c in CLASSES) + "];\n\n")
        f.write("    /// <summary>The tables in the order 3-B.2a, 3-B.2b, 3-B.2c, 3-B.2d, B.1 (ISO/IEC 13818-3, low sampling frequencies): sblimit.</summary>\n")
        f.write("    public static readonly int[] SubbandLimit = [" + ", ".join(str(EXPECTED[n][0]) for n in NAMES) + "];\n\n")
        f.write("    /// <summary>Width in bits of the allocation field of every subband, per table.</summary>\n")
        f.write("    public static readonly byte[][] AllocationBits =\n    [\n")
        for name in NAMES:
            f.write("        [" + ", ".join(str(tables[name][sb][0]) for sb in range(32)) + "],\n")
        f.write("    ];\n\n")
        f.write("    /// <summary>The quantization class (index into <see cref=\"ClassLevels\"/>) of every allocation code, per table and subband: [table][subband * 16 + code].</summary>\n")
        f.write("    public static readonly byte[][] Quantizer =\n    [\n")
        for name in NAMES:
            values = []
            for sb in range(32):
                row = tables[name][sb][1]
                for code in range(16):
                    cell = row[code]
                    values.append(0 if not cell else CLASSES.index(cell) + 1)
            f.write("        [" + ", ".join(str(v) for v in values) + "],\n")
        f.write("    ];\n}\n")
    print("wrote", path)


main()
