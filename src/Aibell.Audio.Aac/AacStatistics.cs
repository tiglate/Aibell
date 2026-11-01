namespace Aibell.Audio.Aac;

/// <summary>Counters of which coding tools a decoder has met. They exist so tests can prove a fixture really exercised a tool.</summary>
public sealed class AacStatistics
{
    public long LongChannels { get; internal set; }

    public long StartChannels { get; internal set; }

    public long ShortChannels { get; internal set; }

    public long StopChannels { get; internal set; }

    public long KaiserBesselChannels { get; internal set; }

    public long TnsChannels { get; internal set; }

    public long PulseChannels { get; internal set; }

    public long MidSideBands { get; internal set; }

    public long IntensityBands { get; internal set; }

    public long CouplingElements { get; internal set; }

    public long NoiseBands { get; internal set; }

    internal void Count(WindowSequence sequence)
    {
        switch (sequence)
        {
            case WindowSequence.OnlyLong: LongChannels++; break;
            case WindowSequence.LongStart: StartChannels++; break;
            case WindowSequence.EightShort: ShortChannels++; break;
            default: StopChannels++; break;
        }
    }
}
