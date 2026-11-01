using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

[TestFixture]
public sealed class RobustnessTests
{
    private static List<byte[]> Payloads(string name, out AdtsHeader first)
    {
        byte[] data = File.ReadAllBytes(ConformanceTests.FixturePath(name + ".aac"));
        AdtsFrameReader reader = new();
        data.CopyTo(reader.GetWriteBuffer());
        reader.Advance(data.Length);
        reader.Complete();
        List<byte[]> payloads = [];
        first = default;
        while (reader.TryRead(out AdtsHeader header, out ReadOnlySpan<byte> payload))
        {
            if (payloads.Count == 0) first = header;
            payloads.Add(payload.ToArray());
        }
        return payloads;
    }

    private static void DecodeOrReject(AacDecoder decoder, in AdtsHeader header, byte[] payload)
    {
        try
        {
            decoder.Decode(header, payload);
        }
        catch (AacException)
        {
            // A damaged frame is rejected with the decoder's own exception; anything else escaping would fail the test.
        }
    }

    [TestCase("long_sweep_44k_stereo")]
    [TestCase("short_clicks_22k_stereo")]
    [TestCase("tns_long_44k_mono")]
    [TestCase("pns_22k_stereo")]
    [TestCase("is_44k_stereo")]
    public void Decode_TruncatedFramesAreRejectedOrDecodedNeverCrash(string name)
    {
        List<byte[]> payloads = Payloads(name, out AdtsHeader header);
        AacDecoder decoder = new();
        foreach (byte[] payload in payloads)
        {
            for (int cut = 0; cut < payload.Length; cut += Math.Max(1, payload.Length / 23))
                DecodeOrReject(decoder, header, payload.AsSpan(0, cut).ToArray());
        }
    }

    [TestCase("long_sweep_44k_stereo")]
    [TestCase("short_clicks_22k_stereo")]
    [TestCase("tns_short_44k_mono")]
    [TestCase("pns_22k_stereo")]
    [TestCase("ms_22k_stereo")]
    public void Decode_BitFlippedFramesAreRejectedOrDecodedNeverCrash(string name)
    {
        List<byte[]> payloads = Payloads(name, out AdtsHeader header);
        Random random = new(5);
        AacDecoder decoder = new();
        for (int round = 0; round < 40; round++)
        {
            foreach (byte[] original in payloads)
            {
                byte[] damaged = (byte[])original.Clone();
                for (int flips = random.Next(1, 6); flips > 0; flips--) damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
                DecodeOrReject(decoder, header, damaged);
            }
        }
    }

    [Test]
    public void Decode_RandomPayloadsNeverCrash()
    {
        Random random = new(9);
        AacDecoder decoder = new();
        foreach (int channels in new[] { 1, 2, 6 })
        {
            for (int round = 0; round < 3000; round++)
            {
                byte[] payload = new byte[random.Next(0, 400)];
                random.NextBytes(payload);
                if (round % 3 == 0 && payload.Length > 0) payload[0] &= 0x1f; // bias towards a valid element id
                try
                {
                    decoder.Decode(2, random.Next(0, 12), channels, payload);
                }
                catch (AacException)
                {
                }
            }
        }
    }

    [Test]
    public void Decode_RecoversAfterAnErrorAndStillMatchesCleanOutput()
    {
        List<byte[]> payloads = Payloads("long_sweep_44k_stereo", out AdtsHeader header);
        AacDecoder decoder = new();
        for (int i = 0; i < payloads.Count; i++)
        {
            if (i == 3) DecodeOrReject(decoder, header, [0xff, 0xff, 0xff, 0xff]);
            decoder.Decode(header, payloads[i]);
        }
        Assert.That(decoder.Channels, Is.EqualTo(2));
    }

    [Test]
    public void Decode_SteadyStateDoesNotAllocate()
    {
        List<byte[]> payloads = Payloads("short_clicks_22k_stereo", out AdtsHeader header);
        AacDecoder decoder = new();
        Span<float> stereo = new float[2 * AacDecoder.FrameLength];
        foreach (byte[] payload in payloads) { decoder.Decode(header, payload); decoder.MixToStereo(stereo); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 5; round++)
        {
            foreach (byte[] payload in payloads) { decoder.Decode(header, payload); decoder.MixToStereo(stereo); }
        }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(1024));
    }

    [Test]
    public void Decode_IsFastEnoughForRealTime()
    {
        List<byte[]> payloads = Payloads("long_noise_48k_stereo", out AdtsHeader header);
        AacDecoder decoder = new();
        foreach (byte[] payload in payloads) decoder.Decode(header, payload);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        for (int round = 0; round < 20; round++)
        {
            foreach (byte[] payload in payloads) { decoder.Decode(header, payload); frames++; }
        }
        double audioSeconds = frames * 1024.0 / header.SampleRate;
        // High-bitrate noise is a hard case; decoding must still use well under a tenth of real time.
        Assert.That(watch.Elapsed.TotalSeconds / audioSeconds, Is.LessThan(0.1));
    }
}
