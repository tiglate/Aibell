using System.Net;
using NUnit.Framework;

namespace AibellTools.Tests;

[TestFixture]
public sealed class RadioCheckTests
{
    private static readonly bool[] ExpectedOrder = [true, false];
    private HttpListener listener = null!;
    private string prefix = "";
    private CancellationTokenSource stop = null!;
    private HttpClient http = null!;

    [SetUp]
    public void Start()
    {
        int port = FreePort();
        prefix = $"http://127.0.0.1:{port}/";
        listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        stop = new CancellationTokenSource();
        _ = Task.Run(() => Serve(stop.Token), stop.Token);
        http = RadioCheck.CreateClient();
    }

    [TearDown]
    public void Stop()
    {
        stop.Cancel();
        stop.Dispose();
        listener.Close();
        http.Dispose();
    }

    private static int FreePort()
    {
        System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task Serve(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => Handle(context), token);
        }
    }

    private static void Handle(HttpListenerContext context)
    {
        try
        {
            HttpListenerResponse response = context.Response;
            switch (context.Request.Url!.AbsolutePath)
            {
                case "/audio": Write(response, "audio/mpeg", new byte[20000]); break;
                case "/midmp3": Write(response, "audio/mpeg", MidFrameMp3()); break;
                case "/short": Write(response, "audio/mpeg", new byte[100]); break;
                case "/page": Write(response, "text/html", "<html>offline</html>"u8.ToArray()); break;
                case "/redirect": response.StatusCode = 302; response.RedirectLocation = "/audio"; response.Close(); break;
                case "/master.m3u8": Write(response, "application/vnd.apple.mpegurl", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=64000\nmedia.m3u8\n"u8.ToArray()); break;
                case "/media.m3u8": Write(response, "application/vnd.apple.mpegurl", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg1.ts\n"u8.ToArray()); break;
                case "/seg1.ts": Write(response, "video/mp2t", new byte[20000]); break;
                case "/adts": Write(response, "audio/aac", Fixture("long_sweep_44k_stereo.aac")); break;
                case "/sbr": Write(response, "audio/aacp", SbrStream()); break;
                case "/tslive.m3u8": Write(response, "application/vnd.apple.mpegurl", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nreal.ts\n"u8.ToArray()); break;
                case "/real.ts": Write(response, "video/mp2t", Fixture("ms_22k_stereo.ts")); break;
                case "/encrypted.m3u8": Write(response, "application/vnd.apple.mpegurl", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXTINF:6,\nreal.ts\n"u8.ToArray()); break;
                case "/hemaster.m3u8": Write(response, "application/vnd.apple.mpegurl", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=32000,CODECS=\"mp4a.40.5\"\nbroken.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=96000,CODECS=\"mp4a.40.2\"\ntslive.m3u8\n"u8.ToArray()); break;
                case "/notplaylist.m3u8": Write(response, "text/plain", "hello"u8.ToArray()); break;
                default: response.StatusCode = 404; response.Close(); break;
            }
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }

    // Repeated so the stream is longer than the check's minimum of audio bytes; the fixtures are tiny.
    private static byte[] Fixture(string name)
    {
        byte[] once = File.ReadAllBytes(Path.Combine(RepositoryRoot.Find(), "tests", "fixtures", "aac", name));
        return [.. once, .. once, .. once];
    }

    /// <summary>ADTS frames whose only content is a fill element announcing SBR, which is how HE-AAC hides behind an LC header.</summary>
    private static byte[] SbrStream()
    {
        byte[] payload = [0xc5, 0xa0, 0x01, 0xc0];
        int length = 7 + payload.Length;
        byte[] frame = [0xff, 0xf1, 0x58, 0x80, (byte)(length >> 3), (byte)((length & 7) << 5 | 0x1f), 0xfc, .. payload];
        return [.. Enumerable.Range(0, 400).SelectMany(_ => frame)];
    }

    /// <summary>Layer III 128 kbps 44.1 kHz frames (417 bytes) that begin 100 bytes before a frame boundary, as a live stream joined mid-frame does.</summary>
    private static byte[] MidFrameMp3()
    {
        byte[] frame = [0xff, 0xfb, 0x90, 0x00, .. new byte[413]];
        return [.. new byte[100], .. Enumerable.Range(0, 20).SelectMany(_ => frame)];
    }

    private static void Write(HttpListenerResponse response, string type, byte[] body)
    {
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
        response.Close();
    }

    private RadioCheck.Station Station(string path, bool hls = false) => new("test-" + path.Trim('/'), "Test " + path, "XX", prefix + path.TrimStart('/'), hls, null);

    [Test]
    public async Task Check_AudioStream_IsAvailable()
    {
        Assert.That((await RadioCheck.CheckAsync(http, Station("audio"))).Ok, Is.True);
    }

    [Test]
    public async Task Check_RedirectedAudioStream_IsAvailable()
    {
        Assert.That((await RadioCheck.CheckAsync(http, Station("redirect"))).Ok, Is.True);
    }

    [TestCase("missing", "HTTP 404")]
    [TestCase("page", "unexpected content type")]
    [TestCase("short", "stream ended after")]
    public async Task Check_BrokenStream_IsReportedWithReason(string path, string reason)
    {
        RadioCheck.Result result = await RadioCheck.CheckAsync(http, Station(path));
        Assert.That(result.Ok, Is.False);
        Assert.That(result.Detail, Does.Contain(reason));
    }

    [Test]
    public async Task Check_HlsMasterPlaylist_FollowsVariantToASegment()
    {
        Assert.That((await RadioCheck.CheckAsync(http, Station("master.m3u8", hls: true))).Ok, Is.True);
    }

    [Test]
    public async Task Check_HlsAddressWithoutFlag_IsStillCheckedAsHls()
    {
        Assert.That((await RadioCheck.CheckAsync(http, Station("media.m3u8"))).Ok, Is.True);
    }

    [Test]
    public async Task Check_AdtsStream_IsIdentifiedAsPlayableAac()
    {
        RadioCheck.Result result = await RadioCheck.CheckAsync(http, Station("adts"));
        Assert.That(result.Ok, Is.True);
        Assert.That(result.Format, Does.Contain("AAC-LC").And.Contain("44.1 kHz"));
        Assert.That(result.Support, Is.EqualTo(RadioCheck.Native.Yes));
    }

    [Test]
    public async Task Check_Mp3StreamJoinedMidFrame_IsStillRecognized()
    {
        RadioCheck.Result result = await RadioCheck.CheckAsync(http, Station("midmp3"));
        Assert.That((result.Format, result.Support), Is.EqualTo(("MP3", RadioCheck.Native.Yes)));
    }

    [Test]
    public async Task Check_AdtsStreamWithSbr_IsFlaggedAsNeedingVlc()
    {
        RadioCheck.Result result = await RadioCheck.CheckAsync(http, Station("sbr"));
        Assert.That(result.Ok, Is.True);
        Assert.That(result.Format, Does.Contain("SBR"));
        Assert.That(result.Support, Is.EqualTo(RadioCheck.Native.No));
    }

    [Test]
    public async Task Check_HlsWithTransportStreamAac_IsIdentifiedAndPlayable()
    {
        RadioCheck.Result result = await RadioCheck.CheckAsync(http, Station("tslive.m3u8", hls: true));
        Assert.That(result.Ok, Is.True);
        Assert.That(result.Format, Does.StartWith("HLS MpegTs AAC-LC"));
        Assert.That(result.Support, Is.EqualTo(RadioCheck.Native.Yes));
    }

    [Test]
    public async Task Check_EncryptedHls_NeedsVlcEvenWhenTheAudioIsAac()
    {
        RadioCheck.Result result = await RadioCheck.CheckAsync(http, Station("encrypted.m3u8", hls: true));
        Assert.That(result.Ok, Is.True);
        Assert.That(result.Format, Does.Contain("encrypted"));
        Assert.That(result.Support, Is.EqualTo(RadioCheck.Native.No));
    }

    [Test]
    public async Task Check_MasterPlaylist_FollowsTheVariantTheBuiltInPlayerWouldChoose()
    {
        // The HE-AAC variant is cheaper but points at a missing playlist; the player (and the check) take the AAC-LC one.
        RadioCheck.Result result = await RadioCheck.CheckAsync(http, Station("hemaster.m3u8", hls: true));
        Assert.That(result.Ok, Is.True, result.Detail);
        Assert.That(result.Support, Is.EqualTo(RadioCheck.Native.Yes));
    }

    [Test]
    public void Report_SummarizesWhichStationsNeedVlc()
    {
        RadioCheck.Station a = Station("adts"), b = Station("sbr");
        using StringWriter text = new();
        RadioCheck.Report([new(a, true, "audio/aac", "ADTS AAC-LC", RadioCheck.Native.Yes), new(b, true, "audio/aacp", "ADTS AAC-LC + SBR", RadioCheck.Native.No)], text);
        Assert.That(text.ToString(), Does.Contain("1 play natively, 1 need VLC").And.Contain("needs VLC: ADTS AAC-LC + SBR x1"));
    }

    [Test]
    public async Task Check_NonPlaylistAtHlsAddress_Fails()
    {
        RadioCheck.Result result = await RadioCheck.CheckAsync(http, Station("notplaylist.m3u8", hls: true));
        Assert.That(result.Ok, Is.False);
        Assert.That(result.Detail, Does.Contain("not an HLS playlist"));
    }

    [Test]
    public async Task CheckAll_RetriesOnce_AndKeepsResultOrder()
    {
        List<RadioCheck.Result> results = await RadioCheck.CheckAllAsync(http, [Station("audio"), Station("missing")], 2, logos: false);
        Assert.That(results.Select(r => r.Ok), Is.EqualTo(ExpectedOrder));
    }

    [Test]
    public void Report_ListsFailuresAndReturnsOneOnlyWhenSomethingIsDown()
    {
        RadioCheck.Station good = Station("audio"), bad = Station("missing");
        StringWriter all = new(), some = new();
        Assert.That(RadioCheck.Report([new(good, true, "audio/mpeg")], all), Is.Zero);
        Assert.That(RadioCheck.Report([new(good, true, "audio/mpeg"), new(bad, false, "HTTP 404")], some), Is.EqualTo(1));
        Assert.That(some.ToString(), Does.Contain("1 of 2 stations available").And.Contain(bad.Id).And.Contain(bad.Stream));
    }

    [Test]
    public void Load_ShippedCatalog_ParsesEveryStation()
    {
        List<RadioCheck.Station> stations = RadioCheck.Load(Path.Combine(RepositoryRoot.Find(), "src/Aibell.Infrastructure/Radio/curated-stations.json"));
        Assert.That(stations, Has.Count.GreaterThanOrEqualTo(100));
        Assert.That(stations.Select(s => s.Id), Is.Unique);
        Assert.That(stations.All(s => Uri.TryCreate(s.Stream, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"), Is.True);
    }

    [Test]
    public void Load_EntryWithoutStream_IsRejected()
    {
        using TempFolder folder = new();
        string path = folder.Combine("catalog.json");
        File.WriteAllText(path, """{"stations":[{"id":"a","name":"A"}]}""");
        Assert.That(() => RadioCheck.Load(path), Throws.TypeOf<ToolException>());
    }
}
