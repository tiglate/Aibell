namespace Aibell.Audio.Vorbis.Tests;

/// <summary>Decodes streams written straight from the specification and compares them with a direct (O(N^2)) model of the synthesis.</summary>
public class VorbisSynthesisTests
{
    private sealed record Block(bool Long, bool PreviousLong, bool NextLong, float[][] Spectrum);

    private static float[] Window(int n, bool longBlock, bool previousLong, bool nextLong)
    {
        int center = n / 2;
        int leftStart = 0, leftEnd = center, leftN = n / 2, rightStart = center, rightEnd = n, rightN = n / 2;
        if (longBlock && !previousLong)
        {
            leftStart = n / 4 - VorbisTestStream.ShortSize / 4;
            leftEnd = n / 4 + VorbisTestStream.ShortSize / 4;
            leftN = VorbisTestStream.ShortSize / 2;
        }
        if (longBlock && !nextLong)
        {
            rightStart = n * 3 / 4 - VorbisTestStream.ShortSize / 4;
            rightEnd = n * 3 / 4 + VorbisTestStream.ShortSize / 4;
            rightN = VorbisTestStream.ShortSize / 2;
        }
        float[] w = new float[n];
        for (int i = 0; i < n; i++)
        {
            double value;
            if (i < leftStart) value = 0;
            else if (i < leftEnd) value = Math.Sin(Math.PI / 2 * Math.Pow(Math.Sin((i - leftStart + 0.5) / leftN * Math.PI / 2), 2));
            else if (i < rightStart) value = 1;
            else if (i < rightEnd) value = Math.Sin(Math.PI / 2 * Math.Pow(Math.Sin((i - rightStart + 0.5) / rightN * Math.PI / 2 + Math.PI / 2), 2));
            else value = 0;
            w[i] = (float)value;
        }
        return w;
    }

    private static float[] NaiveImdct(float[] spectrum)
    {
        int m = spectrum.Length;
        float[] y = new float[2 * m];
        for (int n = 0; n < 2 * m; n++)
        {
            double sum = 0;
            for (int k = 0; k < m; k++) sum += spectrum[k] * Math.Cos(Math.PI / m * (n + 0.5 + m / 2.0) * (k + 0.5));
            y[n] = (float)sum;
        }
        return y;
    }

