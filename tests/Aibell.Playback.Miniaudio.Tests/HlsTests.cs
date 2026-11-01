using System.Text;
using Aibell.Application.Playback;
using Aibell.Playback.Core;
using NUnit.Framework;
using static Aibell.Playback.Miniaudio.Tests.EngineTestSupport;

namespace Aibell.Playback.Miniaudio.Tests;

[TestFixture]
public sealed class HlsPlaylistParserTests
{
    private static readonly Uri Base = new("https://radio.example.invalid/live/index.m3u8");

    private static HlsMediaPlaylist Media(string text) => (HlsMediaPlaylist)HlsPlaylistParser.Parse(text, Base);

    [Test]
    public void Parse_MediaPlaylist_ResolvesRelativeUrisAndNumbersSegments()
    {
        HlsMediaPlaylist media = Media("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:100\n" +
            "#EXTINF:5.9,title\nseg100.mp3\n#EXTINF:6.0,\n../other/seg101.mp3?token=a&b=2\n#EXTINF:6,\nhttps://cdn.example.invalid/seg102.mp3\n");

        Assert.Multiple(() =>
        {
            Assert.That(media.MediaSequence, Is.EqualTo(100));
            Assert.That(media.TargetDuration, Is.EqualTo(6));
            Assert.That(media.EndList, Is.False);
            Assert.That(media.Segments.Select(segment => segment.Sequence), Is.EqualTo(new long[] { 100, 101, 102 }));
            Assert.That(media.Segments[0].Uri.AbsoluteUri, Is.EqualTo("https://radio.example.invalid/live/seg100.mp3"));
            Assert.That(media.Segments[1].Uri.AbsoluteUri, Is.EqualTo("https://radio.example.invalid/other/seg101.mp3?token=a&b=2"));
            Assert.That(media.Segments[2].Uri.Host, Is.EqualTo("cdn.example.invalid"));
        });
    }

    [Test]
    public void Parse_EndListAndMissingTargetDuration_IsFinishedWithDerivedDuration()
    {
        HlsMediaPlaylist media = Media("﻿#EXTM3U\r\n#EXTINF:4.2,\r\na.mp3\r\n#EXT-X-ENDLIST\r\n");
        Assert.Multiple(() =>
        {
            Assert.That(media.EndList, Is.True);
            Assert.That(media.TargetDuration, Is.EqualTo(5));
            Assert.That(media.MediaSequence, Is.Zero);
        });
    }

    [Test]
    public void ChooseVariant_PrefersAudioOnlyMp3ThenLowestBandwidth()
    {
        HlsMasterPlaylist master = (HlsMasterPlaylist)HlsPlaylistParser.Parse(
            "#EXTM3U\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"avc1.4d401f,mp4a.40.2\",RESOLUTION=320x180\nvideo-low.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=192000,CODECS=\"mp4a.40.2\"\naac-high.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.40.34\"\nmp3-128.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=96000,CODECS=\"mp4a.40.34\"\nmp3-96.m3u8\n", Base);

        Assert.That(HlsPlaylistParser.ChooseVariant(master).Uri.AbsolutePath, Is.EqualTo("/live/mp3-96.m3u8"));
    }

