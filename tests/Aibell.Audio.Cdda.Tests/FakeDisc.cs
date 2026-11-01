namespace Aibell.Audio.Cdda.Tests;

/// <summary>An in-memory disc: the left sample of frame n (counted from sector 0) is <c>n % 30000</c>, the right one its negative.</summary>
internal sealed class FakeDisc(int sectors) : ICdAudioReader
{
    public List<(int Lba, int Count)> Requests { get; } = [];

    public HashSet<int> BadSectors { get; } = [];

    /// <summary>Sectors that fail this many times before they read fine.</summary>
    public Dictionary<int, int> FailuresLeft { get; } = [];

    public bool Gone { get; set; }

    public bool Disposed { get; private set; }

    public static short Left(long frame) => (short)(frame % 30000);

    public bool ReadSectors(int lba, int count, Span<byte> destination)
    {
        Requests.Add((lba, count));
        if (Gone) throw new IOException("There is no disc in the drive.");
        if (lba < 0 || lba + count > sectors) return false;
        for (int sector = 0; sector < count; sector++)
        {
            if (BadSectors.Contains(lba + sector)) return false;
            if (FailuresLeft.TryGetValue(lba + sector, out int left) && left > 0)
            {
                FailuresLeft[lba + sector] = left - 1;
                return false;
            }
        }
        for (int sector = 0; sector < count; sector++)
        {
            for (int frame = 0; frame < CddaDecoder.FramesPerSector; frame++)
            {
                long absolute = (long)(lba + sector) * CddaDecoder.FramesPerSector + frame;
                int at = sector * CddaDecoder.SectorBytes + frame * 4;
                BitConverter.TryWriteBytes(destination.Slice(at, 2), Left(absolute));
                BitConverter.TryWriteBytes(destination.Slice(at + 2, 2), (short)-Left(absolute));
            }
        }
        return true;
    }

    public void Dispose() => Disposed = true;
}
