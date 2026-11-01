using Aibell.Playback.Core;
using NUnit.Framework;
using static Aibell.Playback.Miniaudio.Tests.EngineTestSupport;

namespace Aibell.Playback.Miniaudio.Tests;

[TestFixture]
public sealed class DecoderAndStreamTests
{
    private const uint Rate = 48000;

    [TestCase("fixture.wav")]
    [TestCase("fixture.flac")]
    [TestCase("fixture.mp3")]
    [TestCase("fixture.ogg")]
    public void Decoder_ProducesAboutOneSecondOfNonSilentStereo(string fixture)
    {
        using NativeDecoderSource? source = NativeDecoderSource.TryOpen(File.OpenRead(Fixture(fixture)), Rate, out string? failure);
        Assert.That(source, Is.Not.Null, failure);

        (long frames, float peak) = Drain(source!);

        Assert.That(frames, Is.InRange(Rate - 3000, Rate + 3000), "A one-second tone decodes to about one second of frames.");
        Assert.That(peak, Is.GreaterThan(0.05f), "The decoded tone must not be silent.");
    }

    [Test]
    public void Decoder_ResamplesToTheOutputRate()
    {
        using NativeDecoderSource? source = NativeDecoderSource.TryOpen(File.OpenRead(Fixture("fixture.flac")), 44100, out _);

        (long frames, _) = Drain(source!);

        Assert.That(frames, Is.InRange(44100 - 3000, 44100 + 3000));
    }

