using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Playback.LibVLC;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Playback;

[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed class LibVlcHandoffIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] Formats = ["flac", "m4a", "mp3", "ogg", "opus", "wav"];

    [SetUp]
    public void RequireNativeRuntime()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to characterize native handoff.");
    }

    [TestCaseSource(nameof(Formats))]
    public async Task NaturalEnd_LoadNextCodec_ReachesPlayingAndEndsOnce(string format)
    {
        string nextFormat = Formats[(Array.IndexOf(Formats, format) + 1) % Formats.Length];
        List<HandoffSample> samples = [];
        for (int iteration = 0; iteration < 3; iteration++)
        {
            using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
            Track first = Fixture(format), next = Fixture(nextFormat);
            TaskCompletionSource<long> firstEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<long> nextPlaying = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource nextEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int firstEnds = 0, nextEnds = 0;
            engine.StateChanged += (_, snapshot) =>
            {
                long timestamp = Stopwatch.GetTimestamp();
                if (snapshot.Status == PlaybackStatus.Faulted)
                {
                    InvalidOperationException failure = new(snapshot.Error);
                    firstEnded.TrySetException(failure); nextPlaying.TrySetException(failure); nextEnded.TrySetException(failure);
                }
                if (snapshot.Track?.Id == first.Id && snapshot.Status == PlaybackStatus.Ended)
                {
                    Interlocked.Increment(ref firstEnds); firstEnded.TrySetResult(timestamp);
                }
                if (snapshot.Track?.Id == next.Id && snapshot.Status == PlaybackStatus.Playing)
                    nextPlaying.TrySetResult(timestamp);
                if (snapshot.Track?.Id == next.Id && snapshot.Status == PlaybackStatus.Ended)
                {
                    Interlocked.Increment(ref nextEnds); nextEnded.TrySetResult();
                }
            };
            await engine.LoadAsync(first); await engine.SetMutedAsync(true); await engine.PlayAsync();
            long endedAt = await firstEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Never issue a native command from inside LibVLC's callback thread.
            long commandAt = Stopwatch.GetTimestamp();
            await engine.LoadAsync(next); await engine.PlayAsync();
            long playingAt = await nextPlaying.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await nextEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100); // Observe late terminal callbacks from both media.
            Assert.Multiple(() =>
            {
                Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(next.Id));
                Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Ended));
                Assert.That(engine.Snapshot.IsMuted, Is.True);
                Assert.That(firstEnds, Is.EqualTo(1)); Assert.That(nextEnds, Is.EqualTo(1));
            });
            samples.Add(new(Stopwatch.GetElapsedTime(endedAt, commandAt).TotalMilliseconds,
                Stopwatch.GetElapsedTime(commandAt, playingAt).TotalMilliseconds,
                Stopwatch.GetElapsedTime(endedAt, playingAt).TotalMilliseconds));
        }
        string output = Path.Combine(TestContext.CurrentContext.TestDirectory, "handoff-results");
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, $"{format}-to-{nextFormat}.json");
        using LibVLCSharp.Shared.LibVLC runtime = new("--no-video", "--quiet");
        var report = new
        {
            Measurement = "native-event-handoff; not an audible-gap or crossfade measurement",
            FirstCodec = format,
            NextCodec = nextFormat,
            Output = "muted dummy",
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            NativeLibVlcVersion = runtime.Version,
            SourceRevision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local-worktree",
            TimestampUtc = DateTimeOffset.UtcNow,
            Samples = samples,
            MedianEndToPlayingMilliseconds = samples.Select(sample => sample.EndToPlayingMilliseconds).Order().ElementAt(samples.Count / 2),
            MaximumEndToPlayingMilliseconds = samples.Max(sample => sample.EndToPlayingMilliseconds)
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions));
        TestContext.AddTestAttachment(path, "Sequential native handoff timing; excludes physical audio output and the UI dispatcher.");
        TestContext.Out.WriteLine($"{format} -> {nextFormat}: median {report.MedianEndToPlayingMilliseconds:F2} ms; max {report.MaximumEndToPlayingMilliseconds:F2} ms (native events only)");
    }

    private sealed record HandoffSample(double ContinuationDelayMilliseconds, double LoadToPlayingMilliseconds, double EndToPlayingMilliseconds);

    private static Track Fixture(string extension) => new(Guid.NewGuid(),
        new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", $"fixture.{extension}")),
        "Fixture Tone", "Aibell Test Runner", "Synthetic Fixtures", TimeSpan.FromSeconds(1));
}
