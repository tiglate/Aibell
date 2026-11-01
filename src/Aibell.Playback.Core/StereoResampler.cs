namespace Aibell.Playback.Core;

/// <summary>
/// Streaming sample-rate converter for interleaved stereo float: a windowed-sinc kernel (Blackman window) tabulated in 256 phases with
/// linear interpolation between phases. When the rates are equal it is a plain copy. Used for sources, such as the AAC decoder, that
/// produce audio at the stream's own rate while the device runs at another.
/// </summary>
internal sealed class StereoResampler
{
    private const int Phases = 256;
    private const int BaseHalfWidth = 12;
    private const int Channels = 2;

    private readonly int inputRate;
    private readonly double step;
    private readonly int halfWidth;
    private readonly float[] kernel = [];
    private float[] buffer;
    private int count;
    private double position;

    public StereoResampler(int inputRate, int outputRate, int maximumPush)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        this.inputRate = inputRate;
        Passthrough = inputRate == outputRate;
        step = (double)inputRate / outputRate;
        double scale = Math.Max(1.0, step);
        halfWidth = (int)Math.Ceiling(BaseHalfWidth * scale);
        buffer = new float[(maximumPush + 4 * halfWidth + 16) * Channels];
        if (Passthrough) return;
        double cutoff = Math.Min(1.0, 1.0 / step) * 0.96;
        int taps = 2 * halfWidth;
        kernel = new float[(Phases + 1) * taps];
        for (int phase = 0; phase <= Phases; phase++)
        {
            double fraction = (double)phase / Phases;
            double sum = 0;
            double[] row = new double[taps];
            for (int t = 0; t < taps; t++)
            {
                double x = t - halfWidth + 1 - fraction;
                double window = 0.42 + 0.5 * Math.Cos(Math.PI * x / halfWidth) + 0.08 * Math.Cos(2 * Math.PI * x / halfWidth);
                double arg = Math.PI * cutoff * x;
                double sinc = Math.Abs(arg) < 1e-12 ? 1.0 : Math.Sin(arg) / arg;
                row[t] = Math.Abs(x) >= halfWidth ? 0 : cutoff * sinc * window;
                sum += row[t];
            }
            for (int t = 0; t < taps; t++) kernel[phase * taps + t] = (float)(row[t] / sum);
        }
        Reset();
    }

    public bool Passthrough { get; }

    public int InputRate => inputRate;

    /// <summary>Input frames that can still be pushed before the buffer is full.</summary>
    public int Room => buffer.Length / Channels - count;

    /// <summary>Forgets buffered audio; the next input is treated as the start of a new stream.</summary>
    public void Reset()
    {
        // Zeros stand in for the audio before the start so the first samples have a full kernel.
        Array.Clear(buffer);
        count = Passthrough ? 0 : halfWidth;
        position = count;
    }

    public void Push(ReadOnlySpan<float> stereo, int frames)
    {
        if (frames > Room) throw new InvalidOperationException("The resampler input is full.");
        stereo[..(frames * Channels)].CopyTo(buffer.AsSpan(count * Channels));
        count += frames;
    }

    /// <summary>Produces up to <paramref name="maxFrames"/> frames from what has been pushed; 0 means it needs more input.</summary>
    public int Pull(Span<float> output, int maxFrames)
    {
        int produced = 0;
        if (Passthrough)
        {
            produced = Math.Min(maxFrames, count);
            buffer.AsSpan(0, produced * Channels).CopyTo(output);
            Buffer.BlockCopy(buffer, produced * Channels * sizeof(float), buffer, 0, (count - produced) * Channels * sizeof(float));
            count -= produced;
            return produced;
        }
        int taps = 2 * halfWidth;
        while (produced < maxFrames && (int)position + halfWidth < count)
        {
            int first = (int)position - halfWidth + 1;
            double fraction = position - (int)position;
            double scaled = fraction * Phases;
            int phase = (int)scaled;
            float blend = (float)(scaled - phase);
            ReadOnlySpan<float> low = kernel.AsSpan(phase * taps, taps);
            ReadOnlySpan<float> high = kernel.AsSpan((phase + 1) * taps, taps);
            float left = 0, right = 0;
            for (int t = 0; t < taps; t++)
            {
                float weight = low[t] + (high[t] - low[t]) * blend;
                int at = (first + t) * Channels;
                left += buffer[at] * weight;
                right += buffer[at + 1] * weight;
            }
            output[produced * Channels] = left;
            output[produced * Channels + 1] = right;
            produced++;
            position += step;
        }
        int drop = (int)position - halfWidth;
        if (drop > 0)
        {
            Buffer.BlockCopy(buffer, drop * Channels * sizeof(float), buffer, 0, (count - drop) * Channels * sizeof(float));
            count -= drop;
            position -= drop;
        }
        return produced;
    }
}
