using Aibell.Playback.Core;
using NUnit.Framework;

namespace Aibell.Playback.Alsa.Tests;

public sealed class AudioRingBufferTests
{
    [Test]
    public void Discard_DoesNotReuseSlotsUntilReaderAcknowledges()
    {
        AudioRingBuffer ring = new(4, 1);
        Assert.That(ring.Write([1, 2, 3, 4]), Is.EqualTo(4));
        ring.DiscardAll();
        Assert.That(ring.Available, Is.Zero);
        Assert.That(ring.Free, Is.Zero);
        Assert.That(ring.Write([5, 6]), Is.Zero);

        float[] output = new float[4];
        Assert.That(ring.Read(output), Is.Zero);
        Assert.That(ring.Free, Is.EqualTo(4));
        Assert.That(ring.Write([5, 6]), Is.EqualTo(2));
        Assert.That(ring.Read(output), Is.EqualTo(2));
        Assert.That(output[..2], Is.EqualTo(new float[] { 5, 6 }));
    }
}
