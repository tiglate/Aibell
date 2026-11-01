namespace Aibell.Audio.Mp3;

/// <summary>
/// The Layer III decoding process of ISO/IEC 11172-3 and 13818-3: side information, bit reservoir, scalefactors, Huffman decoding, requantization, stereo processing,
/// reordering, alias reduction, IMDCT and the polyphase synthesis. One instance decodes one stream; nothing is allocated while decoding.
/// </summary>
internal sealed class Mp3Layer3
{
    private const int ReservoirSize = 511;
    private const float SqrtHalf = 0.70710678118654752f;

    private static readonly float[] Pow43 = BuildPow43();
    private static readonly float[] GainTable = BuildPowers(0.25, 320, 260);
    private static readonly float[] ScaleTable = BuildPowers(-0.5, 160, 0);
    private static readonly Mp3HuffmanTable[] PairTables = BuildPairTables();
    private static readonly Mp3HuffmanTable Count1A = new(4, 0, Codes(Mp3Tables.Count1A), Lengths(Mp3Tables.Count1A));
    private static readonly Mp3HuffmanTable Count1B = new(4, 0, Codes(Mp3Tables.Count1B), Lengths(Mp3Tables.Count1B));
    private static readonly float[] AliasCs = BuildAlias(cosine: true);
    private static readonly float[] AliasCa = BuildAlias(cosine: false);
    private static readonly (int Slen1, int Slen2)[] SlenTable =
        [(0, 0), (0, 1), (0, 2), (0, 3), (3, 0), (1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (2, 3), (3, 1), (3, 2), (3, 3), (4, 2), (4, 3)];

    private static readonly int[][] Partitions0 = [[6, 5, 5, 5], [9, 9, 9, 9], [6, 9, 9, 9]];
    private static readonly int[][] Partitions1 = [[6, 5, 7, 3], [9, 9, 12, 6], [6, 9, 12, 6]];
    private static readonly int[][] Partitions2 = [[11, 10, 0, 0], [18, 18, 0, 0], [15, 18, 0, 0]];
    private static readonly int[][] Partitions3 = [[7, 7, 7, 0], [12, 12, 12, 0], [6, 15, 12, 0]];
    private static readonly int[][] Partitions4 = [[6, 6, 6, 3], [12, 9, 9, 6], [6, 12, 9, 6]];
    private static readonly int[][] Partitions5 = [[8, 8, 5, 0], [15, 12, 9, 0], [6, 18, 9, 0]];

    private static readonly int[] ScfsiStarts = [0, 6, 11, 16, 21];

    private readonly Dictionary<int, Mp3BandLayout> layouts = [];
    private readonly Mp3SideInfo side = new();
    private readonly byte[] main = new byte[ReservoirSize + 2048 + 16];
    private readonly int[][] quantized = [new int[576 + 8], new int[576 + 8]];
    private readonly float[][] spectrum = [new float[576], new float[576]];
    private readonly float[] ordered = new float[576];
    private readonly float[][] overlap = [new float[576], new float[576]];
    private readonly float[] subbands = new float[576];
    private readonly float[] block = new float[36];
    private readonly float[] column = new float[32];
    private readonly Mp3Synthesis[] synthesis = [new(), new()];
    private readonly int[] scratchValues = new int[40];
    private readonly int[] scratchLimits = new int[40];
    private int reservoirLength;

    /// <summary>The decoded samples of the last frame, one array per channel (1152 or 576 samples each).</summary>
    public float[][] Output { get; } = [new float[1152], new float[1152]];

    public void Reset()
    {
        reservoirLength = 0;
        foreach (float[] o in overlap) Array.Clear(o);
        foreach (Mp3Synthesis s in synthesis) s.Reset();
    }

    /// <summary>
    /// Decodes one frame into <see cref="Output"/>. Returns false when the frame could not be decoded in full (a back pointer into data that was not received, damaged
    /// side information); the output then holds what the filterbank still had to play, which is silence in the steady state.
    /// </summary>
    public bool Decode(byte[] frame, int length, in Mp3Header header)
    {
        int channels = header.Channels;
        int samples = header.SamplesPerFrame;
        int sideOffset = 4 + (header.HasCrc ? 2 : 0);
        int payloadStart = sideOffset + header.SideInfoLength;
        int payloadLength = length - payloadStart;
        bool parsed = payloadLength >= 0 && side.Parse(frame, sideOffset, header);
        if (!parsed)
        {
            RenderSilence(channels, samples, header);
            return false;
        }

        Array.Copy(frame, payloadStart, main, reservoirLength, payloadLength);
        int total = reservoirLength + payloadLength;
        Array.Clear(main, total, 8);
        bool complete = side.MainDataBegin <= reservoirLength;
        if (complete)
        {
            DecodeGranules(header, (reservoirLength - side.MainDataBegin) * 8, total * 8, channels);
        }
        else RenderSilence(channels, samples, header);
        int keep = Math.Min(total, ReservoirSize);
        Array.Copy(main, total - keep, main, 0, keep);
        reservoirLength = keep;
        return complete;
    }

    private void RenderSilence(int channels, int samples, in Mp3Header header)
    {
        // Feed zeros through the filterbank so the overlapping tails fade out instead of being cut.
        for (int gr = 0; gr < header.Granules; gr++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                Array.Clear(spectrum[ch]);
                Synthesize(ch, gr, blockType: 0, mixed: false, shortBlocks: false, MixedLines(header), header);
            }
        }
        _ = samples;
    }

