namespace Aibell.Audio;

/// <summary>
/// A decoder producing interleaved float samples in +/-1 at the stream's own sample rate and channel count. Channels are always in
/// the WAVE order (front left, front right, front centre, LFE, back left, back right, ...), whatever the container uses.
/// Implementations allocate nothing while decoding once they are open.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    int SampleRate { get; }

    int Channels { get; }

    /// <summary>Length in frames, or -1 when the stream does not say.</summary>
    long TotalFrames { get; }

    /// <summary>Decodes up to <paramref name="frames"/> frames into <paramref name="destination"/> (at least frames * Channels floats). Returns 0 at the end of the stream; throws <see cref="InvalidDataException"/> on damaged data.</summary>
    int Read(Span<float> destination, int frames);

    /// <summary>Moves to <paramref name="frame"/>; false when the source cannot seek there.</summary>
    bool Seek(long frame);
}
