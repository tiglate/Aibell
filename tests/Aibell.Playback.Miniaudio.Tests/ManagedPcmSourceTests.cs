using Aibell.Application.Playback;
using Aibell.Playback.Core;
using NUnit.Framework;
using static Aibell.Playback.Miniaudio.Tests.EngineTestSupport;

namespace Aibell.Playback.Miniaudio.Tests;

/// <summary>The "C# Playback" decoders measured against miniaudio's on the same files (miniaudio is only a black-box yardstick here).</summary>
[TestFixture]
public sealed class ManagedPcmSourceTests
{
    private static float[] DrainAll(IPcmSource source)
    {
        List<float> all = [];
        float[] buffer = new float[4096 * 2];
        int read;
        while ((read = source.Read(buffer, 4096)) > 0) all.AddRange(buffer.AsSpan(0, read * 2).ToArray());
        return [.. all];
    }

    private static ManagedPcmSource OpenManaged(string fixture, uint rate)
    {
        ManagedPcmSource? source = ManagedPcmSource.TryOpen(File.OpenRead(Fixture(fixture)), rate);
        Assert.That(source, Is.Not.Null, fixture);
        return source!;
    }

    [TestCase("fixture.wav")]
    [TestCase("fixture.flac")]
    [TestCase("fixture.ogg")]
    public void SameRate_MatchesMiniaudioSampleForSample(string fixture)
    {
        using ManagedPcmSource managed = OpenManaged(fixture, 48000);
        using NativeDecoderSource? native = NativeDecoderSource.TryOpen(File.OpenRead(Fixture(fixture)), 48000, out string? failure);
        Assert.That(native, Is.Not.Null, failure);

        float[] expected = DrainAll(native!);
        float[] actual = DrainAll(managed);

        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        float worst = 0;
        for (int i = 0; i < expected.Length; i++) worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        Assert.That(worst, Is.LessThan(1e-6f));
    }

    [TestCase("fixture.wav")]
    [TestCase("fixture.flac")]
    [TestCase("fixture.ogg")]
    [TestCase("fixture.mp3")]
    [TestCase("fixture.opus")]
    public void OtherRate_ProducesTheRightLengthAndAToneOfTheRightLoudness(string fixture)
    {
        using ManagedPcmSource managed = OpenManaged(fixture, 44100);
        float[] pcm = DrainAll(managed);
        Assert.That(pcm.Length / 2, Is.InRange(44100 - 400, 44100 + 400));
        Assert.That(pcm.Max(), Is.GreaterThan(0.05f));
    }

    [Test]
    public void AnAacM4aFile_PlaysAndSeeks()
    {
        using ManagedPcmSource managed = OpenManaged("aac-mp4/stereo_44k.m4a", 48000);
        float[] pcm = DrainAll(managed);

        Assert.That(pcm.Length / 2, Is.InRange(24000 - 400, 24000 + 400));
        Assert.That(pcm.Max(), Is.GreaterThan(0.2f));
        Assert.That(managed.Seek(10000), Is.True);
        Assert.That(DrainAll(managed).Length / 2, Is.InRange(14000 - 400, 14000 + 400));
    }

    [Test]
    public void AnAppleLosslessFile_PlaysBitForBit()
    {
        using ManagedPcmSource managed = OpenManaged("alac.m4a", 44100);
        float[] pcm = DrainAll(managed);

        using System.IO.Compression.GZipStream gzip = new(File.OpenRead(Fixture("alac.pcm.gz")), System.IO.Compression.CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        int[] reference = new int[bytes.Length / 4];
        Buffer.BlockCopy(bytes.ToArray(), 0, reference, 0, reference.Length * 4);

        Assert.That(pcm, Has.Length.EqualTo(reference.Length));
        for (int i = 0; i < pcm.Length; i++)
        {
            if (pcm[i] * 32768f != reference[i] >> 16) Assert.Fail($"Sample {i} differs.");
        }
        Assert.That(managed.Seek(10000), Is.True);
        Assert.That(DrainAll(managed), Is.EqualTo(pcm[(10000 * 2)..]));
    }

    [Test]
    public void AnMp4FileThatNoManagedDecoderReads_IsLeftToTheOtherDecoders()
    {
        using FileStream stream = File.OpenRead(Fixture("aac-mp4/fragmented.m4a"));
        Assert.That(ManagedPcmSource.TryOpen(stream, 48000), Is.Null);
        Assert.That(stream.Position, Is.EqualTo(0));
    }

    [Test]
    public void AnAiffFile_PlaysAndSeeks()
    {
        short[] samples = new short[48000 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (i / 2) / 48000.0));
        using MemoryStream stream = new(Aibell.Audio.Aiff.Tests.AiffBuilder.Build(2, 48000, 16, Aibell.Audio.Aiff.Tests.AiffBuilder.Pcm16Be(samples)));
        using ManagedPcmSource? managed = ManagedPcmSource.TryOpen(stream, 48000);
        Assert.That(managed, Is.Not.Null);
        float[] pcm = DrainAll(managed!);
        Assert.That(pcm, Has.Length.EqualTo(samples.Length));
        Assert.That(managed!.Seek(12000), Is.True);
        Assert.That(DrainAll(managed), Is.EqualTo(pcm[(12000 * 2)..]));
    }

