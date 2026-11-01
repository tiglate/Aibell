using Aibell.Application.Playback;
using NUnit.Framework;

namespace Aibell.Playback.Miniaudio.Tests;

/// <summary>
/// Regression tests for a crash found on a real file: a Vorbis comment header that spans several Ogg pages made stb_vorbis (push mode)
/// allocate with a garbage length and free uninitialized pointers, killing the whole process with no message.
/// </summary>
[TestFixture]
public sealed class VorbisHeaderTests
{
    private static byte[] Fixture() => File.ReadAllBytes(EngineTestSupport.Fixture("fixture.ogg"));

    private static long ReadAll(NativeDecoderSource source)
    {
        float[] buffer = new float[4096 * 2];
        long frames = 0;
        int read;
        while ((read = source.Read(buffer, 4096)) > 0) frames += read;
        return frames;
    }

    [Test]
    public void Open_CommentHeaderOverSeveralPages_DecodesTheWholeFile()
    {
        byte[] plain = Fixture(), long_ = OggTestFiles.WithLongCommentHeader(plain);
        long expected;
        using (NativeDecoderSource baseline = NativeDecoderSource.TryOpen(new MemoryStream(plain), 48000, out _)!) expected = ReadAll(baseline);

        using NativeDecoderSource? source = NativeDecoderSource.TryOpen(new MemoryStream(long_), 48000, out string? failure);

        Assert.That(source, Is.Not.Null, failure);
        Assert.That(ReadAll(source!), Is.EqualTo(expected), "the audio is the same whatever the comment header holds");
    }

    [Test]
    public void Open_EveryTruncationOfALongHeader_FailsCleanlyInsteadOfCrashing()
    {
        byte[] file = OggTestFiles.WithLongCommentHeader(Fixture());
        int headerEnd = OggTestFiles.HeaderLength(file);
        int opened = 0, refused = 0;

        // A crash here would take the test host down, which is how the original bug showed itself.
        for (int cut = 0; cut <= headerEnd + 300; cut += cut < 600 ? 1 : 29)
        {
            using NativeDecoderSource? source = NativeDecoderSource.TryOpen(new MemoryStream(file, 0, cut), 48000, out _);
            if (source is null) refused++; else opened++;
        }

        Assert.That(refused, Is.GreaterThan(0));
        Assert.That(opened, Is.GreaterThan(0), "prefixes that include the whole header open");
    }

    [Test]
    public void Open_CorruptedCommentLengths_AreRefusedNotFollowed()
    {
        byte[] file = OggTestFiles.WithLongCommentHeader(Fixture());
        byte[] vendorLength = BitConverter.GetBytes(int.MaxValue);
        int vendorAt = Array.IndexOf(file, (byte)'v', 40); // the "vorbis" tag of the comment packet follows its packet type byte
        Assert.That(vendorAt, Is.GreaterThan(0));
        Array.Copy(vendorLength, 0, file, vendorAt + 6, 4);

        using NativeDecoderSource? source = NativeDecoderSource.TryOpen(new MemoryStream(file), 48000, out string? failure);

        Assert.That(source, Is.Null);
        Assert.That(failure, Does.Contain("does not recognize"));
    }

    [Test]
    public async Task Engine_PlaysAFileWithALongCommentHeaderToTheEnd()
    {
        string path = Path.Combine(Path.GetTempPath(), "aibell-long-comment-" + Guid.NewGuid().ToString("N") + ".ogg");
        await File.WriteAllBytesAsync(path, OggTestFiles.WithLongCommentHeader(Fixture()));
        try
        {
            using MiniaudioPlaybackEngine engine = EngineTestSupport.NewEngine();
            await engine.LoadAsync(EngineTestSupport.TrackFor(new Uri(path)));

            await engine.PlayAsync();

            PlaybackSnapshot ended = await EngineTestSupport.WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted);
            Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
        }
        finally { File.Delete(path); }
    }
}
