using System.Buffers.Binary;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class MpegAudioDataTests
{
    [Test]
    public void Read_TagOnlyChange_KeepsFrameHashAndDurationDespiteAtlEstimateDrift()
    {
        using MemoryStream source = new(Fixture());
        MpegAudioData original = MpegAudioData.Read(source);
        byte[] bytes = Fixture(); bytes[21] ^= 1; // Only a title character inside the ID3 tag.
        using MemoryStream edited = new(bytes);
        MpegAudioData after = MpegAudioData.Read(edited);
        Assert.That(after, Is.EqualTo(original));
        Assert.That(after.Duration.TotalMilliseconds, Is.EqualTo(1032).Within(0.001));
        Assert.That(after.SampleRate, Is.EqualTo(48000)); Assert.That(after.Channels, Is.EqualTo(1));
    }

    [Test]
    public void Read_AudioByteChange_ChangesFingerprint()
    {
        byte[] bytes = Fixture(); using MemoryStream original = new(bytes.ToArray());
        MpegAudioData first = MpegAudioData.Read(original);
        bytes[^1] ^= 1;
        using MemoryStream changed = new(bytes);
        Assert.That(MpegAudioData.Read(changed).Hash, Is.Not.EqualTo(first.Hash));
    }

    [TestCase("tag-flags")]
    [TestCase("tag-size")]
    [TestCase("bad-sync")]
    [TestCase("free-format")]
    [TestCase("truncated")]
    [TestCase("xing-count")]
    [TestCase("trailing")]
    public void Read_UnverifiedOrMalformedLayout_Rejects(string fault)
    {
        byte[] bytes = Fixture(); int audio = 10 + (bytes[6] << 21) + (bytes[7] << 14) + (bytes[8] << 7) + bytes[9];
        switch (fault)
        {
            case "tag-flags": bytes[5] = 16; break;
            case "tag-size": bytes[6] = 127; break;
            case "bad-sync": bytes[audio] = 0; break;
            case "free-format": bytes[audio + 2] &= 15; break;
            case "truncated": bytes = bytes[..^1]; break;
            case "xing-count": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(audio + 29), 999); break;
            case "trailing": bytes = [.. bytes, 0, 0, 0, 0]; break;
        }
        using MemoryStream input = new(bytes);
        Assert.That(() => MpegAudioData.Read(input), Throws.Exception);
        input.Position = 0; Assert.That(MpegAudioData.TryRead(input, default), Is.Null); Assert.That(input.Position, Is.Zero);
    }

    [Test]
    public void Read_Cancelled_StopsFrameInspection()
    {
        using MemoryStream input = new(Fixture()); using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.That(() => MpegAudioData.Read(input, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    }
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.mp3"));
}
