namespace Aibell.Audio.Mp3;

/// <summary>The inverse MDCT (with its windows) and the polyphase synthesis filterbank of Layer III, with the cosine and window tables they need.</summary>
internal static class Mp3Transforms
{
    /// <summary>cos(pi / 72 * (2i + 19) * (2k + 1)) for i in 0..35, k in 0..17.</summary>
    public static readonly float[] CosLong = BuildCosines(36, 18);

    /// <summary>cos(pi / 24 * (2i + 7) * (2k + 1)) for i in 0..11, k in 0..5.</summary>
    public static readonly float[] CosShort = BuildCosines(12, 6);

    /// <summary>The long windows by block type (0 normal, 1 start, 2 unused, 3 stop), 36 values each.</summary>
    public static readonly float[][] LongWindows = BuildLongWindows();

    /// <summary>The window of the three short blocks.</summary>
    public static readonly float[] ShortWindow = BuildShortWindow();

    /// <summary>N[i][k] = cos((16 + i)(2k + 1) pi / 64) of the polyphase matrixing, i in 0..63, k in 0..31.</summary>
    public static readonly float[] Matrix = BuildMatrix();

    private static float[] BuildCosines(int n, int half)
    {
        float[] table = new float[n * half];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < half; k++) table[i * half + k] = (float)Math.Cos(Math.PI / (2 * n) * (2 * i + 1 + n / 2) * (2 * k + 1));
        }
        return table;
    }

    private static float[][] BuildLongWindows()
    {
        float[][] windows = [new float[36], new float[36], new float[36], new float[36]];
        for (int i = 0; i < 36; i++) windows[0][i] = (float)Math.Sin(Math.PI / 36 * (i + .5));
        for (int i = 0; i < 18; i++) windows[1][i] = windows[0][i];
        for (int i = 18; i < 24; i++) windows[1][i] = 1f;
        for (int i = 24; i < 30; i++) windows[1][i] = (float)Math.Sin(Math.PI / 12 * (i - 18 + .5));
        for (int i = 6; i < 12; i++) windows[3][i] = (float)Math.Sin(Math.PI / 12 * (i - 6 + .5));
        for (int i = 12; i < 18; i++) windows[3][i] = 1f;
        for (int i = 18; i < 36; i++) windows[3][i] = windows[0][i];
        return windows;
    }

    private static float[] BuildShortWindow()
    {
        float[] window = new float[12];
        for (int i = 0; i < 12; i++) window[i] = (float)Math.Sin(Math.PI / 12 * (i + .5));
        return window;
    }

    private static float[] BuildMatrix()
    {
        float[] matrix = new float[64 * 32];
        for (int i = 0; i < 64; i++)
        {
            for (int k = 0; k < 32; k++) matrix[i * 32 + k] = (float)Math.Cos((16 + i) * (2 * k + 1) * Math.PI / 64);
        }
        return matrix;
    }

    /// <summary>The 36 windowed outputs of a long block for 18 spectral values.</summary>
    public static void ImdctLong(ReadOnlySpan<float> input, int blockType, Span<float> output)
    {
        float[] window = LongWindows[blockType];
        for (int i = 0; i < 36; i++)
        {
            float sum = 0;
            ReadOnlySpan<float> row = CosLong.AsSpan(i * 18, 18);
            for (int k = 0; k < 18; k++) sum += input[k] * row[k];
            output[i] = sum * window[i];
        }
    }

    /// <summary>The 36 outputs of three short blocks (6 values each, <paramref name="input"/> holds window 0, 1, 2 one after the other), windowed and overlapped.</summary>
    public static void ImdctShort(ReadOnlySpan<float> input, Span<float> output)
    {
        output[..36].Clear();
        for (int w = 0; w < 3; w++)
        {
            ReadOnlySpan<float> x = input.Slice(w * 6, 6);
            for (int i = 0; i < 12; i++)
            {
                float sum = 0;
                ReadOnlySpan<float> row = CosShort.AsSpan(i * 6, 6);
                for (int k = 0; k < 6; k++) sum += x[k] * row[k];
                output[6 + 6 * w + i] += sum * ShortWindow[i];
            }
        }
    }
}

/// <summary>The synthesis polyphase filterbank state of one channel: 32 subband samples in, 32 PCM samples out.</summary>
internal sealed class Mp3Synthesis
{
    private readonly float[] v = new float[1024];
    private readonly float[] vector = new float[64];
    private int offset;

    public void Reset()
    {
        Array.Clear(v);
        offset = 0;
    }

    public void Process(ReadOnlySpan<float> subbands, Span<float> output)
    {
        float[] matrix = Mp3Transforms.Matrix;
        for (int i = 0; i < 64; i++)
        {
            ReadOnlySpan<float> row = matrix.AsSpan(i * 32, 32);
            float sum = 0;
            for (int k = 0; k < 32; k++) sum += row[k] * subbands[k];
            vector[i] = sum;
        }
        offset = (offset - 64) & 1023;
        vector.CopyTo(v, offset);
        float[] window = Mp3Tables.SynthesisWindow;
        for (int j = 0; j < 32; j++)
        {
            float sum = 0;
            for (int q = 0; q < 16; q++)
            {
                // U[32q + j] comes from V[64q + j] for even q and from V[64q + 32 + j] for odd q.
                int index = 64 * q + ((q & 1) << 5) + j;
                sum += v[(offset + index) & 1023] * window[32 * q + j];
            }
            output[j] = sum;
        }
    }
}
