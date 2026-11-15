using Aibell.Mpd.Client;
using Aibell.Mpd.Config;

namespace Aibell.Mpd.Tests;

[TestFixture]
public sealed class MpdSupportTests
{
    private const string VersionText = """
        Music Player Daemon 0.23.14 (0.23.14)
        Copyright 2003-2007 Warren Dukes <warren.dukes@gmail.com>

        Decoders plugins:
         [vorbis] ogg oga
         [flac] flac
         [ffmpeg] aac ac3 rtp:// rtsp://
         [pcm]

        Filters:
         libsamplerate soxr

        Output plugins:
         shout null fifo alsa pipewire pulse httpd

        Encoder plugins:
         null vorbis opus

        Playlist plugins:
         extm3u m3u pls

        Protocols:
         file:// http://
        """;

    [Test]
    public void VersionInfo_ReadsTheVersionAndWhichPluginsExist()
    {
        MpdVersionInfo info = MpdVersionInfo.Parse(VersionText);
        Assert.That(info.Version, Is.EqualTo("0.23.14"));
        Assert.That(info.Supports("audio_output", "alsa"), Is.True);
        Assert.That(info.Supports("audio_output", "jack"), Is.False);
        Assert.That(info.Supports("playlist_plugin", "m3u"), Is.True);
        Assert.That(info.Supports("playlist_plugin", "xspf"), Is.False);
        Assert.That(info.Supports("resampler", "soxr"), Is.True);
        Assert.That(info.Supports("resampler", "internal"), Is.True, "the internal resampler is always built in");
        Assert.That(info.Supports("decoder", "flac"), Is.True);
        Assert.That(info.Supports("decoder", "wavpack"), Is.False);
        Assert.That(info.Supports("database", "simple"), Is.True, "kinds the program does not list are never ruled out");
    }

    [Test]
    public void VersionInfo_ListsDecodersByTheirBracketedNames()
    {
        MpdVersionInfo info = MpdVersionInfo.Parse("Music Player Daemon 0.23.0\n\nDecoders plugins:\n [vorbis] ogg oga\n [pcm]\n [ffmpeg] aac rtp://\n");
        Assert.That(info.Supports("decoder", "vorbis"), Is.True);
        Assert.That(info.Supports("decoder", "mad"), Is.False);
        Assert.That(MpdVersionInfo.Parse("").Version, Is.Empty);
        Assert.That(MpdVersionInfo.Parse("").Supports("audio_output", "alsa"), Is.True);
        Assert.Throws<ArgumentNullException>(() => MpdVersionInfo.Parse(null!));
    }

    [Test]
    public async Task VersionInfo_Discover_ReturnsNullWhenTheProgramIsMissing() =>
        Assert.That(await MpdVersionInfo.DiscoverAsync("/nonexistent/aibell-mpd"), Is.Null);

    [Test]
    public void ConnectionSettings_RoundTripAndFallBackToDefaults()
    {
        string directory = Directory.CreateTempSubdirectory("aibell-mpd-settings-").FullName;
        try
        {
            string path = Path.Combine(directory, "sub", "mpd-connection.json");
            Assert.That(MpdConnectionSettings.Load(path), Is.EqualTo(MpdConnectionSettings.Default));
            MpdConnectionSettings custom = new(false, "music.lan", 6601);
            custom.Save(path);
            Assert.That(MpdConnectionSettings.Load(path), Is.EqualTo(custom));
            File.WriteAllText(path, "{ not json");
            Assert.That(MpdConnectionSettings.Load(path), Is.EqualTo(MpdConnectionSettings.Default));
            File.WriteAllText(path, "{\"Automatic\":false,\"Host\":\"\",\"Port\":0}");
            Assert.That(MpdConnectionSettings.Load(path), Is.EqualTo(MpdConnectionSettings.Default));
            File.WriteAllText(path, "null");
            Assert.That(MpdConnectionSettings.Load(path), Is.EqualTo(MpdConnectionSettings.Default));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ServiceController_ChecksAndRestartsOnlyThroughTheRunner()
    {
        List<string> calls = [];
        int exit = 0;
        string output = "";
        MpdServiceController controller = new((arguments, _) =>
        {
            calls.Add(string.Join(' ', arguments));
            return Task.FromResult(new MpdProcessResult(exit, output));
        });
        if (OperatingSystem.IsLinux()) Assert.That(await controller.IsUserServiceAvailableAsync(), Is.True);
        Assert.That(await controller.RestartAsync(), Is.Empty);
        exit = 5;
        output = "Failed to restart mpd.service\n";
        Assert.That(await controller.RestartAsync(), Is.EqualTo("Failed to restart mpd.service"));
        output = "";
        Assert.That(await controller.RestartAsync(), Does.Contain("code 5"));
        if (OperatingSystem.IsLinux()) Assert.That(await controller.IsUserServiceAvailableAsync(), Is.False);
        Assert.That(calls, Does.Contain("--user restart mpd.service"));
        MpdServiceController failing = new((_, _) => throw new System.ComponentModel.Win32Exception("no systemctl"));
        Assert.That(await failing.IsUserServiceAvailableAsync(), Is.False);
    }

    [Test]
    public async Task IdleWatcher_ReportsChangesUntilCancelled()
    {
        int sent = 0;
        await using FakeMpdServer server = new(line =>
        {
            if (line.StartsWith("idle", StringComparison.Ordinal))
            {
                return Interlocked.Increment(ref sent) == 1 ? FakeMpdServer.Text("changed: player\nOK\n") : Array.Empty<byte>();
            }
            return line == "noidle" ? FakeMpdServer.Text("OK\n") : FakeMpdServer.Text("OK\n");
        });
        MpdIdleWatcher watcher = new(new MpdEndpoint("127.0.0.1", server.Port));
        TaskCompletionSource<IReadOnlyList<string>> got = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancel = new();
        Task run = watcher.WatchAsync(changed => { got.TrySetResult(changed); return Task.CompletedTask; }, cancel.Token);
        Assert.That(await got.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(new[] { "player" }));
        await cancel.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThatAsync(() => watcher.WatchAsync(null!, CancellationToken.None), Throws.TypeOf<ArgumentNullException>());
    }
}