    [Test]
    public void ChooseVariant_NoAudioOnlyVariant_UsesAnAudioRenditionThenLowestBandwidth()
    {
        HlsMasterPlaylist withRendition = (HlsMasterPlaylist)HlsPlaylistParser.Parse(
            "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"Main, English\",URI=\"audio.m3u8\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=1280x720\nv720.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=640x360\nv360.m3u8\n", Base);
        HlsMasterPlaylist videoOnly = (HlsMasterPlaylist)HlsPlaylistParser.Parse(
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=1280x720\nv720.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=640x360\nv360.m3u8\n", Base);

        Assert.Multiple(() =>
        {
            Assert.That(HlsPlaylistParser.ChooseVariant(withRendition).Uri.AbsolutePath, Is.EqualTo("/live/audio.m3u8"));
            Assert.That(HlsPlaylistParser.ChooseVariant(videoOnly).Uri.AbsolutePath, Is.EqualTo("/live/v360.m3u8"));
        });
    }

    [TestCase("#EXTINF:5,\na.mp3\n", Description = "missing header")]
    [TestCase("#EXTM3U\n#EXTINF:abc,\na.mp3\n", Description = "bad duration")]
    [TestCase("#EXTM3U\n#EXTINF:-1,\na.mp3\n", Description = "negative duration")]
    [TestCase("#EXTM3U\n#EXT-X-TARGETDURATION:0\n#EXTINF:5,\na.mp3\n", Description = "zero target")]
    [TestCase("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:-4\n#EXTINF:5,\na.mp3\n", Description = "negative sequence")]
    [TestCase("#EXTM3U\n#EXT-X-TARGETDURATION:6\n", Description = "no segments")]
    [TestCase("#EXTM3U\nstray.mp3\n", Description = "uri without tag")]
    [TestCase("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv.m3u8\n#EXTINF:5,\na.mp3\n", Description = "mixed")]
    [TestCase("#EXTM3U\n#EXT-X-STREAM-INF:CODECS=\"unterminated\nv.m3u8\n", Description = "bad attribute")]
    [TestCase("#EXTM3U\n#EXTINF:5,\nftp://example.invalid/a.mp3\n", Description = "foreign scheme")]
    [TestCase("#EXTM3U\n#EXTINF:5,\nhttps://user:pw@example.invalid/a.mp3\n", Description = "credentials")]
    [TestCase("#EXTM3U\n#EXTINF:5,\nhttp://example.invalid/a.mp3\n", Description = "https downgrade")]
    public void Parse_MalformedOrUnsafe_Throws(string text) =>
        Assert.That(() => HlsPlaylistParser.Parse(text, Base), Throws.TypeOf<InvalidDataException>());

    [TestCase("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXTINF:5,\na.ts\n")]
    [TestCase("#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:5,\na.m4s\n")]
    [TestCase("#EXTM3U\n#EXTINF:5,\n#EXT-X-BYTERANGE:100@0\na.mp3\n")]
    public void Parse_EncryptedFragmentedOrByteRanged_SaysToChooseVlc(string text) =>
        Assert.That(() => HlsPlaylistParser.Parse(text, Base), Throws.TypeOf<NotSupportedException>().With.Message.Contains("VLC"));

    [Test]
    public void Parse_UnencryptedKeyTag_IsAccepted() =>
        Assert.That(Media("#EXTM3U\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:5,\na.mp3\n").Segments, Has.Count.EqualTo(1));

    [Test]
    public void Parse_TooManySegmentsOrLines_Throws()
    {
        StringBuilder text = new("#EXTM3U\n#EXT-X-TARGETDURATION:1\n");
        for (int i = 0; i <= HlsPlaylistParser.MaximumSegments; i++) text.Append("#EXTINF:1,\ns").Append(i).Append(".mp3\n");
        Assert.That(() => HlsPlaylistParser.Parse(text.ToString(), Base), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => HlsPlaylistParser.Parse("#EXTM3U\n" + new string('\n', HlsPlaylistParser.MaximumLines), Base), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Parse_HttpBaseMayReferenceHttps()
    {
        HlsMediaPlaylist media = (HlsMediaPlaylist)HlsPlaylistParser.Parse("#EXTM3U\n#EXTINF:5,\nhttps://cdn.example.invalid/a.mp3\n", new Uri("http://radio.example.invalid/x.m3u8"));
        Assert.That(media.Segments[0].Uri.Scheme, Is.EqualTo("https"));
    }

    [Test]
    public void ValidateAddress_FragmentIsDroppedAndHttpsRedirectToHttpIsRefused()
    {
        Assert.That(HlsPlaylistParser.ValidateAddress(new Uri("https://a.example.invalid/x.mp3?q=1#frag"), Base).AbsoluteUri, Is.EqualTo("https://a.example.invalid/x.mp3?q=1"));
        Assert.That(() => HlsPlaylistParser.ValidateAddress(new Uri("http://a.example.invalid/x.mp3"), Base), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void SniffSegment_RecognizesTheFormatsThatDecideSupport()
    {
        byte[] mp3 = File.ReadAllBytes(Fixture("fixture.mp3"));
        byte[] id3 = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4, 5, 0xff, 0xfb, 0x90, 0x00];
        byte[] adts = [0xff, 0xf1, 0x50, 0x80, 0, 0, 0, 0];
        byte[] ts = new byte[400];
        ts[0] = 0x47;
        ts[188] = 0x47;
        byte[] mp4 = [0, 0, 0, 24, .. "ftypiso5"u8, 0, 0, 0, 0];

        Assert.Multiple(() =>
        {
            Assert.That(HlsAudioStream.SniffSegment(mp3), Is.EqualTo(HlsSegmentKind.MpegAudio));
            Assert.That(HlsAudioStream.SniffSegment(id3), Is.EqualTo(HlsSegmentKind.MpegAudio));
            Assert.That(HlsAudioStream.SniffSegment(adts), Is.EqualTo(HlsSegmentKind.Aac));
            Assert.That(HlsAudioStream.SniffSegment([.. id3[..15], .. adts]), Is.EqualTo(HlsSegmentKind.Aac));
            Assert.That(HlsAudioStream.SniffSegment(ts), Is.EqualTo(HlsSegmentKind.MpegTransportStream));
            Assert.That(HlsAudioStream.SniffSegment(mp4), Is.EqualTo(HlsSegmentKind.Mp4));
            Assert.That(HlsAudioStream.SniffSegment("OggS"u8), Is.EqualTo(HlsSegmentKind.Unknown));
            Assert.That(HlsAudioStream.SniffSegment([]), Is.EqualTo(HlsSegmentKind.Unknown));
        });
    }
}

[TestFixture]
public sealed class HlsAudioStreamTests
{
    private static readonly HlsOptions Fast = new()
    {
        MinimumReload = TimeSpan.FromMilliseconds(10),
        MaximumReload = TimeSpan.FromMilliseconds(40),
        StallTimeout = TimeSpan.FromMilliseconds(400),
        RequestTimeout = TimeSpan.FromSeconds(5)
    };

    private HttpClient client = null!;
    private HlsTestServer server = null!;

    [SetUp]
    public void SetUp()
    {
        client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        server = new HlsTestServer();
    }

    [TearDown]
    public void TearDown()
    {
        server.Dispose();
        client.Dispose();
    }

    // Segments here are opaque bytes the stream must pass through unchanged; they start with a byte no sniffer treats as a format.
    private static byte[] Chunk(int number) => [0xaa, (byte)number, .. Enumerable.Repeat((byte)number, 300)];

    private static string Playlist(long sequence, int[] segments, bool end, int target = 1, string prefix = "")
    {
        StringBuilder text = new($"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:{target}\n#EXT-X-MEDIA-SEQUENCE:{sequence}\n");
        foreach (int number in segments) text.Append("#EXTINF:1.0,\n").Append(prefix).Append("seg").Append(number).Append(".bin\n");
        if (end) text.Append("#EXT-X-ENDLIST\n");
        return text.ToString();
    }

    private void Segments(string directory, params int[] numbers)
    {
        foreach (int number in numbers) server.Map($"{directory}seg{number}.bin", Chunk(number));
    }

    private static byte[] ReadAll(Stream stream)
    {
        // Stream.CopyTo asks for a length, which a live stream does not have.
        using MemoryStream all = new();
        byte[] buffer = new byte[1000];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) all.Write(buffer, 0, read);
        return all.ToArray();
    }

    private static byte[] Expected(params int[] numbers) => numbers.SelectMany(Chunk).ToArray();

    [Test]
    public async Task Read_VodMediaPlaylist_ReturnsTheConcatenatedSegmentsAndEnds()
    {
        server.Map("/vod/index.m3u8", Playlist(0, [0, 1, 2], end: true));
        Segments("/vod/", 0, 1, 2);

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("vod/index.m3u8"), CancellationToken.None, Fast);

        Assert.Multiple(() =>
        {
            Assert.That(stream.IsLive, Is.False);
            Assert.That(ReadAll(stream), Is.EqualTo(Expected(0, 1, 2)));
            Assert.That(stream.Read(new byte[16]), Is.Zero, "still at the end");
        });
    }

    [Test]
    public async Task Read_AbsoluteAndParentRelativeSegmentUrls_AreResolved()
    {
        server.Map("/a/b/index.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n../seg0.bin\n#EXTINF:1,\n" + server.Address("abs/seg1.bin").AbsoluteUri + "\n#EXT-X-ENDLIST\n");
        server.Map("/a/seg0.bin", Chunk(0)).Map("/abs/seg1.bin", Chunk(1));

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("a/b/index.m3u8"), CancellationToken.None, Fast);

        Assert.That(ReadAll(stream), Is.EqualTo(Expected(0, 1)));
    }

    [Test]
    public async Task Open_MasterPlaylist_FollowsTheLowestBandwidthAudioVariantOnly()
    {
        server.Map("/m/master.m3u8",
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=256000,CODECS=\"mp4a.40.34\"\nhigh/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"mp4a.40.34\"\nlow/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=32000,CODECS=\"avc1.42001e\",RESOLUTION=160x90\nvideo/index.m3u8\n");
        server.Map("/m/low/index.m3u8", Playlist(0, [7], end: true));
        server.Map("/m/low/seg7.bin", Chunk(7));
        server.Map("/m/high/index.m3u8", Playlist(0, [8], end: true));
        server.Map("/m/video/index.m3u8", Playlist(0, [9], end: true));

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("m/master.m3u8"), CancellationToken.None, Fast);

        Assert.Multiple(() =>
        {
            Assert.That(ReadAll(stream), Is.EqualTo(Expected(7)));
            Assert.That(server.Requests("/m/high/index.m3u8"), Is.Zero);
            Assert.That(server.Requests("/m/video/index.m3u8"), Is.Zero);
        });
    }

