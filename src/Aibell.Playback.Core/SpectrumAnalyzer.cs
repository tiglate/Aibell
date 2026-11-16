using Aibell.Audio;

namespace Aibell.Playback.Core;

/// <summary>
/// A live spectrum for the visualizer. The audio thread only mixes each buffer to mono into a ring (<see cref="Write"/>, nothing at all while
/// disabled); the reader (the UI thread) windows the latest samples, runs an FFT and folds the bins into log-spaced bands. Torn reads of the
/// ring are harmless here: a glitch in one frame of a picture is invisible.
/// </summary>
internal sealed class SpectrumAnalyzer
{
    private const int FftSize = 2048;
    private const int RingSize = 8192;
    private const double MinFrequency = 40;
    private const double MaxFrequency = 16000;
    private const double FloorDb = -62;
    private const long StaleMilliseconds = 250;

    private readonly float[] ring = new float[RingSize];
    private readonly float[] window = new float[FftSize];
    private readonly float[] input = new float[2 * FftSize];
    private readonly float[] output = new float[2 * FftSize];
    private readonly ComplexFft fft = new(FftSize);
    private long written;
    private long lastWriteTicks;
    private volatile bool enabled;
    private uint sampleRate = 44100;
    private int[] edges = [];

    public SpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++) window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1))));
    }

    public bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }

    /// <summary>The output rate; set when the device opens.</summary>
    public uint SampleRate
    {
        get => sampleRate;
        set
        {
            sampleRate = value == 0 ? 44100 : value;
            edges = [];
        }
    }

    /// <summary>Audio thread: records interleaved stereo. Costs one flag read when the visualizer is off.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (!enabled) return;
        long position = Volatile.Read(ref written);
        for (int i = 0; i + 1 < interleaved.Length; i += AudioChannels.Channels)
        {
            ring[(int)(position & (RingSize - 1))] = 0.5f * (interleaved[i] + interleaved[i + 1]);
            position++;
        }
        Volatile.Write(ref written, position);
        Volatile.Write(ref lastWriteTicks, Environment.TickCount64);
    }

    public bool Read(Span<float> bands)
    {
        bands.Clear();
        if (!enabled || bands.Length == 0) return false;
        long end = Volatile.Read(ref written);
        if (end < FftSize || Environment.TickCount64 - Volatile.Read(ref lastWriteTicks) > StaleMilliseconds) return false;
        long start = end - FftSize;
        for (int i = 0; i < FftSize; i++)
        {
            input[2 * i] = ring[(int)((start + i) & (RingSize - 1))] * window[i];
            input[(2 * i) + 1] = 0;
        }
        fft.Transform(input, output);

        int[] bounds = BandEdges(bands.Length);
        // A full-scale sine through a Hann window peaks at FftSize / 4 in the transform.
        const double reference = FftSize / 4.0;
        for (int band = 0; band < bands.Length; band++)
        {
            float peak = 0;
            for (int bin = bounds[band]; bin < bounds[band + 1]; bin++)
            {
                float re = output[2 * bin], im = output[(2 * bin) + 1];
                peak = Math.Max(peak, (re * re) + (im * im));
            }
            double db = 10 * Math.Log10((peak / (reference * reference)) + 1e-12);
            // Tilt the top up a little (about 3 dB per octave) so the treble is not always flat next to the bass, as music is pink.
            db += 3 * Math.Log2((double)(bounds[band] + 1) / Math.Max(bounds[0], 1));
            bands[band] = (float)Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
        }
        return true;
    }

    // Bin edges for count bands, log-spaced from MinFrequency to MaxFrequency, each band at least one bin wide.
    private int[] BandEdges(int count)
    {
        int[] cached = edges;
        if (cached.Length == count + 1) return cached;
        double binWidth = (double)sampleRate / FftSize;
        double top = Math.Min(MaxFrequency, sampleRate * 0.45);
        int maxBin = FftSize / 2;
        int[] result = new int[count + 1];
        result[0] = Math.Max(1, (int)(MinFrequency / binWidth));
        for (int band = 1; band <= count; band++)
        {
            double frequency = MinFrequency * Math.Pow(top / MinFrequency, (double)band / count);
            result[band] = Math.Clamp((int)Math.Round(frequency / binWidth), result[band - 1] + 1, maxBin);
        }
        // Clamping can leave the last edges equal at the Nyquist end; keep every band non-empty by walking back.
        for (int band = count; band > 0 && result[band] <= result[band - 1]; band--) result[band - 1] = result[band] - 1;
        return edges = result;
    }
}
