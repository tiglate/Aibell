using System.Buffers.Binary;
using Aibell.Audio.Ogg;
using Aibell.Audio.Ogg.Tests;

namespace Aibell.Audio.Vorbis.Tests;

/// <summary>An LSB-first bit writer (Vorbis I section 2).</summary>
internal sealed class VorbisBitWriter
{
    private readonly List<byte> bytes = [];
    private int bits;

    public void Write(ulong value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (bits == 0) bytes.Add(0);
            if ((value >> i & 1) != 0) bytes[^1] |= (byte)(1 << bits);
            bits = (bits + 1) & 7;
        }
    }

    public byte[] ToArray() => [.. bytes];

    /// <summary>Writes a two-bit Huffman codeword, most significant bit first (the codebooks used here give every entry a two-bit code equal to its number).</summary>
    public void Code2(int entry)
    {
        Write((ulong)(entry >> 1 & 1), 1);
        Write((ulong)(entry & 1), 1);
    }
}

/// <summary>
/// Builds small Vorbis streams from the specification so that coding paths the real encoder does not use (residue type 0, flat floors, odd block layouts)
/// can be decoded and compared with an independent model of the output.
/// </summary>
internal sealed class VorbisTestStream
{
    public const int ShortSize = 64;
    public const int LongSize = 256;

    public int Channels { get; init; } = 2;

    public int ResidueType { get; init; } = 1;

    public bool Coupling { get; init; }

    public int PartitionSize { get; init; } = 4;

    public int ResidueEnd { get; init; } = 1 << 20;

    // Codebook 0: scalar, 2-bit codes (floor Y bits are read raw, so it serves only as filler); 1: VQ dim 2 (lattice); 2: VQ dim 4 (explicit); 3: classes, dim 2.
    public static readonly float[][] Book1 = [[0, 0], [1, 0], [0, 1], [1, 1]];
    public static readonly float[][] Book2 = BuildBook2();

    // Explicit values are min + delta * m with min -1, delta 1/8 and a 4-bit m.
    private static int Book2Multiplicand(int entry, int dimension) => (entry * 5 + dimension * 3) % 16;

    private static float[][] BuildBook2()
    {
        float[][] vectors = new float[4][];
        for (int e = 0; e < 4; e++) vectors[e] = [.. Enumerable.Range(0, 4).Select(j => -1f + 0.125f * Book2Multiplicand(e, j))];
        return vectors;
    }

    public byte[] Identification()
    {
        VorbisBitWriter w = new();
        w.Write(1, 8);
        foreach (byte b in "vorbis"u8) w.Write(b, 8);
        w.Write(0, 32);
        w.Write((ulong)Channels, 8);
        w.Write(44100, 32);
        w.Write(0, 32);
        w.Write(0, 32);
        w.Write(0, 32);
        w.Write(6, 4);
        w.Write(8, 4);
        w.Write(1, 1);
        return w.ToArray();
    }

    public static byte[] Comment()
    {
        VorbisBitWriter w = new();
        w.Write(3, 8);
        foreach (byte b in "vorbis"u8) w.Write(b, 8);
        w.Write(4, 32);
        foreach (byte b in "test"u8) w.Write(b, 8);
        w.Write(0, 32);
        w.Write(1, 1);
        return w.ToArray();
    }

    private static ulong PackFloat(double value)
    {
        if (value == 0) return 0;
        ulong sign = value < 0 ? 0x80000000UL : 0;
        double magnitude = Math.Abs(value);
        int exponent = (int)Math.Floor(Math.Log2(magnitude)) + 788 - 20;
        double mantissa = Math.Round(magnitude * Math.Pow(2, 788 - exponent));
        return sign | (ulong)exponent << 21 | (ulong)mantissa;
    }

    private static void WriteBook(VorbisBitWriter w, int dimensions, int lookupType, float[]? explicitValues)
    {
        w.Write(0x564342, 24);
        w.Write((ulong)dimensions, 16);
        w.Write(4, 24);
        w.Write(0, 1); // unordered
        w.Write(0, 1); // not sparse
        for (int i = 0; i < 4; i++) w.Write(1, 5); // every length 2
        w.Write((ulong)lookupType, 4);
        if (lookupType == 0) return;
        w.Write(PackFloat(lookupType == 1 ? 0 : -1), 32);
        w.Write(PackFloat(lookupType == 1 ? 1 : 0.125), 32);
        w.Write(3, 4); // 4 bits per value
        w.Write(0, 1);
        if (lookupType == 1)
        {
            w.Write(0, 4);
            w.Write(1, 4); // two lattice values: 0 and 1
        }
        else
        {
            for (int e = 0; e < 4; e++)
            {
                for (int d = 0; d < dimensions; d++) w.Write((ulong)Book2Multiplicand(e, d), 4);
            }
        }
    }

