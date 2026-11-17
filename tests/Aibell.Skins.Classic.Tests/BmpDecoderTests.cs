using System.Buffers.Binary;

namespace Aibell.Skins.Classic.Tests;

[TestFixture]
public sealed class BmpDecoderTests
{
    [Test]
    public void Decodes24BitBottomUpRowsWithPadding()
    {
        byte[] bmp = SkinTestData.Bmp24(3, 2, (x, y) => (uint)(x * 0x10 << 16 | y * 0x20 << 8 | 0x05));
        SkinImage image = BmpDecoder.Decode(bmp);
        Assert.That(image.Width, Is.EqualTo(3));
        Assert.That(image.Height, Is.EqualTo(2));
        Assert.That(image[0, 0], Is.EqualTo(0xFF000005u));
        Assert.That(image[2, 1], Is.EqualTo(0xFF202005u));
    }

    [Test]
    public void Decodes8BitPaletteTopDown()
    {
        uint[] palette = [0x000000, 0x112233, 0xABCDEF];
        // Two rows of 3 pixels, padded to 4 bytes, stored top row first.
        byte[] pixels = [1, 2, 0, 0, 2, 1, 1, 0];
        SkinImage image = BmpDecoder.Decode(SkinTestData.Indexed(3, 2, 8, palette, pixels, topDown: true));
        Assert.That(image[0, 0], Is.EqualTo(0xFF112233u));
        Assert.That(image[1, 0], Is.EqualTo(0xFFABCDEFu));
        Assert.That(image[0, 1], Is.EqualTo(0xFFABCDEFu));
    }

    [Test]
    public void Decodes1And4BitPixels()
    {
        SkinImage mono = BmpDecoder.Decode(SkinTestData.Indexed(8, 1, 1, [0x000000, 0xFFFFFF], [0b1010_0001, 0, 0, 0]));
        Assert.That(mono[0, 0], Is.EqualTo(0xFFFFFFFFu));
        Assert.That(mono[1, 0], Is.EqualTo(0xFF000000u));
        Assert.That(mono[7, 0], Is.EqualTo(0xFFFFFFFFu));

        SkinImage nibbles = BmpDecoder.Decode(SkinTestData.Indexed(3, 1, 4, [0x000000, 0x0000FF, 0x00FF00], [0x12, 0x10, 0, 0]));
        Assert.That(nibbles[0, 0], Is.EqualTo(0xFF0000FFu));
        Assert.That(nibbles[1, 0], Is.EqualTo(0xFF00FF00u));
        Assert.That(nibbles[2, 0], Is.EqualTo(0xFF0000FFu));
    }

    [Test]
    public void DecodesRunLength8()
    {
        // Row 0 (bottom): three of colour 1, then an absolute run of 2, 1, 2 (padded to a word); end of line;
        // row 1: four of colour 2; end of bitmap.
        byte[] rle = [3, 1, 0, 3, 2, 1, 2, 0, 0, 0, 4, 2, 0, 1];
        SkinImage image = BmpDecoder.Decode(SkinTestData.Indexed(6, 2, 8, [0x000000, 0xFF0000, 0x00FF00], rle, compression: 1));
        Assert.That(image[0, 1], Is.EqualTo(0xFFFF0000u));
        Assert.That(image[3, 1], Is.EqualTo(0xFF00FF00u));
        Assert.That(image[4, 1], Is.EqualTo(0xFFFF0000u));
        Assert.That(image[5, 1], Is.EqualTo(0xFF00FF00u));
        Assert.That(image[3, 0], Is.EqualTo(0xFF00FF00u));
        Assert.That(image[4, 0], Is.EqualTo(0xFF000000u));
    }

    [Test]
    public void DecodesRunLength4WithDelta()
    {
        // Two pixels alternating 1/2, a delta of one pixel right, then an absolute run of three nibbles (padded to a word).
        byte[] rle = [2, 0x12, 0, 2, 1, 0, 0, 3, 0x21, 0x20, 0, 1];
        SkinImage image = BmpDecoder.Decode(SkinTestData.Indexed(6, 1, 4, [0x000000, 0x0000FF, 0x00FF00], rle, compression: 2));
        Assert.That(image[0, 0], Is.EqualTo(0xFF0000FFu));
        Assert.That(image[1, 0], Is.EqualTo(0xFF00FF00u));
        Assert.That(image[2, 0], Is.EqualTo(0xFF000000u));
        Assert.That(image[3, 0], Is.EqualTo(0xFF00FF00u));
        Assert.That(image[4, 0], Is.EqualTo(0xFF0000FFu));
        Assert.That(image[5, 0], Is.EqualTo(0xFF00FF00u));
    }

