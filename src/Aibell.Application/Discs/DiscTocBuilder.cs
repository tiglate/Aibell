namespace Aibell.Application.Discs;

/// <summary>One entry of a raw disc table of contents. <see cref="Offset"/> is in sectors including the 150-sector lead-in.</summary>
public readonly record struct RawDiscTrack(int Number, bool IsData, int Offset);

/// <summary>Turns a raw table of contents into the audio-only <see cref="DiscToc"/> the rest of the application uses.</summary>
public static class DiscTocBuilder
{
    // On discs with a data session after the audio, the audio lead-out is 11400 sectors before the data session starts.
    private const int SessionGap = 11400;

    public static DiscToc? Build(IEnumerable<RawDiscTrack> tracks, int leadOutOffset)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        RawDiscTrack[] ordered = [.. tracks.OrderBy(track => track.Number)];
        int start = Array.FindIndex(ordered, track => !track.IsData);
        if (start < 0) return null;

        // Use the first run of consecutive audio tracks; whatever follows it bounds the run.
        int end = start;
        while (end + 1 < ordered.Length && !ordered[end + 1].IsData && ordered[end + 1].Number == ordered[end].Number + 1) end++;
        int leadOut = end + 1 < ordered.Length ? ordered[end + 1].Offset - SessionGap : leadOutOffset;

        int[] offsets = [.. ordered[start..(end + 1)].Select(track => track.Offset)];
        try
        {
            return new DiscToc(offsets, leadOut, ordered[start].Number);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
