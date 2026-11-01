using System.IO.Compression;
using System.Text;
using Aibell.Application.Playback;
using Aibell.Audio.Aac;
using Aibell.Audio.Aac.Tests;
using Aibell.Playback.Core;
using NUnit.Framework;
using static Aibell.Playback.Miniaudio.Tests.EngineTestSupport;

namespace Aibell.Playback.Miniaudio.Tests;

[TestFixture]
public sealed class StereoResamplerTests
{
    private static float[] Sine(int rate, int frames, double frequency)
    {
        float[] stereo = new float[frames * 2];
        for (int n = 0; n < frames; n++) stereo[2 * n] = stereo[2 * n + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * n / rate));
        return stereo;
    }

    private static float[] Convert(float[] input, int inputRate, int outputRate)
    {
        StereoResampler resampler = new(inputRate, outputRate, 1000);
        List<float> output = [];
        float[] chunk = new float[4000 * 2];
        for (int offset = 0; offset < input.Length / 2; offset += 1000)
        {
            int frames = Math.Min(1000, input.Length / 2 - offset);
            resampler.Push(input.AsSpan(offset * 2, frames * 2), frames);
            int got;
            while ((got = resampler.Pull(chunk, 4000)) > 0) output.AddRange(chunk.AsSpan(0, got * 2).ToArray());
        }
        return [.. output];
    }

    [TestCase(48000, 44100)]
    [TestCase(44100, 48000)]
    [TestCase(96000, 48000)]
    [TestCase(22050, 48000)]
    [TestCase(8000, 44100)]
    [TestCase(24000, 44100)]
    public void Convert_KeepsAToneAtTheRightFrequencyAndAmplitude(int inputRate, int outputRate)
    {
        const double Frequency = 1000;
        float[] output = Convert(Sine(inputRate, inputRate, Frequency), inputRate, outputRate);

        Assert.That(output.Length / 2, Is.InRange(outputRate - 80, outputRate + 2));
        double worst = 0;
        for (int m = 400; m < output.Length / 2 - 400; m++)
        {
            double expected = 0.5 * Math.Sin(2 * Math.PI * Frequency * m / outputRate);
            worst = Math.Max(worst, Math.Abs(output[2 * m] - expected));
            Assert.That(output[2 * m + 1], Is.EqualTo(output[2 * m]));
        }
        Assert.That(worst, Is.LessThan(2e-3));
    }

    [Test]
    public void Convert_DownsamplingRemovesFrequenciesAboveTheNewNyquist()
    {
        // 30 kHz at a 96 kHz input would alias to 18 kHz in a naive 48 kHz decimation.
        float[] output = Convert(Sine(96000, 96000, 30000), 96000, 48000);
        double peak = 0;
        for (int m = 400; m < output.Length / 2 - 400; m++) peak = Math.Max(peak, Math.Abs(output[2 * m]));
        Assert.That(peak, Is.LessThan(0.005));
    }

    [Test]
    public void Convert_EqualRatesPassSamplesThroughUnchanged()
    {
        float[] input = Sine(48000, 3000, 440);
        Assert.That(Convert(input, 48000, 48000), Is.EqualTo(input));
        Assert.That(new StereoResampler(48000, 48000, 10).Passthrough, Is.True);
    }
}

[TestFixture]
public sealed class AacPlaybackTests
{
    private HlsTestServer server = null!;
    private HttpClient client = null!;

    private static readonly HlsOptions Fast = new()
    {
        RequestTimeout = TimeSpan.FromSeconds(5),
        StallTimeout = TimeSpan.FromSeconds(2),
        MinimumReload = TimeSpan.FromMilliseconds(20),
        MaximumReload = TimeSpan.FromMilliseconds(100)
    };

    [SetUp]
    public void Start()
    {
        server = new HlsTestServer();
        client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
    }

    [TearDown]
    public void Stop()
    {
        server.Dispose();
        client.Dispose();
    }

