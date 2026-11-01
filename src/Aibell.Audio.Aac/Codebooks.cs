namespace Aibell.Audio.Aac;

/// <summary>The spectral Huffman codebooks as decoders plus the index-to-tuple translation of ISO/IEC 14496-3 subclause 4.6.3.3.</summary>
internal static class Codebooks
{
    public const int ZeroBook = 0;
    public const int NoiseBook = 13;
    public const int IntensityBook2 = 14;
    public const int IntensityBook = 15;

    private static readonly int[] Dimension = [0, 4, 4, 4, 4, 2, 2, 2, 2, 2, 2, 2];
    private static readonly int[] LargestAbsolute = [0, 1, 1, 2, 2, 4, 4, 7, 7, 12, 12, 16];
    private static readonly bool[] UnsignedBook = [false, false, false, true, true, false, false, true, true, true, true, true];

    public static readonly HuffmanDecoder ScaleFactor = new(HuffmanTables.ScaleFactor);

    private static readonly HuffmanDecoder[] Books =
    [
        null!, new(HuffmanTables.Spectrum1), new(HuffmanTables.Spectrum2), new(HuffmanTables.Spectrum3), new(HuffmanTables.Spectrum4),
        new(HuffmanTables.Spectrum5), new(HuffmanTables.Spectrum6), new(HuffmanTables.Spectrum7), new(HuffmanTables.Spectrum8),
        new(HuffmanTables.Spectrum9), new(HuffmanTables.Spectrum10), new(HuffmanTables.Spectrum11)
    ];

    // Tuples[book][index * dimension + i] in decoding order (w, x, y, z for quads; y, z for pairs).
    private static readonly sbyte[][] Tuples = BuildTuples();

    public static int Size(int book) => Dimension[book];

    public static bool IsUnsigned(int book) => UnsignedBook[book];

    public static int Lav(int book) => LargestAbsolute[book];

    /// <summary>Decodes one quad or pair of quantized values into <paramref name="values"/> (before escape handling for book 11).</summary>
    public static void Decode(int book, ref BitReader reader, Span<int> values)
    {
        int index = Books[book].Decode(ref reader);
        int dimension = Dimension[book];
        sbyte[] table = Tuples[book];
        for (int i = 0; i < dimension; i++) values[i] = table[index * dimension + i];
        if (UnsignedBook[book])
        {
            for (int i = 0; i < dimension; i++)
            {
                if (values[i] != 0 && reader.ReadFlag()) values[i] = -values[i];
            }
        }
    }

    private static sbyte[][] BuildTuples()
    {
        sbyte[][] result = new sbyte[12][];
        for (int book = 1; book <= 11; book++)
        {
            int dimension = Dimension[book], lav = LargestAbsolute[book];
            int modulus = UnsignedBook[book] ? lav + 1 : 2 * lav + 1;
            int offset = UnsignedBook[book] ? 0 : lav;
            int count = 1;
            for (int i = 0; i < dimension; i++) count *= modulus;
            sbyte[] table = new sbyte[count * dimension];
            for (int index = 0; index < count; index++)
            {
                int rest = index;
                for (int i = dimension - 1; i >= 0; i--)
                {
                    table[index * dimension + i] = (sbyte)(rest % modulus - offset);
                    rest /= modulus;
                }
            }
            result[book] = table;
        }
        return result;
    }
}
