namespace Aibell.Audio.Aac;

/// <summary>Per-channel IMDCT, windowing and overlap-add (ISO/IEC 14496-3 subclause 4.6.11).</summary>
internal sealed class Filterbank
{
    private const int Long = 1024;
    private const int Short = 128;
    private const float LongScale = 2f / 2048f / 32768f;
    private const float ShortScale = 2f / 256f / 32768f;

    private static readonly float[][] LongWindows = [Windows.Sine(2048), Windows.KaiserBesselDerived(2048, 4)];
    private static readonly float[][] ShortWindows = [Windows.Sine(256), Windows.KaiserBesselDerived(256, 6)];

    private readonly Imdct longTransform;
    private readonly Imdct shortTransform;
    private readonly float[] frame = new float[2 * Long];
    private readonly float[] block = new float[2 * Short];
    private readonly float[] overlap = new float[Long];
    private int previousShape;

    public Filterbank(Imdct longTransform, Imdct shortTransform)
    {
        this.longTransform = longTransform;
        this.shortTransform = shortTransform;
    }

    public void Reset()
    {
        Array.Clear(overlap);
        previousShape = 0;
    }

    /// <summary>Turns 1024 spectral values into 1024 output samples (float, full scale ±1).</summary>
    public void Process(ReadOnlySpan<float> spectrum, WindowSequence sequence, int shape, Span<float> output)
    {
        float[] leftLong = LongWindows[previousShape], rightLong = LongWindows[shape];
        if (sequence == WindowSequence.EightShort)
        {
            Array.Clear(frame);
            float[] leftFirst = ShortWindows[previousShape], current = ShortWindows[shape];
            for (int w = 0; w < 8; w++)
            {
                shortTransform.Transform(spectrum.Slice(w * Short, Short), block, ShortScale);
                float[] left = w == 0 ? leftFirst : current;
                int at = 448 + w * Short;
                for (int n = 0; n < Short; n++) frame[at + n] += block[n] * left[n];
                for (int n = Short; n < 2 * Short; n++) frame[at + n] += block[n] * current[n];
            }
        }
        else
        {
            longTransform.Transform(spectrum, frame, LongScale);
            float[] shortLeft = ShortWindows[previousShape], shortRight = ShortWindows[shape];
            switch (sequence)
            {
                case WindowSequence.OnlyLong:
                    for (int n = 0; n < Long; n++) frame[n] *= leftLong[n];
                    for (int n = Long; n < 2 * Long; n++) frame[n] *= rightLong[n];
                    break;
                case WindowSequence.LongStart:
                    for (int n = 0; n < Long; n++) frame[n] *= leftLong[n];
                    for (int n = 1472; n < 1600; n++) frame[n] *= shortRight[Short + n - 1472];
                    Array.Clear(frame, 1600, 2 * Long - 1600);
                    break;
                default: // LongStop
                    Array.Clear(frame, 0, 448);
                    for (int n = 448; n < 576; n++) frame[n] *= shortLeft[n - 448];
                    for (int n = Long; n < 2 * Long; n++) frame[n] *= rightLong[n];
                    break;
            }
        }
        for (int n = 0; n < Long; n++)
        {
            output[n] = overlap[n] + frame[n];
            overlap[n] = frame[Long + n];
        }
        previousShape = shape;
    }
}