    public byte[] Setup()
    {
        VorbisBitWriter w = new();
        w.Write(5, 8);
        foreach (byte b in "vorbis"u8) w.Write(b, 8);
        w.Write(3, 8); // four codebooks
        WriteBook(w, 1, 0, null);
        WriteBook(w, 2, 1, null);
        WriteBook(w, 4, 2, null);
        WriteBook(w, 2, 0, null);
        w.Write(0, 6); // one time transform
        w.Write(0, 16);
        w.Write(0, 6); // one floor
        w.Write(1, 16);
        w.Write(0, 5); // no partitions
        w.Write(0, 2); // multiplier 1
        w.Write(7, 4); // range bits: X = 0, 128
        w.Write(0, 6); // one residue
        w.Write((ulong)ResidueType, 16);
        w.Write(0, 24);
        w.Write((ulong)ResidueEnd, 24);
        w.Write((ulong)PartitionSize - 1, 24);
        w.Write(1, 6); // two classifications
        w.Write(3, 8); // class book
        w.Write(1, 3); w.Write(0, 1); // class 0: pass 0 only
        w.Write(3, 3); w.Write(0, 1); // class 1: passes 0 and 1
        w.Write(1, 8); // class 0 pass 0: book 1
        w.Write(2, 8); // class 1 pass 0: book 2
        w.Write(1, 8); // class 1 pass 1: book 1
        w.Write(0, 6); // one mapping
        w.Write(0, 16);
        w.Write(0, 1); // one submap
        if (Coupling)
        {
            w.Write(1, 1);
            w.Write(0, 8);
            int bits = VorbisCodebook.Ilog(Channels - 1);
            w.Write(0, bits);
            w.Write(1, bits);
        }
        else w.Write(0, 1);
        w.Write(0, 2);
        w.Write(0, 8);
        w.Write(0, 8);
        w.Write(0, 8);
        w.Write(1, 6); // two modes
        w.Write(0, 1); w.Write(0, 16); w.Write(0, 16); w.Write(0, 8);
        w.Write(1, 1); w.Write(0, 16); w.Write(0, 16); w.Write(0, 8);
        w.Write(1, 1);
        return w.ToArray();
    }

    public sealed record Frame(bool Long, bool PreviousLong, bool NextLong, int[] FloorY, int[][] Classes, int[][][] Entries)
    {
        /// <summary>Per channel: a value for floor Y (negative marks an unused floor).</summary>
        public int Size => Long ? LongSize : ShortSize;
    }

    /// <summary>Writes an audio packet and returns the residue vectors (before the floor) it is meant to decode to, per channel.</summary>
    public byte[] Packet(Frame frame, out float[][] residue)
    {
        VorbisBitWriter w = new();
        w.Write(0, 1);
        w.Write(frame.Long ? 1UL : 0, 1);
        if (frame.Long)
        {
            w.Write(frame.PreviousLong ? 1UL : 0, 1);
            w.Write(frame.NextLong ? 1UL : 0, 1);
        }
        int half = frame.Size / 2;
        for (int c = 0; c < Channels; c++)
        {
            if (frame.FloorY[c] < 0)
            {
                w.Write(0, 1);
                continue;
            }
            w.Write(1, 1);
            w.Write((ulong)frame.FloorY[c], 8);
            w.Write((ulong)frame.FloorY[c], 8);
        }

        bool[] skip = new bool[Channels];
        for (int c = 0; c < Channels; c++) skip[c] = frame.FloorY[c] < 0;
        if (Coupling && (!skip[0] || !skip[1])) skip[0] = skip[1] = false;

        // The vectors are bundled per channel, or as one interleaved vector for type 2.
        int vectorCount = ResidueType == 2 ? 1 : Channels;
        int vectorLength = ResidueType == 2 ? half * Channels : half;
        float[][] vectors = new float[vectorCount][];
        for (int v = 0; v < vectorCount; v++) vectors[v] = new float[vectorLength];
        bool allSkipped = skip.All(s => s);
        int limitEnd = Math.Min(ResidueEnd, vectorLength);
        int partitions = limitEnd / PartitionSize;
        if (!(ResidueType == 2 && allSkipped) && partitions > 0)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                int partition = 0;
                while (partition < partitions)
                {
                    if (pass == 0)
                    {
                        for (int v = 0; v < vectorCount; v++)
                        {
                            if (ResidueType != 2 && skip[v]) continue;
                            // classwords = 2: the class of partition and partition+1 are combined as digits base 2 (first one most significant).
                            int cw = frame.Classes[v][partition] * 2 + (partition + 1 < partitions ? frame.Classes[v][partition + 1] : 0);
                            w.Code2(cw);
                        }
                    }
                    for (int i = 0; i < 2 && partition < partitions; i++)
                    {
                        for (int v = 0; v < vectorCount; v++)
                        {
                            if (ResidueType != 2 && skip[v]) continue;
                            int cls = frame.Classes[v][partition];
                            if (pass == 1 && cls == 0) continue;
                            float[][] book = pass == 1 || cls == 0 ? Book1 : Book2;
                            int dimensions = book[0].Length;
                            int[] entries = frame.Entries[v][partition * 2 + pass];
                            int steps = PartitionSize / dimensions;
                            for (int s = 0; s < steps; s++)
                            {
                                w.Code2(entries[s]);
                                for (int j = 0; j < dimensions; j++)
                                {
                                    int index = ResidueType == 0 ? partition * PartitionSize + s + j * steps : partition * PartitionSize + s * dimensions + j;
                                    vectors[v][index] += book[entries[s]][j];
                                }
                            }
                        }
                        partition++;
                    }
                }
            }
        }
        residue = new float[Channels][];
        for (int c = 0; c < Channels; c++)
        {
            residue[c] = new float[half];
            if (ResidueType == 2)
            {
                for (int i = 0; i < half; i++) residue[c][i] = vectors[0][i * Channels + c];
            }
            else Array.Copy(vectors[c], residue[c], half);
        }
        return w.ToArray();
    }

    /// <summary>The whole stream in an Ogg container.</summary>
    public byte[] Ogg(IReadOnlyList<byte[]> audioPackets, long finalGranule)
    {
        OggTestWriter writer = new(11);
        writer.Page([Identification()], 0, OggPageKind.BeginOfStream);
        writer.Page([Comment(), Setup()], 0);
        for (int i = 0; i < audioPackets.Count; i += 4)
            writer.Page([.. audioPackets.Skip(i).Take(4)], i + 4 >= audioPackets.Count ? finalGranule : -1 + 0 * i, i + 4 >= audioPackets.Count ? OggPageKind.EndOfStream : OggPageKind.None);
        return writer.ToArray();
    }
}
