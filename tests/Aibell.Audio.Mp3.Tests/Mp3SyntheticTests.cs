namespace Aibell.Audio.Mp3.Tests;

/// <summary>Streams of random valid frames using mixed blocks, intensity stereo, scfsi and the other features real encoders skip, against FFmpeg's decode.</summary>
public class Mp3SyntheticTests
{
    private static string Path(string name) => System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "mp3-synthetic", name);

    public static IEnumerable<string> Names =>
        Directory.GetFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "mp3-synthetic"), "*.mp3").Select(f => System.IO.Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal);

    [TestCaseSource(nameof(Names))]
    public void Decoding_AgreesWithTheYardstick(string name)
    {
        using Mp3Decoder decoder = Mp3Decoder.Open(File.OpenRead(Path(name + ".mp3")));
        float[] pcm = Mp3FixtureTests.DecodeAll(decoder);
        using System.IO.Compression.GZipStream gzip = new(File.OpenRead(Path(name + ".f32.gz")), System.IO.Compression.CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        float[] reference = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes.ToArray(), 0, reference, 0, reference.Length * 4);
        TestContext.Out.WriteLine($"{name}: {pcm.Length} vs {reference.Length}, SNR {Mp3FixtureTests.SnrDb(pcm, reference):F1} dB");
        Assert.That(pcm, Has.Length.EqualTo(reference.Length));
        Assert.That(Mp3FixtureTests.SnrDb(pcm, reference), Is.GreaterThan(80));
    }
}
