namespace Aibell.Application.Discs;

/// <summary>
/// Table of contents of an audio CD, reduced to the audio tracks. Offsets are in sectors (75 per second) counted from the
/// start of the disc including the standard 150-sector lead-in, the convention every disc-identification service uses.
/// </summary>
public sealed record DiscToc
{
    public const int SectorsPerSecond = 75;
    public const int LeadInSectors = 150;

    public DiscToc(IReadOnlyList<int> trackOffsets, int leadOutOffset, int firstTrackNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(trackOffsets);
        if (trackOffsets.Count is 0 or > 99) throw new ArgumentException("An audio CD has between 1 and 99 tracks.", nameof(trackOffsets));
        if (firstTrackNumber is < 1 or > 99 || firstTrackNumber + trackOffsets.Count - 1 > 99)
            throw new ArgumentOutOfRangeException(nameof(firstTrackNumber));
        for (int index = 0; index < trackOffsets.Count; index++)
        {
            int next = index + 1 < trackOffsets.Count ? trackOffsets[index + 1] : leadOutOffset;
            if (trackOffsets[index] < 0 || next <= trackOffsets[index])
                throw new ArgumentException("Track offsets must increase and end before the lead-out.", nameof(trackOffsets));
        }
        TrackOffsets = [.. trackOffsets];
        LeadOutOffset = leadOutOffset;
        FirstTrackNumber = firstTrackNumber;
    }

    public IReadOnlyList<int> TrackOffsets { get; }

    public int LeadOutOffset { get; }

    public int FirstTrackNumber { get; }

    public int TrackCount => TrackOffsets.Count;

    public int LastTrackNumber => FirstTrackNumber + TrackCount - 1;

    /// <summary>The sectors of a track as a drive addresses them: from sector 0 (which lies <see cref="LeadInSectors"/> after the TOC's zero) up to, not including, <c>End</c>.</summary>
    public (int Start, int End) SectorRangeOf(int index)
    {
        if ((uint)index >= (uint)TrackCount) throw new ArgumentOutOfRangeException(nameof(index));
        int end = index + 1 < TrackCount ? TrackOffsets[index + 1] : LeadOutOffset;
        return (TrackOffsets[index] - LeadInSectors, end - LeadInSectors);
    }

    public TimeSpan DurationOf(int index)
    {
        if ((uint)index >= (uint)TrackCount) throw new ArgumentOutOfRangeException(nameof(index));
        int end = index + 1 < TrackCount ? TrackOffsets[index + 1] : LeadOutOffset;
        return TimeSpan.FromSeconds((end - TrackOffsets[index]) / (double)SectorsPerSecond);
    }
}
