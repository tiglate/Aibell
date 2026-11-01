using System.Net;
using System.Net.Sockets;
using System.Text;
using Aibell.Application.Playback;
using Aibell.Playback.LibVLC;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Playback;

public sealed partial class LibVlcResumeIntegrationTests
{
    [TestCase("stop")]
    [TestCase("load")]
    [TestCase("dispose")]
    [TestCase("cancel")]
    public async Task ResumeStartup_BlockedNativeInput_InvalidationCancelsWithoutRevivingOldTrack(string action)
    {
        await using BlockedInput input = new();
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track() with { Location = input.Location });
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource cancellation = new();
        Task startup = engine.PlayAsync(cancellation.Token);
        await input.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(startup.IsCompleted, Is.False, "The owned server holds the input before native startup can finish.");
        if (action == "stop") await engine.StopAsync();
        else if (action == "load") await engine.LoadAsync(Track());
        else if (action == "dispose") engine.Dispose();
        else cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () => await startup.WaitAsync(TimeSpan.FromSeconds(10)));
        input.Release.TrySetResult();
        if (action == "dispose")
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await engine.PlayAsync());
            return;
        }
        if (action == "stop") await engine.LoadAsync(Track());
        if (action == "cancel") await engine.LoadAsync(Track());
        using Capture capture = new(NativePlayer(engine));
        await engine.SetVolumeAsync(1);
        await engine.PlayAsync();
        Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(1000));
        Assert.That(engine.Snapshot.Track!.Location, Is.EqualTo(new Uri(path)));
    }

    [Test]
    public async Task ResumeStartup_RepeatedFreshInputs_FirstPcmNeverContainsOpeningSamples()
    {
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track());
        using Capture capture = new(NativePlayer(engine));
        await engine.SetVolumeAsync(1);
        for (int iteration = 0; iteration < 16; iteration++)
        {
            await engine.StopAsync();
            capture.Reset();
            double seconds = iteration % 2 == 0 ? 5 : 5.25;
            await engine.SeekAsync(TimeSpan.FromSeconds(seconds));
            await engine.PlayAsync();
            int expected = 1000 + (int)(seconds * 1000);
            Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                Is.InRange((short)expected, (short)(expected + 5)), $"Fresh input {iteration}");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ResumeStartup_SeekAndPauseDuringOpening_UsesLatestTarget(bool pause)
    {
        await using BlockedInput input = new(File.ReadAllBytes(path));
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track() with { Location = input.Location });
        using Capture capture = new(NativePlayer(engine));
        await engine.SetVolumeAsync(1);
        await engine.SeekAsync(TimeSpan.FromSeconds(1));
        Task startup = engine.PlayAsync();
        await input.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await engine.SeekAsync(TimeSpan.FromSeconds(5.25));
        if (pause) await engine.PauseAsync();
        input.Release.TrySetResult();
        await startup.WaitAsync(TimeSpan.FromSeconds(10));
        if (pause)
        {
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Paused));
            Assert.That(capture.FirstSample.Task.IsCompleted, Is.False, "Pause during opening must retain the output gate.");
            await engine.PlayAsync();
        }
        Assert.That(await capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.InRange((short)6250, (short)6255));
    }

    [Test]
    public async Task ResumeStartup_JoinedCallerCancellation_DoesNotCancelOriginalInput()
    {
        await using BlockedInput input = new();
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track() with { Location = input.Location });
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        Task original = engine.PlayAsync();
        await input.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using CancellationTokenSource joinedCancellation = new();
        Task joined = engine.PlayAsync(joinedCancellation.Token);
        joinedCancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () => await joined);
        Assert.That(original.IsCompleted, Is.False);
        await engine.StopAsync();
        Assert.That(original.IsCompleted, Is.True, "Stop drains the invalidated startup outside the operation lock.");
        await Assert.CatchAsync<OperationCanceledException>(async () => await original);
    }

    [Test]
    public async Task ResumeStartup_UnexpectedHttpFailure_FaultsAndDoesNotPlayOpening()
    {
        await using BlockedInput input = new();
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track() with { Location = input.Location });
        using Capture capture = new(NativePlayer(engine));
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        Task startup = engine.PlayAsync();
        await input.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        input.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await startup.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Faulted));
        Assert.That(capture.FirstSample.Task.IsCompleted, Is.False);
    }

    [Test]
    public async Task ResumeStartup_HeadersNeverArrive_TimesOutStopsAndFaults()
    {
        await using BlockedInput input = new();
        using LibVlcPlaybackEngine engine = new(["--aout=dummy"]);
        await engine.LoadAsync(Track() with { Location = input.Location });
        using Capture capture = new(NativePlayer(engine));
        await engine.SeekAsync(TimeSpan.FromSeconds(5));
        Task startup = engine.PlayAsync();
        await input.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await startup.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Faulted));
        Assert.That(engine.Snapshot.Error, Does.Contain("ten seconds"));
        Assert.That(capture.FirstSample.Task.IsCompleted, Is.False);
    }

    private sealed class BlockedInput : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task server;
        private readonly byte[]? audio;
        public Uri Location { get; }
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockedInput(byte[]? audio = null)
        {
            this.audio = audio;
            listener.Start();
            Location = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/blocked.wav");
            server = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(shutdown.Token);
                    await using NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[4096];
                    int used = 0;
                    string request = "";
                    while (used < buffer.Length)
                    {
                        int count = await stream.ReadAsync(buffer.AsMemory(used), shutdown.Token);
                        if (count == 0) break;
                        used += count;
                        request = Encoding.ASCII.GetString(buffer, 0, used);
                        if (request.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    Requested.TrySetResult();
                    await Release.Task.WaitAsync(shutdown.Token);
                    if (audio is null)
                    {
                        await stream.WriteAsync("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), shutdown.Token);
                        continue;
                    }
                    string? range = request.Split("\r\n").FirstOrDefault(line => line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase));
                    int offset = range is null ? 0 : int.Parse(range[13..].Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);
                    offset = Math.Clamp(offset, 0, audio.Length);
                    string status = range is null ? "200 OK" : "206 Partial Content";
                    string contentRange = range is null ? "" : $"Content-Range: bytes {offset}-{audio.Length - 1}/{audio.Length}\r\n";
                    string headers = $"HTTP/1.1 {status}\r\nContent-Type: audio/wav\r\nAccept-Ranges: bytes\r\nContent-Length: {audio.Length - offset}\r\n{contentRange}Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), shutdown.Token);
                    await stream.WriteAsync(audio.AsMemory(offset), shutdown.Token);
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            shutdown.Dispose();
        }
    }
}