    [Test]
    public async Task Open_MasterThatPointsAtAMasterAgain_IsRejected()
    {
        server.Map("/n/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nsecond.m3u8\n");
        server.Map("/n/second.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nthird.m3u8\n");
        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("n/master.m3u8"), CancellationToken.None, Fast),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task Read_LivePlaylist_StartsThreeSegmentsFromTheEdgeAndFollowsRefreshes()
    {
        server.Map("/live/index.m3u8", request => HlsReply.Playlist(request switch
        {
            1 => Playlist(0, [0, 1, 2, 3, 4, 5], end: false),
            2 => Playlist(0, [0, 1, 2, 3, 4, 5], end: false), // nothing new yet
            3 => Playlist(2, [2, 3, 4, 5, 6], end: false),
            _ => Playlist(3, [3, 4, 5, 6, 7], end: true)
        }));
        Segments("/live/", 0, 1, 2, 3, 4, 5, 6, 7);

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("live/index.m3u8"), CancellationToken.None, Fast);

        Assert.Multiple(() =>
        {
            Assert.That(stream.IsLive, Is.True);
            Assert.That(ReadAll(stream), Is.EqualTo(Expected(3, 4, 5, 6, 7)), "no repeats, no skipped segments");
            Assert.That(server.Requests("/live/seg0.bin") + server.Requests("/live/seg1.bin") + server.Requests("/live/seg2.bin"), Is.Zero);
        });
    }

    [Test]
    public async Task Read_LiveServerMovesPastTheListener_ContinuesAtTheOldestAvailableSegment()
    {
        server.Map("/jump/index.m3u8", request => HlsReply.Playlist(request == 1 ? Playlist(0, [0, 1, 2], end: false) : Playlist(10, [10, 11], end: true)));
        Segments("/jump/", 0, 1, 2, 10, 11);

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("jump/index.m3u8"), CancellationToken.None, Fast);

        Assert.That(ReadAll(stream), Is.EqualTo(Expected(0, 1, 2, 10, 11)));
    }

    [Test]
    public async Task Read_LiveServerRestartsItsNumbering_KeepsPlaying()
    {
        server.Map("/restart/index.m3u8", request => HlsReply.Playlist(request == 1 ? Playlist(500, [500], end: false) : Playlist(0, [20], end: true)));
        server.Map("/restart/seg500.bin", Chunk(5)).Map("/restart/seg20.bin", Chunk(2));

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("restart/index.m3u8"), CancellationToken.None, Fast);

        Assert.That(ReadAll(stream), Is.EqualTo(Expected(5, 2)));
    }

    [Test]
    public async Task Read_LiveStreamThatStopsGrowing_EndsAfterTheStallTimeout()
    {
        server.Map("/stall/index.m3u8", Playlist(0, [0, 1], end: false));
        Segments("/stall/", 0, 1);

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("stall/index.m3u8"), CancellationToken.None, Fast);
        DateTime started = DateTime.UtcNow;

        Assert.Multiple(() =>
        {
            Assert.That(ReadAll(stream), Is.EqualTo(Expected(0, 1)));
            Assert.That(DateTime.UtcNow - started, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300)));
        });
    }

    [Test]
    public async Task Read_PlaylistRefreshKeepsFailing_ThrowsInsteadOfHanging()
    {
        server.Map("/dead/index.m3u8", request => request == 1 ? HlsReply.Playlist(Playlist(0, [0], end: false)) : HlsReply.Fail(503));
        Segments("/dead/", 0);

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("dead/index.m3u8"), CancellationToken.None, Fast);

        Assert.That(() => ReadAll(stream), Throws.TypeOf<IOException>());
    }

    [Test]
    public async Task Open_PlaylistRedirect_ResolvesRelativeSegmentsAgainstTheFinalAddress()
    {
        server.Map("/old/index.m3u8", _ => HlsReply.Redirect("/new/deep/index.m3u8"));
        server.Map("/new/deep/index.m3u8", Playlist(0, [4], end: true));
        server.Map("/new/deep/seg4.bin", Chunk(4));

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("old/index.m3u8"), CancellationToken.None, Fast);

        Assert.That(ReadAll(stream), Is.EqualTo(Expected(4)));
    }

    [Test]
    public async Task Open_SegmentRedirectToAnotherPath_IsFollowed()
    {
        server.Map("/seg/index.m3u8", Playlist(0, [4], end: true));
        server.Map("/seg/seg4.bin", _ => HlsReply.Redirect("/cdn/real4.bin"));
        server.Map("/cdn/real4.bin", Chunk(4));

        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("seg/index.m3u8"), CancellationToken.None, Fast);

        Assert.That(ReadAll(stream), Is.EqualTo(Expected(4)));
    }

    [Test]
    public async Task Open_RedirectLoop_FailsInsteadOfLooping()
    {
        server.Map("/loop/a.m3u8", _ => HlsReply.Redirect("/loop/b.m3u8"));
        server.Map("/loop/b.m3u8", _ => HlsReply.Redirect("/loop/a.m3u8"));
        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("loop/a.m3u8"), CancellationToken.None, Fast), Throws.InstanceOf<Exception>());
    }

    [TestCase("not a playlist at all")]
    [TestCase("#EXTM3U\n#EXTINF:oops,\nseg0.bin\n")]
    [TestCase("#EXTM3U\n#EXT-X-TARGETDURATION:6\n")]
    public async Task Open_MalformedPlaylist_ThrowsInvalidData(string text)
    {
        server.Map("/bad/index.m3u8", text);
        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("bad/index.m3u8"), CancellationToken.None, Fast), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task Open_OversizedPlaylist_IsRefused()
    {
        server.Map("/huge/index.m3u8", "#EXTM3U\n" + new string('#', HlsAudioStream.MaximumPlaylistBytes));
        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("huge/index.m3u8"), CancellationToken.None, Fast), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task Open_OversizedSegment_IsRefused()
    {
        server.Map("/big/index.m3u8", Playlist(0, [0], end: true));
        server.Map("/big/seg0.bin", new byte[HlsAudioStream.MaximumSegmentBytes + 1]);
        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("big/index.m3u8"), CancellationToken.None, Fast), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task Open_FirstSegmentMissing_ThrowsAndLaterMissingSegmentFailsTheRead()
    {
        server.Map("/gone/index.m3u8", Playlist(0, [0], end: true));
        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("gone/index.m3u8"), CancellationToken.None, Fast), Throws.TypeOf<InvalidOperationException>());

        server.Map("/later/index.m3u8", Playlist(0, [0, 1], end: true));
        Segments("/later/", 0);
        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("later/index.m3u8"), CancellationToken.None, Fast);
        Assert.That(() => ReadAll(stream), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task Open_PlaylistServerError_Throws()
    {
        server.Map("/err/index.m3u8", _ => HlsReply.Fail(500));
        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("err/index.m3u8"), CancellationToken.None, Fast), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task Open_SlowServer_TimesOutWithinTheRequestTimeout()
    {
        server.Map("/slow/index.m3u8", _ =>
        {
            Thread.Sleep(1500);
            return HlsReply.Playlist(Playlist(0, [0], end: true));
        });
        HlsOptions quick = Fast with { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("slow/index.m3u8"), CancellationToken.None, quick), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Open_CancelledToken_Cancels()
    {
        server.Map("/c/index.m3u8", Playlist(0, [0], end: true));
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();
        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("c/index.m3u8"), cancel.Token, Fast), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Dispose_WhileWaitingForTheLiveEdge_UnblocksTheReader()
    {
        server.Map("/wait/index.m3u8", Playlist(0, [0], end: false, target: 30));
        Segments("/wait/", 0);
        HlsOptions patient = Fast with { MinimumReload = TimeSpan.FromSeconds(30), MaximumReload = TimeSpan.FromSeconds(30), StallTimeout = TimeSpan.FromMinutes(5) };
        HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("wait/index.m3u8"), CancellationToken.None, patient);
        byte[] buffer = new byte[Chunk(0).Length];
        Task reader = Task.Run(() =>
        {
            int total = 0;
            while (total < 10_000) total += stream.Read(buffer);
        });
        await Task.Delay(200);
        Assert.That(reader.IsCompleted, Is.False, "the reader waits for the next segment");

        stream.Dispose();

        Assert.That(async () => await reader.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<Exception>());
    }

    [Test]
    public async Task Seek_WithinTheProbeWindowRewinds_ButTheLiveEdgeCannotBeRewoundOrSeekedToTheEnd()
    {
        server.Map("/seek/index.m3u8", Playlist(0, [0, 1], end: true));
        Segments("/seek/", 0, 1);
        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("seek/index.m3u8"), CancellationToken.None, Fast);
        byte[] first = new byte[400];
        byte[] again = new byte[400];

        int a = stream.Read(first);
        stream.Seek(0, SeekOrigin.Begin);
        int b = stream.Read(again);

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.GreaterThan(0));
            Assert.That(again[..b], Is.EqualTo(first[..a]));
            Assert.That(() => stream.Seek(0, SeekOrigin.End), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => stream.Seek(10_000_000, SeekOrigin.Begin), Throws.TypeOf<IOException>());
            Assert.That(() => _ = stream.Length, Throws.TypeOf<NotSupportedException>());
            Assert.That(stream.CanSeek && stream.CanRead && !stream.CanWrite);
        });
    }

    [Test]
    public async Task Seek_AfterTheProbeWindowHasPassed_RefusesToRewind()
    {
        int[] numbers = Enumerable.Range(0, 5).ToArray();
        server.Map("/far/index.m3u8", Playlist(0, numbers, end: true));
        foreach (int number in numbers) server.Map($"/far/seg{number}.bin", [0xaa, .. new byte[HlsAudioStream.HeadLimit / 3]]);
        await using HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("far/index.m3u8"), CancellationToken.None, Fast);
        ReadAll(stream);

        Assert.That(() => stream.Seek(HlsAudioStream.HeadLimit + 10, SeekOrigin.Begin), Throws.TypeOf<IOException>());
    }

    [Test]
    public async Task Open_FragmentedMp4Segments_SayToChooseVlc()
    {
        byte[] segment = [0, 0, 0, 24, .. "ftypiso5"u8, 0, 0, 0, 1, .. new byte[100]];
        server.Map("/codec/index.m3u8", Playlist(0, [0], end: true));
        server.Map("/codec/seg0.bin", segment);

        await Assert.ThatAsync(() => HlsAudioStream.OpenAsync(client, server.Address("codec/index.m3u8"), CancellationToken.None, Fast),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("Choose VLC under Preferences > Playback"));
    }

    [Test]
    public async Task Decode_Mp3Segments_PlaysThroughTheNativeDecoder()
    {
        byte[] mp3 = LiveMp3Frames();
        server.Map("/mp3/index.m3u8", Playlist(0, [0, 1, 2], end: true));
        foreach (int number in new[] { 0, 1, 2 }) server.Map($"/mp3/seg{number}.bin", mp3);

        HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("mp3/index.m3u8"), CancellationToken.None, Fast);
        using NativeDecoderSource? source = NativeDecoderSource.TryOpen(stream, 48000, out string? failure);

        Assert.That(source, Is.Not.Null, failure);
        (long frames, float peak) = Drain(source!);
        Assert.Multiple(() =>
        {
            Assert.That(frames, Is.InRange(3 * 48000 - 12000, 3 * 48000 + 12000), "three one-second segments");
            Assert.That(peak, Is.GreaterThan(0.05f));
        });
    }

    [Test]
    public async Task Decode_Mp3SegmentsWithPackedAudioId3Tags_StillDecode()
    {
        byte[] mp3 = LiveMp3Frames();
        byte[] tag = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 16, .. new byte[16]];
        byte[] tagged = [.. tag, .. mp3];
        server.Map("/id3/index.m3u8", Playlist(0, [0, 1], end: true));
        server.Map("/id3/seg0.bin", tagged).Map("/id3/seg1.bin", tagged);

        HlsAudioStream stream = await HlsAudioStream.OpenAsync(client, server.Address("id3/index.m3u8"), CancellationToken.None, Fast);
        using NativeDecoderSource? source = NativeDecoderSource.TryOpen(stream, 48000, out string? failure);

        Assert.That(source, Is.Not.Null, failure);
        (long frames, _) = Drain(source!);
        Assert.That(frames, Is.InRange(2 * 48000 - 9000, 2 * 48000 + 9000));
    }

    [Test]
    public async Task Factory_HlsAddress_OpensAnMp3StreamAndRefusesTransportStreams()
    {
        byte[] mp3 = LiveMp3Frames();
        server.Map("/f/good.m3u8", Playlist(0, [0, 1], end: true));
        server.Map("/f/seg0.bin", mp3).Map("/f/seg1.bin", mp3);
        server.Map("/f/ts.m3u8", Playlist(0, [9], end: true));
        server.Map("/f/seg9.bin", [0x47, 0x40, 0x00, 0x10, .. new byte[184], 0x47, 0x40, 0x11, 0x10, .. new byte[184]]);
        AudioSourceFactory factory = new(client, Fast, nativeDecoderFallback: MiniaudioDecoders);

        using IPcmSource good = await factory.OpenAsync(server.Address("f/good.m3u8"), 48000, CancellationToken.None);
        (long frames, _) = Drain(good);

        Assert.Multiple(() =>
        {
            Assert.That(frames, Is.InRange(2 * 48000 - 8000, 2 * 48000 + 8000));
            Assert.That(async () => await factory.OpenAsync(server.Address("f/ts.m3u8"), 48000, CancellationToken.None),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("Choose VLC"));
        });
    }

    [Test]
    public async Task Engine_HlsTrack_PlaysToTheEndLikeAnyOtherTrack()
    {
        byte[] mp3 = LiveMp3Frames();
        server.Map("/e/index.m3u8", Playlist(0, [0, 1], end: true));
        server.Map("/e/seg0.bin", mp3).Map("/e/seg1.bin", mp3);
        using var engine = NewEngine(client);
        await engine.LoadAsync(TrackFor(server.Address("e/index.m3u8"), 2));

        await engine.PlayAsync();

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted, 15);
        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Engine_HlsTransportStream_FaultsWithTheChooseVlcMessage()
    {
        server.Map("/t/index.m3u8", Playlist(0, [0], end: true));
        server.Map("/t/seg0.bin", [0x47, 0x40, 0x00, 0x10, .. new byte[184], 0x47, 0x40, 0x11, 0x10, .. new byte[184]]);
        using var engine = NewEngine(client);
        await engine.LoadAsync(TrackFor(server.Address("t/index.m3u8"), 1));

        try { await engine.PlayAsync(); }
        catch (Exception) { /* the failure may be reported by the call or by the state */ }

        PlaybackSnapshot faulted = await WaitForAsync(engine, state => state.Status == PlaybackStatus.Faulted, 8);
        Assert.That(faulted.Error, Does.Contain("Choose VLC"));
    }

    /// <summary>
    /// The fixture's bare MP3 frames: without its ID3 tag and its leading Xing/Info frame. That frame announces a total length, which makes
    /// the decoder stop at the end of the first file; real HLS MP3 segments are plain frames, so the tests use them that way.
    /// </summary>
    private static byte[] LiveMp3Frames()
    {
        byte[] file = File.ReadAllBytes(Fixture("fixture.mp3"));
        int at = 10 + ((file[6] & 0x7f) << 21 | (file[7] & 0x7f) << 14 | (file[8] & 0x7f) << 7 | file[9] & 0x7f);
        int[] kilobits = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        int[] rates = [44100, 48000, 32000];
        int length = 144 * kilobits[file[at + 2] >> 4] * 1000 / rates[file[at + 2] >> 2 & 3] + (file[at + 2] >> 1 & 1);
        Assert.That(file.AsSpan(at + length, 2).SequenceEqual(new byte[] { 0xff, 0xfb }), "the next frame follows the info frame");
        return file[(at + length)..];
    }

    private static (long Frames, float Peak) Drain(IPcmSource source)
    {
        float[] buffer = new float[4096 * 2];
        long frames = 0;
        float peak = 0;
        int read;
        while ((read = source.Read(buffer, 4096)) > 0)
        {
            frames += read;
            for (int i = 0; i < read * 2; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
        }
        return (frames, peak);
    }
}
