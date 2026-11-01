namespace Aibell.Audio;

/// <summary>Folds interleaved multi-channel audio (WAVE channel order) down to interleaved stereo. Create one per channel count; mixing allocates nothing.</summary>
public sealed class ChannelMixer
{
    private const float Centre = 0.70710678f;

    private readonly int channels;
    private readonly float[] left;
    private readonly float[] right;

    public ChannelMixer(int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        this.channels = channels;
        left = new float[channels];
        right = new float[channels];
        // Roles per channel count in WAVE order: L R C (centre) S (side/back left) T (right counterpart) B (back centre) . (dropped, LFE).
        string roles = channels switch
        {
            1 => "M",
            2 => "LR",
            3 => "LRC",
            4 => "LRst",
            5 => "LRCst",
            6 => "LRC.st",
            7 => "LRC.Bst",
            8 => "LRC.stst",
            _ => "LRC.stst" + new string('.', channels - 8)
        };
        for (int c = 0; c < channels; c++)
        {
            switch (roles[c])
            {
                case 'M': left[c] = right[c] = 1f; break;
                case 'L': left[c] = 1f; break;
                case 'R': right[c] = 1f; break;
                case 'C': left[c] = right[c] = Centre; break;
                case 'B': left[c] = right[c] = Centre * Centre; break;
                case 's': left[c] += Centre; break;
                case 't': right[c] += Centre; break;
            }
        }
        if (channels > 2)
        {
            float sumLeft = 0, sumRight = 0;
            foreach (float w in left) sumLeft += w;
            foreach (float w in right) sumRight += w;
            float norm = 1f / Math.Max(1f, Math.Max(sumLeft, sumRight));
            for (int c = 0; c < channels; c++) { left[c] *= norm; right[c] *= norm; }
        }
    }

    public int Channels => channels;

    public void ToStereo(ReadOnlySpan<float> source, Span<float> stereo, int frames)
    {
        if (channels == 1)
        {
            for (int i = 0; i < frames; i++) stereo[2 * i] = stereo[2 * i + 1] = source[i];
            return;
        }
        if (channels == 2)
        {
            source[..(frames * 2)].CopyTo(stereo);
            return;
        }
        for (int i = 0; i < frames; i++)
        {
            float l = 0, r = 0;
            ReadOnlySpan<float> frame = source.Slice(i * channels, channels);
            for (int c = 0; c < frame.Length; c++)
            {
                l += frame[c] * left[c];
                r += frame[c] * right[c];
            }
            stereo[2 * i] = l;
            stereo[2 * i + 1] = r;
        }
    }
}