    [TestCase(".aiff")]
    [TestCase(".aif")]
    [TestCase(".aifc")]
    public async Task Factory_PlaysAiffWithoutTheManagedEngineSetting(string extension)
    {
        short[] samples = new short[4800 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (i / 2) / 48000.0));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, Aibell.Audio.Aiff.Tests.AiffBuilder.Build(2, 48000, 16, Aibell.Audio.Aiff.Tests.AiffBuilder.Pcm16Be(samples),
            compression: extension == ".aifc" ? "twos" : null));
        try
        {
            using HttpClient client = new();
            using IPcmSource source = await new AudioSourceFactory(client).OpenAsync(new Uri(path), 48000, CancellationToken.None);
            float[] buffer = new float[2 * 4800];
            Assert.That(source.Read(buffer, 4800), Is.EqualTo(4800));
            Assert.That(buffer.Max(Math.Abs), Is.GreaterThan(0.1f));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestCase("fixture.wav")]
    [TestCase("fixture.flac")]
    [TestCase("fixture.mp3")]
    [TestCase("fixture.ogg")]
    public void Seek_LandsOnTheRequestedFrame(string fixture)
    {
        using ManagedPcmSource managed = OpenManaged(fixture, 48000);
        float[] all = DrainAll(managed);
        Assert.That(managed.Seek(12000), Is.True);
        float[] tail = DrainAll(managed);
        Assert.That(tail, Is.EqualTo(all[(12000 * 2)..]));
    }

    [Test]
    public void Seek_InAnOpusFile_LandsCloseToPlayingStraightThrough()
    {
        using ManagedPcmSource managed = OpenManaged("fixture.opus", 48000);
        float[] all = DrainAll(managed);
        Assert.That(managed.Seek(12000), Is.True);
        float[] tail = DrainAll(managed);
        Assert.That(tail, Has.Length.EqualTo(all.Length - 12000 * 2));
        double signal = 0, noise = 0;
        for (int i = 0; i < tail.Length; i++)
        {
            signal += all[12000 * 2 + i] * all[12000 * 2 + i];
            noise += (tail[i] - all[12000 * 2 + i]) * (tail[i] - all[12000 * 2 + i]);
        }
        Assert.That(10 * Math.Log10(signal / Math.Max(noise, 1e-12)), Is.GreaterThan(40));
    }

    [Test]
    public void Seek_WithAnotherRate_ConvertsTheFramePosition()
    {
        using ManagedPcmSource managed = OpenManaged("fixture.wav", 44100);
        Assert.That(managed.Seek(22050), Is.True);
        Assert.That(DrainAll(managed).Length / 2, Is.InRange(22050 - 300, 22050 + 300));
    }

    [Test]
    public void UnknownData_IsLeftForTheOtherDecoders()
    {
        using MemoryStream stream = new("this is not audio at all, just text"u8.ToArray());
        Assert.That(ManagedPcmSource.TryOpen(stream, 48000), Is.Null);
        Assert.That(stream.Position, Is.Zero);
    }

    [Test]
    public void Id3v2Tag_IsSkippedBeforeTheAudio()
    {
        byte[] wav = File.ReadAllBytes(Fixture("fixture.wav"));
        byte[] tag = [(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 20, .. new byte[20]];
        using MemoryStream stream = new([.. tag, .. wav]);
        using ManagedPcmSource? source = ManagedPcmSource.TryOpen(stream, 48000);
        Assert.That(source, Is.Not.Null);
        Assert.That(DrainAll(source!).Length / 2, Is.InRange(48000 - 10, 48000 + 10));
    }

    [TestCase("fixture.wav")]
    [TestCase("fixture.flac")]
    [TestCase("fixture.ogg")]
    [TestCase("fixture.mp3")]
    [TestCase("fixture.opus")]
    public async Task Engine_WithManagedCodecs_PlaysToTheEnd(string fixture)
    {
        using MiniaudioPlaybackEngine engine = new(null, useNullBackend: true, managedCodecs: true);
        await engine.LoadAsync(TrackFor(fixture));
        await engine.PlayAsync();
        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status == PlaybackStatus.Ended, seconds: 15);
        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended));
    }
}
