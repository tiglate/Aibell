using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Playback.Core;
using NUnit.Framework;

namespace Aibell.Playback.Miniaudio.Tests;

internal static class EngineTestSupport
{
    public static string Fixture(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", name);

    /// <summary>
    /// miniaudio's own decoders, which live behind the native library and are the last resort
    /// after the managed ones. Tests that build the source factory themselves need this.
    /// </summary>
    public static IPcmSource? MiniaudioDecoders(Stream stream, uint sampleRate) =>
        NativeDecoderSource.TryOpen(stream, sampleRate, out _);

    public static Track TrackFor(Uri location, double seconds = 1) =>
        new(Guid.NewGuid(), location, "Fixture", "Aibell", "Fixtures", TimeSpan.FromSeconds(seconds));

    public static Track TrackFor(string fixture, double seconds = 1) => TrackFor(new Uri(Fixture(fixture)), seconds);

    // The silent backend paces the callback like a real device, so playback takes real time but needs no sound card.
    public static MiniaudioPlaybackEngine NewEngine(HttpClient? http = null) => new(http, useNullBackend: true);

    public static async Task<PlaybackSnapshot> WaitForAsync(MiniaudioPlaybackEngine engine, Func<PlaybackSnapshot, bool> condition, int seconds = 10)
    {
        TaskCompletionSource<PlaybackSnapshot> reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, PlaybackSnapshot state)
        {
            if (condition(state)) reached.TrySetResult(state);
        }
        engine.StateChanged += Handler;
        try
        {
            if (condition(engine.Snapshot)) return engine.Snapshot;
            return await reached.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            engine.StateChanged -= Handler;
        }
    }
}
