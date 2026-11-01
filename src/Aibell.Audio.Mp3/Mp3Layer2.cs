namespace Aibell.Audio.Mp3;

/// <summary>
/// Layer II decoder (ISO/IEC 11172-3, 2.4.1.6 and 2.4.3.3): bit allocation by subband through one of the five allocation tables, scalefactor selection, three
/// scalefactors per subband, grouped or single sample codes, intensity stereo above the bound, then the polyphase synthesis shared with Layers I and III.
/// </summary>
internal sealed class Mp3Layer2
{
    private const int FrameSamples = 1152;

    private static readonly float[] ScaleFactors = BuildScaleFactors();

    /// <summary>Per quantization class (1..17): the bits of one sample code, and the bits of one codeword (three samples when grouped).</summary>
    private static readonly int[] ClassSampleBits = BuildClassBits(grouped: false);

    private static readonly int[] ClassCodewordBits = BuildClassBits(grouped: true);

    private readonly byte[] data = new byte[4096 + 8];
    private readonly byte[][] quantizer = [new byte[32], new byte[32]];
    private readonly byte[][] selection = [new byte[32], new byte[32]];
    private readonly float[][] factors = [new float[96], new float[96]];
    private readonly float[][] subbands = [new float[FrameSamples], new float[FrameSamples]];
    private readonly Mp3Synthesis[] synthesis = [new(), new()];

    /// <summary>The decoded samples of the last frame, one array per channel (1152 samples each).</summary>
    public float[][] Output { get; } = [new float[FrameSamples], new float[FrameSamples]];

    public void Reset()
    {
        foreach (Mp3Synthesis s in synthesis) s.Reset();
    }

    private static float[] BuildScaleFactors()
    {
        float[] table = new float[64];
        for (int i = 0; i < 63; i++) table[i] = (float)Math.Pow(2, 1 - i / 3.0);
        return table;
    }

    private static int[] BuildClassBits(bool grouped)
    {
        int[] bits = new int[Mp3Layer2Tables.ClassLevels.Length];
        for (int c = 1; c < bits.Length; c++)
        {
            int levels = Mp3Layer2Tables.ClassLevels[c];
            int sample = levels switch { 3 => 2, 5 => 3, 9 => 4, _ => System.Numerics.BitOperations.Log2((uint)levels + 1) };
            bits[c] = grouped ? levels switch { 3 => 5, 5 => 7, 9 => 10, _ => sample } : sample;
        }
        return bits;
    }

    /// <summary>The table of Table 3-B.2 for a frame: 0 to 3 for a, b, c, d (MPEG-1), 4 for the low sampling frequencies of MPEG-2.</summary>
    private static int SelectTable(in Mp3Header header, int channels)
    {
        if (!header.IsMpeg1) return 4;
        if (header.IsFreeFormat) return header.SampleRate == 48000 ? 0 : 1;
        int perChannel = header.BitRate / 1000 / channels;
        if (perChannel <= 48) return header.SampleRate == 32000 ? 3 : 2;
        if (perChannel <= 80) return 0;
        return header.SampleRate == 48000 ? 0 : 1;
    }

    /// <summary>Decodes one frame into <see cref="Output"/>. Returns false when the frame is damaged (it then plays as silence).</summary>
    public bool Decode(byte[] frame, int length, in Mp3Header header)
    {
        int channels = header.Channels;
        bool ok = length <= 4096 && Parse(frame, length, header, channels);
        if (!ok)
        {
            foreach (float[] s in subbands) Array.Clear(s);
        }
        for (int ch = 0; ch < channels; ch++)
        {
            for (int s = 0; s < 36; s++) synthesis[ch].Process(subbands[ch].AsSpan(s * 32, 32), Output[ch].AsSpan(s * 32, 32));
        }
        return ok;
    }