    [Test]
    public void MediaFoundation_ConvertsAacToTheOutputRateAndSeeks()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Media Foundation exists only on Windows.");
            return;
        }
        // Real devices often run at 44.1 kHz while the fixture is 48 kHz, so the Source Reader must convert.
        using MediaFoundationSource source = MediaFoundationSource.Open(Fixture("fixture.m4a"), 44100);

        float[] buffer = new float[4096 * 2];
        long frames = 0;
        int read;
        while ((read = source.Read(buffer, 4096)) > 0) frames += read;
        Assert.That(frames, Is.InRange(44100 - 3000, 44100 + 3000));

        Assert.That(source.Seek(22050), Is.True);
        long afterSeek = 0;
        while ((read = source.Read(buffer, 4096)) > 0) afterSeek += read;
        Assert.That(afterSeek, Is.InRange(22050 - 3000, 22050 + 3000));
    }

    [Test]
    public void Decoder_Seek_SkipsAhead()
    {
        using NativeDecoderSource? source = NativeDecoderSource.TryOpen(File.OpenRead(Fixture("fixture.flac")), Rate, out _);

        Assert.That(source!.Seek(Rate / 2), Is.True);
        (long frames, _) = Drain(source);

        Assert.That(frames, Is.InRange(Rate / 2 - 2000, Rate / 2 + 2000));
    }

    [Test]
    public void Decoder_RejectsAFileThatIsNotAudio()
    {
        using MemoryStream notAudio = new(new byte[2048]);

        NativeDecoderSource? source = NativeDecoderSource.TryOpen(notAudio, Rate, out string? failure);

        Assert.That(source, Is.Null);
        Assert.That(failure, Is.Not.Null);
    }

    [Test]
    public async Task HttpStream_WithRanges_ReadsAndSeeksLikeTheFile()
    {
        byte[] expected = File.ReadAllBytes(Fixture("fixture.mp3"));
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: false);
        using HttpClient client = new();
        await using HttpAudioStream stream = await HttpAudioStream.OpenAsync(client, server.Address, CancellationToken.None);

        Assert.That(stream.Length, Is.EqualTo(expected.Length));
        byte[] head = new byte[100];
        stream.ReadExactly(head);
        stream.Seek(expected.Length - 50, SeekOrigin.Begin);
        byte[] tail = new byte[50];
        stream.ReadExactly(tail);
        stream.Seek(200, SeekOrigin.Begin);
        byte[] middle = new byte[300];
        stream.ReadExactly(middle);

        Assert.That(head, Is.EqualTo(expected[..100]));
        Assert.That(tail, Is.EqualTo(expected[^50..]));
        Assert.That(middle, Is.EqualTo(expected[200..500]));
        Assert.That(stream.Read(new byte[10]), Is.Zero.Or.GreaterThan(0));
    }

    [Test]
    public async Task HttpStream_Live_CanRewindWithinTheKeptStartButNotFarBeyond()
    {
        byte[] expected = File.ReadAllBytes(Fixture("fixture.mp3"));
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: true);
        using HttpClient client = new();
        await using HttpAudioStream stream = await HttpAudioStream.OpenAsync(client, server.Address, CancellationToken.None);

        byte[] first = new byte[1000];
        stream.ReadExactly(first);
        stream.Seek(0, SeekOrigin.Begin);
        byte[] again = new byte[1000];
        stream.ReadExactly(again);

        Assert.That(again, Is.EqualTo(first));
        Assert.That(again, Is.EqualTo(expected[..1000]));
        Assert.Throws<IOException>(() => stream.Seek(10_000_000, SeekOrigin.Begin));
    }

    [Test]
    public async Task HttpStream_ServerRefusingRanges_StillPlaysFromTheStart()
    {
        byte[] expected = File.ReadAllBytes(Fixture("fixture.mp3"));
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: false, refuseRanges: true);
        using HttpClient client = new();
        await using HttpAudioStream stream = await HttpAudioStream.OpenAsync(client, server.Address, CancellationToken.None);

        byte[] first = new byte[500];
        stream.ReadExactly(first);

        Assert.That(first, Is.EqualTo(expected[..500]));
    }

    [TestCase(404, "404")]
    [TestCase(503, "503")]
    public async Task HttpStream_ServerErrorStatus_NamesTheStatusInTheMessage(int status, string expected)
    {
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: false) { Status = status };
        using HttpClient client = new();

        await Assert.ThatAsync(() => HttpAudioStream.OpenAsync(client, server.Address, CancellationToken.None),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains(expected));
    }

    [Test]
    public async Task HttpStream_ServerThatStopsHonouringRanges_FailsTheSeekInsteadOfReturningWrongAudio()
    {
        // Larger than the skip-ahead window, so a seek must ask the server again instead of reading through.
        string big = Path.Combine(Path.GetTempPath(), "aibell-ranges-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(big, new byte[512 * 1024]);
        try
        {
            using AudioTestServer server = new(big, live: false) { StopHonouringRangesAfterFirstRequest = true };
            using HttpClient client = new();
            await using HttpAudioStream stream = await HttpAudioStream.OpenAsync(client, server.Address, CancellationToken.None);
            stream.ReadExactly(new byte[100]);

            stream.Seek(300 * 1024, SeekOrigin.Begin);

            Assert.That(() => stream.ReadExactly(new byte[100]), Throws.InstanceOf<IOException>().With.Message.Contains("byte ranges"));
        }
        finally { File.Delete(big); }
    }

    [Test]
    public async Task Engine_ServerError_FaultsWithTheStatusInTheMessage()
    {
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: false) { Status = 404 };
        using HttpClient client = new();
        using var engine = NewEngine(client);
        await engine.LoadAsync(TrackFor(server.Address));

        await Assert.ThatAsync(() => engine.PlayAsync(), Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("404"));

        Assert.That(engine.Snapshot.Status, Is.EqualTo(Aibell.Application.Playback.PlaybackStatus.Faulted));
        Assert.That(engine.Snapshot.Error, Does.Contain("404"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Engine_PlaysAnHttpStream(bool live)
    {
        using AudioTestServer server = new(Fixture("fixture.mp3"), live);
        using HttpClient client = new();
        using var engine = NewEngine(client);
        await engine.LoadAsync(TrackFor(server.Address, live ? 0 : 1));
        await engine.PlayAsync();

        var ended = await WaitForAsync(engine, state => state.Status is Aibell.Application.Playback.PlaybackStatus.Ended or Aibell.Application.Playback.PlaybackStatus.Faulted);

        Assert.That(ended.Status, Is.EqualTo(Aibell.Application.Playback.PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Engine_SeeksWithinAnHttpDownload()
    {
        using AudioTestServer server = new(Fixture("fixture.mp3"), live: false);
        using HttpClient client = new();
        using var engine = NewEngine(client);
        await engine.LoadAsync(TrackFor(server.Address));
        await engine.PlayAsync();

        await engine.SeekAsync(TimeSpan.FromSeconds(0.8));
        var ended = await WaitForAsync(engine, state => state.Status is Aibell.Application.Playback.PlaybackStatus.Ended or Aibell.Application.Playback.PlaybackStatus.Faulted, 5);

        Assert.That(ended.Status, Is.EqualTo(Aibell.Application.Playback.PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Engine_RefusesAudioCds()
    {
        using var engine = NewEngine();
        await engine.LoadAsync(TrackFor(Aibell.Application.Discs.DiscLocation.Create("/dev/sr0", 1)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PlayAsync());

        Assert.That(error!.Message, Does.Contain("VLC"));
    }

    private static (long Frames, float Peak) Drain(NativeDecoderSource source)
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
