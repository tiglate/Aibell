using System.IO.Compression;
using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

/// <summary>
/// AAC in MP4 / M4A files made by FFmpeg's encoder and muxer (see tests/fixtures/generate-aac-mp4.sh), compared with FFmpeg's own decode.
/// The reference is a development-time oracle only; the fixtures are synthetic signals generated for this repository.
/// </summary>
[TestFixture]
public sealed class Mp4AacTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "aac-mp4", name);

    private static Mp4AacDecoder Open(string name) => Mp4AacDecoder.Open(new MemoryStream(File.ReadAllBytes(FixturePath(name + ".m4a"))));

    private static float[] ReadAll(Mp4AacDecoder decoder)
    {
        List<float> samples = [];
        float[] buffer = new float[1000 * decoder.Channels];
        int frames;
        while ((frames = decoder.Read(buffer, 1000)) > 0) samples.AddRange(buffer.AsSpan(0, frames * decoder.Channels).ToArray());
        return [.. samples];
    }

    private static short[] Reference(string name)
    {
        using GZipStream gzip = new(File.OpenRead(FixturePath(name + ".pcm.gz")), CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        byte[] raw = bytes.ToArray();
        short[] samples = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, samples, 0, raw.Length);
        return samples;
    }

    // FFmpeg's reference was clipped to 16 bits and rounded; do the same to ours before comparing.
    private static double SnrDb(float[] actual, short[] expected, int count)
    {
        double signal = 0, error = 0;
        for (int i = 0; i < count; i++)
        {
            signal += (double)expected[i] * expected[i];
            double d = Math.Clamp(Math.Round(actual[i] * 32768f), -32768, 32767) - expected[i];
            error += d * d;
        }
        return error == 0 ? 200 : 10 * Math.Log10(signal / error);
    }

    [TestCase("stereo_44k", 44100, 2, 22050)]
    [TestCase("faststart_stereo_44k", 44100, 2, 22050)]
    [TestCase("mono_48k", 48000, 1, 24000)]
    [TestCase("surround_48k", 48000, 6, 14400)]
    public void Decode_MatchesFfmpegAndKeepsTheEditListLength(string name, int rate, int channels, int frames)
    {
        using Mp4AacDecoder decoder = Open(name);
        Assert.That((decoder.SampleRate, decoder.Channels, decoder.TotalFrames), Is.EqualTo((rate, channels, (long)frames)));

        float[] samples = ReadAll(decoder);

        // FFmpeg's decode keeps the padding of the last block; the length the edit list gives is what a player should stop at.
        Assert.That(samples, Has.Length.EqualTo(frames * channels));
        short[] reference = Reference(name);
        Assert.That(reference.Length, Is.GreaterThanOrEqualTo(samples.Length));
        Assert.That(SnrDb(samples, reference, samples.Length), Is.GreaterThan(50), name);
    }

    [Test]
    public void Decode_WithoutAnEditList_PlaysTheEncoderDelayAndStopsAtTheMediaDuration()
    {
        using Mp4AacDecoder decoder = Open("noedit_stereo_44k");
        float[] samples = ReadAll(decoder);
        short[] reference = Reference("noedit_stereo_44k");

        // Nothing is skipped at the start (FFmpeg's decode starts at the same place); the time-to-sample table ends the audio 478 frames before the last block does.
        Assert.That(decoder.TotalFrames, Is.EqualTo(22050 + 1024));
        Assert.That(samples, Has.Length.EqualTo(decoder.TotalFrames * 2));
        Assert.That(SnrDb(samples, reference, samples.Length), Is.GreaterThan(50));
    }

    [TestCase("stereo_44k")]
    [TestCase("surround_48k")]
    public void Seek_LandsOnTheSameSamplesAsPlayingFromTheStart(string name)
    {
        using Mp4AacDecoder whole = Open(name);
        float[] expected = ReadAll(whole);
        int channels = whole.Channels;
        using Mp4AacDecoder decoder = Open(name);
        foreach (long target in new long[] { 0, 1, 1023, 1024, 1025, 5000, 9999, whole.TotalFrames - 1 })
        {
            Assert.That(decoder.Seek(target), Is.True, $"seek {target}");
            float[] buffer = new float[300 * channels];
            int frames = decoder.Read(buffer, 300);
            Assert.That(frames, Is.EqualTo((int)Math.Min(300, whole.TotalFrames - target)), $"read after {target}");
            for (int i = 0; i < frames * channels; i++)
            {
                if (buffer[i] != expected[target * channels + i]) Assert.Fail($"Seek {target}: sample {i} differs.");
            }
        }
    }

    [Test]
    public void Seek_ToTheEndReadsNothing_AndBeyondTheEndFails()
    {
        using Mp4AacDecoder decoder = Open("stereo_44k");

        Assert.That(decoder.Seek(decoder.TotalFrames), Is.True);
        Assert.That(decoder.Read(new float[64], 32), Is.EqualTo(0));
        Assert.That(decoder.Seek(decoder.TotalFrames + 1), Is.False);
        Assert.That(decoder.Seek(-1), Is.False);
        Assert.That(decoder.Seek(0), Is.True);
        Assert.That(decoder.Read(new float[64], 32), Is.EqualTo(32));
    }

    [Test]
    public void Decode_AllocatesNothingOnceOpen()
    {
        using Mp4AacDecoder decoder = Open("stereo_44k");
        float[] buffer = new float[4096 * 2];
        decoder.Read(buffer, 4096);

        long before = GC.GetAllocatedBytesForCurrentThread();
        while (decoder.Read(buffer, 4096) > 0) { }
        decoder.Seek(3000);
        decoder.Read(buffer, 4096);

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(1024));
    }

    [Test]
    public void Open_FragmentedFile_IsRefused()
    {
        Assert.That(() => Open("fragmented"), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Open_FileWithoutAMovieBox_IsRefused()
    {
        byte[] data = File.ReadAllBytes(FixturePath("stereo_44k.m4a"));
        // The movie box of this file is last: cutting the file short removes it.
        Assert.That(() => Mp4AacDecoder.Open(new MemoryStream(data, 0, data.Length / 2)), Throws.InstanceOf<Exception>().And.Matches<Exception>(e => e is NotSupportedException or InvalidDataException));
    }

    [Test]
    public void Open_HeAac_IsRefused()
    {
        byte[] data = File.ReadAllBytes(FixturePath("stereo_44k.m4a"));
        // AudioSpecificConfig of AAC-LC, 44.1 kHz, stereo is 12 10; object type 5 (SBR) makes it 2a 10.
        int at = data.AsSpan().IndexOf(new byte[] { 0x05, 0x80, 0x80, 0x80, 0x05, 0x12, 0x10 });
        Assert.That(at, Is.GreaterThan(0));
        data[at + 5] = 0x2a;

        Assert.That(() => Mp4AacDecoder.Open(new MemoryStream(data)), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Open_FileCutShortInTheAudio_PlaysWhatIsThere()
    {
        // moov first, then mdat: a download cut off in the middle of mdat leaves a playable beginning.
        byte[] data = File.ReadAllBytes(FixturePath("faststart_stereo_44k.m4a"));
        using Mp4AacDecoder decoder = Mp4AacDecoder.Open(new MemoryStream(data, 0, data.Length * 2 / 3));

        float[] samples = ReadAll(decoder);

        Assert.That(decoder.TotalFrames, Is.InRange(5000, 22049));
        Assert.That(samples, Has.Length.EqualTo(decoder.TotalFrames * 2));
    }

    [Test]
    public void Decode_DamagedBlocks_BecomeSilenceAndPlaybackContinues()
    {
        byte[] data = File.ReadAllBytes(FixturePath("faststart_stereo_44k.m4a"));
        Random random = new(7);
        // The audio is the last part of the file: damage a stretch in the middle of it.
        for (int i = data.Length / 2; i < data.Length / 2 + 300; i++) data[i] = (byte)random.Next(256);
        using Mp4AacDecoder decoder = Mp4AacDecoder.Open(new MemoryStream(data));

        float[] samples = ReadAll(decoder);

        Assert.That(samples, Has.Length.EqualTo(decoder.TotalFrames * 2));
    }

    [TestCase(new byte[] { 0x2a, 0x10 }, "HE-AAC")]
    [TestCase(new byte[] { 0x12, 0x10, 0x56, 0xe5, 0x80 }, "HE-AAC")]
    [TestCase(new byte[] { 0x17, 0x90 }, "explicit sampling rate")]
    [TestCase(new byte[] { 0x12, 0x00 }, "channel configuration")]
    [TestCase(new byte[] { 0x12, 0x14 }, "960")]
    [TestCase(new byte[] { 0x12, 0x12 }, "Scalable")]
    [TestCase(new byte[] { 0x22, 0x10 }, "object type")]
    public void ParseConfig_RefusesWhatTheDecoderCannotPlay(byte[] config, string reason)
    {
        Assert.That(() => AudioSpecificConfig.Parse(config), Throws.TypeOf<NotSupportedException>().With.Message.Contain(reason));
    }

    [Test]
    public void ParseConfig_ReadsAacLc()
    {
        AudioSpecificConfig config = AudioSpecificConfig.Parse([0x12, 0x10]);

        Assert.That((config.ObjectType, config.SampleRate, config.ChannelConfiguration, config.Channels), Is.EqualTo((2, 44100, 2, 2)));
        Assert.That(AudioSpecificConfig.Parse([0x11, 0xb8]).Channels, Is.EqualTo(8));
        // The SBR extension with sbrPresentFlag 0, as FFmpeg's muxer writes it, is plain AAC-LC.
        Assert.That(AudioSpecificConfig.Parse([0x12, 0x10, 0x56, 0xe5, 0x00]).SampleRate, Is.EqualTo(44100));
        Assert.That(() => AudioSpecificConfig.Parse([0x12]), Throws.TypeOf<InvalidDataException>());
    }
}
