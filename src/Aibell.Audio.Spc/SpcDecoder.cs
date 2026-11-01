namespace Aibell.Audio.Spc;

/// <summary>
/// Plays an SPC file by running its saved SNES sound unit (SPC700 CPU and S-DSP) and returning the DSP's 32 kHz stereo output. The
/// emulation never ends by itself, so the length comes from the file's tag: the play time followed by the fade-out (a default is used
/// when the tag has none). Seeking forward keeps running the emulator; seeking back restarts it from the saved state.
/// </summary>
public sealed class SpcDecoder : IAudioDecoder
{
    public const int NativeSampleRate = SpcDsp.SampleRate;

    private const float Scale = 1f / 32768f;

    private readonly SpcFile file;
    private readonly Spc700 machine = new();
    private readonly long playFrames;
    private readonly long fadeFrames;
    private long position;
    private bool disposed;

    private SpcDecoder(SpcFile file, TimeSpan? length)
    {
        this.file = file;
        (TimeSpan play, TimeSpan fade) = length is { } forced ? (forced, TimeSpan.Zero) : file.ResolveLengths();
        playFrames = (long)(play.TotalSeconds * NativeSampleRate);
        fadeFrames = (long)(fade.TotalSeconds * NativeSampleRate);
        TotalFrames = playFrames + fadeFrames;
        machine.Load(file);
    }

    public SpcFile File => file;

    public int SampleRate => NativeSampleRate;

    public int Channels => 2;

    public long TotalFrames { get; }

    /// <summary>True when the bytes start an SPC file.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) => SpcFile.Sniff(head);

    /// <summary>Reads the whole file. Throws <see cref="InvalidDataException"/> for anything that is not an SPC file.</summary>
    public static SpcDecoder Open(Stream stream) => new(SpcFile.Open(stream), null);

    public static SpcDecoder Open(SpcFile file, TimeSpan? length = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new SpcDecoder(file, length);
    }

    public int Read(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int wanted = (int)Math.Min(frames, TotalFrames - position);
        if (wanted <= 0) return 0;
        for (int i = 0; i < wanted; i++)
        {
            machine.RunFrame(out int left, out int right);
            float gain = 1f;
            long intoFade = position + i - playFrames;
            if (intoFade >= 0) gain = fadeFrames == 0 ? 0f : 1f - (float)intoFade / fadeFrames;
            destination[i * 2] = left * Scale * gain;
            destination[i * 2 + 1] = right * Scale * gain;
        }
        position += wanted;
        return wanted;
    }

    public bool Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame < 0 || frame > TotalFrames) return false;
        if (frame < position)
        {
            machine.Load(file);
            position = 0;
        }
        while (position < frame)
        {
            machine.RunFrame(out _, out _);
            position++;
        }
        return true;
    }

    public void Dispose() => disposed = true;
}
