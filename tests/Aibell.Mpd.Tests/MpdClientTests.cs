using System.Text;
using Aibell.Mpd.Client;

namespace Aibell.Mpd.Tests;

[TestFixture]
public sealed class MpdClientTests
{
    private static byte[] Ok(string body = "") => FakeMpdServer.Text(body + "OK\n");

    private static async Task<MpdClient> Connect(FakeMpdServer server, string? password = null) =>
        await MpdClient.ConnectAsync(new MpdEndpoint("127.0.0.1", server.Port, password));

    [Test]
    public async Task Connect_ReadsTheGreetingAndSendsThePassword()
    {
        await using FakeMpdServer server = new(line => line == "password \"s3cret\"" ? Ok() : FakeMpdServer.Text("ACK [4@0] {} no\n"));
        await using MpdClient client = await Connect(server, "s3cret");
        Assert.That(client.ProtocolVersion, Is.EqualTo("0.23.0"));
        Assert.That(server.Received, Is.EqualTo(new[] { "password \"s3cret\"" }));
    }

    [Test]
    public async Task Connect_ReportsAWrongPasswordAndAWrongServer()
    {
        await Assert.CatchAsync<MpdProtocolException>(async () =>
        {
            await using FakeMpdServer server = new(_ => FakeMpdServer.Text("ACK [3@0] {password} incorrect password\n"));
            await Connect(server, "bad");
        });
        MpdProtocolException? wrong = await Assert.CatchAsync<MpdProtocolException>(async () =>
        {
            await using FakeMpdServer server = new(_ => Ok(), "HTTP/1.1 400 Bad Request which is a rather long line indeed\n");
            await Connect(server);
        })!;
        Assert.That(wrong!.Message, Does.Contain("not an MPD server"));
    }