    // Overlap-add on a timeline: the centres of consecutive blocks are n/4 + n'/4 apart; the finished audio runs from the first centre to the last.
    private static float[] ExpectedPcm(List<Block> blocks, int channels)
    {
        int first = (blocks[0].Long ? VorbisTestStream.LongSize : VorbisTestStream.ShortSize) / 2;
        List<int> centers = [first];
        for (int k = 1; k < blocks.Count; k++)
        {
            int previous = blocks[k - 1].Long ? VorbisTestStream.LongSize : VorbisTestStream.ShortSize;
            int current = blocks[k].Long ? VorbisTestStream.LongSize : VorbisTestStream.ShortSize;
            centers.Add(centers[^1] + previous / 4 + current / 4);
        }
        int total = centers[^1] + 512;
        float[][] timeline = new float[channels][];
        for (int c = 0; c < channels; c++) timeline[c] = new float[total];
        for (int k = 0; k < blocks.Count; k++)
        {
            Block block = blocks[k];
            int n = block.Long ? VorbisTestStream.LongSize : VorbisTestStream.ShortSize;
            float[] window = Window(n, block.Long, block.PreviousLong, block.NextLong);
            for (int c = 0; c < channels; c++)
            {
                float[] y = NaiveImdct(block.Spectrum[c]);
                for (int i = 0; i < n; i++) timeline[c][centers[k] - n / 2 + i] += y[i] * window[i];
            }
        }
        int frames = centers[^1] - centers[0];
        float[] pcm = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++) pcm[i * channels + c] = timeline[c][centers[0] + i];
        }
        return pcm;
    }

    private static void InverseCoupling(float[] magnitude, float[] angle)
    {
        for (int i = 0; i < magnitude.Length; i++)
        {
            float m = magnitude[i], a = angle[i];
            float newM, newA;
            if (m > 0)
            {
                if (a > 0) { newM = m; newA = m - a; }
                else { newA = m; newM = m + a; }
            }
            else if (a > 0) { newM = m; newA = m + a; }
            else { newA = m; newM = m - a; }
            magnitude[i] = newM;
            angle[i] = newA;
        }
    }

    private static (byte[] File, float[] Expected) Build(VorbisTestStream stream, bool[] longBlocks, int seed, int[]? unusedFloorChannels = null)
    {
        Random random = new(seed);
        List<byte[]> packets = [];
        List<Block> blocks = [];
        for (int k = 0; k < longBlocks.Length; k++)
        {
            bool isLong = longBlocks[k];
            bool previousLong = k == 0 || longBlocks[k - 1];
            bool nextLong = k == longBlocks.Length - 1 || longBlocks[k + 1];
            int n = isLong ? VorbisTestStream.LongSize : VorbisTestStream.ShortSize;
            int half = n / 2;
            int vectorLength = stream.ResidueType == 2 ? half * stream.Channels : half;
            int partitions = Math.Min(stream.ResidueEnd, vectorLength) / stream.PartitionSize;
            int vectors = stream.ResidueType == 2 ? 1 : stream.Channels;
            int[][] classes = new int[vectors][];
            int[][][] entries = new int[vectors][][];
            for (int v = 0; v < vectors; v++)
            {
                classes[v] = new int[partitions + 1];
                entries[v] = new int[2 * (partitions + 1)][];
                for (int p = 0; p < partitions; p++)
                {
                    classes[v][p] = random.Next(2);
                    for (int pass = 0; pass < 2; pass++)
                    {
                        int dimensions = pass == 1 || classes[v][p] == 0 ? 2 : 4;
                        entries[v][p * 2 + pass] = [.. Enumerable.Range(0, stream.PartitionSize / dimensions).Select(_ => random.Next(4))];
                    }
                }
            }
            int[] floorY = [.. Enumerable.Range(0, stream.Channels).Select(c => unusedFloorChannels?.Contains(c) == true && k % 2 == 0 ? -1 : 10 + random.Next(240))];
            VorbisTestStream.Frame frame = new(isLong, previousLong, nextLong, floorY, classes, entries);
            packets.Add(stream.Packet(frame, out float[][] residue));
            bool[] skip = [.. floorY.Select(y => y < 0)];
            if (stream.Coupling && (!skip[0] || !skip[1])) InverseCoupling(residue[0], residue[1]);
            float[][] spectrum = new float[stream.Channels][];
            for (int c = 0; c < stream.Channels; c++)
            {
                spectrum[c] = new float[half];
                if (skip[c]) continue;
                float floor = Floor1Table.InverseDb[floorY[c]];
                for (int i = 0; i < half; i++) spectrum[c][i] = residue[c][i] * floor;
            }
            blocks.Add(new Block(isLong, previousLong, nextLong, spectrum));
        }
        float[] expected = ExpectedPcm(blocks, stream.Channels);
        return (stream.Ogg(packets, expected.Length / stream.Channels), expected);
    }

    private static void AssertDecodes(VorbisTestStream stream, bool[] longBlocks, int seed, int[]? unused = null)
    {
        (byte[] file, float[] expected) = Build(stream, longBlocks, seed, unused);
        using VorbisDecoder decoder = VorbisDecoder.Open(new MemoryStream(file));
        float[] pcm = VorbisFixtureTests.DecodeAll(decoder);
        Assert.That(pcm, Has.Length.EqualTo(expected.Length));
        double worst = 0, peak = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(pcm[i] - expected[i]));
            peak = Math.Max(peak, Math.Abs(expected[i]));
        }
        Assert.That(peak, Is.GreaterThan(0.01), "the test signal must not be silent");
        Assert.That(worst, Is.LessThan(2e-4 * Math.Max(1, peak)));
    }

    private static readonly bool[] Mixed = [true, true, false, false, true, false, true, true, false, true];

    [TestCase(0, false, 4)]
    [TestCase(1, false, 4)]
    [TestCase(2, false, 4)]
    [TestCase(0, true, 8)]
    [TestCase(1, true, 8)]
    [TestCase(2, true, 8)]
    [TestCase(0, false, 16)]
    [TestCase(2, true, 16)]
    public void EveryResidueType_WithAndWithoutCoupling(int type, bool coupling, int partitionSize) =>
        AssertDecodes(new VorbisTestStream { ResidueType = type, Coupling = coupling, PartitionSize = partitionSize }, Mixed, type * 100 + partitionSize);

    [Test]
    public void LongBlocksOnly_AndShortBlocksOnly()
    {
        AssertDecodes(new VorbisTestStream { ResidueType = 1 }, [true, true, true, true, true], 1);
        AssertDecodes(new VorbisTestStream { ResidueType = 2 }, [false, false, false, false, false, false], 2);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void ResidueEnd_LimitsTheCodedRange(int type) =>
        AssertDecodes(new VorbisTestStream { ResidueType = type, ResidueEnd = 40 }, Mixed, 40 + type);

    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(2, false)]
    [TestCase(2, true)]
    [TestCase(0, true)]
    public void AChannelWithAnUnusedFloor_IsSilent_UnlessCouplingKeepsItsResidueAlive(int type, bool coupling) =>
        AssertDecodes(new VorbisTestStream { ResidueType = type, Coupling = coupling }, Mixed, 77, [1]);

    [Test]
    public void SingleChannelStreams()
    {
        foreach (int type in new[] { 0, 1, 2 }) AssertDecodes(new VorbisTestStream { Channels = 1, ResidueType = type }, Mixed, type);
    }

    [TestCase(1)]
    [TestCase(2)]
    public void ChannelCounts_AreReorderedToWaveOrder(int type)
    {
        // Six channels with distinct floors: the output channel order must be the WAVE one (the model writes Vorbis order, so compare per channel after mapping).
        VorbisTestStream stream = new() { Channels = 6, ResidueType = type };
        (byte[] file, float[] expected) = Build(stream, Mixed, 5);
        using VorbisDecoder decoder = VorbisDecoder.Open(new MemoryStream(file));
        float[] pcm = VorbisFixtureTests.DecodeAll(decoder);
        int[] waveOf = [0, 2, 1, 4, 5, 3];
        for (int i = 0; i < expected.Length / 6; i++)
        {
            for (int c = 0; c < 6; c++) Assert.That(pcm[i * 6 + waveOf[c]], Is.EqualTo(expected[i * 6 + c]).Within(2e-4), $"frame {i} channel {c}");
        }
    }

    [Test]
    public void TruncatedPackets_AreNotErrors()
    {
        VorbisTestStream stream = new() { ResidueType = 1 };
        Random random = new(3);
        for (int round = 0; round < 60; round++)
        {
            (byte[] file, _) = Build(stream, Mixed, round);
            Ogg.OggPacketReader reader = new(new MemoryStream(file), VorbisIdentification.IsIdentification);
            List<byte[]> all = [];
            while (reader.ReadPacket(out Ogg.OggPacket packet)) all.Add(packet.Data.ToArray());
            Ogg.Tests.OggTestWriter writer = new(11);
            writer.Page([all[0]], 0, Ogg.OggPageKind.BeginOfStream);
            writer.Page([all[1], all[2]], 0);
            writer.Page([.. all.Skip(3).Select(p => random.Next(2) == 0 ? p : p[..random.Next(p.Length + 1)])], -1, Ogg.OggPageKind.EndOfStream);
            using VorbisDecoder decoder = VorbisDecoder.Open(new MemoryStream(writer.ToArray()));
            Assert.That(() => VorbisFixtureTests.DecodeAll(decoder), Throws.Nothing);
        }
    }
}
