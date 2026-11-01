#!/usr/bin/env python3
"""Writes MP3 streams made of random but valid Layer III frames that use what real encoders (LAME) do not: mixed blocks, intensity stereo, scfsi, preflag,
scalefac_scale, the long-escape Huffman tables and all three MPEG versions. The frames are built from the same specification text as the decoder's tables
(Huffman codes, scalefactor bands). FFmpeg then decodes each stream, as a black-box yardstick, into <name>.f32.gz.

usage: generate-mp3-synthetic.py <spec.txt> <output dir> <ffmpeg>
"""
import gzip
import importlib.util
import os
import random
import subprocess
import sys

OPTS = set(os.environ.get('GEN_OPTS', 'quiet,legalseq,esc').split(','))
here = os.path.dirname(os.path.abspath(__file__))
spec_module = importlib.util.spec_from_file_location("tables", os.path.join(here, "generate-mp3-tables.py"))
tables = importlib.util.module_from_spec(spec_module)
spec_module.loader.exec_module(tables)


class Bits:
    def __init__(self):
        self.bits = []

    def put(self, value, count):
        for i in range(count - 1, -1, -1):
            self.bits.append((value >> i) & 1)

    def code(self, text):
        self.bits.extend(1 if c == "1" else 0 for c in text)

    def __len__(self):
        return len(self.bits)

    def to_bytes(self):
        padded = self.bits + [0] * (-len(self.bits) % 8)
        return bytes(int("".join(map(str, padded[i:i + 8])), 2) for i in range(0, len(padded), 8))