    [Test]
    public async Task Status_ParsesEverythingAndTheCurrentSong()
    {
        await using FakeMpdServer server = new(line => line switch
        {
            "status" => Ok("volume: 70\nrepeat: 1\nrandom: 0\nsingle: oneshot\nconsume: 1\nplaylist: 12\nplaylistlength: 3\nstate: play\nsong: 1\nsongid: 9\nelapsed: 12.5\nduration: 200.25\nbitrate: 320\nxfade: 4\naudio: 44100:16:2\npartition: default\nupdating_db: 3\n"),
            "currentsong" => Ok("file: a/b.flac\nTitle: Song\nArtist: Band\nArtist: Guest\nAlbum: LP\nTime: 200\nduration: 200.250\nPos: 1\nId: 9\nLast-Modified: 2024-05-01T12:00:00Z\n"),
            "stats" => Ok("artists: 4\nalbums: 5\nsongs: 6\nuptime: 70\ndb_playtime: 3600\ndb_update: 1700000000\nplaytime: 10\n"),
            _ => FakeMpdServer.Text("ACK [5@0] {} unknown\n"),
        });
        await using MpdClient client = await Connect(server);
        MpdStatus status = await client.GetStatusAsync();
        Assert.That(status.Volume, Is.EqualTo(70));
        Assert.That(status, Has.Property("Repeat").True.And.Property("Random").False.And.Property("Consume").True);
        Assert.That(status.SingleMode, Is.EqualTo(MpdSingleMode.OneShot));
        Assert.That(status.State, Is.EqualTo(MpdState.Playing));
        Assert.That(status.Song, Is.EqualTo(1));
        Assert.That(status.SongId, Is.EqualTo(9));
        Assert.That(status.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(12.5)));
        Assert.That(status.Duration, Is.EqualTo(TimeSpan.FromSeconds(200.25)));
        Assert.That(status.Crossfade, Is.EqualTo(TimeSpan.FromSeconds(4)));
        Assert.That(status.Audio, Is.EqualTo("44100:16:2"));
        Assert.That(status.IsUpdatingDatabase, Is.True);
        Assert.That(status.PlaylistVersion, Is.EqualTo(12));
        MpdSong song = (await client.GetCurrentSongAsync())!;
        Assert.That(song.Uri, Is.EqualTo("a/b.flac"));
        Assert.That(song.Title, Is.EqualTo("Song"));
        Assert.That(song.Tags["artist"], Is.EqualTo(new[] { "Band", "Guest" }));
        Assert.That(song.Album, Is.EqualTo("LP"));
        Assert.That(song.AlbumArtist, Is.Null);
        Assert.That(song.Duration, Is.EqualTo(TimeSpan.FromSeconds(200.25)));
        Assert.That(song.Position, Is.EqualTo(1));
        Assert.That(song.Id, Is.EqualTo(9));
        Assert.That(song.LastModified, Is.EqualTo(new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero)));
        MpdStats stats = await client.GetStatsAsync();
        Assert.That(stats.Songs, Is.EqualTo(6));
        Assert.That(stats.Uptime, Is.EqualTo(TimeSpan.FromSeconds(70)));
        Assert.That(stats.DatabaseUpdate, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1700000000)));
    }

    [Test]
    public async Task EmptyStatusAndNoCurrentSong_UseDefaults()
    {
        await using FakeMpdServer server = new(line => line == "status" ? Ok("volume: -1\nstate: stop\n") : Ok());
        await using MpdClient client = await Connect(server);
        MpdStatus status = await client.GetStatusAsync();
        Assert.That(status.Volume, Is.Null);
        Assert.That(status.State, Is.EqualTo(MpdState.Stopped));
        Assert.That(status.SingleMode, Is.EqualTo(MpdSingleMode.Off));
        Assert.That(status.Elapsed, Is.Null);
        Assert.That(await client.GetCurrentSongAsync(), Is.Null);
    }

    [Test]
    public async Task Commands_AreSentWithQuotedArguments()
    {
        await using FakeMpdServer server = new(_ => Ok("Id: 77\nupdating_db: 5\n"));
        await using MpdClient client = await Connect(server);
        await client.PlayAsync();
        await client.PlayAsync(3);
        await client.PlayIdAsync(8);
        await client.PauseAsync(true);
        await client.StopAsync();
        await client.NextAsync();
        await client.PreviousAsync();
        await client.SeekAsync(TimeSpan.FromSeconds(61.5));
        await client.SetVolumeAsync(250);
        await client.SetRepeatAsync(true);
        await client.SetRandomAsync(false);
        await client.SetConsumeAsync(true);
        await client.SetSingleAsync(MpdSingleMode.OneShot);
        await client.SetSingleAsync(MpdSingleMode.On);
        await client.SetSingleAsync(MpdSingleMode.Off);
        await client.SetCrossfadeAsync(-3);
        await client.AddAsync("dir/a \"b\".mp3");
        int id = await client.AddIdAsync("x.mp3");
        int positioned = await client.AddIdAsync("y.mp3", 2);
        await client.DeleteAsync(4);
        await client.DeleteIdAsync(5);
        await client.ClearAsync();
        await client.MoveAsync(1, 2);
        await client.LoadPlaylistAsync("mix");
        await client.SavePlaylistAsync("new");
        await client.DeletePlaylistAsync("old");
        await client.RenamePlaylistAsync("a", "b");
        await client.AddToPlaylistAsync("a", "u.mp3");
        await client.RemoveFromPlaylistAsync("a", 0);
        await client.SetOutputEnabledAsync(1, true);
        await client.SetOutputEnabledAsync(2, false);
        await client.PingAsync();
        int job = await client.UpdateDatabaseAsync();
        int folderJob = await client.UpdateDatabaseAsync("rock");
        Assert.That(new[] { id, positioned, job, folderJob }, Is.EqualTo(new[] { 77, 77, 5, 5 }));
        Assert.That(server.Received, Is.EqualTo(new[]
        {
            "play", "play 3", "playid 8", "pause 1", "stop", "next", "previous", "seekcur 61.5", "setvol 100", "repeat 1", "random 0", "consume 1",
            "single oneshot", "single 1", "single 0", "crossfade 0", "add \"dir/a \\\"b\\\".mp3\"", "addid \"x.mp3\"", "addid \"y.mp3\" 2", "delete 4",
            "deleteid 5", "clear", "move 1 2", "load \"mix\"", "save \"new\"", "rm \"old\"", "rename \"a\" \"b\"", "playlistadd \"a\" \"u.mp3\"",
            "playlistdelete \"a\" 0", "enableoutput 1", "disableoutput 2", "ping", "update", "update \"rock\"",
        }));
    }

    [Test]
    public async Task Queue_PlaylistsFoldersOutputsAndSearch_AreParsed()
    {
        await using FakeMpdServer server = new(line => line switch
        {
            "playlistinfo" => Ok("file: a.mp3\nTitle: A\nPos: 0\nId: 1\nfile: b.mp3\nPos: 1\nId: 2\nTime: 61\n"),
            "listplaylists" => Ok("playlist: Mix\nLast-Modified: 2024-01-02T03:04:05Z\nplaylist: Plain\n"),
            "listplaylistinfo \"Mix\"" => Ok("file: a.mp3\nfile: b.mp3\n"),
            "lsinfo" => Ok("directory: Rock\nLast-Modified: 2024-01-02T03:04:05Z\nfile: top.mp3\nTitle: Top\nplaylist: list.m3u\n"),
            "lsinfo \"Rock/Sub\"" => Ok("file: Rock/Sub/x.flac\nTitle: X\n"),
            "outputs" => Ok("outputid: 0\noutputname: Speakers\nplugin: alsa\noutputenabled: 1\noutputid: 1\noutputname: Stream\nplugin: httpd\noutputenabled: 0\n"),
            "search \"(any contains 'it\\\\'s \\\\\\\\ here')\"" => Ok("file: found.mp3\nArtist: Z\n"),
            "search \"(file contains 'it\\\\'s \\\\\\\\ here')\"" => Ok("file: found.mp3\nfile: by-name.mp3\n"),
            "search \"(Artist contains 'z')\"" => Ok("file: z.mp3\n"),
            "list \"artist\"" => Ok("Artist: One\nArtist: \nArtist: Two\n"),
            _ => FakeMpdServer.Text("ACK [50@0] {} nothing\n"),
        });
        await using MpdClient client = await Connect(server);
        IReadOnlyList<MpdSong> queue = await client.GetQueueAsync();
        Assert.That(queue.Select(song => song.DisplayTitle), Is.EqualTo(new[] { "A", "b.mp3" }));
        Assert.That(queue[1].Duration, Is.EqualTo(TimeSpan.FromSeconds(61)));
        IReadOnlyList<MpdStoredPlaylist> lists = await client.GetPlaylistsAsync();
        Assert.That(lists.Select(list => list.Name), Is.EqualTo(new[] { "Mix", "Plain" }));
        Assert.That(lists[0].LastModified, Is.Not.Null);
        Assert.That(lists[1].LastModified, Is.Null);
        Assert.That((await client.GetPlaylistSongsAsync("Mix")).Count, Is.EqualTo(2));
        IReadOnlyList<MpdEntry> top = await client.ListFolderAsync();
        Assert.That(top.Select(entry => entry.Kind), Is.EqualTo(new[] { MpdEntryKind.Directory, MpdEntryKind.Song, MpdEntryKind.Playlist }));
        Assert.That(top[0].Name, Is.EqualTo("Rock"));
        Assert.That(top[1].Song!.Title, Is.EqualTo("Top"));
        Assert.That((await client.ListFolderAsync("Rock/Sub"))[0].Name, Is.EqualTo("x.flac"));
        IReadOnlyList<MpdOutput> outputs = await client.GetOutputsAsync();
        Assert.That(outputs.Select(output => (output.Id, output.Name, output.Enabled, output.Plugin)), Is.EqualTo(new[] { (0, "Speakers", true, (string?)"alsa"), (1, "Stream", false, "httpd") }));
        IReadOnlyList<MpdSong> merged = await client.SearchAsync("it's \\ here");
        Assert.That(merged.Select(song => song.Uri), Is.EqualTo(new[] { "found.mp3", "by-name.mp3" }));
        Assert.That(merged[0].Artist, Is.EqualTo("Z"));
        Assert.That((await client.SearchAsync("z", "Artist")).Single().Uri, Is.EqualTo("z.mp3"));
        Assert.That(await client.ListTagAsync("artist"), Is.EqualTo(new[] { "One", "Two" }));
        MpdProtocolException? error = await Assert.ThrowsAsync<MpdProtocolException>(() => client.GetPlaylistSongsAsync("none"))!;
        Assert.That(error!.Code, Is.EqualTo(MpdProtocolException.NoExist));

    }

    [Test]
    public async Task Picture_IsReadInChunksAndFallsBackToEmbeddedArt()
    {
        byte[] image = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();
        await using FakeMpdServer server = new(line => line switch
        {
            "albumart \"cover.mp3\" 0" => FakeMpdServer.Binary("size: 20\n", image[..8]),
            "albumart \"cover.mp3\" 8" => FakeMpdServer.Binary("size: 20\n", image[8..16]),
            "albumart \"cover.mp3\" 16" => FakeMpdServer.Binary("size: 20\n", image[16..]),
            "albumart \"embedded.mp3\" 0" => FakeMpdServer.Text("ACK [50@0] {albumart} No file exists\n"),
            "readpicture \"embedded.mp3\" 0" => FakeMpdServer.Binary("size: 3\ntype: image/png\n", [1, 2, 3]),
            "albumart \"none.mp3\" 0" => FakeMpdServer.Text("ACK [50@0] {albumart} No file exists\n"),
            "readpicture \"none.mp3\" 0" => Ok(),
            "albumart \"huge.mp3\" 0" => FakeMpdServer.Binary("size: 99999999\n", [1]),
            "readpicture \"huge.mp3\" 0" => FakeMpdServer.Text("ACK [5@0] {readpicture} unknown command\n"),
            _ => FakeMpdServer.Text("ACK [50@0] {} nothing\n"),
        });
        await using MpdClient client = await Connect(server);
        Assert.That((await client.GetPictureAsync("cover.mp3"))!.Data, Is.EqualTo(image));
        MpdPicture embedded = (await client.GetPictureAsync("embedded.mp3"))!;
        Assert.That(embedded.Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(embedded.MimeType, Is.EqualTo("image/png"));
        Assert.That(await client.GetPictureAsync("none.mp3"), Is.Null);
        Assert.That(await client.GetPictureAsync("huge.mp3"), Is.Null);
    }

    [Test]
    public async Task Idle_ReturnsChangedSubsystemsAndEndsCleanlyWhenCancelled()
    {
        TaskCompletionSource noidle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeMpdServer server = new(line =>
        {
            if (line == "idle \"player\" \"mixer\"") return FakeMpdServer.Text("changed: player\nchanged: mixer\nOK\n");
            if (line == "idle") return Array.Empty<byte>();
            if (line == "noidle") { noidle.TrySetResult(); return Ok(); }
            return Ok();
        });
        await using MpdConnection connection = await MpdConnection.ConnectAsync(new MpdEndpoint("127.0.0.1", server.Port));
        Assert.That(await connection.IdleAsync(["player", "mixer"]), Is.EqualTo(new[] { "player", "mixer" }));
        using CancellationTokenSource cancel = new();
        Task<IReadOnlyList<string>> waiting = connection.IdleAsync(null, cancel.Token);
        await Task.Delay(50);
        await cancel.CancelAsync();
        Assert.That(await waiting, Is.Empty);
        await noidle.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Connection_ReportsAClosedServerAndRejectsUseAfterDispose()
    {
        await using FakeMpdServer server = new(line => line == "close" ? null : Ok());
        MpdConnection connection = await MpdConnection.ConnectAsync(new MpdEndpoint("127.0.0.1", server.Port));
        await Assert.ThrowsAsync<EndOfStreamException>(() => connection.SendAsync("close"));
        await connection.DisposeAsync();
        await connection.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.SendAsync("ping"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.IdleAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => connection.SendAsync(""));
        Assert.Throws<ArgumentNullException>(() => new MpdClient(null!));
    }

    [Test]
    public async Task Connection_HandlesLongLinesAndMalformedBinary()
    {
        string longValue = new('x', 40000);
        await using FakeMpdServer server = new(line => line switch
        {
            "long" => Ok($"Title: {longValue}\n"),
            "badbinary" => FakeMpdServer.Text("binary: 2\nabXOK\n"),
            _ => Ok(),
        });
        await using MpdConnection connection = await MpdConnection.ConnectAsync(new MpdEndpoint("127.0.0.1", server.Port));
        Assert.That((await connection.SendAsync("long")).First("Title"), Has.Length.EqualTo(40000));
        await Assert.ThrowsAsync<MpdProtocolException>(() => connection.SendAsync("badbinary"));
    }

    [Test]
    public async Task Connection_RejectsOversizeBinaryAndLines()
    {
        await using FakeMpdServer big = new(_ => FakeMpdServer.Text("binary: 999999999\n"));
        await using MpdConnection one = await MpdConnection.ConnectAsync(new MpdEndpoint("127.0.0.1", big.Port));
        await Assert.ThrowsAsync<MpdProtocolException>(() => one.SendAsync("x"));
        await using FakeMpdServer overlong = new(_ => FakeMpdServer.Text(new string('y', 2_000_000)));
        await using MpdConnection two = await MpdConnection.ConnectAsync(new MpdEndpoint("127.0.0.1", overlong.Port));
        await Assert.ThrowsAsync<MpdProtocolException>(() => two.SendAsync("x"));
        await using FakeMpdServer noise = new(_ => FakeMpdServer.Text("weird line\nkey:\nlist_OK\nOK\n"));
        await using MpdConnection three = await MpdConnection.ConnectAsync(new MpdEndpoint("127.0.0.1", noise.Port));
        Assert.That((await three.SendAsync("x")).Pairs.Single().Key, Is.EqualTo("key"));
    }

    [Test]
    public async Task Connect_OverALocalSocket_Works()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix sockets are exercised on Linux.");
        string path = Path.Combine(Path.GetTempPath(), $"aibell-mpd-{Guid.NewGuid():N}.sock");
        using System.Net.Sockets.Socket listener = new(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            Task serve = Task.Run(async () =>
            {
                using System.Net.Sockets.Socket peer = await listener.AcceptAsync();
                await peer.SendAsync(Encoding.UTF8.GetBytes("OK MPD 0.22.0\n"));
            });
            await using MpdClient client = await MpdClient.ConnectAsync(new MpdEndpoint(path));
            Assert.That(client.ProtocolVersion, Is.EqualTo("0.22.0"));
            await serve;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Connect_FailsWhenNothingListens()
    {
        System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await Assert.CatchAsync<System.Net.Sockets.SocketException>(() => MpdClient.ConnectAsync(new MpdEndpoint("127.0.0.1", port)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => MpdConnection.ConnectAsync(null!));
    }

    [Test]
    public void Command_QuotesAndFormats()
    {
        Assert.That(MpdCommand.Format("x", 1, 2L, 1.5, true, TimeSpan.FromSeconds(2), "a b", DayOfWeek.Monday), Is.EqualTo("x 1 2 1.5 1 2 \"a b\" \"Monday\""));
        Assert.That(MpdCommand.Quote("a\\b\"c"), Is.EqualTo("\"a\\\\b\\\"c\""));
        Assert.Throws<ArgumentException>(() => MpdCommand.Quote("a\nb"));
        Assert.Throws<ArgumentException>(() => MpdCommand.Format("x", new object?[] { null }));
        Assert.Throws<ArgumentException>(() => MpdCommand.Format(""));
        Assert.Throws<ArgumentException>(() => MpdCommand.ContainsFilter("", "a"));
        Assert.That(MpdCommand.ContainsFilter("Artist", "a'b"), Is.EqualTo("(Artist contains 'a\\'b')"));
    }

    [Test]
    public void Exception_ParsesAckLines()
    {
        MpdProtocolException error = MpdProtocolException.Parse("ACK [50@2] {play} song doesn't exist: \"10240\"");
        Assert.That((error.Code, error.ListIndex, error.Command, error.Message), Is.EqualTo((50, 2, "play", "song doesn't exist: \"10240\"")));
        Assert.That(MpdProtocolException.Parse("ACK [5@0] no command").Message, Is.EqualTo("no command"));
        Assert.That(MpdProtocolException.Parse("ACK [x@y] {a} b").Message, Is.EqualTo("[x@y] {a} b"));
        Assert.That(MpdProtocolException.Parse("garbage").Message, Is.EqualTo("garbage"));
        Assert.Throws<ArgumentNullException>(() => MpdProtocolException.Parse(null!));
        Assert.That(new MpdProtocolException().Message, Is.Not.Empty);
        Assert.That(new MpdProtocolException("m", new InvalidOperationException()).InnerException, Is.Not.Null);
    }

    [Test]
    public void Models_DescribeThemselves()
    {
        Assert.That(new MpdEntry(MpdEntryKind.Song, "a/b/c.mp3").Name, Is.EqualTo("c.mp3"));
        Assert.That(new MpdEntry(MpdEntryKind.Directory, "top").Name, Is.EqualTo("top"));
        MpdSong plain = new("dir\\file.ogg", new Dictionary<string, IReadOnlyList<string>>());
        Assert.That(plain.DisplayTitle, Is.EqualTo("file.ogg"));
        Assert.That(plain.Tag("x"), Is.Null);
        Assert.That(MpdResponse.Empty.First("x"), Is.Null);
    }

    [Test]
    public void Parser_RejectsNullAndToleratesGarbage()
    {
        Assert.Throws<ArgumentNullException>(() => MpdParser.ParseStatus(null!));
        Assert.Throws<ArgumentNullException>(() => MpdParser.ParseStats(null!));
        Assert.Throws<ArgumentNullException>(() => MpdParser.ParseSongs(null!));
        Assert.Throws<ArgumentNullException>(() => MpdParser.ParseEntries(null!));
        Assert.Throws<ArgumentNullException>(() => MpdParser.ParsePlaylists(null!));
        Assert.Throws<ArgumentNullException>(() => MpdParser.ParseOutputs(null!));
        MpdStatus status = MpdParser.ParseStatus([new("volume", "abc"), new("elapsed", "-3"), new("song", "x")]);
        Assert.That((status.Volume, status.Elapsed, status.Song), Is.EqualTo(((int?)null, (TimeSpan?)null, (int?)null)));
        Assert.That(MpdParser.ParseStats([]).Songs, Is.Zero);
        Assert.That(MpdParser.ParseSongs([new("Title", "orphan")]), Is.Empty);
    }
}