    private bool Parse(byte[] frame, int length, in Mp3Header header, int channels)
    {
        Array.Copy(frame, data, length);
        Array.Clear(data, length, 8);
        foreach (float[] s in subbands) Array.Clear(s);
        int table = SelectTable(header, channels);
        int sblimit = Mp3Layer2Tables.SubbandLimit[table];
        byte[] allocationBits = Mp3Layer2Tables.AllocationBits[table];
        byte[] classes = Mp3Layer2Tables.Quantizer[table];
        int bound = header.Mode == 1 ? Math.Min(4 + 4 * header.ModeExtension, sblimit) : sblimit;
        int limit = length * 8;
        Mp3BitReader reader = new(data) { Position = (4 + (header.HasCrc ? 2 : 0)) * 8 };

        for (int ch = 0; ch < 2; ch++) Array.Clear(quantizer[ch]);
        for (int sb = 0; sb < sblimit; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (sb >= bound && ch == 1)
                {
                    quantizer[1][sb] = quantizer[0][sb];
                    continue;
                }
                int code = (int)reader.Read(allocationBits[sb]);
                quantizer[ch][sb] = classes[sb * 16 + code];
            }
        }
        for (int sb = 0; sb < sblimit; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (quantizer[ch][sb] != 0) selection[ch][sb] = (byte)reader.Read(2);
            }
        }
        if (reader.Position > limit) return false;
        for (int sb = 0; sb < sblimit; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (quantizer[ch][sb] == 0) continue;
                if (!ReadScalefactors(ref reader, factors[ch].AsSpan(sb * 3, 3), selection[ch][sb])) return false;
            }
        }
        if (reader.Position > limit) return false;

        for (int gr = 0; gr < 12; gr++)
        {
            int part = gr / 4;
            for (int sb = 0; sb < sblimit; sb++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    int cls = quantizer[ch][sb];
                    if (cls == 0) continue;
                    bool shared = sb >= bound && channels == 2;
                    if (!ReadSamples(ref reader, cls, ch, sb, gr, factors[ch][sb * 3 + part], shared ? factors[1][sb * 3 + part] : 0f, shared)) return false;
                    if (shared) break;
                }
            }
        }
        return reader.Position <= limit;
    }

    // One, two or three coded scalefactors, depending on the selection information, spread over the three parts of the frame.
    private static bool ReadScalefactors(ref Mp3BitReader reader, Span<float> target, int selection)
    {
        int a = (int)reader.Read(6);
        int b = a;
        int c = a;
        if (selection == 0)
        {
            b = (int)reader.Read(6);
            c = (int)reader.Read(6);
        }
        else if (selection == 1)
        {
            c = (int)reader.Read(6);
        }
        else if (selection == 3)
        {
            b = c = (int)reader.Read(6);
        }
        if (a == 63 || b == 63 || c == 63) return false;
        target[0] = ScaleFactors[a];
        target[1] = ScaleFactors[b];
        target[2] = ScaleFactors[c];
        return true;
    }

    // Reads the three samples of one granule: one codeword of three degrouped samples, or three single codes. s" = C (s"' + D) comes to (2q - L + 1) / L for the
    // code q of a class with L levels.
    private bool ReadSamples(ref Mp3BitReader reader, int cls, int ch, int sb, int gr, float factor, float secondFactor, bool shared)
    {
        int levels = Mp3Layer2Tables.ClassLevels[cls];
        float inverse = 1f / levels;
        int first = 3 * gr;
        if (ClassCodewordBits[cls] != ClassSampleBits[cls])
        {
            int word = (int)reader.Read(ClassCodewordBits[cls]);
            if (word >= levels * levels * levels) return false;
            for (int i = 0; i < 3; i++)
            {
                int q = word % levels;
                word /= levels;
                Store(ch, sb, first + i, (2 * q - levels + 1) * inverse, factor, secondFactor, shared);
            }
            return true;
        }
        int bits = ClassSampleBits[cls];
        for (int i = 0; i < 3; i++)
        {
            int q = (int)reader.Read(bits);
            if (q >= levels) return false;
            Store(ch, sb, first + i, (2 * q - levels + 1) * inverse, factor, secondFactor, shared);
        }
        return true;
    }

    private void Store(int ch, int sb, int slot, float value, float factor, float secondFactor, bool shared)
    {
        subbands[ch][slot * 32 + sb] = value * factor;
        if (shared) subbands[1][slot * 32 + sb] = value * secondFactor;
    }
}
