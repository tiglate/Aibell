namespace Aibell.Audio.Ogg;

/// <summary>One packet of the logical stream being followed.</summary>
public readonly ref struct OggPacket
{
    public OggPacket(ReadOnlySpan<byte> data, long pageGranule, bool beginOfStream, bool endOfStream, bool lostBefore, long pageOffset)
    {
        Data = data;
        PageGranule = pageGranule;
        BeginOfStream = beginOfStream;
        EndOfStream = endOfStream;
        LostBefore = lostBefore;
        PageOffset = pageOffset;
    }

    public ReadOnlySpan<byte> Data { get; }

    /// <summary>The granule position of the page this packet ends on, when it is the last packet completed on that page; otherwise -1.</summary>
    public long PageGranule { get; }

    /// <summary>True for the first packet of a logical stream (a link, when streams are chained).</summary>
    public bool BeginOfStream { get; }

    /// <summary>True for the last packet of a logical stream.</summary>
    public bool EndOfStream { get; }

    /// <summary>True when pages were lost or skipped (or the reader was repositioned) just before this packet.</summary>
    public bool LostBefore { get; }

    /// <summary>The stream offset of the page this packet ends on.</summary>
    public long PageOffset { get; }
}

/// <summary>
/// Reassembles the packets of one logical Ogg stream (RFC 3533): pages of other streams are skipped; packets continued across pages are joined; a missing
/// page drops the packet it interrupted. The stream followed is the first one whose opening packet <c>accepts</c> approves; when it ends, the next such
/// stream (a chained link) is followed.
/// </summary>
public sealed class OggPacketReader
{
    private readonly OggPageReader pages;
    private readonly Func<ReadOnlySpan<byte>, bool> accepts;
    private byte[] packet = new byte[8192];
    private int packetLength;
    private bool assembling;
    private bool following;
    private bool hasSerial;
    private uint serial;
    private uint expectedSequence;
    private bool haveSequence;
    private bool lost;
    private bool pageLoaded;
    private int segmentIndex;
    private int bodyOffset;
    private bool packetStartsOnPage;

    public OggPacketReader(Stream stream, Func<ReadOnlySpan<byte>, bool> accepts)
    {
        pages = new OggPageReader(stream);
        this.accepts = accepts;
    }

    public OggPageReader Pages => pages;

    public bool CanSeek => pages.CanSeek;

    public uint SerialNumber => serial;

    /// <summary>Forgets the packet in progress and continues at the first intact page at or after <paramref name="offset"/> that belongs to the followed stream.</summary>
    public bool SeekToPage(long offset)
    {
        if (!pages.Seek(offset)) return false;
        assembling = false;
        pageLoaded = false;
        packetLength = 0;
        haveSequence = false;
        lost = true;
        // The stream is still the one followed, even if its last packet was already returned.
        following = hasSerial;
        return true;
    }

    /// <summary>Measures a packet (for example its duration in samples); a negative result counts as 0.</summary>
    public delegate int PacketMeasure(ReadOnlySpan<byte> packet);

    /// <summary>
    /// Adds up <paramref name="measure"/> over the packets that end on the current page after the one last returned. A packet that continues onto the next page is not
    /// included. Zero when no page is loaded.
    /// </summary>
    public long MeasureRestOfPage(PacketMeasure measure)
    {
        if (!pageLoaded) return 0;
        ReadOnlySpan<byte> segments = pages.Segments;
        ReadOnlySpan<byte> body = pages.Body;
        long total = 0;
        int index = segmentIndex;
        int offset = bodyOffset;
        int start = offset;
        while (index < segments.Length)
        {
            int lace = segments[index++];
            offset += lace;
            if (lace == 255) continue;
            total += Math.Max(0, measure(body.Slice(start, offset - start)));
            start = offset;
        }
        return total;
    }

    /// <summary>Reads the next packet. False at the end of the data.</summary>
    public bool ReadPacket(out OggPacket result)
    {
        while (true)
        {
            if (!pageLoaded && !LoadPage())
            {
                result = default;
                return false;
            }
            OggPageHeader header = pages.Header;
            ReadOnlySpan<byte> segments = pages.Segments;
            ReadOnlySpan<byte> body = pages.Body;
            while (segmentIndex < segments.Length)
            {
                int lace = segments[segmentIndex++];
                int needed = packetLength + lace;
                if (needed > packet.Length) Array.Resize(ref packet, Math.Max(packet.Length * 2, needed));
                body.Slice(bodyOffset, lace).CopyTo(packet.AsSpan(packetLength));
                packetLength += lace;
                bodyOffset += lace;
                if (lace == 255)
                {
                    assembling = true;
                    continue;
                }
                bool lastOfPage = segmentIndex == segments.Length;
                bool begin = header.IsFirst && packetStartsOnPage;
                bool end = header.IsLast && lastOfPage;
                bool wasLost = lost;
                lost = false;
                assembling = false;
                packetStartsOnPage = false;
                int length = packetLength;
                packetLength = 0;
                result = new OggPacket(packet.AsSpan(0, length), lastOfPage ? header.Granule : -1, begin, end, wasLost, header.Offset);
                if (end) following = false;
                return true;
            }
            // The page is used up; a trailing 255 lace leaves `assembling` set for the next page.
            pageLoaded = false;
        }
    }

