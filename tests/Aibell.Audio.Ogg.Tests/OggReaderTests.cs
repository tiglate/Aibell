namespace Aibell.Audio.Ogg.Tests;

public class OggReaderTests
{
    private static byte[] Packet(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static bool AcceptsAll(ReadOnlySpan<byte> first) => true;

    private static List<(byte[] Data, long Granule, bool Begin, bool End, bool Lost)> ReadAll(byte[] file, Func<ReadOnlySpan<byte>, bool>? accepts = null)
    {
        OggPacketReader reader = new(new MemoryStream(file), accepts ?? AcceptsAll);
        List<(byte[], long, bool, bool, bool)> all = [];
        while (reader.ReadPacket(out OggPacket packet)) all.Add((packet.Data.ToArray(), packet.PageGranule, packet.BeginOfStream, packet.EndOfStream, packet.LostBefore));
        return all;
    }

    [Test]
    public void SeveralPackets_OnOnePage()
    {
        OggTestWriter writer = new(7);
        byte[][] packets = [Packet(30, 1), Packet(0, 2), Packet(254, 3), Packet(255, 4), Packet(256, 5), Packet(1000, 6)];
        writer.Page(packets, 12345, OggPageKind.BeginOfStream | OggPageKind.EndOfStream);
        var read = ReadAll(writer.ToArray());
        Assert.That(read.Select(r => r.Data), Is.EqualTo(packets));
        Assert.That(read.Select(r => r.Granule), Is.EqualTo(new long[] { -1, -1, -1, -1, -1, 12345 }));
        Assert.That(read[0].Begin, Is.True);
        Assert.That(read[1].Begin, Is.False);
        Assert.That(read.Take(5).Select(r => r.End), Is.All.False);
        Assert.That(read[5].End, Is.True);
        Assert.That(read.Select(r => r.Lost), Is.All.False);
    }

    [Test]
    public void LongPackets_ContinueAcrossPages()
    {
        OggTestWriter writer = new(1);
        byte[] big = Packet(255 * 255 + 700, 9);
        byte[] first = big[..(255 * 254)];
        byte[] rest = big[(255 * 254)..];
        writer.Page([Packet(10, 1), first], -1, OggPageKind.BeginOfStream, lastPacketContinues: true);
        writer.Page([rest, Packet(20, 2)], 500, OggPageKind.Continued | OggPageKind.EndOfStream);
        var read = ReadAll(writer.ToArray());
        Assert.That(read, Has.Count.EqualTo(3));
        Assert.That(read[1].Data, Is.EqualTo(big));
        Assert.That(read[0].Granule, Is.EqualTo(-1));
        Assert.That(read[1].Granule, Is.EqualTo(-1));
        Assert.That(read[2].Granule, Is.EqualTo(500));
        Assert.That(read[2].End, Is.True);
    }

    [Test]
    public void APacketOfExactlyTwoFiftyFiveBytesMultiples_EndsWithAZeroLace()
    {
        OggTestWriter writer = new(1);
        byte[][] packets = [Packet(510, 1), Packet(255, 2), Packet(5, 3)];
        writer.Page(packets, 99, OggPageKind.BeginOfStream | OggPageKind.EndOfStream);
        Assert.That(ReadAll(writer.ToArray()).Select(r => r.Data), Is.EqualTo(packets));
    }

    [Test]
    public void OtherStreams_AreSkipped_AndTheFirstAcceptedOneIsFollowed()
    {
        OggTestWriter video = new(100), audio = new(200);
        video.Page([Packet(40, 1)], 0, OggPageKind.BeginOfStream);
        audio.Page([[0x01, (byte)'v', (byte)'o', (byte)'r']], 0, OggPageKind.BeginOfStream);
        byte[] interleaved = [.. video.ToArray(), .. audio.ToArray()];
        OggTestWriter video2 = new(100), audio2 = new(200);
        video2.Page([Packet(40, 2)], 5);
        audio2.Page([Packet(33, 3)], 10, OggPageKind.EndOfStream);
        byte[] file = [.. interleaved, .. video2.ToArray(), .. audio2.ToArray()];
        var read = ReadAll(file, first => first.Length > 0 && first[0] == 1);
        Assert.That(read, Has.Count.EqualTo(2));
        Assert.That(read[0].Data, Is.EqualTo(new byte[] { 0x01, (byte)'v', (byte)'o', (byte)'r' }));
        Assert.That(read[1].Data, Has.Length.EqualTo(33));
    }

    [Test]
    public void StreamsThatAreNotAccepted_YieldNothing()
    {
        OggTestWriter writer = new(1);
        writer.Page([Packet(40, 1)], 0, OggPageKind.BeginOfStream | OggPageKind.EndOfStream);
        Assert.That(ReadAll(writer.ToArray(), _ => false), Is.Empty);
    }

    [Test]
    public void ChainedStreams_AreFollowedOneAfterTheOther()
    {
        OggTestWriter a = new(1), b = new(2);
        a.Stream([Packet(20, 1), Packet(20, 2), Packet(20, 3)], 2, i => (i + 1) * 100);
        b.Stream([Packet(30, 4), Packet(30, 5)], 1, i => (i + 1) * 50);
        var read = ReadAll([.. a.ToArray(), .. b.ToArray()]);
        Assert.That(read, Has.Count.EqualTo(5));
        Assert.That(read.Select(r => r.Begin), Is.EqualTo(new[] { true, false, false, true, false }));
        Assert.That(read.Select(r => r.End), Is.EqualTo(new[] { false, false, true, false, true }));
    }

    [Test]
    public void AMissingPage_DropsTheInterruptedPacket_AndIsReported()
    {
        OggTestWriter writer = new(1);
        byte[] big = Packet(255 * 3 + 100, 1);
        writer.Page([Packet(10, 0)], 0, OggPageKind.BeginOfStream);
        writer.Page([big[..255]], -1, lastPacketContinues: true);
        writer.Page([big[255..510]], -1, OggPageKind.Continued, lastPacketContinues: true);
        writer.Page([big[510..]], 800, OggPageKind.Continued);
        writer.Page([Packet(7, 2)], 900);
        byte[] whole = writer.ToArray();

        // Remove the second page of the long packet.
        OggPageReader pages = new(new MemoryStream(whole));
        List<long> offsets = [];
        while (pages.ReadPage()) offsets.Add(pages.Header.Offset);
        byte[] damaged = [.. whole[..(int)offsets[2]], .. whole[(int)offsets[3]..]];
        var read = ReadAll(damaged);
        Assert.That(read.Select(r => r.Data.Length), Is.EqualTo(new[] { 10, 7 }));
        Assert.That(read[1].Lost, Is.True);
        Assert.That(read[0].Lost, Is.False);
    }

    [Test]
    public void PagesWithABadCrc_AreSkipped_AndJunkIsResynchronizedOver()
    {
        OggTestWriter writer = new(1);
        writer.Page([Packet(10, 1)], 0, OggPageKind.BeginOfStream);
        writer.Page([Packet(20, 2)], 10);
        writer.Page([Packet(30, 3)], 20);
        writer.Page([Packet(40, 4)], 30, OggPageKind.EndOfStream);
        byte[] file = writer.ToArray();
        OggPageReader pages = new(new MemoryStream(file));
        List<long> offsets = [];
        while (pages.ReadPage()) offsets.Add(pages.Header.Offset);
        file[offsets[1] + 40] ^= 0x10;
        byte[] withJunk = [.. file[..(int)offsets[3]], .. "OggSOgg\0 not a page"u8.ToArray(), .. new byte[100], .. file[(int)offsets[3]..]];
        var read = ReadAll(withJunk);
        Assert.That(read.Select(r => r.Data.Length), Is.EqualTo(new[] { 10, 30, 40 }));
        Assert.That(read[1].Lost, Is.True);
    }

    [Test]
    public void ATruncatedLastPage_IsIgnored()
    {
        OggTestWriter writer = new(1);
        writer.Page([Packet(10, 1)], 0, OggPageKind.BeginOfStream);
        writer.Page([Packet(500, 2)], 10);
        byte[] file = writer.ToArray();
        Assert.That(ReadAll(file[..^20]), Has.Count.EqualTo(1));
    }

    [Test]
    public void SeekToPage_ContinuesWithTheNextPacketsAndSkipsAContinuedOne()
    {
        OggTestWriter writer = new(1);
        byte[] big = Packet(255 * 2 + 50, 1);
        writer.Page([Packet(10, 0)], 0, OggPageKind.BeginOfStream);
        writer.Page([big[..255]], -1, lastPacketContinues: true);
        long secondPart = writer.Length;
        writer.Page([big[255..], Packet(8, 3)], 70, OggPageKind.Continued);
        writer.Page([Packet(9, 4)], 80, OggPageKind.EndOfStream);
        OggPacketReader reader = new(new MemoryStream(writer.ToArray()), AcceptsAll);
        Assert.That(reader.ReadPacket(out _), Is.True);
        Assert.That(reader.SeekToPage(secondPart), Is.True);
        List<int> lengths = [];
        List<bool> lost = [];
        while (reader.ReadPacket(out OggPacket packet))
        {
            lengths.Add(packet.Data.Length);
            lost.Add(packet.LostBefore);
        }
        Assert.That(lengths, Is.EqualTo(new[] { 8, 9 }));
        Assert.That(lost, Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public void ForwardOnlyStreams_AreRead_ButCannotSeek()
    {
        OggTestWriter writer = new(1);
        writer.Stream([.. Enumerable.Range(0, 50).Select(i => Packet(300 + i, i))], 4, i => i * 10);
        OggPacketReader reader = new(new Aibell.Audio.Wav.Tests.ForwardOnlyStream(writer.ToArray()), AcceptsAll);
        int count = 0;
        while (reader.ReadPacket(out _)) count++;
        Assert.That(count, Is.EqualTo(50));
        Assert.That(reader.SeekToPage(0), Is.False);
    }

    [Test]
    public void RandomDamage_NeverThrows()
    {
        OggTestWriter writer = new(1);
        writer.Stream([.. Enumerable.Range(0, 80).Select(i => Packet(i * 37 % 700, i))], 5, i => i * 10);
        byte[] good = writer.ToArray();
        Random random = new(3);
        for (int round = 0; round < 500; round++)
        {
            byte[] bad = (byte[])good.Clone();
            for (int f = 0; f < 1 + random.Next(8); f++) bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
            if (round % 3 == 0) bad = bad[..random.Next(bad.Length)];
            ReadAll(bad);
        }
    }

    [Test]
    public void RealFile_StartsWithTheVorbisIdentificationHeader()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "fixture.ogg");
        var read = ReadAll(File.ReadAllBytes(path), first => first.Length > 7 && first[0] == 1 && first[1..7].SequenceEqual("vorbis"u8));
        Assert.That(read, Has.Count.GreaterThan(5));
        Assert.That(read[0].Data[0], Is.EqualTo(1));
        Assert.That(read[0].Begin, Is.True);
        Assert.That(read[^1].End, Is.True);
        Assert.That(read[^1].Granule, Is.GreaterThan(40000));
    }

    [Test]
    public void PageHeader_ExposesTheFields()
    {
        OggTestWriter writer = new(0xABCDEF01);
        writer.Page([Packet(5, 1)], -1, OggPageKind.BeginOfStream);
        writer.Page([Packet(5, 2)], 777, OggPageKind.EndOfStream);
        OggPageReader pages = new(new MemoryStream(writer.ToArray()));
        Assert.That(pages.ReadPage(), Is.True);
        Assert.That(pages.Header.IsFirst, Is.True);
        Assert.That(pages.Header.Granule, Is.EqualTo(-1));
        Assert.That(pages.Header.Serial, Is.EqualTo(0xABCDEF01));
        Assert.That(pages.Body.ToArray(), Has.Length.EqualTo(5));
        Assert.That(pages.Segments.ToArray(), Is.EqualTo(new byte[] { 5 }));
        Assert.That(pages.ReadPage(), Is.True);
        Assert.That(pages.Header.Sequence, Is.EqualTo(1u));
        Assert.That(pages.Header.IsLast, Is.True);
        Assert.That(pages.Header.Granule, Is.EqualTo(777));
        Assert.That(pages.ReadPage(), Is.False);
    }
}
