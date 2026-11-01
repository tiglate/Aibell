namespace Aibell.Audio.Vorbis.Tests;

public class VorbisCodebookTests
{
    private static ulong PackFloat(double value)
    {
        if (value == 0) return 0;
        ulong sign = value < 0 ? 0x80000000UL : 0;
        double magnitude = Math.Abs(value);
        int exponent = (int)Math.Floor(Math.Log2(magnitude)) + 788 - 20;
        double mantissa = Math.Round(magnitude * Math.Pow(2, 788 - exponent));
        return sign | (ulong)exponent << 21 | (ulong)mantissa;
    }

    private static void Header(VorbisBitWriter w, int dimensions, int entries)
    {
        w.Write(0x564342, 24);
        w.Write((ulong)dimensions, 16);
        w.Write((ulong)entries, 24);
    }

    private static byte[] Unordered(int[] lengths, bool sparse, int dimensions = 1)
    {
        VorbisBitWriter w = new();
        Header(w, dimensions, lengths.Length);
        w.Write(0, 1);
        w.Write(sparse ? 1UL : 0, 1);
        foreach (int length in lengths)
        {
            if (sparse)
            {
                w.Write(length == 0 ? 0UL : 1, 1);
                if (length == 0) continue;
            }
            w.Write((ulong)length - 1, 5);
        }
        w.Write(0, 4);
        return w.ToArray();
    }

    private static VorbisCodebook Read(byte[] data)
    {
        VorbisBitReader reader = new(data);
        return VorbisCodebook.Read(ref reader);
    }

    // Writes a codeword most significant bit first, as the stream carries it.
    private static byte[] Stream(params string[] codewords)
    {
        VorbisBitWriter w = new();
        foreach (string code in codewords)
        {
            foreach (char bit in code) w.Write(bit == '1' ? 1UL : 0, 1);
        }
        return w.ToArray();
    }

    private static int[] DecodeAll(VorbisCodebook book, byte[] stream, int count)
    {
        VorbisBitReader reader = new(stream);
        int[] result = new int[count];
        for (int i = 0; i < count; i++) result[i] = book.DecodeScalar(ref reader);
        return result;
    }