    private bool LoadPage()
    {
        while (pages.ReadPage())
        {
            OggPageHeader header = pages.Header;
            ReadOnlySpan<byte> segments = pages.Segments;
            if (!following)
            {
                if (!header.IsFirst || segments.Length == 0) continue;
                ReadOnlySpan<byte> body = pages.Body;
                if (!accepts(body[..Math.Min(segments[0], body.Length)])) continue;
                serial = header.Serial;
                hasSerial = true;
                following = true;
                assembling = false;
                packetLength = 0;
                haveSequence = false;
                lost = false;
            }
            else if (header.Serial != serial) continue;

            if (haveSequence && header.Sequence != expectedSequence)
            {
                // A page is missing: the packet in progress is gone.
                assembling = false;
                packetLength = 0;
                lost = true;
            }
            haveSequence = true;
            expectedSequence = header.Sequence + 1;

            segmentIndex = 0;
            bodyOffset = 0;
            packetStartsOnPage = true;
            if (header.IsContinued)
            {
                if (!assembling)
                {
                    // The start of this continued packet is not available: skip its remaining pieces.
                    while (segmentIndex < segments.Length)
                    {
                        int lace = segments[segmentIndex++];
                        bodyOffset += lace;
                        if (lace < 255) break;
                    }
                    lost = true;
                }
                else packetStartsOnPage = false;
            }
            else if (assembling)
            {
                assembling = false;
                packetLength = 0;
                lost = true;
            }
            pageLoaded = true;
            return true;
        }
        return false;
    }
}

/// <summary>Locating pages by granule position in a seekable Ogg stream.</summary>
public static class OggSeeker
{
    /// <summary>
    /// Finds the last page of logical stream <paramref name="serial"/> within [<paramref name="start"/>, <paramref name="end"/>) whose granule position is
    /// at most <paramref name="target"/> (pages without a granule position are ignored). The reader is left at an undefined position.
    /// </summary>
    public static bool FindLastPageAtOrBefore(OggPageReader pages, uint serial, long start, long end, long target, out long offset, out long granule)
    {
        offset = -1;
        granule = -1;
        long low = start, high = end;
        for (int round = 0; round < 64 && high - low > 27; round++)
        {
            long middle = low + (high - low) / 2;
            if (!ProbeAtOrAfter(pages, serial, middle, high, out long foundOffset, out long foundGranule))
            {
                high = middle;
                continue;
            }
            if (foundGranule <= target)
            {
                offset = foundOffset;
                granule = foundGranule;
                low = foundOffset + 1;
            }
            else high = middle;
        }
        return offset >= 0;
    }

    /// <summary>Finds the last page of the stream that has a granule position, scanning the final part of the data. Returns the number of pages of other streams seen after it via <paramref name="foreignPagesAfter"/>.</summary>
    public static bool FindLastPage(OggPageReader pages, uint serial, long start, long end, out OggPageHeader last, out bool foreignAfter)
    {
        last = default;
        foreignAfter = false;
        long from = Math.Max(start, end - 2 * OggPageReader.MaximumPageLength);
        if (!pages.Seek(from)) return false;
        bool found = false;
        while (pages.ReadPage())
        {
            OggPageHeader header = pages.Header;
            if (header.Serial == serial)
            {
                foreignAfter = false;
                if (header.Granule >= 0)
                {
                    last = header;
                    found = true;
                }
            }
            else if (header.IsFirst) foreignAfter = true;
        }
        return found;
    }

    private static bool ProbeAtOrAfter(OggPageReader pages, uint serial, long from, long limit, out long offset, out long granule)
    {
        offset = granule = -1;
        if (!pages.Seek(from)) return false;
        while (pages.ReadPage())
        {
            OggPageHeader header = pages.Header;
            if (header.Offset >= limit && header.Offset > from + 4 * OggPageReader.MaximumPageLength) return false;
            if (header.Serial != serial || header.Granule < 0) continue;
            offset = header.Offset;
            granule = header.Granule;
            return true;
        }
        return false;
    }
}