    private static string AacPath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "aac", name);

    private static List<byte[]> Frames(string name)
    {
        byte[] data = File.ReadAllBytes(AacPath(name + ".aac"));
        List<byte[]> frames = [];
        for (int offset = 0; offset < data.Length && AdtsHeader.TryParse(data.AsSpan(offset), out AdtsHeader header); offset += header.FrameLength)
            frames.Add(data.AsSpan(offset, header.FrameLength).ToArray());
        return frames;
    }

    private static float[] DecodeDirectly(byte[] data)
    {
        AdtsFrameReader reader = new();
        AacDecoder decoder = new();
        data.CopyTo(reader.GetWriteBuffer());
        reader.Advance(data.Length);
        reader.Complete();
        List<float> samples = [];
        while (reader.TryRead(out AdtsHeader header, out ReadOnlySpan<byte> payload))
        {
            decoder.Decode(header, payload);
            for (int n = 0; n < AacDecoder.FrameLength; n++)
            {
                for (int c = 0; c < 2; c++) samples.Add(decoder.GetChannel(c)[n] * 32768f);
            }
        }
        return [.. samples];
    }

    private static byte[] Concat(IEnumerable<byte[]> parts) => [.. parts.SelectMany(part => part)];

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

    private static string Playlist(int[] segments, bool end = true, string extension = "ts", string extra = "")
    {
        StringBuilder text = new("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n" + extra);
        foreach (int number in segments) text.Append("#EXTINF:1.0,\nseg").Append(number).Append('.').Append(extension).Append('\n');
        if (end) text.Append("#EXT-X-ENDLIST\n");
        return text.ToString();
    }

    [Test]
    public void Source_PlaysAdtsAtTheDeviceRate()
    {
        byte[] data = File.ReadAllBytes(AacPath("long_sweep_44k_stereo.aac"));
        int decodedFrames = Frames("long_sweep_44k_stereo").Count * 1024;

        using AacPcmSource? source = AacPcmSource.TryOpen(new MemoryStream(data), 48000);
        Assert.That(source, Is.Not.Null);
        (long frames, float peak) = Drain(source!);

        Assert.That(frames, Is.InRange(decodedFrames * 48000L / 44100 - 200, decodedFrames * 48000L / 44100 + 50));
        Assert.That(peak, Is.GreaterThan(0.2f));
    }

    [Test]
    public void Source_AtTheStreamRateIsBitExactWithTheDecoder()
    {
        byte[] data = File.ReadAllBytes(AacPath("long_noise_48k_stereo.aac"));
        float[] expected = DecodeDirectly(data);
        using AacPcmSource source = AacPcmSource.TryOpen(new MemoryStream(data), 48000)!;

        float[] buffer = new float[2 * expected.Length / 2 + 4096];
        int frames = source.Read(buffer, buffer.Length / 2);
        List<float> all = [.. buffer.AsSpan(0, frames * 2).ToArray()];

        Assert.That(all, Has.Count.EqualTo(expected.Length));
        double worst = 0;
        for (int i = 0; i < expected.Length; i++) worst = Math.Max(worst, Math.Abs(all[i] * 32768f - expected[i]));
        Assert.That(worst, Is.LessThan(1e-3));
    }

    [Test]
    public void Source_RecoversFromGarbageInTheMiddleOfTheStream()
    {
        List<byte[]> frames = Frames("long_sweep_44k_stereo");
        byte[] data = Concat([.. frames.Take(5), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], .. frames.Skip(5).Take(5), new byte[3000], .. frames.Skip(10)]);

        using AacPcmSource source = AacPcmSource.TryOpen(new MemoryStream(data), 44100)!;
        (long played, _) = Drain(source);

        Assert.That(played, Is.GreaterThan((frames.Count - 3) * 1024L));
    }

    [Test]
    public void Source_ANonAacStreamIsLeftAloneForTheOtherDecoders()
    {
        MemoryStream mp3 = new(File.ReadAllBytes(Fixture("fixture.mp3")));
        Assert.That(AacPcmSource.TryOpen(mp3, 48000), Is.Null);
        Assert.That(mp3.Position, Is.Zero);
    }

    [Test]
    public void Source_HeAacIsRefusedUpFrontWithTheChooseVlcMessage()
    {
        byte[] sbr = Concat(Enumerable.Range(0, 8).Select(_ => AacTestData.FillFrame(13, samplingIndex: 6)));
        NotSupportedException? failure = Assert.Throws<NotSupportedException>(() => AacPcmSource.TryOpen(new MemoryStream(sbr), 48000));
        Assert.That(failure!.Message, Does.Contain("HE-AAC").And.Contain("Choose VLC"));
    }

    [Test]
    public void Source_SeekIsRefusedForStreamsThatAreNotLocalFilesAndReadsContinue()
    {
        using AacPcmSource source = AacPcmSource.TryOpen(new MemoryStream(File.ReadAllBytes(AacPath("long_sweep_44k_stereo.aac"))), 44100)!;
        Assert.That(source.Seek(5000), Is.False);
        Assert.That(source.Read(new float[2048], 1024), Is.EqualTo(1024));
    }

    /// <summary>Rewrites ADTS frames with CRC protection (protection_absent = 0 and the two CRC bytes), computed with the library's own CRC code.</summary>
    private static List<byte[]> WithCrc(List<byte[]> frames, Func<int, ushort, ushort>? alter = null)
    {
        AacDecoder decoder = new();
        List<byte[]> result = [];
        for (int i = 0; i < frames.Count; i++)
        {
            byte[] plain = frames[i];
            byte[] frame = new byte[plain.Length + 2];
            plain.AsSpan(0, 7).CopyTo(frame);
            plain.AsSpan(7).CopyTo(frame.AsSpan(9));
            frame[1] &= 0xfe;
            int length = frame.Length;
            frame[3] = (byte)(frame[3] & 0xfc | length >> 11);
            frame[4] = (byte)(length >> 3);
            frame[5] = (byte)(frame[5] & 0x1f | (length & 7) << 5);
            Assert.That(AdtsHeader.TryParse(frame, out AdtsHeader header), Is.True);
            decoder.Decode(header, frame.AsSpan(9));
            ushort crc = AdtsCrc.Compute(frame, 9, decoder.ProtectedRanges);
            if (alter is not null) crc = alter(i, crc);
            frame[7] = (byte)(crc >> 8);
            frame[8] = (byte)crc;
            result.Add(frame);
        }
        return result;
    }

    [Test]
    public void Source_CrcProtectedStream_PlaysAndSilencesOnlyTheFrameThatFailsItsCrc()
    {
        List<byte[]> good = WithCrc(Frames("long_noise_48k_stereo"));
        Assert.That(good.Count, Is.GreaterThan(8));
        using AacPcmSource clean = AacPcmSource.TryOpen(new MemoryStream(Concat(good)), 48000)!;
        float[] reference = ReadAll(clean);

        List<byte[]> damaged = WithCrc(Frames("long_noise_48k_stereo"), (index, crc) => index == 5 ? (ushort)(crc ^ 1) : crc);
        using AacPcmSource source = AacPcmSource.TryOpen(new MemoryStream(Concat(damaged)), 48000)!;
        float[] played = ReadAll(source);

        Assert.That(played, Has.Length.EqualTo(reference.Length));
        Assert.That(played.AsSpan(5 * 2048, 2048).ToArray(), Is.All.Zero, "the frame with the bad CRC is silenced");
        Assert.That(played.AsSpan(3 * 2048, 2048).ToArray(), Is.EqualTo(reference.AsSpan(3 * 2048, 2048).ToArray()));
        Assert.That(played.AsSpan(8 * 2048, 2048).ToArray().Any(value => value != 0), Is.True);
    }

    [Test]
    public void Source_StreamWhoseCrcNeverMatches_IsPlayedInFull()
    {
        // If our reading of the CRC rules were wrong, silencing everything would be worse than ignoring the check.
        List<byte[]> frames = WithCrc(Frames("long_noise_48k_stereo"), (_, crc) => (ushort)(crc ^ 0x1234));
        using AacPcmSource source = AacPcmSource.TryOpen(new MemoryStream(Concat(frames)), 48000)!;
        float[] played = ReadAll(source);
        Assert.That(played.AsSpan(4 * 2048, 2048).ToArray().Any(value => value != 0), Is.True);
        Assert.That(played, Has.Length.EqualTo(frames.Count * 2048));
    }

    /// <summary>A constant-bitrate file: the same frame many times, so a byte estimate of a position is exact.</summary>
    private static string ConstantRateFile(int repeats)
    {
        List<byte[]> frames = Frames("long_noise_48k_stereo");
        byte[] frame = frames[4];
        string path = Path.Combine(Path.GetTempPath(), $"aibell-cbr-{Guid.NewGuid():N}.aac");
        File.WriteAllBytes(path, Concat(Enumerable.Repeat(frame, repeats)));
        return path;
    }

    [Test]
    public async Task Factory_RangeCapableNetworkAacFile_SeeksByEstimate()
    {
        string path = ConstantRateFile(80);
        try
        {
            using AudioTestServer fileServer = new(path, live: false);
            using IPcmSource source = await new AudioSourceFactory(client, Fast).OpenAsync(fileServer.Address, 48000, CancellationToken.None);
            Assert.That(source, Is.TypeOf<AacPcmSource>());
            float[] buffer = new float[2 * 1024];
            Assert.That(source.Read(buffer, 1024), Is.EqualTo(1024));

            Assert.That(source.Seek(40 * 1024), Is.True);
            long left = 0;
            int read;
            while ((read = source.Read(buffer, 1024)) > 0) left += read;

            // Landing at frame 40 of 80, give or take the frame used to rebuild the overlap.
            Assert.That(left, Is.InRange(39 * 1024, 41 * 1024));
            Assert.That(source.Seek(0), Is.True);
            Assert.That(source.Read(buffer, 1024), Is.EqualTo(1024));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Factory_LiveNetworkAac_RefusesSeekAndKeepsPlaying()
    {
        string path = ConstantRateFile(80);
        try
        {
            using AudioTestServer live = new(path, live: true);
            using IPcmSource source = await new AudioSourceFactory(client, Fast).OpenAsync(live.Address, 48000, CancellationToken.None);
            float[] buffer = new float[2 * 4096];
            Assert.That(source.Read(buffer, 4096), Is.GreaterThan(0));
            Assert.That(source.Seek(40 * 1024), Is.False);
            Assert.That(source.Read(buffer, 1024), Is.GreaterThan(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestCase(3, false)]
    [TestCase(4, true)]
    public void Source_FramesWithSeveralRawDataBlocks_PlayLikeSingleBlockFrames(int perFrame, bool crc)
    {
        List<byte[]> singles = Frames("long_noise_48k_stereo");
        using AacPcmSource expected = AacPcmSource.TryOpen(new MemoryStream(Concat(singles)), 48000)!;
        using AacPcmSource actual = AacPcmSource.TryOpen(new MemoryStream(Concat(AacTestData.Regroup(singles, perFrame, crc))), 48000)!;
        Assert.That(ReadAll(actual), Is.EqualTo(ReadAll(expected)));
    }

    [Test]
    public void Source_StreamWithCouplingElementsAndNoChannelConfiguration_PlaysFoldedDownToStereo()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "aac-synthetic", "cce_dependent_before_tns.aac"));
        using AacPcmSource source = AacPcmSource.TryOpen(new MemoryStream(stream), 44100)!;
        float[] played = ReadAll(source);
        Assert.That(played, Has.Length.EqualTo(8 * 1024 * 2));
        Assert.That(played.Max(Math.Abs), Is.GreaterThan(0.01f), "the centre and the pair reach the stereo mix");
        Assert.That(played.Max(Math.Abs), Is.LessThanOrEqualTo(1f));
    }

    private static float[] ReadAll(AacPcmSource source)
    {
        List<float> all = [];
        float[] buffer = new float[2 * 4096];
        int read;
        while ((read = source.Read(buffer, 4096)) > 0) all.AddRange(buffer.AsSpan(0, read * 2).ToArray());
        return [.. all];
    }

    [TestCase(0)]
    [TestCase(1024)]
    [TestCase(5 * 1024)]
    [TestCase(5 * 1024 + 700)]
    [TestCase(1000000)]
    public void Source_SeekInALocalFile_ContinuesWithTheSameSamples(int frame)
    {
        using AacPcmSource source = AacPcmSource.TryOpen(File.OpenRead(AacPath("long_noise_48k_stereo.aac")), 48000)!;
        float[] full = ReadAll(source);

        Assert.That(source.Seek(frame), Is.True);
        float[] rest = ReadAll(source);

        int expectedStart = (int)Math.Min(frame / 1024, full.Length / 2 / 1024 - 1) * 1024;
        Assert.That(rest, Has.Length.EqualTo(full.Length - expectedStart * 2));
        Assert.That(rest, Is.EqualTo(full.AsSpan(expectedStart * 2).ToArray()));
    }

    [Test]
    public void Source_SeekTwiceAndAcrossRates_StaysConsistent()
    {
        using AacPcmSource source = AacPcmSource.TryOpen(File.OpenRead(AacPath("long_sweep_44k_stereo.aac")), 48000)!;
        float[] full = ReadAll(source);
        Assert.That(source.Seek(48000 / 10), Is.True);
        float[] tenth = ReadAll(source);
        Assert.That(source.Seek(0), Is.True);
        float[] again = ReadAll(source);

        Assert.That(again, Is.EqualTo(full).Within(1e-6));
        Assert.That(tenth.Length, Is.InRange(full.Length - 2 * 48000 / 10 - 2 * 2400, full.Length - 2 * 48000 / 10 + 2 * 2400));
    }

    [Test]
    public async Task Factory_PlainHttpAacWithoutAnExtension_Plays()
    {
        server.Map("/radio", File.ReadAllBytes(AacPath("long_sweep_44k_stereo.aac")));
        AudioSourceFactory factory = new(client, Fast);

        using IPcmSource source = await factory.OpenAsync(server.Address("radio"), 48000, CancellationToken.None);
        (long frames, float peak) = Drain(source);

        Assert.That(source, Is.TypeOf<AacPcmSource>());
        Assert.That(frames, Is.GreaterThan(10000));
        Assert.That(peak, Is.GreaterThan(0.2f));
    }

    [Test]
    public async Task Factory_AacExtensionUrl_PlaysWithoutMediaFoundation()
    {
        server.Map("/live.aac", File.ReadAllBytes(AacPath("long_sweep_44k_stereo.aac")));
        using IPcmSource source = await new AudioSourceFactory(client, Fast).OpenAsync(server.Address("live.aac"), 44100, CancellationToken.None);
        Assert.That(Drain(source).Frames, Is.GreaterThan(10000));
    }

    [Test]
    public async Task Factory_Mp3OverHttp_StillGoesToMiniaudio()
    {
        server.Map("/radio.mp3", File.ReadAllBytes(Fixture("fixture.mp3")));
        using IPcmSource source = await new AudioSourceFactory(client, Fast, nativeDecoderFallback: MiniaudioDecoders).OpenAsync(server.Address("radio.mp3"), 48000, CancellationToken.None);
        Assert.That(source, Is.TypeOf<NativeDecoderSource>());
    }

    [Test]
    public async Task Hls_TransportStreamSegments_PlayAcrossSegmentsAndContinuityResets()
    {
        List<byte[]> frames = Frames("long_sweep_44k_stereo");
        // Each segment restarts its continuity counter somewhere else, as encoders and CDNs do.
        server.Map("/ts/index.m3u8", Playlist([0, 1, 2]));
        for (int number = 0; number < 3; number++) server.Map($"/ts/seg{number}.ts", TestMpegTs.Mux(frames, framesPerPes: 3, firstContinuity: number * 5));
        AudioSourceFactory factory = new(client, Fast);

        using IPcmSource source = await factory.OpenAsync(server.Address("ts/index.m3u8"), 44100, CancellationToken.None);
        (long played, float peak) = Drain(source);

        Assert.That(source, Is.TypeOf<AacPcmSource>());
        Assert.That(played, Is.InRange(3L * frames.Count * 1024 - 2000, 3L * frames.Count * 1024 + 100));
        Assert.That(peak, Is.GreaterThan(0.2f));
    }

    [Test]
    public async Task Hls_GzippedPlaylist_IsDecompressed()
    {
        List<byte[]> frames = Frames("long_sweep_44k_stereo");
        using MemoryStream zipped = new();
        using (System.IO.Compression.GZipStream gzip = new(zipped, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(Playlist([0])));
        server.Map("/gz/index.m3u8", zipped.ToArray());
        server.Map("/gz/seg0.ts", TestMpegTs.Mux(frames));

        using IPcmSource source = await new AudioSourceFactory(client, Fast).OpenAsync(server.Address("gz/index.m3u8"), 44100, CancellationToken.None);

        Assert.That(Drain(source).Frames, Is.GreaterThan(10000));
    }

    [Test]
    public async Task Hls_PackedAdtsSegmentsWithId3Tags_Play()
    {
        List<byte[]> frames = Frames("long_sweep_44k_stereo");
        byte[] tag = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 45, .. new byte[45]];
        server.Map("/aac/index.m3u8", Playlist([0, 1], extension: "aac"));
        for (int number = 0; number < 2; number++) server.Map($"/aac/seg{number}.aac", Concat([tag, .. frames]));

        using IPcmSource source = await new AudioSourceFactory(client, Fast).OpenAsync(server.Address("aac/index.m3u8"), 44100, CancellationToken.None);
        (long played, _) = Drain(source);

        Assert.That(played, Is.InRange(2L * frames.Count * 1024 - 2000, 2L * frames.Count * 1024 + 100));
    }

    [Test]
    public async Task Hls_MasterPlaylistWithHeAacAndLcVariants_ChoosesLc()
    {
        List<byte[]> frames = Frames("long_sweep_44k_stereo");
        server.Map("/m/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=32000,CODECS=\"mp4a.40.5\"\nhe.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.40.2\"\nlc.m3u8\n");
        server.Map("/m/lc.m3u8", Playlist([0]));
        server.Map("/m/seg0.ts", TestMpegTs.Mux(frames));

        using IPcmSource source = await new AudioSourceFactory(client, Fast).OpenAsync(server.Address("m/master.m3u8"), 44100, CancellationToken.None);

        Assert.That(Drain(source).Frames, Is.GreaterThan(10000));
    }

    [Test]
    public async Task Hls_TransportStreamWithOtherAudio_IsRefusedWithTheChooseVlcMessage()
    {
        server.Map("/ac3/index.m3u8", Playlist([0]));
        server.Map("/ac3/seg0.ts", TestMpegTs.Mux(Frames("long_sweep_44k_stereo"), stream: 0x81));

        await Assert.ThatAsync(() => new AudioSourceFactory(client, Fast).OpenAsync(server.Address("ac3/index.m3u8"), 44100, CancellationToken.None),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("Choose VLC"));
    }

    [Test]
    public async Task Hls_HeAacTransportStream_IsRefusedWithTheChooseVlcMessage()
    {
        List<byte[]> sbr = [.. Enumerable.Range(0, 10).Select(_ => AacTestData.FillFrame(13, samplingIndex: 6))];
        server.Map("/he/index.m3u8", Playlist([0]));
        server.Map("/he/seg0.ts", TestMpegTs.Mux(sbr));

        await Assert.ThatAsync(() => new AudioSourceFactory(client, Fast).OpenAsync(server.Address("he/index.m3u8"), 44100, CancellationToken.None),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("HE-AAC").And.Message.Contains("Choose VLC"));
    }

    [Test]
    public async Task Hls_LiveTransportStreamKeepsPlayingNewSegmentsUntilItStalls()
    {
        List<byte[]> frames = Frames("long_sweep_44k_stereo");
        // A window that advances: the first playlist shows segments 0-1, later ones 1-3 and then nothing new.
        server.Map("/live/index.m3u8", number => HlsReply.Playlist(number switch
        {
            1 => Playlist([0, 1], end: false),
            _ => "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n" +
                 "#EXTINF:1.0,\nseg0.ts\n#EXTINF:1.0,\nseg1.ts\n#EXTINF:1.0,\nseg2.ts\n#EXTINF:1.0,\nseg3.ts\n"
        }));
        for (int number = 0; number < 4; number++) server.Map($"/live/seg{number}.ts", TestMpegTs.Mux(frames, firstContinuity: number * 3));

        using IPcmSource source = await new AudioSourceFactory(client, Fast).OpenAsync(server.Address("live/index.m3u8"), 44100, CancellationToken.None);
        (long played, _) = Drain(source);

        Assert.That(played, Is.GreaterThan(3L * frames.Count * 1024));
    }

    [Test]
    public async Task Engine_AacTransportStreamHls_PlaysToTheEnd()
    {
        List<byte[]> frames = Frames("long_sweep_44k_stereo");
        server.Map("/e/index.m3u8", Playlist([0, 1]));
        for (int number = 0; number < 2; number++) server.Map($"/e/seg{number}.ts", TestMpegTs.Mux(frames));
        using var engine = NewEngine(client);
        await engine.LoadAsync(TrackFor(server.Address("e/index.m3u8"), 1));

        await engine.PlayAsync();

        PlaybackSnapshot ended = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted, 15);
        Assert.That(ended.Status, Is.EqualTo(PlaybackStatus.Ended), ended.Error);
    }

    [Test]
    public async Task Engine_HeAacStream_FaultsWithTheChooseVlcMessage()
    {
        server.Map("/radio-he", Concat(Enumerable.Range(0, 12).Select(_ => AacTestData.FillFrame(13, samplingIndex: 6))));
        using var engine = NewEngine(client);
        await engine.LoadAsync(TrackFor(server.Address("radio-he"), 1));

        try { await engine.PlayAsync(); }
        catch (Exception) { /* reported by the call or by the state */ }

        if (OperatingSystem.IsWindows())
        {
            // Windows hands a stream the managed decoder refuses to Media Foundation, which plays it, so nothing tells the user to choose VLC.
            PlaybackSnapshot played = await WaitForAsync(engine, state => state.Status is PlaybackStatus.Ended or PlaybackStatus.Faulted, 8);
            Assert.That(played.Error ?? "", Does.Not.Contain("Choose VLC"));
            return;
        }
        PlaybackSnapshot faulted = await WaitForAsync(engine, state => state.Status == PlaybackStatus.Faulted, 8);
        Assert.That(faulted.Error, Does.Contain("HE-AAC").And.Contain("Choose VLC"));
    }
}
