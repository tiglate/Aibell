namespace Aibell.Audio.Opus.Tests;

/// <summary>
/// Decodes the official RFC 6716 conformance vectors (opus_demo packet files) and compares them with the reference output. The vectors are large, so they are not part of
/// the repository: point AIBELL_OPUS_VECTORS at a directory holding testvectorNN.bit / testvectorNN.dec (the RFC 8251 set) to run these tests.
/// </summary>
[TestFixture]
public class ConformanceVectorTests
{
    public static IEnumerable<int> Vectors => Enumerable.Range(1, 12);

    [TestCaseSource(nameof(Vectors))]
    public void Decode_MatchesReferenceOutput(int number)
    {
        string? dir = Environment.GetEnvironmentVariable("AIBELL_OPUS_VECTORS");
        if (dir is null || !Directory.Exists(dir)) Assert.Ignore("AIBELL_OPUS_VECTORS is not set.");
        byte[] bits = File.ReadAllBytes(Path.Combine(dir, $"testvector{number:00}.bit"));
        byte[] reference = File.ReadAllBytes(Path.Combine(dir, $"testvector{number:00}.dec"));

        var decoder = new OpusDecoder(2);
        float[] pcm = new float[OpusDecoder.MaxPacketFrames * 2];
        int pos = 0, refPos = 0, rangeMismatches = 0;
        double signal = 0, noise = 0;
        while (pos + 8 <= bits.Length)
        {
            int length = (bits[pos] << 24) | (bits[pos + 1] << 16) | (bits[pos + 2] << 8) | bits[pos + 3];
            uint finalRange = (uint)((bits[pos + 4] << 24) | (bits[pos + 5] << 16) | (bits[pos + 6] << 8) | bits[pos + 7]);
            pos += 8;
            byte[] data = bits.AsSpan(pos, length).ToArray();
            pos += length;
            int frames = decoder.Decode(length == 0 ? null : data, 0, length, pcm, 0, OpusDecoder.MaxPacketFrames);
            Assert.That(frames, Is.GreaterThan(0));
            if (length > 0 && decoder.FinalRange != finalRange) rangeMismatches++;
            for (int i = 0; i < frames * 2; i++)
            {
                double expected = (short)(reference[refPos] | (reference[refPos + 1] << 8));
                refPos += 2;
                double actual = Math.Clamp(Math.Round(pcm[i] * 32768.0), -32768, 32767);
                signal += expected * expected;
                noise += (expected - actual) * (expected - actual);
            }
        }
        double snr = 10 * Math.Log10(signal / Math.Max(noise, 1e-9));
        Assert.Multiple(() =>
        {
            Assert.That(refPos, Is.EqualTo(reference.Length));
            Assert.That(rangeMismatches, Is.Zero);
            Assert.That(snr, Is.GreaterThan(80));
        });
    }
}