    private int MixedLines(in Mp3Header header) => Layout(header).MixedLongLines;

    private Mp3BandLayout Layout(in Mp3Header header)
    {
        int key = header.SampleRate * 2 + (header.IsMpeg1 ? 1 : 0);
        if (!layouts.TryGetValue(key, out Mp3BandLayout? layout))
        {
            layout = new Mp3BandLayout(header.SampleRate, header.IsMpeg1);
            layouts[key] = layout;
        }
        return layout;
    }

    private void DecodeGranules(in Mp3Header header, int mainStartBit, int totalBits, int channels)
    {
        Mp3BandLayout layout = Layout(header);
        Mp3BitReader reader = new(main);
        int position = mainStartBit;
        for (int gr = 0; gr < header.Granules; gr++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                Mp3GranuleChannel g = side.Granules[gr][ch];
                reader.Position = position;
                int end = Math.Min(position + g.Part23Length, totalBits);
                bool valid = ReadScalefactors(ref reader, header, g, side.Granules[0][ch], gr, ch, side.Scfsi[ch]);
                int partTwoEnd = reader.Position;
                if (valid && partTwoEnd <= end) DecodeHuffman(ref reader, g, layout, end, quantized[ch]);
                else Array.Clear(quantized[ch]);
                position += g.Part23Length;
                Requantize(g, layout, quantized[ch], spectrum[ch]);
            }
            if (channels == 2 && header.Mode == 1 && header.ModeExtension != 0) ProcessStereo(header, layout, side.Granules[gr][0], side.Granules[gr][1]);
            for (int ch = 0; ch < channels; ch++)
            {
                Mp3GranuleChannel g = side.Granules[gr][ch];
                Synthesize(ch, gr, g.BlockType, g.Mixed && g.IsShort, g.IsShort, layout.MixedLongLines, header);
            }
        }
    }

    // ---- scalefactors ---------------------------------------------------------------------------------------------------------------------

    private bool ReadScalefactors(ref Mp3BitReader reader, in Mp3Header header, Mp3GranuleChannel g, Mp3GranuleChannel first, int gr, int ch, bool[] scfsi)
    {
        Array.Clear(g.ScalefactorsLong);
        Array.Clear(g.ScalefactorsShort);
        Array.Fill(g.LimitLong, 7);
        Array.Fill(g.LimitShort, 7);
        if (header.IsMpeg1)
        {
            (int slen1, int slen2) = SlenTable[g.ScalefacCompress];
            if (g.IsShort)
            {
                if (g.Mixed)
                {
                    for (int sfb = 0; sfb < 8; sfb++) g.ScalefactorsLong[sfb] = (int)reader.Read(slen1);
                    for (int sfb = 3; sfb < 12; sfb++)
                    {
                        int bits = sfb < 6 ? slen1 : slen2;
                        for (int w = 0; w < 3; w++) g.ScalefactorsShort[sfb * 3 + w] = (int)reader.Read(bits);
                    }
                }
                else
                {
                    for (int sfb = 0; sfb < 12; sfb++)
                    {
                        int bits = sfb < 6 ? slen1 : slen2;
                        for (int w = 0; w < 3; w++) g.ScalefactorsShort[sfb * 3 + w] = (int)reader.Read(bits);
                    }
                }
            }
            else
            {
                int[] starts = ScfsiStarts;
                for (int band = 0; band < 4; band++)
                {
                    int bits = band < 2 ? slen1 : slen2;
                    for (int sfb = starts[band]; sfb < starts[band + 1]; sfb++)
                    {
                        if (gr == 1 && scfsi[band]) g.ScalefactorsLong[sfb] = first.ScalefactorsLong[sfb];
                        else g.ScalefactorsLong[sfb] = (int)reader.Read(bits);
                    }
                }
            }
            return true;
        }
        return ReadScalefactorsLsf(ref reader, header, g, ch);
    }

    private bool ReadScalefactorsLsf(ref Mp3BitReader reader, in Mp3Header header, Mp3GranuleChannel g, int ch)
    {
        int compress = g.ScalefacCompress;
        bool intensityChannel = header.Mode == 1 && (header.ModeExtension & 1) != 0 && ch == 1;
        Span<int> slen = stackalloc int[4];
        Span<int> count = stackalloc int[4];
        // Rows: block kind 0 long, 1 short, 2 mixed.
        int kind = !g.IsShort ? 0 : g.Mixed ? 2 : 1;
        g.Preflag = false;
        int[] rowCounts;
        if (!intensityChannel)
        {
            if (compress < 400)
            {
                slen[0] = (compress >> 4) / 5;
                slen[1] = (compress >> 4) % 5;
                slen[2] = (compress % 16) >> 2;
                slen[3] = compress % 4;
                rowCounts = Partitions0[kind];
            }
            else if (compress < 500)
            {
                int c = compress - 400;
                slen[0] = (c >> 2) / 5;
                slen[1] = (c >> 2) % 5;
                slen[2] = c % 4;
                slen[3] = 0;
                rowCounts = Partitions1[kind];
            }
            else
            {
                int c = compress - 500;
                slen[0] = c / 3;
                slen[1] = c % 3;
                slen[2] = 0;
                slen[3] = 0;
                g.Preflag = true;
                rowCounts = Partitions2[kind];
            }
            g.IntensityScale = 0;
        }
        else
        {
            g.IntensityScale = compress & 1;
            int c = compress >> 1;
            if (c < 180)
            {
                slen[0] = c / 36;
                slen[1] = (c % 36) / 6;
                slen[2] = (c % 36) % 6;
                slen[3] = 0;
                rowCounts = Partitions3[kind];
            }
            else if (c < 244)
            {
                c -= 180;
                slen[0] = (c % 64) >> 4;
                slen[1] = (c % 16) >> 2;
                slen[2] = c % 4;
                slen[3] = 0;
                rowCounts = Partitions4[kind];
            }
            else
            {
                c -= 244;
                slen[0] = c / 3;
                slen[1] = c % 3;
                slen[2] = 0;
                slen[3] = 0;
                rowCounts = Partitions5[kind];
            }
        }
        rowCounts.CopyTo(count);
        int index = 0;
        for (int partition = 0; partition < 4; partition++)
        {
            for (int i = 0; i < count[partition] && index < scratchValues.Length; i++, index++)
            {
                scratchValues[index] = slen[partition] == 0 ? 0 : (int)reader.Read(slen[partition]);
                scratchLimits[index] = slen[partition] == 0 ? int.MaxValue : (1 << slen[partition]) - 1;
            }
        }
        for (int i = 0; i < index; i++)
        {
            if (kind == 0)
            {
                if (i < 22)
                {
                    g.ScalefactorsLong[i] = scratchValues[i];
                    g.LimitLong[i] = scratchLimits[i];
                }
            }
            else if (kind == 1)
            {
                if (i < 36)
                {
                    g.ScalefactorsShort[i] = scratchValues[i];
                    g.LimitShort[i] = scratchLimits[i];
                }
            }
            else if (i < 6)
            {
                g.ScalefactorsLong[i] = scratchValues[i];
                g.LimitLong[i] = scratchLimits[i];
            }
            else if (i - 6 + 9 < g.ScalefactorsShort.Length)
            {
                g.ScalefactorsShort[i - 6 + 9] = scratchValues[i];
                g.LimitShort[i - 6 + 9] = scratchLimits[i];
            }
        }
        g.LimitLong[21] = int.MaxValue;
        for (int i = 36; i < g.LimitShort.Length; i++) g.LimitShort[i] = int.MaxValue;
        return true;
    }

    // ---- Huffman decoding -----------------------------------------------------------------------------------------------------------------

    private static void DecodeHuffman(ref Mp3BitReader reader, Mp3GranuleChannel g, Mp3BandLayout layout, int endBit, int[] values)
    {
        Array.Clear(values);
        Mp3Band[] bands = !g.IsShort ? layout.Long : g.Mixed ? layout.Mixed : layout.Short;
        int bigEnd = Math.Min(g.BigValues * 2, 576);
        int region0 = LinesOfBands(bands, g.Region0Count + 1);
        int region1 = LinesOfBands(bands, g.Region0Count + g.Region1Count + 2);
        int line = 0;
        for (int region = 0; region < 3 && line < bigEnd; region++)
        {
            int regionEnd = Math.Min(region == 0 ? region0 : region == 1 ? region1 : 576, bigEnd);
            Mp3HuffmanTable table = PairTables[g.TableSelect[region]];
            int dim = table.Dimension;
            int linBits = table.LinBits;
            if (table.Dimension == 1)
            {
                line = Math.Max(line, regionEnd);
                continue;
            }
            while (line < regionEnd)
            {
                if (reader.Position >= endBit) return;
                int symbol = table.Decode(ref reader);
                if (symbol < 0) return;
                int x = symbol / dim;
                int y = symbol % dim;
                if (x == 15 && linBits > 0) x += (int)reader.Read(linBits);
                if (x != 0 && reader.Read(1) != 0) x = -x;
                if (y == 15 && linBits > 0) y += (int)reader.Read(linBits);
                if (y != 0 && reader.Read(1) != 0) y = -y;
                values[line++] = x;
                values[line++] = y;
            }
        }
        line = Math.Max(line, bigEnd);
        Mp3HuffmanTable quad = g.Count1TableSelect == 0 ? Count1A : Count1B;
        while (line + 4 <= 576 && reader.Position < endBit)
        {
            int symbol = quad.Decode(ref reader);
            if (symbol < 0) return;
            for (int i = 0; i < 4; i++)
            {
                int bit = (symbol >> (3 - i)) & 1;
                if (bit != 0) values[line + i] = reader.Read(1) != 0 ? -1 : 1;
            }
            if (reader.Position > endBit)
            {
                // The last quadruple ran past the end of the granule's data: it is padding.
                Array.Clear(values, line, 4);
                return;
            }
            line += 4;
        }
    }

    private static int LinesOfBands(Mp3Band[] bands, int count)
    {
        int lines = 0;
        for (int i = 0; i < count && i < bands.Length; i++) lines += bands[i].Width;
        return Math.Min(lines, 576);
    }

    // ---- requantization -------------------------------------------------------------------------------------------------------------------

    private static void Requantize(Mp3GranuleChannel g, Mp3BandLayout layout, int[] values, float[] output)
    {
        Mp3Band[] bands = !g.IsShort ? layout.Long : g.Mixed ? layout.Mixed : layout.Short;
        int scale = g.ScalefacScale ? 2 : 1;
        ReadOnlySpan<byte> pretab = Mp3Tables.Pretab;
        Array.Clear(output);
        foreach (Mp3Band band in bands)
        {
            int exponent;
            int scalefactor;
            if (band.Window < 0)
            {
                scalefactor = g.ScalefactorsLong[band.Sfb] + (g.Preflag ? pretab[band.Sfb] : 0);
                exponent = g.GlobalGain - 210;
            }
            else
            {
                scalefactor = g.ScalefactorsShort[band.Sfb * 3 + band.Window];
                exponent = g.GlobalGain - 210 - 8 * g.SubblockGain[band.Window];
            }
            float factor = GainTable[Math.Clamp(exponent + 260, 0, GainTable.Length - 1)] * ScaleTable[Math.Min(scale * scalefactor, ScaleTable.Length - 1)];
            int end = band.Start + band.Width;
            for (int l = band.Start; l < end; l++)
            {
                int v = values[l];
                if (v == 0) continue;
                output[l] = v < 0 ? -Pow43[-v] * factor : Pow43[v] * factor;
            }
        }
    }

    // ---- stereo ---------------------------------------------------------------------------------------------------------------------------

    private void ProcessStereo(in Mp3Header header, Mp3BandLayout layout, Mp3GranuleChannel left, Mp3GranuleChannel right)
    {
        bool ms = (header.ModeExtension & 2) != 0;
        bool intensity = (header.ModeExtension & 1) != 0;
        float[] l = spectrum[0];
        float[] r = spectrum[1];
        if (left.IsShort != right.IsShort || (left.IsShort && left.Mixed != right.Mixed))
        {
            if (ms) MidSide(l, r, 0, 576);
            return;
        }
        if (!intensity)
        {
            MidSide(l, r, 0, 576);
            return;
        }
        Mp3Band[] bands = !right.IsShort ? layout.Long : right.Mixed ? layout.Mixed : layout.Short;
        // Everything above the last band that has a non-zero value in the right channel (per window for short blocks) is intensity coded.
        Span<int> lastNonZero = stackalloc int[4];
        lastNonZero.Fill(-1);
        int[] q = quantized[1];
        for (int b = 0; b < bands.Length; b++)
        {
            Mp3Band band = bands[b];
            for (int i = band.Start; i < band.Start + band.Width; i++)
            {
                if (q[i] != 0)
                {
                    lastNonZero[band.Window + 1] = b;
                    break;
                }
            }
        }
        for (int b = 0; b < bands.Length; b++)
        {
            Mp3Band band = bands[b];
            int end = band.Start + band.Width;
            bool intensityBand = b > lastNonZero[band.Window + 1];
            // The last band has no scalefactor of its own: it uses the intensity position of the band below.
            int posSfb = band.Window < 0 ? Math.Min(band.Sfb, 20) : Math.Min(band.Sfb, 11);
            int position = band.Window < 0 ? right.ScalefactorsLong[posSfb] : right.ScalefactorsShort[posSfb * 3 + band.Window];
            int limit = band.Window < 0 ? right.LimitLong[posSfb] : right.LimitShort[posSfb * 3 + band.Window];
            bool legal = header.IsMpeg1 ? position < 7 : position < limit;
            if (intensityBand && legal)
            {
                float kl, kr;
                if (header.IsMpeg1)
                {
                    float ratio = (float)Math.Tan(position * Math.PI / 12);
                    kl = ratio / (1 + ratio);
                    kr = 1 / (1 + ratio);
                }
                else
                {
                    int x = right.IntensityScale == 1 ? 1 : 2;
                    if (position == 0)
                    {
                        kl = 1;
                        kr = 1;
                    }
                    else if (position % 2 == 1)
                    {
                        kl = (float)Math.Pow(2, -((position + 1) / (4.0 * x)));
                        kr = 1;
                    }
                    else
                    {
                        kl = 1;
                        kr = (float)Math.Pow(2, -(position / (4.0 * x)));
                    }
                }
                for (int i = band.Start; i < end; i++)
                {
                    float value = l[i];
                    l[i] = value * kl;
                    r[i] = value * kr;
                }
            }
            else if (ms) MidSide(l, r, band.Start, end);
        }
    }

    private static void MidSide(float[] l, float[] r, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            float m = l[i], s = r[i];
            l[i] = (m + s) * SqrtHalf;
            r[i] = (m - s) * SqrtHalf;
        }
    }

    // ---- hybrid synthesis -----------------------------------------------------------------------------------------------------------------

    private void Synthesize(int ch, int gr, int blockType, bool mixed, bool shortBlocks, int mixedLines, in Mp3Header header)
    {
        float[] xr = spectrum[ch];
        float[] ov = overlap[ch];
        Mp3BandLayout layout = Layout(header);
        int longSubbands = shortBlocks ? (mixed ? mixedLines / 18 : 0) : 32;
        if (shortBlocks)
        {
            // Reorder the short part into polyphase subband order (window by window within a subband).
            Mp3Band[] bands = mixed ? layout.Mixed : layout.Short;
            Array.Clear(ordered);
            if (mixed) Array.Copy(xr, ordered, mixedLines);
            foreach (Mp3Band band in bands)
            {
                if (band.Window < 0) continue;
                int f0 = layout.ShortEdges[band.Sfb];
                for (int k = 0; k < band.Width; k++)
                {
                    int f = f0 + k;
                    ordered[(f / 6) * 18 + band.Window * 6 + f % 6] = xr[band.Start + k];
                }
            }
            Array.Copy(ordered, xr, 576);
        }
        // Alias reduction between the long-block subbands.
        for (int sb = 1; sb < longSubbands; sb++)
        {
            for (int i = 0; i < 8; i++)
            {
                int up = 18 * sb - 1 - i, down = 18 * sb + i;
                float a = xr[up], b = xr[down];
                xr[up] = a * AliasCs[i] - b * AliasCa[i];
                xr[down] = b * AliasCs[i] + a * AliasCa[i];
            }
        }
        Span<float> y = block;
        for (int sb = 0; sb < 32; sb++)
        {
            ReadOnlySpan<float> input = xr.AsSpan(sb * 18, 18);
            Span<float> tail = ov.AsSpan(sb * 18, 18);
            bool silent = true;
            foreach (float v in input)
            {
                if (v != 0)
                {
                    silent = false;
                    break;
                }
            }
            Span<float> sub = subbands.AsSpan(sb * 18, 18);
            if (silent)
            {
                for (int i = 0; i < 18; i++)
                {
                    sub[i] = tail[i];
                    tail[i] = 0;
                }
            }
            else
            {
                if (sb < longSubbands) Mp3Transforms.ImdctLong(input, shortBlocks ? 0 : blockType, y);
                else Mp3Transforms.ImdctShort(input, y);
                for (int i = 0; i < 18; i++)
                {
                    sub[i] = y[i] + tail[i];
                    tail[i] = y[i + 18];
                }
            }
            if ((sb & 1) != 0)
            {
                for (int i = 1; i < 18; i += 2) sub[i] = -sub[i];
            }
        }
        float[] pcm = Output[ch];
        int baseIndex = gr * 576;
        for (int t = 0; t < 18; t++)
        {
            for (int sb = 0; sb < 32; sb++) column[sb] = subbands[sb * 18 + t];
            synthesis[ch].Process(column, pcm.AsSpan(baseIndex + t * 32, 32));
        }
    }

    // ---- tables ---------------------------------------------------------------------------------------------------------------------------

    private static float[] BuildPow43()
    {
        float[] table = new float[8207 + 16];
        for (int i = 0; i < table.Length; i++) table[i] = (float)Math.Pow(i, 4.0 / 3.0);
        return table;
    }

    private static float[] BuildPowers(double step, int count, int zeroOffset)
    {
        // Entry i holds 2^(step * (i - zeroOffset)).
        float[] table = new float[count];
        for (int i = 0; i < count; i++) table[i] = (float)Math.Pow(2, step * (i - zeroOffset));
        return table;
    }

    private static Mp3HuffmanTable[] BuildPairTables()
    {
        Mp3HuffmanTable[] tables = new Mp3HuffmanTable[32];
        for (int n = 0; n < 32; n++)
        {
            (int dimension, int linBits, uint[] codes, byte[] lengths) = Mp3Tables.Huffman[n];
            tables[n] = dimension == 0 ? new Mp3HuffmanTable(1, 0, [0], [0]) : new Mp3HuffmanTable(dimension, linBits, codes, lengths);
        }
        return tables;
    }

    private static uint[] Codes((byte Code, byte Length)[] source) => [.. source.Select(entry => (uint)entry.Code)];

    private static byte[] Lengths((byte Code, byte Length)[] source) => [.. source.Select(entry => entry.Length)];

    private static float[] BuildAlias(bool cosine)
    {
        double[] c = [-0.6, -0.535, -0.33, -0.185, -0.095, -0.041, -0.0142, -0.0037];
        float[] table = new float[8];
        for (int i = 0; i < 8; i++)
        {
            double norm = Math.Sqrt(1 + c[i] * c[i]);
            table[i] = (float)(cosine ? 1 / norm : c[i] / norm);
        }
        return table;
    }
}