    [Test]
    public void TheSpecificationsExample_AssignsTheDocumentedCodewords()
    {
        VorbisCodebook book = Read(Unordered([2, 4, 4, 4, 4, 2, 3, 3], false));
        byte[] stream = Stream("00", "0100", "0101", "0110", "0111", "10", "110", "111");
        Assert.That(DecodeAll(book, stream, 8), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
    }

    [Test]
    public void SparseCodebooks_SkipUnusedEntries()
    {
        VorbisCodebook book = Read(Unordered([0, 1, 2, 0, 3, 3], true));
        Assert.That(DecodeAll(book, Stream("0", "10", "110", "111", "0"), 5), Is.EqualTo(new[] { 1, 2, 4, 5, 1 }));
    }

    [Test]
    public void OrderedCodebooks_ListTheCountPerLength()
    {
        // Entries 0..4 with lengths 1, 2, 3, 3, 3 -> hmm: 1 + 1/2 + ... must be complete: lengths 1,2,3,3 are complete; use 4 entries.
        VorbisBitWriter w = new();
        Header(w, 1, 4);
        w.Write(1, 1); // ordered
        w.Write(0, 5); // first length 1
        w.Write(1, VorbisCodebook.Ilog(4)); // one entry of length 1
        w.Write(1, VorbisCodebook.Ilog(3)); // one of length 2
        w.Write(2, VorbisCodebook.Ilog(2)); // two of length 3
        w.Write(0, 4);
        VorbisCodebook book = Read(w.ToArray());
        Assert.That(DecodeAll(book, Stream("0", "10", "110", "111"), 4), Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void LongCodewords_UseTheSlowPath()
    {
        int[] lengths = [.. Enumerable.Range(1, 15), 15];
        VorbisCodebook book = Read(Unordered(lengths, false));
        // Entry 0 (length 1) takes 0, entry 1 (length 2) takes 10, entry 2 takes 110, ...: a chain of ones ending in a zero; the last two share length 15.
        string[] expected = new string[16];
        for (int i = 0; i < 15; i++) expected[i] = new string('1', i) + "0";
        expected[15] = new string('1', 15);
        Assert.That(DecodeAll(book, Stream(expected), 16), Is.EqualTo(Enumerable.Range(0, 16).ToArray()));
    }

    [Test]
    public void BadTrees_AreRefused()
    {
        Assert.Throws<InvalidDataException>(() => Read(Unordered([1, 1, 1], false)));
        Assert.Throws<InvalidDataException>(() => Read(Unordered([2, 2, 2], false)));
        Assert.Throws<InvalidDataException>(() => Read(Unordered([1, 2], false)));
    }

    [Test]
    public void SingleEntryCodebooks_AreTolerated_WithAOneBitCode()
    {
        VorbisCodebook book = Read(Unordered([1], false));
        Assert.That(DecodeAll(book, Stream("0", "1", "0"), 3), Is.EqualTo(new[] { 0, 0, 0 }));
        VorbisCodebook sparse = Read(Unordered([0, 0, 1], true));
        Assert.That(DecodeAll(sparse, Stream("0"), 1), Is.EqualTo(new[] { 2 }));
        Assert.Throws<InvalidDataException>(() => Read(Unordered([2], false)));
    }

    [Test]
    public void ReadingPastTheEnd_IsReportedNotThrown()
    {
        VorbisCodebook book = Read(Unordered([1, 2, 2], false));
        VorbisBitReader reader = new(Stream("10"));
        Assert.That(book.DecodeScalar(ref reader), Is.EqualTo(1));
        Assert.That(reader.EndOfPacket, Is.False);
        // Six padding zeros remain in the byte: they decode as entry 0 repeatedly; once they are used up the packet has ended.
        int guard = 0;
        while (book.DecodeScalar(ref reader) >= 0 && guard++ < 20) { }
        Assert.That(reader.EndOfPacket, Is.True);
        Assert.That(book.DecodeScalar(ref reader), Is.EqualTo(-1));
    }

    [Test]
    public void LatticeLookup_BuildsVectorsFromTheValueList()
    {
        // 9 entries of dimension 2: lookup1_values(9, 2) = 3 values {0, 1, 2}; entry e is (v[e % 3], v[e / 3 % 3]) scaled by delta 0.5 from min -1.
        VorbisBitWriter w = new();
        Header(w, 2, 9);
        w.Write(0, 1);
        w.Write(0, 1);
        for (int i = 0; i < 9; i++) w.Write(3, 5); // all length 4: 9 of 16 codes
        w.Write(1, 4);
        w.Write(PackFloat(-1), 32);
        w.Write(PackFloat(0.5), 32);
        w.Write(1, 4); // 2 bits per value
        w.Write(0, 1);
        for (int v = 0; v < 3; v++) w.Write((ulong)v, 2);
        // Nine codes of length 4 fill only 9 of 16 slots: the tree is underspecified.
        byte[] data = w.ToArray();
        Assert.Throws<InvalidDataException>(() => Read(data));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LatticeAndExplicitLookups_WithAndWithoutSequences(bool sequence)
    {
        // 16 entries of length 4 make a complete tree. Dimension 2, lookup1_values(16, 2) = 4 values.
        foreach (int type in new[] { 1, 2 })
        {
            VorbisBitWriter w = new();
            Header(w, 2, 16);
            w.Write(0, 1);
            w.Write(0, 1);
            for (int i = 0; i < 16; i++) w.Write(3, 5);
            w.Write((ulong)type, 4);
            w.Write(PackFloat(-1), 32);
            w.Write(PackFloat(0.25), 32);
            w.Write(3, 4); // 4 bits
            w.Write(sequence ? 1UL : 0, 1);
            int values = type == 1 ? 4 : 32;
            int[] multiplicands = [.. Enumerable.Range(0, values).Select(i => (i * 7 + 3) % 16)];
            foreach (int m in multiplicands) w.Write((ulong)m, 4);
            VorbisCodebook book = Read(w.ToArray());
            Assert.That(book.HasVectors, Is.True);
            for (int entry = 0; entry < 16; entry++)
            {
                // Entry codes are the 4-bit numbers themselves, most significant bit first.
                VorbisBitReader reader = new(Stream(Convert.ToString(entry, 2).PadLeft(4, '0')));
                ReadOnlySpan<float> vector = book.DecodeVector(ref reader);
                float last = 0;
                for (int d = 0; d < 2; d++)
                {
                    int index = type == 1 ? (int)(entry / Math.Pow(4, d)) % 4 : entry * 2 + d;
                    float expected = multiplicands[index] * 0.25f - 1 + last;
                    Assert.That(vector[d], Is.EqualTo(expected).Within(1e-6), $"type {type} entry {entry} dimension {d}");
                    if (sequence) last = expected;
                }
            }
        }
    }

    [Test]
    public void VectorsOfAScalarOnlyCodebook_AreEmpty()
    {
        VorbisCodebook book = Read(Unordered([1, 1], false));
        VorbisBitReader reader = new(Stream("0"));
        Assert.That(book.DecodeVector(ref reader).IsEmpty, Is.True);
        Assert.That(book.HasVectors, Is.False);
    }

    [Test]
    public void Ilog_MatchesTheSpecificationsExamples()
    {
        Assert.That(new[] { 0, 1, 2, 3, 4, 7, -5 }.Select(VorbisCodebook.Ilog), Is.EqualTo(new[] { 0, 1, 2, 2, 3, 3, 0 }));
    }
}
