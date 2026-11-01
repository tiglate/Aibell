namespace Aibell.Audio.Mp3;

/// <summary>
/// Layer I decoder (ISO/IEC 11172-3, 2.4.1.5 and 2.4.3.2): 4-bit allocations, 6-bit scalefactors and 12 samples of 2 to 15 bits per subband and frame, joint stereo
/// as intensity stereo above the bound, then the polyphase synthesis shared with Layer III.
/// </summary>
internal sealed class Mp3Layer1
{
    private const int FrameSamples = 384;

    /// <summary>factor = 2^(1 - i/3) for the 6-bit scalefactor index i; index 63 is not allowed and gives 0.</summary>
    private static readonly float[] ScaleFactors = BuildScaleFactors();

    private readonly byte[] data = new byte[4096 + 8];
    private readonly int[][] bits = [new int[32], new int[32]];
    private readonly float[][] factors = [new float[32], new float[32]];
    private readonly float[][] subbands = [new float[FrameSamples], new float[FrameSamples]];
    private readonly Mp3Synthesis[] synthesis = [new(), new()];

    /// <summary>The decoded samples of the last frame, one array per channel (384 samples each).</summary>
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
            for (int s = 0; s < 12; s++) synthesis[ch].Process(subbands[ch].AsSpan(s * 32, 32), Output[ch].AsSpan(s * 32, 32));
        }
        return ok;
    }

    private bool Parse(byte[] frame, int length, in Mp3Header header, int channels)
    {
        Array.Copy(frame, data, length);
        Array.Clear(data, length, 8);
        foreach (float[] s in subbands) Array.Clear(s);
        int bound = header.Mode == 1 ? 4 + 4 * header.ModeExtension : 32;
        int limit = length * 8;
        Mp3BitReader reader = new(data) { Position = (4 + (header.HasCrc ? 2 : 0)) * 8 };

        for (int sb = 0; sb < 32; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (sb >= bound && ch == 1)
                {
                    bits[1][sb] = bits[0][sb];
                    continue;
                }
                int code = (int)reader.Read(4);
                if (code == 15) return false;
                bits[ch][sb] = code == 0 ? 0 : code + 1;
            }
        }
        if (reader.Position > limit) return false;
        for (int sb = 0; sb < 32; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (bits[ch][sb] != 0) factors[ch][sb] = ScaleFactors[reader.Read(6)];
            }
        }
        if (reader.Position > limit) return false;

        for (int s = 0; s < 12; s++)
        {
            for (int sb = 0; sb < 32; sb++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    int nb = bits[ch][sb];
                    if (nb == 0) continue;
                    float value = Requantize(reader.Read(nb), nb);
                    subbands[ch][s * 32 + sb] = value * factors[ch][sb];
                    if (sb >= bound && channels == 2)
                    {
                        // Intensity stereo: one sample for both channels, each with its own scalefactor.
                        subbands[1][s * 32 + sb] = value * factors[1][sb];
                        break;
                    }
                }
            }
        }
        return reader.Position <= limit;
    }

    // Inverting the first bit of the sample and reading it as a two's complement fraction is the offset-binary value raw - 2^(nb-1) over 2^(nb-1); then
    // s" = 2^nb / (2^nb - 1) * (s"' + 2^(1-nb)).
    private static float Requantize(uint raw, int nb) => 2f * ((int)raw - (1 << (nb - 1)) + 1) / ((1 << nb) - 1);
}
