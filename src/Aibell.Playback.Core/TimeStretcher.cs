namespace Aibell.Playback.Core;

/// <summary>
/// Changes playback speed without changing pitch (WSOLA: overlap-add of Hann-windowed segments whose start is nudged to the
/// best match with the natural continuation of the previous segment). Works on interleaved stereo float frames: push decoded
/// input, then pull stretched output. At <c>Rate</c> 2 the output is half as long.
/// </summary>
internal sealed class TimeStretcher
{
    private const int Hop = 1024;
    private const int Window = Hop * 2;
    private const int Tolerance = 256;
    private const int Channels = AudioChannels.Channels;
    private static readonly float[] Hann = BuildWindow();

    private readonly float[] overlap = new float[Window * Channels];
    private readonly float[] target = new float[Hop / 2];
    private float[] input = new float[(Window + Tolerance * 2 + 4096) * Channels];
    private int inputFrames;
    private long inputStart;
    private long pushed;
    private long emitted;
    private double analysis;
    private long previous = -1;
    private bool first = true;
    private bool flushed;

    public double Rate { get; set; } = 1.0;

    public void Reset()
    {
        inputFrames = 0;
        inputStart = 0;
        pushed = 0;
        emitted = 0;
        analysis = 0;
        previous = -1;
        first = true;
        flushed = false;
        Array.Clear(overlap);
    }

    public void Push(ReadOnlySpan<float> frames, int count)
    {
        pushed += count;
        Append(frames[..(count * Channels)]);
    }

    /// <summary>Writes up to <c>output.Length / 2</c> stretched frames; returns how many. After <see cref="Flush"/> it ends at the true length.</summary>
    public int Pull(Span<float> output)
    {
        int room = output.Length / Channels;
        int written = 0;
        while (room - written >= Hop)
        {
            long center = (long)Math.Round(analysis);
            if (center + Tolerance + Window > inputStart + inputFrames) break;
            long start = center;
            if (!first)
            {
                long low = Math.Max(center - Tolerance, inputStart);
                long high = Math.Max(center + Tolerance, low);
                start = BestMatch(low, high, previous + Hop);
            }
            AddSegment(start);
            overlap.AsSpan(0, Hop * Channels).CopyTo(output[(written * Channels)..]);
            written += Hop;
            overlap.AsSpan(Hop * Channels, Hop * Channels).CopyTo(overlap);
            overlap.AsSpan(Hop * Channels, Hop * Channels).Clear();
            previous = start;
            first = false;
            analysis += Hop * Rate;
            Trim();
        }
        if (flushed)
        {
            long limit = (long)Math.Ceiling(pushed / Rate);
            int allowed = (int)Math.Clamp(limit - emitted, 0, written);
            written = allowed;
        }
        emitted += written;
        return written;
    }

    /// <summary>Marks the end of the input: pads it with silence so the last real frames are emitted too.</summary>
    public void Flush()
    {
        if (flushed) return;
        flushed = true;
        float[] silence = new float[(Window + Tolerance) * Channels];
        Append(silence);
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        int needed = (inputFrames * Channels) + samples.Length;
        if (needed > input.Length) Array.Resize(ref input, Math.Max(needed, input.Length * 2));
        samples.CopyTo(input.AsSpan(inputFrames * Channels));
        inputFrames += samples.Length / Channels;
    }

    private void AddSegment(long start)
    {
        int offset = (int)(start - inputStart);
        for (int i = 0; i < Window; i++)
        {
            float weight = first && i < Hop ? 1f : Hann[i];
            int from = (offset + i) * Channels;
            overlap[i * Channels] += input[from] * weight;
            overlap[(i * Channels) + 1] += input[from + 1] * weight;
        }
    }

    // The candidate whose first half best matches what naturally followed the previous segment, so the seam is inaudible.
    private long BestMatch(long low, long high, long continuation)
    {
        int t = (int)(continuation - inputStart);
        for (int j = 0; j < target.Length; j++) target[j] = Mono(t + (j * 2));
        long best = low;
        double bestScore = double.NegativeInfinity;
        for (long candidate = low; candidate <= high; candidate++)
        {
            int c = (int)(candidate - inputStart);
            double dot = 0, energy = 1e-9;
            for (int j = 0; j < target.Length; j++)
            {
                float sample = Mono(c + (j * 2));
                dot += sample * target[j];
                energy += sample * sample;
            }
            double score = dot / Math.Sqrt(energy);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    private float Mono(int frame) => (input[frame * Channels] + input[(frame * Channels) + 1]) * 0.5f;

    private void Trim()
    {
        long keepFrom = Math.Min((long)Math.Round(analysis) - Tolerance, previous + Hop);
        int drop = (int)Math.Min(keepFrom - inputStart, inputFrames);
        if (drop < 4096) return;
        Array.Copy(input, drop * Channels, input, 0, (inputFrames - drop) * Channels);
        inputFrames -= drop;
        inputStart += drop;
    }

    private static float[] BuildWindow()
    {
        float[] window = new float[Window];
        for (int i = 0; i < Window; i++) window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / Window)));
        return window;
    }
}
