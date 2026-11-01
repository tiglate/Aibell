namespace Aibell.Audio.Core.Tests;

public class ChannelMixerTests
{
    [Test]
    public void Mono_IsDuplicated()
    {
        float[] stereo = new float[4];
        new ChannelMixer(1).ToStereo([0.25f, -0.5f], stereo, 2);
        Assert.That(stereo, Is.EqualTo(new[] { 0.25f, 0.25f, -0.5f, -0.5f }));
    }

    [Test]
    public void Stereo_IsCopied()
    {
        float[] stereo = new float[4];
        new ChannelMixer(2).ToStereo([1f, 2f, 3f, 4f], stereo, 2);
        Assert.That(stereo, Is.EqualTo(new[] { 1f, 2f, 3f, 4f }));
    }

    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(10)]
    public void FullScaleInput_NeverExceedsFullScale(int channels)
    {
        float[] source = new float[channels];
        Array.Fill(source, 1f);
        float[] stereo = new float[2];
        new ChannelMixer(channels).ToStereo(source, stereo, 1);
        Assert.That(stereo[0], Is.InRange(0.5f, 1.0001f));
        Assert.That(stereo[1], Is.InRange(0.5f, 1.0001f));
    }

    [Test]
    public void Surround_KeepsFrontChannelsOnTheirSide()
    {
        // 5.1: FL FR FC LFE BL BR. Only the front left plays: nothing reaches the right output.
        float[] stereo = new float[2];
        new ChannelMixer(6).ToStereo([1f, 0, 0, 0, 0, 0], stereo, 1);
        Assert.That(stereo[0], Is.GreaterThan(0.3f));
        Assert.That(stereo[1], Is.EqualTo(0f));
    }

    [Test]
    public void Lfe_IsDropped()
    {
        float[] stereo = new float[2];
        new ChannelMixer(6).ToStereo([0, 0, 0, 1f, 0, 0], stereo, 1);
        Assert.That(stereo, Is.EqualTo(new[] { 0f, 0f }));
    }

    [Test]
    public void ZeroChannels_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelMixer(0));
}
