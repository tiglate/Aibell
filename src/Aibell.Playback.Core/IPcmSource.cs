namespace Aibell.Playback.Core;

/// <summary>A decoder producing interleaved stereo float frames at the output device's sample rate.</summary>
internal interface IPcmSource : IDisposable
{
    /// <summary>Decodes up to <paramref name="frames"/> frames. Returns 0 at the end of the stream and throws on failure.</summary>
    int Read(Span<float> destination, int frames);

    /// <summary>Moves to a frame position; false when the source cannot seek there.</summary>
    bool Seek(long frame);
}
