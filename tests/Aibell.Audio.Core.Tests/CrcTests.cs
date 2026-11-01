namespace Aibell.Audio.Core.Tests;

public class CrcTests
{
    private static readonly byte[] Check = "123456789"u8.ToArray();

    [Test]
    public void Crc8_MatchesTheCatalogueCheckValue() => Assert.That(Crc.Crc8(Check), Is.EqualTo(0xF4));

    [Test]
    public void Crc16_MatchesTheCatalogueCheckValue() => Assert.That(Crc.Crc16(Check), Is.EqualTo(0xFEE8));

    [Test]
    public void Crc32Ogg_MatchesABitwiseComputation()
    {
        byte[] data = new byte[1000];
        new Random(5).NextBytes(data);
        uint expected = 0;
        foreach (byte b in data)
        {
            expected ^= (uint)b << 24;
            for (int bit = 0; bit < 8; bit++) expected = (expected & 0x80000000) != 0 ? (expected << 1) ^ 0x04C11DB7 : expected << 1;
        }
        Assert.That(Crc.Crc32Ogg(data), Is.EqualTo(expected));
    }

    [Test]
    public void Crcs_CanBeContinuedAcrossBlocks()
    {
        Assert.That(Crc.Crc16(Check.AsSpan(4), Crc.Crc16(Check.AsSpan(0, 4))), Is.EqualTo(Crc.Crc16(Check)));
        Assert.That(Crc.Crc8(Check.AsSpan(4), Crc.Crc8(Check.AsSpan(0, 4))), Is.EqualTo(Crc.Crc8(Check)));
        Assert.That(Crc.Crc32Ogg(Check.AsSpan(4), Crc.Crc32Ogg(Check.AsSpan(0, 4))), Is.EqualTo(Crc.Crc32Ogg(Check)));
    }
}
