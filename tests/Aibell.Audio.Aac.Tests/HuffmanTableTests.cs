using Aibell.Audio.Aac;

namespace Aibell.Audio.Aac.Tests;

[TestFixture]
public sealed class HuffmanTableTests
{
    private static IEnumerable<TestCaseData> Books()
    {
        yield return new TestCaseData("scalefactor", HuffmanTables.ScaleFactor, 121);
        yield return new TestCaseData("1", HuffmanTables.Spectrum1, 81);
        yield return new TestCaseData("2", HuffmanTables.Spectrum2, 81);
        yield return new TestCaseData("3", HuffmanTables.Spectrum3, 81);
        yield return new TestCaseData("4", HuffmanTables.Spectrum4, 81);
        yield return new TestCaseData("5", HuffmanTables.Spectrum5, 81);
        yield return new TestCaseData("6", HuffmanTables.Spectrum6, 81);
        yield return new TestCaseData("7", HuffmanTables.Spectrum7, 64);
        yield return new TestCaseData("8", HuffmanTables.Spectrum8, 64);
        yield return new TestCaseData("9", HuffmanTables.Spectrum9, 169);
        yield return new TestCaseData("10", HuffmanTables.Spectrum10, 169);
        yield return new TestCaseData("11", HuffmanTables.Spectrum11, 289);
    }

    [TestCaseSource(nameof(Books))]
    public void Codebook_IsAPrefixCodeOfTheExpectedSize(string name, (byte Length, uint Code)[] book, int size)
    {
        Assert.That(book, Has.Length.EqualTo(size), name);
        double kraft = 0;
        foreach ((byte length, uint code) in book)
        {
            Assert.That(code >> length, Is.Zero, $"{name}: codeword wider than its length");
            kraft += Math.Pow(2, -length);
        }
        Assert.That(kraft, Is.LessThanOrEqualTo(1.0 + 1e-12), name);
        for (int i = 0; i < book.Length; i++)
        {
            for (int j = 0; j < book.Length; j++)
            {
                if (i == j || book[i].Length > book[j].Length) continue;
                Assert.That(book[j].Code >> (book[j].Length - book[i].Length) == book[i].Code, Is.False, $"{name}: {i} is a prefix of {j}");
            }
        }
    }

    [TestCaseSource(nameof(Books))]
    public void Decoder_RoundTripsEverySymbol(string name, (byte Length, uint Code)[] book, int size)
    {
        HuffmanDecoder decoder = new(book);
        for (int symbol = 0; symbol < size; symbol++)
        {
            // Codeword followed by a different word's bits so the decoder must stop at the right length.
            ulong bits = (ulong)book[symbol].Code << (32 - book[symbol].Length) | (0x5a5a5a5au >> book[symbol].Length);
            byte[] bytes = [(byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits, 0, 0];
            BitReader reader = new(bytes);
            Assert.That(decoder.Decode(ref reader), Is.EqualTo(symbol), $"{name} symbol {symbol}");
            Assert.That(reader.BitPosition, Is.EqualTo(book[symbol].Length));
        }
    }
}
