using System.IO.Compression;
using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

/// <summary>
/// Streams with coupling channel elements, written by <see cref="SyntheticStream"/>. The .aac files in tests/fixtures/aac-synthetic are exactly
/// what <see cref="Build"/> returns (a test checks it); tests/fixtures/generate-aac.sh decodes them with FFmpeg for the reference PCM.
/// </summary>
internal static class SyntheticFixtures
{
    public static readonly string[] Names =
        ["cce_dependent_before_tns", "cce_dependent_after_tns_signed", "cce_independent", "cce_shared_list"];

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "fixtures", "aac-synthetic");

    public static byte[] Build(string name)
    {
        using MemoryStream file = new();
        for (int frame = 0; frame < 8; frame++)
        {
            SyntheticStream block = new();
            block.ProgramConfig(independentCoupling: name == "cce_independent");
            bool tns = name.Contains("tns", StringComparison.Ordinal) || name.Contains("before", StringComparison.Ordinal) || name.Contains("after", StringComparison.Ordinal);
            block.SingleChannel(0, 150, 30, SyntheticStream.Spectrum(10 + frame), tns);
            block.ChannelPair(0, 146, SyntheticStream.Spectrum(20 + frame), 148, SyntheticStream.Spectrum(30 + frame), 30, tns);
            int[] coupled = SyntheticStream.Spectrum(40 + frame, 24);
            switch (name)
            {
                case "cce_dependent_before_tns":
                    // Targets: the SCE (list 0, natural scale) and both channels of the CPE (lists 1 and 2, one common, one per band).
                    block.Coupling(0, independent: false, [new(false, 0), new(true, 0, Left: true, Right: true)], domain: 0, signedGains: false, scale: 1,
                        globalGain: 140, maxSfb: 24, coefficients: coupled, gains: [[2], Enumerable.Range(0, 24).Select(band => band == 0 ? -1 : band % 5 - 2).ToArray()]);
                    break;
                case "cce_dependent_after_tns_signed":
                    block.Coupling(0, independent: false, [new(false, 0), new(true, 0, Left: false, Right: true)], domain: 1, signedGains: true, scale: 3,
                        globalGain: 138, maxSfb: 24, coefficients: coupled, gains: [Enumerable.Range(0, 24).Select(band => band % 4 == 3 ? 3 : band % 3).ToArray()]);
                    break;
                case "cce_independent":
                    block.Coupling(0, independent: true, [new(false, 0), new(true, 0, Left: true, Right: false)], domain: 0, signedGains: false, scale: 0,
                        globalGain: 142, maxSfb: 24, coefficients: coupled, gains: [[-3]]);
                    break;
                default: // one gain list shared by both channels of the pair
                    block.Coupling(0, independent: false, [new(true, 0)], domain: 0, signedGains: false, scale: 2,
                        globalGain: 140, maxSfb: 24, coefficients: coupled, gains: []);
                    break;
            }
            block.End();
            file.Write(SyntheticStream.AdtsFrame(0, block.ToBytes()));
        }
        return file.ToArray();
    }

    internal static (float[] Samples, AacStatistics Statistics) Decode(byte[] stream, int exponentSign = -1)
    {
        AdtsFrameReader reader = new();
        stream.CopyTo(reader.GetWriteBuffer());
        reader.Advance(stream.Length);
        reader.Complete();
        AacDecoder decoder = new() { CouplingExponentSign = exponentSign };
        List<float> samples = [];
        while (reader.TryRead(out AdtsHeader header, out _))
        {
            int offset = header.HeaderLength;
            for (int block = 0; block < header.RawBlocks; block++)
            {
                decoder.DecodeBlock(reader.LastFrame, header, block, ref offset, out _);
                for (int n = 0; n < AacDecoder.FrameLength; n++)
                {
                    for (int c = 0; c < decoder.Channels; c++) samples.Add(decoder.GetChannel(c)[n] * 32768f);
                }
            }
        }
        return ([.. samples], decoder.Statistics);
    }
}

[TestFixture]
public sealed class CouplingTests
{
    [TestCaseSource(typeof(SyntheticFixtures), nameof(SyntheticFixtures.Names))]
    public void CommittedFixture_IsWhatTheWriterProduces(string name) =>
        Assert.That(File.ReadAllBytes(Path.Combine(SyntheticFixtures.Directory, name + ".aac")), Is.EqualTo(SyntheticFixtures.Build(name)));

    [TestCaseSource(typeof(SyntheticFixtures), nameof(SyntheticFixtures.Names))]
    public void Decode_MatchesFfmpegOnCouplingStreams(string name)
    {
        (float[] samples, AacStatistics statistics) = SyntheticFixtures.Decode(SyntheticFixtures.Build(name));
        Assert.That(statistics.CouplingElements, Is.EqualTo(8));
        using GZipStream gzip = new(File.OpenRead(Path.Combine(SyntheticFixtures.Directory, name + ".pcm.gz")), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        short[] reference = new short[raw.Length / 2];
        Buffer.BlockCopy(raw.ToArray(), 0, reference, 0, (int)raw.Length);
        Assert.That(samples, Has.Length.EqualTo(reference.Length));
        // FFmpeg writes front-left, front-right, centre; the decoder keeps the stream's own order (centre, left, right).
        short[] reordered = new short[reference.Length];
        for (int n = 0; n + 2 < reference.Length; n += 3)
        {
            reordered[n] = reference[n + 2];
            reordered[n + 1] = reference[n];
            reordered[n + 2] = reference[n + 1];
        }
        Assert.That(ConformanceTests.SnrDb(samples, reordered), Is.GreaterThan(50), name);
    }

    [Test]
    public void Decode_TheOppositeGainDirectionDiffersOnlyWhereAGainListIsUsed()
    {
        // The ISO texts write cc_scale^gain, FFmpeg (and the default here) cc_scale^-gain. Where a target is coupled at natural scale (list 0) they agree.
        (float[] natural, _) = SyntheticFixtures.Decode(SyntheticFixtures.Build("cce_shared_list"));
        (float[] literal, _) = SyntheticFixtures.Decode(SyntheticFixtures.Build("cce_shared_list"), exponentSign: 1);
        Assert.That(natural, Is.EqualTo(literal));
        (float[] withLists, _) = SyntheticFixtures.Decode(SyntheticFixtures.Build("cce_dependent_before_tns"));
        (float[] withListsLiteral, _) = SyntheticFixtures.Decode(SyntheticFixtures.Build("cce_dependent_before_tns"), exponentSign: 1);
        Assert.That(withLists, Is.Not.EqualTo(withListsLiteral));
    }

    [Test]
    public void Coupling_StreamsDecodeToAudibleLevels()
    {
        (float[] coupled, _) = SyntheticFixtures.Decode(SyntheticFixtures.Build("cce_shared_list"));
        Assert.That(coupled.Any(value => Math.Abs(value) > 1000), Is.True);
    }

    [Test]
    public void Writer_ExplicitGenerate()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_WRITE_SYNTHETIC") != "1") Assert.Ignore("Set AIBELL_WRITE_SYNTHETIC=1 to rewrite tests/fixtures/aac-synthetic.");
        string target = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "fixtures", "aac-synthetic");
        System.IO.Directory.CreateDirectory(target);
        foreach (string name in SyntheticFixtures.Names) File.WriteAllBytes(Path.Combine(target, name + ".aac"), SyntheticFixtures.Build(name));
    }
}
