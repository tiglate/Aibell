namespace Aibell.Playback.PipeWire;

/// <summary>
/// One of the formats a sink says it can play: the rate, how many channels and the sample format.
/// </summary>
internal readonly record struct AudioFormat(uint SampleRate, uint Channels, uint Format, uint MediaType = SpaMediaType.Audio, uint MediaSubtype = SpaMediaSubtype.Raw)
{
    /// <summary>The frames one buffer of this format holds.</summary>
    public int FramesPerSecond => (int)SampleRate;
}

/// <summary>
/// Picks the format to play in from the ones a sink offers.
///
/// The rule is to prefer forty-eight kilohertz, not the highest rate. A higher rate does not add
/// anything: the material was recorded at forty-four point one or forty-eight kilohertz, so
/// playing it at ninety-six or a hundred and ninety-two kilohertz means resampling it up to a
/// rate it was never recorded at. The sink then has to bring it back down again before it
/// reaches the converter, which is a second resampling step and not a cleaner one. Forty-eight
/// kilohertz is what the material, the format negotiation and the audio hardware agree on, so it
/// is the rate that avoids resampling work altogether.
/// </summary>
internal static class FormatChooser
{
    /// <summary>The rate the material and the hardware agree on.</summary>
    public const uint PreferredRate = 48000;

    /// <summary>
    /// Picks the format to use from the ones on offer. Prefers the rate closest to
    /// <see cref="PreferredRate"/>, breaking a tie towards the lower rate, and prefers stereo.
    /// Returns null when nothing suitable is offered.
    /// </summary>
    public static AudioFormat? Choose(IReadOnlyList<AudioFormat> offered, uint preferredRate = PreferredRate)
    {
        ArgumentNullException.ThrowIfNull(offered);
        if (offered.Count == 0) return null;

        AudioFormat? best = null;
        long bestDistance = long.MaxValue;
        foreach (AudioFormat candidate in offered)
        {
            if (candidate.SampleRate <= 0) continue;
            if (candidate.Channels <= 0) continue;
            long distance = Math.Abs((long)candidate.SampleRate - preferredRate);
            // The same distance keeps the lower rate, so the choice does not depend on the order.
            if (best is not null && (distance > bestDistance
                || (distance == bestDistance && candidate.SampleRate >= best.Value.SampleRate))) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// Picks the format to use, preferring a stereo one when the sink offers several at the same
    /// rate. The channels of the chosen format decide how the audio is mixed.
    /// </summary>
    public static AudioFormat? ChooseWithChannels(IReadOnlyList<AudioFormat> offered, uint preferredRate = PreferredRate)
    {
        ArgumentNullException.ThrowIfNull(offered);
        List<AudioFormat> usable = [.. offered.Where(candidate => candidate.SampleRate > 0 && candidate.Channels > 0)];
        if (usable.Count == 0) return null;

        AudioFormat? best = null;
        long bestDistance = long.MaxValue;
        foreach (AudioFormat candidate in usable)
        {
            long distance = Math.Abs((long)candidate.SampleRate - preferredRate);
            // A stereo format is preferred at the same rate, because the pipeline mixes to stereo.
            long channelPenalty = candidate.Channels == 2 ? 0 : 1;
            long score = (distance * 2) + channelPenalty;
            if (best is not null && score >= bestDistance) continue;
            best = candidate;
            bestDistance = score;
        }
        return best;
    }
}
