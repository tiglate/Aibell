namespace Aibell.Audio.Mp3;

/// <summary>One scalefactor band in coded order: its first line, its width, the scalefactor band number and the short window (-1 for a long band).</summary>
internal readonly record struct Mp3Band(int Start, int Width, int Sfb, int Window);

/// <summary>The scalefactor band layouts of one sampling rate: for long blocks, short blocks and mixed blocks, in the order the coded values are stored.</summary>
internal sealed class Mp3BandLayout
{
    public Mp3BandLayout(int sampleRate, bool mpeg1)
    {
        int[] longWidths = Mp3Tables.LongBandWidths(sampleRate);
        int[] shortWidths = Mp3Tables.ShortBandWidths(sampleRate);
        LongEdges = new int[23];
        for (int i = 0; i < longWidths.Length; i++) LongEdges[i + 1] = LongEdges[i] + longWidths[i];
        // The bands above the last tabulated one (up to line 576) form band 21.
        for (int i = longWidths.Length; i < 22; i++) LongEdges[i + 1] = i == 21 ? 576 : LongEdges[i];
        LongEdges[22] = 576;
        ShortEdges = new int[14];
        for (int i = 0; i < shortWidths.Length; i++) ShortEdges[i + 1] = ShortEdges[i] + shortWidths[i];
        ShortEdges[13] = 192;

        List<Mp3Band> longBands = [];
        for (int sfb = 0; sfb < 22; sfb++) longBands.Add(new Mp3Band(LongEdges[sfb], LongEdges[sfb + 1] - LongEdges[sfb], sfb, -1));
        Long = [.. longBands];

        Short = ShortBandsFrom(0, 0);
        MixedLongBands = mpeg1 ? 8 : 6;
        MixedLongLines = LongEdges[MixedLongBands];
        List<Mp3Band> mixed = [];
        for (int sfb = 0; sfb < MixedLongBands; sfb++) mixed.Add(longBands[sfb]);
        mixed.AddRange(ShortBandsFrom(3, MixedLongLines));
        Mixed = [.. mixed];
    }

    /// <summary>The first line of each long band, plus 576.</summary>
    public int[] LongEdges { get; }

    /// <summary>The first frequency (0-191) of each short band, plus 192.</summary>
    public int[] ShortEdges { get; }

    public Mp3Band[] Long { get; }

    public Mp3Band[] Short { get; }

    public Mp3Band[] Mixed { get; }

    /// <summary>The number of long bands at the bottom of a mixed block.</summary>
    public int MixedLongBands { get; }

    /// <summary>The number of lines of a mixed block that are transformed as a long block (two or four subbands).</summary>
    public int MixedLongLines { get; }

    private Mp3Band[] ShortBandsFrom(int firstSfb, int codedStart)
    {
        List<Mp3Band> bands = [];
        int position = codedStart;
        for (int sfb = firstSfb; sfb < 13; sfb++)
        {
            int width = ShortEdges[sfb + 1] - ShortEdges[sfb];
            for (int window = 0; window < 3; window++)
            {
                bands.Add(new Mp3Band(position, width, sfb, window));
                position += width;
            }
        }
        return [.. bands];
    }
}
