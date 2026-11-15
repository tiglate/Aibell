using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aibell.Mpd.Client;
using Aibell.Mpd.Config;

namespace Aibell.Mpd.Tests;

/// <summary>
/// Runs a real <c>mpd --no-daemon</c> on its own port with a temporary configuration, never the user's daemon. It needs the <c>mpd</c>
/// program and <c>AIBELL_MPD_INTEGRATION_TESTS=1</c>. The configuration is written with <see cref="MpdConfigDocument"/>, so MPD
/// starting at all shows that a file the editor wrote is accepted.
/// </summary>
[TestFixture]
public sealed class MpdIntegrationTests
{
    private string directory = string.Empty;
    private Process? server;
    private int port;

    [SetUp]
    public async Task StartMpd()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_MPD_INTEGRATION_TESTS") != "1") Assert.Ignore("Set AIBELL_MPD_INTEGRATION_TESTS=1 to run against a real MPD.");
        string? program = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Select(folder => Path.Combine(folder, "mpd")).FirstOrDefault(File.Exists);
        if (program is null) Assert.Ignore("The mpd program is not installed.");

        directory = Directory.CreateTempSubdirectory("aibell-mpd-it-").FullName;
        string music = Path.Combine(directory, "music");
        Directory.CreateDirectory(Path.Combine(music, "Album"));
        Directory.CreateDirectory(Path.Combine(directory, "playlists"));
        File.WriteAllBytes(Path.Combine(music, "Album", "silence.wav"), Silence(1));
        File.WriteAllBytes(Path.Combine(music, "second.wav"), Silence(2));

        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        MpdConfigDocument config = MpdConfigDocument.CreateEmpty();
        config.Set("music_directory", music);
        config.Set("playlist_directory", Path.Combine(directory, "playlists"));
        config.Set("db_file", Path.Combine(directory, "database"));
        config.Set("log_file", Path.Combine(directory, "log"));
        config.Set("bind_to_address", "127.0.0.1");
        config.Set("port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        config.Set("zeroconf_enabled", "no");
        MpdConfigBlock output = config.AddBlock("audio_output");
        output.Set("type", "null");
        output.Set("name", "Test output");
        Assert.That(config.Validate(), Is.Empty);
        string path = Path.Combine(directory, "mpd.conf");
        MpdConfigFile.Save(config, path);

        server = Process.Start(new ProcessStartInfo(program, ["--no-daemon", "--stderr", path]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (server.HasExited) Assert.Fail("MPD exited: " + await server.StandardError.ReadToEndAsync());
            try
            {
                using TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }
        Assert.Fail("MPD did not start listening.");
    }

    [TearDown]
    public void StopMpd()
    {
        if (server is { HasExited: false }) { server.Kill(); server.WaitForExit(); }
        server?.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static byte[] Silence(int seconds)
    {
        int bytes = 44100 * 2 * seconds;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + bytes);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(44100);
        writer.Write(44100 * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(bytes);
        writer.Write(new byte[bytes]);
        return stream.ToArray();
    }

    [Test]
    public async Task TheClientDrivesARealServer()
    {
        await using MpdClient client = await MpdClient.ConnectAsync(new MpdEndpoint("127.0.0.1", port));
        await client.UpdateDatabaseAsync();
        for (int attempt = 0; attempt < 100 && (await client.GetStatusAsync()).IsUpdatingDatabase; attempt++) await Task.Delay(100);

        IReadOnlyList<MpdEntry> top = await client.ListFolderAsync();
        Assert.That(top.Select(entry => entry.Name), Is.EquivalentTo(new[] { "Album", "second.wav" }));
        Assert.That((await client.ListFolderAsync("Album")).Single().Kind, Is.EqualTo(MpdEntryKind.Song));
        Assert.That((await client.SearchAsync("silence")).Single().Uri, Is.EqualTo("Album/silence.wav"));

        await client.ClearAsync();
        await client.AddAsync("Album");
        int id = await client.AddIdAsync("second.wav");
        IReadOnlyList<MpdSong> queue = await client.GetQueueAsync();
        Assert.That(queue.Select(song => song.Uri), Is.EqualTo(new[] { "Album/silence.wav", "second.wav" }));
        Assert.That(queue[1].Id, Is.EqualTo(id));

        await client.SetRepeatAsync(true);
        await client.SetRandomAsync(true);
        await client.SetSingleAsync(MpdSingleMode.On);
        await client.SetConsumeAsync(true);
        await client.SetCrossfadeAsync(2);
        MpdStatus status = await client.GetStatusAsync();
        Assert.That((status.Repeat, status.Random, status.SingleMode, status.Consume, status.Crossfade), Is.EqualTo((true, true, MpdSingleMode.On, true, (TimeSpan?)TimeSpan.FromSeconds(2))));
        await client.SetRepeatAsync(false);
        await client.SetRandomAsync(false);
        await client.SetSingleAsync(MpdSingleMode.Off);
        await client.SetConsumeAsync(false);

        await client.PlayIdAsync(id);
        Assert.That((await client.GetStatusAsync()).State, Is.EqualTo(MpdState.Playing));
        Assert.That((await client.GetCurrentSongAsync())!.Uri, Is.EqualTo("second.wav"));
        await client.PauseAsync(true);
        Assert.That((await client.GetStatusAsync()).State, Is.EqualTo(MpdState.Paused));
        await client.SeekAsync(TimeSpan.FromSeconds(1));
        await client.StopAsync();
        Assert.That((await client.GetStatusAsync()).State, Is.EqualTo(MpdState.Stopped));

        IReadOnlyList<MpdOutput> outputs = await client.GetOutputsAsync();
        Assert.That(outputs.Single().Name, Is.EqualTo("Test output"));
        await client.SetOutputEnabledAsync(outputs[0].Id, false);
        Assert.That((await client.GetOutputsAsync()).Single().Enabled, Is.False);

        await client.SavePlaylistAsync("kept");
        Assert.That((await client.GetPlaylistsAsync()).Select(list => list.Name), Is.EqualTo(new[] { "kept" }));
        Assert.That((await client.GetPlaylistSongsAsync("kept")).Count, Is.EqualTo(2));
        await client.RenamePlaylistAsync("kept", "renamed");
        await client.RemoveFromPlaylistAsync("renamed", 0);
        await client.AddToPlaylistAsync("renamed", "Album/silence.wav");
        await client.ClearAsync();
        await client.LoadPlaylistAsync("renamed");
        Assert.That((await client.GetQueueAsync()).Count, Is.EqualTo(2));
        await client.DeletePlaylistAsync("renamed");
        Assert.That(await client.GetPlaylistsAsync(), Is.Empty);

        Assert.That(await client.ListTagAsync("album"), Is.Empty);
        Assert.That((await client.GetStatsAsync()).Songs, Is.EqualTo(2));
        Assert.That(await client.GetPictureAsync("second.wav"), Is.Null);
        MpdProtocolException? missing = await Assert.ThrowsAsync<MpdProtocolException>(() => client.GetPlaylistSongsAsync("nope"));
        Assert.That(missing!.Code, Is.EqualTo(MpdProtocolException.NoExist));
    }

    [Test]
    public async Task IdleReportsChangesFromAnotherConnection()
    {
        await using MpdConnection watcher = await MpdConnection.ConnectAsync(new MpdEndpoint("127.0.0.1", port));
        Task<IReadOnlyList<string>> waiting = watcher.IdleAsync(["playlist"]);
        await using MpdClient client = await MpdClient.ConnectAsync(new MpdEndpoint("127.0.0.1", port));
        await client.ClearAsync();
        await client.AddAsync("second.wav");
        Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(new[] { "playlist" }));
    }
}
