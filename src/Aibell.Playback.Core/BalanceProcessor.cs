namespace Aibell.Playback.Core;

/// <summary>
/// The left/right balance of interleaved stereo: moving towards one side lowers the other channel, linearly, down to silence
/// at the end (the classic players' balance). <see cref="Set"/> may be called from any thread; the gains glide to the new
/// values over one buffer, so dragging the slider makes no click. Centred, the audio is left untouched.
/// </summary>
internal sealed class BalanceProcessor
{
    private volatile float target;
    private float left = 1f, right = 1f;

    public double Balance => target;

    public static double Clamp(double balance) => double.IsFinite(balance) ? Math.Clamp(balance, -1, 1) : 0;

    public void Set(double balance) => target = (float)Clamp(balance);

    /// <summary>The gains for <paramref name="balance"/>: the side it moves towards stays at full level.</summary>
    public static (float Left, float Right) Gains(double balance)
    {
        float value = (float)Clamp(balance);
        return (Math.Min(1f, 1f - value), Math.Min(1f, 1f + value));
    }

    public void Process(Span<float> buffer)
    {
        (float toLeft, float toRight) = Gains(target);
        float fromLeft = left, fromRight = right;
        if (fromLeft == 1f && fromRight == 1f && toLeft == 1f && toRight == 1f) return;
        int frames = buffer.Length / AudioChannels.Channels;
        for (int frame = 0; frame < frames; frame++)
        {
            float step = (frame + 1f) / frames;
            buffer[frame * 2] *= fromLeft + (toLeft - fromLeft) * step;
            buffer[(frame * 2) + 1] *= fromRight + (toRight - fromRight) * step;
        }
        left = toLeft;
        right = toRight;
    }
}
