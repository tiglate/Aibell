namespace Aibell.Audio.Cdda;

/// <summary>Raw access to the audio sectors of a disc in a drive (digital audio extraction).</summary>
public interface ICdAudioReader : IDisposable
{
    /// <summary>
    /// Reads <paramref name="count"/> consecutive sectors starting at <paramref name="lba"/> (0 is the first sector after the 2 second lead-in,
    /// the position of "00:02:00") into <paramref name="destination"/>, <see cref="CddaDecoder.SectorBytes"/> bytes each. Returns false when the
    /// drive could not read them (a scratch, a busy drive); throws <see cref="IOException"/> when the drive or disc is gone.
    /// </summary>
    bool ReadSectors(int lba, int count, Span<byte> destination);
}
