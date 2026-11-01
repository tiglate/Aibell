namespace Aibell.Archive.Rar.Tests;

[TestFixture]
public class RarPrimitivesTests
{
    [Test]
    public void Crc32_MatchesTheStandardCheckValue()
    {
        Assert.That(RarCrc32.Compute("123456789"u8), Is.EqualTo(0xCBF43926u));
    }

    [Test]
    public void BitReader_ReadsMostSignificantBitFirstAndZeroFillsPastTheEnd()
    {
        var reader = new RarBitReader([0b1010_0000, 0xFF], 0, 2);

        Assert.Multiple(() =>
        {
            Assert.That(reader.Read(3), Is.EqualTo(0b101));
            Assert.That(reader.Read(5), Is.EqualTo(0));
            Assert.That(reader.Read(8), Is.EqualTo(0xFF));
            Assert.That(reader.Overrun, Is.False);
            Assert.That(reader.Read(4), Is.EqualTo(0));
            Assert.That(reader.Overrun, Is.True);
        });
    }

    [Test]
    public void Huffman_AssignsCanonicalCodes()
    {
        // Lengths 1,2,3,3 give the codes 0, 10, 110, 111.
        var code = new RarHuffman();
        code.Build(new byte[] { 1, 2, 3, 3 });
        var reader = new RarBitReader([0b0101_1011, 0b1000_0000], 0, 2);

        Assert.Multiple(() =>
        {
            Assert.That(code.Decode(reader), Is.EqualTo(0));
            Assert.That(code.Decode(reader), Is.EqualTo(1));
            Assert.That(code.Decode(reader), Is.EqualTo(2));
            Assert.That(code.Decode(reader), Is.EqualTo(3));
        });
    }

    [Test]
    public void Huffman_RejectsAnOverSubscribedCode()
    {
        var code = new RarHuffman();

        Assert.Throws<InvalidDataException>(() => code.Build(new byte[] { 1, 1, 1 }));
    }

    [Test]
    public void Huffman_RejectsBitsThatMatchNoCode()
    {
        var code = new RarHuffman();
        code.Build(new byte[] { 2, 2 });   // codes 00 and 01 only
        var reader = new RarBitReader([0xFF, 0xFF], 0, 2);

        Assert.Throws<InvalidDataException>(() => code.Decode(reader));
    }
}
