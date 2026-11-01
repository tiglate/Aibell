namespace Aibell.Audio.Midi;

/// <summary>
/// Reverb after Schroeder and Moorer: four parallel feedback comb filters with a low-pass in the loop (the high frequencies die first, as in a room),
/// then two all-pass filters in series, separately for each ear with different delay lengths. The mono send goes in, stereo wet sound is added to the output.
/// </summary>
internal sealed class Reverb
{
    private const float InputGain = .035f;
    private const float Feedback = .84f;
    private const float Damping = .35f;
    private const float AllpassGain = .5f;

    // Delay lengths in samples at 44.1 kHz (mutually prime), scaled to the actual rate.
    private static readonly int[] CombLeft = [1117, 1327, 1559, 1753];
    private static readonly int[] CombRight = [1151, 1367, 1601, 1789];
    private static readonly int[] AllpassLeft = [556, 441];
    private static readonly int[] AllpassRight = [579, 464];

    private readonly Channel left, right;

    public Reverb(int sampleRate)
    {
        left = new Channel(CombLeft, AllpassLeft, sampleRate);
        right = new Channel(CombRight, AllpassRight, sampleRate);
    }

    public void Clear()
    {
        left.Clear();
        right.Clear();
    }

    public void Process(ReadOnlySpan<float> send, Span<float> output, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            float x = send[i] * InputGain;
            output[i * 2] += left.Process(x);
            output[i * 2 + 1] += right.Process(x);
        }
    }

    private sealed class Channel(int[] combs, int[] allpasses, int sampleRate)
    {
        private readonly float[][] combBuffers = [.. combs.Select(n => new float[Scale(n, sampleRate)])];
        private readonly int[] combPosition = new int[combs.Length];
        private readonly float[] combDamp = new float[combs.Length];
        private readonly float[][] allpassBuffers = [.. allpasses.Select(n => new float[Scale(n, sampleRate)])];
        private readonly int[] allpassPosition = new int[allpasses.Length];

        public void Clear()
        {
            foreach (float[] b in combBuffers) Array.Clear(b);
            foreach (float[] b in allpassBuffers) Array.Clear(b);
            Array.Clear(combPosition);
            Array.Clear(combDamp);
            Array.Clear(allpassPosition);
        }

        public float Process(float input)
        {
            float sum = 0;
            for (int c = 0; c < combBuffers.Length; c++)
            {
                float[] buffer = combBuffers[c];
                int p = combPosition[c];
                float echo = buffer[p];
                combDamp[c] = echo * (1 - Damping) + combDamp[c] * Damping;
                buffer[p] = input + combDamp[c] * Feedback;
                combPosition[c] = ++p == buffer.Length ? 0 : p;
                sum += echo;
            }
            for (int a = 0; a < allpassBuffers.Length; a++)
            {
                float[] buffer = allpassBuffers[a];
                int p = allpassPosition[a];
                float delayed = buffer[p];
                buffer[p] = sum + delayed * AllpassGain;
                sum = delayed - sum;
                allpassPosition[a] = ++p == buffer.Length ? 0 : p;
            }
            return sum;
        }

        private static int Scale(int samples, int rate) => Math.Max(8, (int)Math.Round(samples * rate / 44100.0));
    }
}

/// <summary>A stereo chorus: two delay lines read at a slowly swept position, a quarter turn apart, so the copy drifts in pitch and widens the sound.</summary>
internal sealed class Chorus
{
    private const double BaseDelay = .012;
    private const double Depth = .003;
    private const double Rate = .55;

    private readonly float[] buffer;
    private readonly int sampleRate;
    private int position;
    private double phase;

    public Chorus(int sampleRate)
    {
        this.sampleRate = sampleRate;
        buffer = new float[(int)(sampleRate * .05) + 4];
    }

    public void Clear()
    {
        Array.Clear(buffer);
        position = 0;
        phase = 0;
    }

    public void Process(ReadOnlySpan<float> send, Span<float> output, int frames)
    {
        double step = 2 * Math.PI * Rate / sampleRate;
        for (int i = 0; i < frames; i++)
        {
            buffer[position] = send[i];
            output[i * 2] += Tap(BaseDelay + Depth * Math.Sin(phase));
            output[i * 2 + 1] += Tap(BaseDelay + Depth * Math.Cos(phase));
            phase += step;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
            if (++position == buffer.Length) position = 0;
        }
    }

    private float Tap(double delaySeconds)
    {
        double delay = delaySeconds * sampleRate;
        int whole = (int)delay;
        float fraction = (float)(delay - whole);
        int a = position - whole;
        if (a < 0) a += buffer.Length;
        int b = a - 1;
        if (b < 0) b += buffer.Length;
        return buffer[a] + (buffer[b] - buffer[a]) * fraction;
    }
}
