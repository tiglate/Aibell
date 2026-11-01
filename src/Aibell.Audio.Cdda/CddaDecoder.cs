namespace Aibell.Audio.Cdda;

/// <summary>
/// One track of an audio CD (Red Book): 44.1 kHz, 16-bit, stereo PCM in 2352-byte sectors, 75 per second. There is nothing to decode; this reads the
/// track's sector range from an <see cref="ICdAudioReader"/> in small chunks and converts the samples to float. A sector the drive cannot read is
/// retried and then played as silence, so a scratch costs a click instead of the track; a long run of unreadable sectors stops playback.
/// </summary>
public sealed class CddaDecoder : IAudioDecoder
{
    public const int SectorBytes = 2352;
    public const int FramesPerSector = SectorBytes / 4;
    public const int Rate = 44100;

    // About 0.2 s per drive request: small enough to start and seek quickly, large enough to keep the drive streaming.
    private const int ChunkSectors = 16;
    private const int RetriesPerSector = 3;
    // 2 seconds of consecutive bad sectors: the disc is damaged or gone, not scratched.
    private const int MaximumBadRun = 2 * 75;

    private readonly ICdAudioReader reader;
    private readonly int startLba;
    private readonly int endLba;
    private readonly byte[] buffer = new byte[ChunkSectors * SectorBytes];
    private int nextLba;
    private int bufferFrames;
    private int bufferPosition;
    private int badRun;
    private bool disposed;

    /// <param name="reader">Owned: disposed with the decoder.</param>
    /// <param name="startLba">First sector of the track.</param>
    /// <param name="endLba">Sector after the last one of the track.</param>
    public CddaDecoder(ICdAudioReader reader, int startLba, int endLba)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(startLba);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endLba, startLba);
        this.reader = reader;
        this.startLba = startLba;
        this.endLba = endLba;
        nextLba = startLba;
    }

    public int SampleRate => Rate;

    public int Channels => 2;

    public long TotalFrames => (long)(endLba - startLba) * FramesPerSector;

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        frames = Math.Min(frames, destination.Length / 2);
        int produced = 0;
        while (produced < frames)
        {
            if (bufferPosition >= bufferFrames && !Fill()) break;
            int count = Math.Min(frames - produced, bufferFrames - bufferPosition);
            ReadOnlySpan<byte> source = buffer.AsSpan(bufferPosition * 4, count * 4);
            Span<float> target = destination.Slice(produced * 2, count * 2);
            for (int index = 0; index < target.Length; index++)
                target[index] = (short)(source[index * 2] | (source[index * 2 + 1] << 8)) / 32768f;
            bufferPosition += count;
            produced += count;
        }
        return produced;
    }

    public bool Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame < 0 || frame > TotalFrames) return false;
        nextLba = startLba + (int)(frame / FramesPerSector);
        bufferFrames = 0;
        bufferPosition = 0;
        int skip = (int)(frame % FramesPerSector);
        if (skip == 0) return true;
        if (!Fill()) return false;
        bufferPosition = skip;
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        reader.Dispose();
    }

    private bool Fill()
    {
        bufferFrames = 0;
        bufferPosition = 0;
        int count = Math.Min(ChunkSectors, endLba - nextLba);
        if (count <= 0) return false;
        if (reader.ReadSectors(nextLba, count, buffer))
        {
            badRun = 0;
        }
        else
        {
            // The whole request failed: find out which sectors are the bad ones.
            for (int sector = 0; sector < count; sector++) ReadSingle(nextLba + sector, buffer.AsSpan(sector * SectorBytes, SectorBytes));
        }
        nextLba += count;
        bufferFrames = count * FramesPerSector;
        return true;
    }

    private void ReadSingle(int lba, Span<byte> destination)
    {
        for (int attempt = 0; attempt < RetriesPerSector; attempt++)
        {
            if (!reader.ReadSectors(lba, 1, destination)) continue;
            badRun = 0;
            return;
        }
        destination.Clear();
        if (++badRun > MaximumBadRun) throw new IOException("The disc cannot be read: " + badRun + " sectors in a row failed.");
    }
}