    [Test]
    public void Decodes16And32BitWithMasks()
    {
        byte[] bmp16 = new byte[54 + 4];
        SkinTestData.Header(bmp16, 1, 1, 16, 54, 0, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp16.AsSpan(54), 0x7C00);
        Assert.That(BmpDecoder.Decode(bmp16)[0, 0], Is.EqualTo(0xFFFF0000u));

        byte[] bmp32 = new byte[54 + 12 + 4];
        SkinTestData.Header(bmp32, 1, 1, 32, 66, 3, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp32.AsSpan(54), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp32.AsSpan(58), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp32.AsSpan(62), 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp32.AsSpan(66), 0x11223344);
        Assert.That(BmpDecoder.Decode(bmp32)[0, 0], Is.EqualTo(0xFF332211u));

        byte[] plain32 = new byte[54 + 4];
        SkinTestData.Header(plain32, 1, 1, 32, 54, 0, 0);
        plain32[54] = 0x30;
        plain32[55] = 0x20;
        plain32[56] = 0x10;
        Assert.That(BmpDecoder.Decode(plain32)[0, 0], Is.EqualTo(0xFF102030u), "alpha is ignored, as classic players did");
    }

    [Test]
    public void ToleratesABadOffsetAndATruncatedFile()
    {
        byte[] bmp = SkinTestData.Bmp24(4, 4, (_, _) => 0x0000FF);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 999_999);
        SkinImage image = BmpDecoder.Decode(bmp.AsSpan(0, bmp.Length - 20));
        Assert.That(image[0, 3], Is.EqualTo(0xFF0000FFu), "the first stored row is the bottom one");
        Assert.That(image[3, 0], Is.EqualTo(0xFF000000u), "missing data stays black");
    }

    [Test]
    public void DecodesTheOs2CoreHeader()
    {
        byte[] data = new byte[14 + 12 + 6 + 4];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24), 8);
        // Palette of two three-byte entries (the core header fills 256, but the data runs out after two), then the pixel.
        data[26] = 0x00; data[27] = 0x00; data[28] = 0x00;
        data[29] = 0x0A; data[30] = 0x0B; data[31] = 0x0C;
        Assert.That(() => BmpDecoder.Decode(data), Throws.Nothing);
    }

    [TestCase(new byte[] { 0x42, 0x4D })]
    [TestCase(new byte[] { 0x50, 0x4B, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void RejectsWhatIsNotABmp(byte[] data) =>
        Assert.That(() => BmpDecoder.Decode(data), Throws.TypeOf<InvalidDataException>());

    [Test]
    public void RejectsUnsupportedHeadersDepthsAndSizes()
    {
        byte[] bmp = SkinTestData.Bmp24(2, 2, (_, _) => 0);
        byte[] depth = (byte[])bmp.Clone();
        BinaryPrimitives.WriteInt16LittleEndian(depth.AsSpan(28), 2);
        Assert.That(() => BmpDecoder.Decode(depth), Throws.TypeOf<InvalidDataException>());

        byte[] huge = (byte[])bmp.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(18), 100_000);
        Assert.That(() => BmpDecoder.Decode(huge), Throws.TypeOf<InvalidDataException>());

        byte[] compression = (byte[])bmp.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(compression.AsSpan(30), 4);
        Assert.That(() => BmpDecoder.Decode(compression), Throws.TypeOf<InvalidDataException>());

        byte[] mismatched = (byte[])bmp.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(mismatched.AsSpan(30), 1);
        Assert.That(() => BmpDecoder.Decode(mismatched), Throws.TypeOf<InvalidDataException>());

        byte[] header = (byte[])bmp.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), 20);
        Assert.That(() => BmpDecoder.Decode(header), Throws.TypeOf<InvalidDataException>());
    }
}
