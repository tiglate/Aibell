using System.Runtime.InteropServices;
using System.Text.Json;
using Aibell.Playback.LibVLC;
using LibVLCSharp.Shared;
using NUnit.Framework;
using LibVlcInstance = LibVLCSharp.Shared.LibVLC;

namespace Aibell.Desktop.Tests.Playback;

// Measures LibVLC's decoded-sample output directly through the audio callback
// API (bypassing any audio device). This characterizes per-format decoder
// sample continuity (encoder delay/padding), not an audible device-clock gap
// or crossfade behavior.
[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed class LibVlcAudioContinuityIntegrationTests
{
    private const uint SampleRate = 48000;
    private const uint Channels = 1;
    private static readonly string[] Formats = ["flac", "m4a", "mp3", "ogg", "opus", "wav"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [SetUp]
    public void RequireNativeRuntime()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
        {
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to characterize native decode continuity.");
        }
    }

    [TestCaseSource(nameof(Formats))]
    public async Task Decode_OneSecondFixture_ReportsDeliveredSampleContinuity(string format)
    {
        Core.Initialize(LibVlcRuntime.ResolveDirectory(OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("AIBELL_LIBVLC_PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));

        using LibVlcInstance libVlc = new("--no-video", "--quiet");
        using MediaPlayer player = new(libVlc);
        PcmCapture capture = new(Channels);
        player.SetAudioFormat("S16N", SampleRate, Channels);
        player.SetAudioCallbacks(capture.PlayCb, null, null, null, null);

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        player.EndReached += (_, _) => ended.TrySetResult();
        player.EncounteredError += (_, _) => ended.TrySetException(new InvalidOperationException("LibVLC reported a playback error."));

        Media media = new(libVlc, FixturePath(format));
        try
        {
            player.Media = media;
            Assert.That(player.Play(), Is.True, "LibVLC rejected Play().");
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Let any already-decoded buffers still in flight reach the play callback.
            await Task.Delay(300);
        }
        finally
        {
            media.Dispose();
        }

        long expectedFrames = SampleRate; // Fixtures are an exact one-second tone.
        long deliveredFrames = capture.TotalFrames;
        double deltaMilliseconds = (deliveredFrames - expectedFrames) / (double)SampleRate * 1000.0;

        Assert.Multiple(() =>
        {
            Assert.That(deliveredFrames, Is.GreaterThan(0), "No decoded samples reached the audio callback.");
            Assert.That(capture.MaxAbsoluteSample, Is.GreaterThan(1000),
                "Captured audio is near-silent; the callback wiring likely bypassed the decoder.");
        });

        string outputDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "audio-continuity-results");
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, $"{format}.json");
        var report = new
        {
            Measurement = "decoded-sample-count continuity via LibVLC audio callbacks; " +
                "not an audible device-clock gap or crossfade measurement",
            Format = format,
            SampleRate,
            Channels,
            ExpectedFrames = expectedFrames,
            DeliveredFrames = deliveredFrames,
            DeltaFrames = deliveredFrames - expectedFrames,
            DeltaMilliseconds = deltaMilliseconds,
            MaxAbsoluteSample16 = capture.MaxAbsoluteSample,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            NativeLibVlcVersion = libVlc.Version,
            SourceRevision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local-worktree",
            TimestampUtc = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions));
        TestContext.AddTestAttachment(path, "Per-format decoded-sample continuity; excludes physical audio output.");
        TestContext.Out.WriteLine(
            $"{format}: delivered {deliveredFrames} frames (expected {expectedFrames}), delta {deltaMilliseconds:F2} ms");
    }

    private static string FixturePath(string extension) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", $"fixture.{extension}");

    // Keeps the native callback delegates alive for the life of the capture
    // and accumulates delivered frame counts and peak amplitude.
    private sealed class PcmCapture
    {
        private readonly object sync = new();
        private readonly uint channels;

        public PcmCapture(uint channels)
        {
            this.channels = channels;
            PlayCb = Play;
        }

        public MediaPlayer.LibVLCAudioPlayCb PlayCb { get; }

        public long TotalFrames { get; private set; }

        public short MaxAbsoluteSample { get; private set; }

        private void Play(IntPtr data, IntPtr samples, uint count, long pts)
        {
            int byteCount = checked((int)(count * channels * sizeof(short)));
            if (byteCount <= 0)
            {
                return;
            }

            byte[] chunk = new byte[byteCount];
            Marshal.Copy(samples, chunk, 0, byteCount);

            short peak = 0;
            for (int offset = 0; offset + 1 < byteCount; offset += 2)
            {
                short sample = (short)(chunk[offset] | (chunk[offset + 1] << 8));
                short magnitude = sample < 0 ? (short)-Math.Max(sample, (short)(short.MinValue + 1)) : sample;
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            lock (sync)
            {
                TotalFrames += count;
                if (peak > MaxAbsoluteSample)
                {
                    MaxAbsoluteSample = peak;
                }
            }
        }
    }
}