BITRATES_MPEG1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
BITRATES_LSF = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]
SLEN_MPEG1 = [(0, 0), (0, 1), (0, 2), (0, 3), (3, 0), (1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (2, 3), (3, 1), (3, 2), (3, 3), (4, 2), (4, 3)]
# Largest value a table can code (without linbits), by table number.
TABLE_MAX = {1: 1, 2: 2, 3: 2, 5: 3, 6: 3, 7: 5, 8: 5, 9: 5, 10: 7, 11: 7, 12: 7, 13: 15, 15: 15}
LINBITS = {16: 1, 17: 2, 18: 3, 19: 4, 20: 6, 21: 8, 22: 10, 23: 13, 24: 4, 25: 5, 26: 6, 27: 7, 28: 8, 29: 9, 30: 11, 31: 13}


class Codec:
    def __init__(self, spec):
        (self.huffman, self.quad, self.rates, self.long_bands, self.short_bands, self.window, self.pretab) = tables.load_tables(spec)
        self.codes = {}
        for n, (dim, entries, bits) in self.huffman.items():
            self.codes[n] = {(i // dim, i % dim): code for i, (code, _) in enumerate(entries)}

    def layout(self, rate, mpeg1):
        lw, sw = self.long_bands[rate], self.short_bands[rate]
        long_edges = [0]
        for w in lw:
            long_edges.append(long_edges[-1] + w)
        long_edges.append(576)
        short_edges = [0]
        for w in sw:
            short_edges.append(short_edges[-1] + w)
        short_edges.append(192)
        longs = [(long_edges[i], long_edges[i + 1] - long_edges[i], i, -1) for i in range(22)]

        def shorts(first, start):
            out, pos = [], start
            for sfb in range(first, 13):
                width = short_edges[sfb + 1] - short_edges[sfb]
                for w in range(3):
                    out.append((pos, width, sfb, w))
                    pos += width
            return out

        mixed_long = 8 if mpeg1 else 6
        mixed = longs[:mixed_long] + shorts(3, long_edges[mixed_long])
        return longs, shorts(0, 0), mixed, short_edges


def pick_table(codec, values):
    top = max((abs(v) for v in values), default=0)
    if top == 0:
        return 0
    for n in (1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15):
        if TABLE_MAX[n] >= top:
            # Prefer a bigger table now and then so that every table gets used.
            return n
    for n in sorted(LINBITS):
        if (15 + (1 << LINBITS[n]) - 1) >= top:
            return n
    raise ValueError(top)


def encode_pair(bits, codec, table, x, y):
    dim, entries, linbits = codec.huffman[table]
    ax, ay = abs(x), abs(y)
    lx = ly = None
    if table in LINBITS:
        if ax >= 15:
            lx, ax = ax - 15, 15
        if ay >= 15:
            ly, ay = ay - 15, 15
    bits.code(codec.codes[table][(ax, ay)])
    if lx is not None:
        bits.put(lx, linbits)
    if x != 0:
        bits.put(1 if x < 0 else 0, 1)
    if ly is not None:
        bits.put(ly, linbits)
    if y != 0:
        bits.put(1 if y < 0 else 0, 1)


class Granule:
    pass


def random_values(rng, bands, limit_lines, loud):
    """Quantized values in coded order: a big-values part, a count1 part and zeros."""
    values = [0] * 576
    big_lines = rng.randrange(0, limit_lines // 2 + 1) * 2 if rng.random() < 0.9 else 0
    big_lines = min(big_lines, limit_lines & ~1)
    for i in range(big_lines):
        scale = max(1, int(24 * (1 - i / max(big_lines, 1)) ** 2)) if loud else 3
        v = int(rng.gauss(0, scale))
        if rng.random() < 0.02 and (loud or 'esc' in OPTS):
            v = rng.choice([-1, 1]) * rng.randrange(15, 60)
        values[i] = max(-300, min(300, v))
    quads = rng.randrange(0, (limit_lines - big_lines) // 4 + 1)
    for i in range(big_lines, big_lines + 4 * quads):
        values[i] = rng.choice([-1, 0, 0, 1])
    return values, big_lines // 2


PREVIOUS = {}


def build_granule(rng, codec, header, ch, gr, intensity_channel, same_as, scfsi, force_long=False):
    mpeg1, rate = header["mpeg1"], header["rate"]
    longs, shorts, mixed, short_edges = codec.layout(rate, mpeg1)
    g = Granule()
    if same_as is not None:
        g.ws, g.block_type, g.mixed = same_as.ws, same_as.block_type, same_as.mixed
    else:
        g.ws = rng.random() < 0.5 and not force_long and 'nows' not in OPTS
        g.block_type = (rng.choice([2]) if 'shortonly' in OPTS else rng.choice([1, 3]) if 'startstop' in OPTS else rng.choice([1, 2, 2, 3])) if g.ws else 0
        g.mixed = g.ws and g.block_type == 2 and rng.random() < 0.5 and 'nomixed' not in OPTS and header['mpeg1']
    if same_as is None and 'legalseq' in OPTS:
        previous = PREVIOUS.get(ch, 0)
        allowed = [0, 1] if previous in (0, 3) else [2, 3]
        if force_long:
            allowed = [t for t in allowed if t in (0,)] or [0]
        chosen = rng.choice(allowed)
        g.ws, g.block_type, g.mixed = chosen != 0, chosen, chosen == 2 and rng.random() < 0.5 and 'nomixed' not in OPTS and header['mpeg1']
    if same_as is None:
        PREVIOUS[ch] = g.block_type if g.ws else 0
    g.short = g.ws and g.block_type == 2
    bands = mixed if g.short and g.mixed else shorts if g.short else longs
    g.bands = bands
    g.global_gain = rng.randrange(150, 200) if 'quiet' not in OPTS else rng.randrange(int(os.environ.get('GGLO', '165')), int(os.environ.get('GGHI', '180')))
    if 'nosub' in OPTS:
        g.subblock_gain = [0, 0, 0]
    g.subblock_gain = [rng.randrange(0, 4) for _ in range(3)]
    g.scale = rng.random() < 0.5 and 'noscale' not in OPTS
    g.preflag = False

    # Scalefactors.
    n_long, n_short = 22, 39
    g.sf_long = [0] * n_long
    g.sf_short = [0] * n_short
    order = []  # the scalefactors in the order they are transmitted: ('l', sfb) or ('s', sfb, window) with their bit width
    if mpeg1:
        g.compress = rng.randrange(16)
        if intensity_channel:
            g.compress = rng.choice([11, 12, 13, 14, 15])
        slen1, slen2 = SLEN_MPEG1[g.compress]
        if g.short:
            if g.mixed:
                for sfb in range(8):
                    order.append((("l", sfb), slen1, False))
                for sfb in range(3, 12):
                    for w in range(3):
                        order.append((("s", sfb, w), slen1 if sfb < 6 else slen2, False))
            else:
                for sfb in range(12):
                    for w in range(3):
                        order.append((("s", sfb, w), slen1 if sfb < 6 else slen2, False))
        else:
            for band, (a, b) in enumerate([(0, 6), (6, 11), (11, 16), (16, 21)]):
                for sfb in range(a, b):
                    reuse = gr == 1 and scfsi[band]
                    order.append((("l", sfb), slen1 if band < 2 else slen2, reuse))
        g.preflag = rng.random() < 0.3 and not g.short and 'nopre' not in OPTS
    else:
        kind = 1 if g.short and not g.mixed else 2 if g.short else 0
        if intensity_channel:
            scale_bit = rng.randrange(2)
            c = rng.randrange(0, 180)
            g.compress = c * 2 + scale_bit
            g.intensity_scale = scale_bit
            s = [c // 36, (c % 36) // 6, (c % 36) % 6, 0]
            counts = [[7, 7, 7, 0], [12, 12, 12, 0], [6, 15, 12, 0]][kind]
        else:
            g.intensity_scale = 0
            pick = rng.randrange(3)
            if pick == 0:
                c = rng.randrange(0, 400)
                s = [(c >> 4) // 5, (c >> 4) % 5, (c % 16) >> 2, c % 4]
                counts = [[6, 5, 5, 5], [9, 9, 9, 9], [6, 9, 9, 9]][kind]
                g.compress = c
            elif pick == 1:
                c = rng.randrange(400, 500)
                d = c - 400
                s = [(d >> 2) // 5, (d >> 2) % 5, d % 4, 0]
                counts = [[6, 5, 7, 3], [9, 9, 12, 6], [6, 9, 12, 6]][kind]
                g.compress = c
            else:
                c = rng.randrange(500, 512)
                d = c - 500
                s = [d // 3, d % 3, 0, 0]
                counts = [[11, 10, 0, 0], [18, 18, 0, 0], [15, 18, 0, 0]][kind]
                g.compress = c
                g.preflag = True
        idx = 0
        for part in range(4):
            for _ in range(counts[part]):
                if kind == 0:
                    key = ("l", idx)
                elif kind == 1:
                    key = ("s", idx // 3, idx % 3)
                else:
                    key = ("l", idx) if idx < 6 else ("s", 3 + (idx - 6) // 3, (idx - 6) % 3)
                order.append((key, s[part], False))
                idx += 1
    g.order = order
    part2 = Bits()
    for key, width, reuse in order:
        if reuse:
            # Reused from granule 0 through scfsi: not transmitted.
            continue
        value = rng.randrange(0, 1 << width) if width and 'zerosf' not in OPTS else 0
        if intensity_channel and width and 'pos0' in OPTS:
            value = 0
        elif intensity_channel and width and not mpeg1:
            # The largest position is the "illegal" one in the standard; decoders disagree on what to do with it, so the yardstick is not asked.
            value = rng.randrange(0, min((1 << width) - 1, 15)) if width > 1 else 0
        if key[0] == "l":
            g.sf_long[key[1]] = value
        else:
            g.sf_short[key[1] * 3 + key[2]] = value
        part2.put(value, width)
    g.part2 = part2

    # Spectral values.
    limit = 576
    if intensity_channel:
        # Nothing above some band, so the rest is intensity coded.
        stop = rng.randrange(1, len(bands))
        limit = bands[stop][0]
    values, big_pairs = random_values(rng, bands, limit, loud='quiet' not in OPTS)
    for o in OPTS:
        if o.startswith('cut='):
            cut = int(o[4:])
            values[cut:] = [0] * (576 - cut)
            big_pairs = min(big_pairs, cut // 2)
        if o == 'nocount1':
            for i in range(big_pairs * 2, 576):
                values[i] = 0
    g.values = values
    g.big_values = big_pairs
    g.count1_table = rng.randrange(2)

    # Regions.
    if g.ws:
        g.region0, g.region1 = (8 if g.short and not g.mixed else 7), 36
    else:
        g.region0, g.region1 = rng.randrange(0, 16), rng.randrange(0, 8)
    def lines(count):
        return min(sum(b[1] for b in bands[:count]), 576)
    r0 = lines(g.region0 + 1)
    r1 = lines(g.region0 + g.region1 + 2)
    big = g.big_values * 2
    bounds = [(0, min(r0, big)), (min(r0, big), min(r1, big)), (min(r1, big), big)]
    g.tables = []
    huff = Bits()
    for start, end in bounds:
        chunk = values[start:end]
        if 'sametable' in OPTS:
            chunk = values[:big]
        table = pick_table(codec, chunk) if chunk else 0
        if table and rng.random() < 0.3 and table in TABLE_MAX:
            options = [n for n in TABLE_MAX if TABLE_MAX[n] >= max(abs(v) for v in chunk)]
            table = rng.choice(options)
        g.tables.append(table)
        if table == 0:
            continue
        for i in range(start, end, 2):
            encode_pair(huff, codec, table, values[i], values[i + 1])
    quad = codec.quad["A" if g.count1_table == 0 else "B"]
    i = big
    while i < 576 and any(values[i:]):
        v = values[i:i + 4]
        index = abs(v[0]) * 8 + abs(v[1]) * 4 + abs(v[2]) * 2 + abs(v[3])
        huff.code(quad[index])
        for x in v:
            if x != 0:
                huff.put(1 if x < 0 else 0, 1)
        i += 4
    # The count1 region ends at the last non-zero quadruple.
    g.huffman = huff
    if g.ws:
        g.tables = (g.tables + [0, 0, 0])[:3]
    return g


def build_frame(rng, codec, spec):
    version = spec["version"]
    mpeg1 = version == 1
    rate_index = spec["rate_index"]
    sample_rate = [[44100, 48000, 32000], [22050, 24000, 16000], [11025, 12000, 8000]][{1: 0, 2: 1, 25: 2}[version]][rate_index]
    mode = spec["mode"]
    mode_ext = spec["mode_ext"] if mode == 1 else 0
    channels = 1 if mode == 3 else 2
    header = {"mpeg1": mpeg1, "rate": sample_rate}
    ngr = 2 if mpeg1 else 1
    bitrate_index = 14 if mpeg1 else 14
    bitrate = (BITRATES_MPEG1 if mpeg1 else BITRATES_LSF)[bitrate_index] * 1000
    frame_len = (144 if mpeg1 else 72) * bitrate // sample_rate
    intensity = mode == 1 and (mode_ext & 1) != 0
    scfsi = [[rng.random() < 0.4 for _ in range(4)] if mpeg1 and 'noscfsi' not in OPTS else [False] * 4 for _ in range(channels)]
    if mode == 1:
        scfsi[1] = [False] * 4
    granules = [[None] * channels for _ in range(ngr)]
    for gr in range(ngr):
        for ch in range(channels):
            same = granules[gr][0] if (ch == 1 and mode == 1) else None
            use_scfsi = scfsi[ch]
            # scfsi only for long blocks in both granules.
            g = build_granule(rng, codec, header, ch, gr, intensity and ch == 1, same, use_scfsi, force_long=any(use_scfsi))
            granules[gr][ch] = g
    if mpeg1:
        for ch in range(channels):
            g0, g1 = granules[0][ch], granules[1][ch]
            for band, (a, b) in enumerate([(0, 6), (6, 11), (11, 16), (16, 21)]):
                if scfsi[ch][band] and not g1.short:
                    for sfb in range(a, b):
                        g1.sf_long[sfb] = g0.sf_long[sfb]
    # Header and side information.
    out = Bits()
    out.put(0x7FF, 11)
    out.put(1 if version != 25 else 0, 1)
    out.put(1 if mpeg1 else 0, 1)
    out.put(1, 2)
    out.put(1, 1)
    out.put(bitrate_index, 4)
    out.put(rate_index, 2)
    out.put(0, 1)
    out.put(0, 1)
    out.put(mode, 2)
    out.put(mode_ext, 2)
    out.put(0, 2)
    out.put(0, 2)
    if mpeg1:
        out.put(0, 9)
        out.put(0, 5 if channels == 1 else 3)
        for ch in range(channels):
            for band in range(4):
                out.put(1 if scfsi[ch][band] else 0, 1)
    else:
        out.put(0, 8)
        out.put(0, 1 if channels == 1 else 2)
    main = Bits()
    for gr in range(ngr):
        for ch in range(channels):
            g = granules[gr][ch]
            p2 = Bits()
            # Re-emit the scalefactors from the final values (scfsi reuse changed some).
            for key, width, reuse in g.order:
                if reuse:
                    continue
                if key[0] == "l":
                    p2.put(g.sf_long[key[1]], width)
                else:
                    p2.put(g.sf_short[key[1] * 3 + key[2]], width)
            g.part2 = p2
            g.part23 = len(p2) + len(g.huffman)
            main.bits.extend(p2.bits)
            main.bits.extend(g.huffman.bits)
    for gr in range(ngr):
        for ch in range(channels):
            g = granules[gr][ch]
            out.put(g.part23, 12)
            out.put(g.big_values, 9)
            out.put(g.global_gain, 8)
            out.put(g.compress, 4 if mpeg1 else 9)
            out.put(1 if g.ws else 0, 1)
            if g.ws:
                out.put(g.block_type, 2)
                out.put(1 if g.mixed else 0, 1)
                out.put(g.tables[0], 5)
                out.put(g.tables[1], 5)
                for w in range(3):
                    out.put(g.subblock_gain[w], 3)
            else:
                for region in range(3):
                    out.put(g.tables[region], 5)
                out.put(g.region0, 4)
                out.put(g.region1, 3)
            if mpeg1:
                out.put(1 if g.preflag else 0, 1)
            out.put(1 if g.scale else 0, 1)
            out.put(g.count1_table, 1)
    data = out.to_bytes() + main.to_bytes()
    if len(data) > frame_len:
        raise ValueError(f"frame data {len(data)} exceeds {frame_len}")
    if 'GEN_DUMP' in os.environ:
        for gr in range(ngr):
            for ch in range(channels):
                g = granules[gr][ch]
                print('GR', gr, ch, 'bt', g.block_type, 'mixed', g.mixed, 'compress', g.compress, 'regions', g.region0, g.region1, 'tables', g.tables, 'big', g.big_values, 'gain', g.global_gain, 'p23', g.part23, file=sys.stderr)
    return data + bytes(frame_len - len(data))


def frame_or_retry(rng, codec, case):
    for _ in range(200):
        try:
            return build_frame(rng, codec, case)
        except ValueError:
            continue
    raise RuntimeError("no frame fits")


def main():
    spec_text, out_dir, ffmpeg = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out_dir, exist_ok=True)
    codec = Codec(spec_text)
    cases = {
        "synthetic_mpeg1_stereo": dict(version=1, rate_index=0, mode=0, mode_ext=0),
        "synthetic_mpeg1_ms": dict(version=1, rate_index=1, mode=1, mode_ext=2),
        "synthetic_mpeg1_intensity": dict(version=1, rate_index=0, mode=1, mode_ext=1),
        "synthetic_mpeg1_ms_intensity": dict(version=1, rate_index=2, mode=1, mode_ext=3),
        "synthetic_mpeg1_mono": dict(version=1, rate_index=1, mode=3, mode_ext=0),
        "synthetic_mpeg2_stereo": dict(version=2, rate_index=0, mode=0, mode_ext=0),
        "synthetic_mpeg2_intensity": dict(version=2, rate_index=1, mode=1, mode_ext=1),
        "synthetic_mpeg2_ms_intensity": dict(version=2, rate_index=2, mode=1, mode_ext=3),
        "synthetic_mpeg2_mono": dict(version=2, rate_index=0, mode=3, mode_ext=0),
        "synthetic_mpeg25_intensity": dict(version=25, rate_index=2, mode=1, mode_ext=1),
        "synthetic_mpeg25_stereo": dict(version=25, rate_index=0, mode=0, mode_ext=0),
    }
    for index, (name, case) in enumerate(cases.items()):
        if os.environ.get('GEN_ONLY') and os.environ['GEN_ONLY'] != name:
            continue
        rng = random.Random(1000 + index + int(os.environ.get('GEN_SEED', '1')))
        frames = b"".join(frame_or_retry(rng, codec, case) for _ in range(10))
        path = os.path.join(out_dir, name + ".mp3")
        with open(path, "wb") as f:
            f.write(frames)
        decoded = subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-f", "f32le", "-c:a", "pcm_f32le", "-"], check=True, capture_output=True).stdout
        with open(os.path.join(out_dir, name + ".f32.gz"), "wb") as f:
            f.write(gzip.compress(decoded, 9, mtime=0))


if __name__ == "__main__":
    main()


def probe(spec_text, out_dir, ffmpeg):
    """One short-block frame per spectral line position (MPEG-2 mono 22.05 kHz), followed by an empty frame: isolates reordering mistakes."""
    codec = Codec(spec_text)
    os.makedirs(out_dir, exist_ok=True)
    longs, shorts, mixed, edges = codec.layout(22050, False)
    frames = []
    for line in list(range(0, 192, 7)) + [191, 190, 189, 188, 187, 186, 185, 184, 120, 100]:
        pass
    positions = sorted(set(list(range(0, 576, 5)) + [b[0] for b in shorts] + [b[0] + b[1] - 1 for b in shorts]))
    for pos in positions:
        for block_type in (2,):
            out = Bits()
            out.put(0x7FF, 11); out.put(1, 1); out.put(0, 1); out.put(1, 2); out.put(1, 1)
            out.put(14, 4); out.put(0, 2); out.put(0, 1); out.put(0, 1); out.put(3, 2); out.put(0, 2); out.put(0, 2); out.put(0, 2)
            out.put(0, 8); out.put(0, 1)
            huff = Bits()
            table = 1
            end = pos - (pos % 2) + 2
            first = pos - (pos % 2)
            for i in range(0, first, 2):
                pass
            # Values: zero up to `first`, then the pair holding the probe. Zeros need a table; table 1 codes (0,0) as '1'.
            for i in range(0, first + 2, 2):
                x, y = (1, 0) if (i == pos - pos % 2 and pos % 2 == 0) else (0, 1) if (i == pos - pos % 2) else (0, 0)
                encode_pair(huff, codec, 1, x, y)
            big = (first + 2) // 2
            g_compress = 0
            out.put(len(huff), 12); out.put(big, 9); out.put(190, 8); out.put(g_compress, 9)
            out.put(1, 1); out.put(2, 2); out.put(0, 1); out.put(1, 5); out.put(1, 5)
            for w in range(3): out.put(0, 3)
            out.put(0, 1); out.put(0, 1)
            data = out.to_bytes() + huff.to_bytes()
            frame_len = 72 * 160000 // 22050
            frames.append(data + bytes(frame_len - len(data)))
            frames.append(bytes(frame_len)[:0] + silent_frame(frame_len))
    path = os.path.join(out_dir, "probe_short_mpeg2_mono.mp3")
    with open(path, "wb") as f:
        f.write(b"".join(frames))
    decoded = subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-f", "f32le", "-c:a", "pcm_f32le", "-"], check=True, capture_output=True).stdout
    with open(os.path.join(out_dir, "probe_short_mpeg2_mono.f32.gz"), "wb") as f:
        f.write(gzip.compress(decoded, 9, mtime=0))
    with open(os.path.join(out_dir, "probe_positions.txt"), "w") as f:
        f.write("\n".join(map(str, positions)))


def silent_frame(frame_len):
    out = Bits()
    out.put(0x7FF, 11); out.put(1, 1); out.put(0, 1); out.put(1, 2); out.put(1, 1)
    out.put(14, 4); out.put(0, 2); out.put(0, 1); out.put(0, 1); out.put(3, 2); out.put(0, 2); out.put(0, 2); out.put(0, 2)
    out.put(0, 8); out.put(0, 1)
    out.put(0, 12); out.put(0, 9); out.put(150, 8); out.put(0, 9); out.put(0, 1); out.put(0, 5 + 2 + 3 + 9 + 2 - 5)
    data = out.to_bytes()
    return data + bytes(frame_len - len(data))


def probe_scalefactors(spec_text, out_dir, ffmpeg):
    """MPEG-1 mono 44.1 kHz short-block frames, each with a single non-zero line and a single non-zero scalefactor, to see which scalefactor scales which band."""
    codec = Codec(spec_text)
    os.makedirs(out_dir, exist_ok=True)
    longs, shorts, mixed, edges = codec.layout(44100, True)
    frames = []
    cases = []
    frame_len = 144 * 320000 // 44100
    for band in shorts:
        start, width, sfb, window = band
        if sfb >= 12:
            continue
        for sf_sfb, sf_window in ((sfb, window), (sfb, (window + 1) % 3), (min(sfb + 1, 11), window)):
            cases.append((start, sfb, window, sf_sfb, sf_window))
    for start, sfb, window, sf_sfb, sf_window in cases:
        out = Bits()
        out.put(0x7FF, 11); out.put(1, 1); out.put(1, 1); out.put(1, 2); out.put(1, 1)
        out.put(14, 4); out.put(0, 2); out.put(0, 1); out.put(0, 1); out.put(3, 2); out.put(0, 2); out.put(0, 2); out.put(0, 2)
        out.put(0, 9); out.put(0, 5)
        for _ in range(4):
            out.put(0, 1)
        granule_data = []
        for gr in range(2):
            p2 = Bits()
            for s in range(12):
                for w in range(3):
                    p2.put(int(os.environ.get('SFVAL', '3')) if (s, w) == (sf_sfb, sf_window) and gr == 0 else 0, int(os.environ.get('SLEN', '2')) if s < 6 else int(os.environ.get('SLEN2', '2')))
            huff = Bits()
            first = start - start % 2
            for i in range(0, first + 2, 2):
                x, y = (0, 0)
                if gr == 0 and i == first:
                    x, y = (1, 0) if start % 2 == 0 else (0, 1)
                encode_pair(huff, codec, 1, x, y)
            granule_data.append((p2, huff, (first + 2) // 2))
        for p2, huff, big in granule_data:
            out.put(len(p2) + len(huff), 12); out.put(big, 9); out.put(int(os.environ.get('GG', '190')), 8); out.put(int(os.environ.get('COMP', '9')), 4)
            out.put(1, 1); out.put(2, 2); out.put(0, 1); out.put(1, 5); out.put(1, 5)
            for w in range(3): out.put(0, 3)
            out.put(0, 1); out.put(0, 1); out.put(0, 1)
        body = Bits()
        for p2, huff, big in granule_data:
            body.bits.extend(p2.bits); body.bits.extend(huff.bits)
        data = out.to_bytes() + body.to_bytes()
        assert len(data) <= frame_len, len(data)
        frames.append(data + bytes(frame_len - len(data)))
    path = os.path.join(out_dir, "probe_sf.mp3")
    with open(path, "wb") as f:
        f.write(b"".join(frames))
    decoded = subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-f", "f32le", "-c:a", "pcm_f32le", "-"], check=True, capture_output=True).stdout
    with open(os.path.join(out_dir, "probe_sf.f32.gz"), "wb") as f:
        f.write(gzip.compress(decoded, 9, mtime=0))
    with open(os.path.join(out_dir, "probe_sf.txt"), "w") as f:
        f.write("\n".join(" ".join(map(str, c)) for c in cases))


def probe_lsf_intensity(spec_text, out_dir, ffmpeg):
    """MPEG-2 joint stereo (intensity) frames: one line in the left channel, one intensity position in the right channel's scalefactors."""
    codec = Codec(spec_text)
    os.makedirs(out_dir, exist_ok=True)
    longs, shorts, mixed, edges = codec.layout(22050, False)
    frame_len = 72 * 160000 // 22050
    frames, cases = [], []
    band = 2
    start = longs[band][0]
    for scale_bit in (0, 1):
        for pos in range(0, 15):
            cases.append((scale_bit, pos))
            out = Bits()
            out.put(0x7FF, 11); out.put(1, 1); out.put(0, 1); out.put(1, 2); out.put(1, 1)
            out.put(14, 4); out.put(0, 2); out.put(0, 1); out.put(0, 1); out.put(1, 2); out.put(1, 2); out.put(0, 2); out.put(0, 2)
            out.put(0, 8); out.put(0, 2)
            main = Bits()
            huff = Bits()
            first = start - start % 2
            for i in range(0, first + 2, 2):
                x, y = (0, 0)
                if i == first:
                    x, y = (1, 0) if start % 2 == 0 else (0, 1)
                encode_pair(huff, codec, 1, x, y)
            # Channel 0: plain scalefactors (compress 0 = no bits), one value.
            out.put(len(huff), 12); out.put((first + 2) // 2, 9); out.put(200, 8); out.put(0, 9)
            out.put(0, 1); out.put(1, 5); out.put(1, 5); out.put(1, 5); out.put(0, 4); out.put(0, 3); out.put(0, 1); out.put(0, 1)
            main.bits.extend(huff.bits)
            # Channel 1: intensity scalefactors, slen1 = 4 bits for the first 7 bands, no spectral values.
            compress = (4 * 36) * 2 + scale_bit
            p2 = Bits()
            for b in range(7):
                p2.put(pos if b == band else 0, 4)
            out.put(len(p2), 12); out.put(0, 9); out.put(200, 8); out.put(compress, 9)
            out.put(0, 1); out.put(1, 5); out.put(1, 5); out.put(1, 5); out.put(0, 4); out.put(0, 3); out.put(0, 1); out.put(0, 1)
            main.bits.extend(p2.bits)
            data = out.to_bytes() + main.to_bytes()
            assert len(data) <= frame_len, len(data)
            frames.append(data + bytes(frame_len - len(data)))
    path = os.path.join(out_dir, "probe_lsf_intensity.mp3")
    open(path, "wb").write(b"".join(frames))
    decoded = subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-f", "f32le", "-c:a", "pcm_f32le", "-"], check=True, capture_output=True).stdout
    open(os.path.join(out_dir, "probe_lsf_intensity.f32"), "wb").write(decoded)
    open(os.path.join(out_dir, "cases.txt"), "w").write("\n".join(f"{a} {b}" for a, b in cases))


def probe_lsf_intensity_short(spec_text, out_dir, ffmpeg):
    """As probe_lsf_intensity, with short blocks: the position of one band and window."""
    codec = Codec(spec_text)
    os.makedirs(out_dir, exist_ok=True)
    longs, shorts, mixed, edges = codec.layout(22050, False)
    frame_len = 72 * 160000 // 22050
    frames, cases = [], []
    for sfb in (1, 4):
        for window in range(3):
            band = [b for b in shorts if b[2] == sfb and b[3] == window][0]
            start = band[0]
            for pos in (0, 1, 2, 5):
                cases.append((sfb, window, pos))
                out = Bits()
                out.put(0x7FF, 11); out.put(1, 1); out.put(0, 1); out.put(1, 2); out.put(1, 1)
                out.put(14, 4); out.put(0, 2); out.put(0, 1); out.put(0, 1); out.put(1, 2); out.put(1, 2); out.put(0, 2); out.put(0, 2)
                out.put(0, 8); out.put(0, 2)
                main = Bits()
                huff = Bits()
                first = start - start % 2
                for i in range(0, first + 2, 2):
                    x, y = (0, 0)
                    if i == first:
                        x, y = (1, 0) if start % 2 == 0 else (0, 1)
                    encode_pair(huff, codec, 1, x, y)
                out.put(len(huff), 12); out.put((first + 2) // 2, 9); out.put(200, 8); out.put(0, 9)
                out.put(1, 1); out.put(2, 2); out.put(0, 1); out.put(1, 5); out.put(1, 5)
                for _ in range(3): out.put(0, 3)
                out.put(0, 1); out.put(0, 1)
                main.bits.extend(huff.bits)
                compress = (4 * 36) * 2
                p2 = Bits()
                for i in range(12):
                    for w in range(3):
                        p2.put(pos if (i, w) == (sfb, window) else 0, 4)
                out.put(len(p2), 12); out.put(0, 9); out.put(200, 8); out.put(compress, 9)
                out.put(1, 1); out.put(2, 2); out.put(0, 1); out.put(1, 5); out.put(1, 5)
                for _ in range(3): out.put(0, 3)
                out.put(0, 1); out.put(0, 1)
                main.bits.extend(p2.bits)
                data = out.to_bytes() + main.to_bytes()
                assert len(data) <= frame_len, len(data)
                frames.append(data + bytes(frame_len - len(data)))
    path = os.path.join(out_dir, "probe_lsf_intensity.mp3")
    open(path, "wb").write(b"".join(frames))
    decoded = subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-f", "f32le", "-c:a", "pcm_f32le", "-"], check=True, capture_output=True).stdout
    open(os.path.join(out_dir, "probe_lsf_intensity.f32"), "wb").write(decoded)
    open(os.path.join(out_dir, "cases.txt"), "w").write("\n".join(" ".join(map(str, c)) for c in cases))
