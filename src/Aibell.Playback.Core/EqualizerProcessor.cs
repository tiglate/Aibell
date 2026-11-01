using Aibell.Domain.Playback;

namespace Aibell.Playback.Core;

/// <summary>
/// The ten-band equalizer: one peaking biquad per band (RBJ cookbook, Q = 1.41, one octave apart) plus the preamp, applied to
/// interleaved stereo on the audio thread. <see cref="Set"/> may be called from any thread; it swaps in new coefficients
/// between buffers and keeps the filter state, so moving a slider makes no click. 0 dB everywhere is exactly unity.
/// </summary>
internal sealed class EqualizerProcessor(uint sampleRate)
{
    private const int Bands = EqualizerSettings.BandCount;
    private const int Channels = AudioChannels.Channels;
    private const double Q = 1.41421356;
    private readonly float[] state = new float[Channels * Bands * 2];
    private volatile Coefficients? coefficients;
    private bool wasActive;

    private sealed record Coefficients(float Preamp, float[] B0, float[] B1, float[] B2, float[] A1, float[] A2);

    public void Set(EqualizerSettings settings)
    {
        if (!settings.IsEnabled)
        {
            coefficients = null;
            return;
        }
        float[] b0 = new float[Bands], b1 = new float[Bands], b2 = new float[Bands], a1 = new float[Bands], a2 = new float[Bands];
        for (int band = 0; band < Bands; band++)
        {
            (b0[band], b1[band], b2[band], a1[band], a2[band]) = Peaking(EqualizerSettings.BandFrequencies[band], settings.Bands[band]);
        }
        coefficients = new Coefficients((float)Math.Pow(10, settings.Preamp / 20), b0, b1, b2, a1, a2);
    }

    public void Process(Span<float> buffer)
    {
        Coefficients? active = coefficients;
        if (active is null)
        {
            wasActive = false;
            return;
        }
        // Stale state from before the equalizer was switched off would ring for a moment.
        if (!wasActive) Array.Clear(state);
        wasActive = true;
        for (int channel = 0; channel < Channels; channel++)
        {
            for (int i = channel; i < buffer.Length; i += Channels)
            {
                float x = buffer[i] * active.Preamp;
                for (int band = 0; band < Bands; band++)
                {
                    int s = ((channel * Bands) + band) * 2;
                    float y = (active.B0[band] * x) + state[s];
                    state[s] = (active.B1[band] * x) - (active.A1[band] * y) + state[s + 1];
                    state[s + 1] = (active.B2[band] * x) - (active.A2[band] * y);
                    x = y;
                }
                buffer[i] = Math.Clamp(x, -1f, 1f);
            }
        }
    }

    private (float B0, float B1, float B2, float A1, float A2) Peaking(double frequency, double gainDb)
    {
        // A band at or above the Nyquist limit of a low-rate device cannot be shaped; leave it flat.
        if (Math.Abs(gainDb) < 0.005 || frequency >= sampleRate * 0.45) return (1, 0, 0, 0, 0);
        double amplitude = Math.Pow(10, gainDb / 40);
        double w = 2 * Math.PI * frequency / sampleRate;
        double alpha = Math.Sin(w) / (2 * Q);
        double cos = Math.Cos(w);
        double a0 = 1 + (alpha / amplitude);
        return ((float)((1 + (alpha * amplitude)) / a0), (float)(-2 * cos / a0), (float)((1 - (alpha * amplitude)) / a0),
            (float)(-2 * cos / a0), (float)((1 - (alpha / amplitude)) / a0));
    }
}
