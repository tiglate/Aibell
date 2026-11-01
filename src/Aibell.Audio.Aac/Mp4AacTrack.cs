using System.Buffers.Binary;

namespace Aibell.Audio.Aac;

/// <summary>
/// The sample table and decoder configuration of the first AAC track of an MP4 / M4A file (ISO/IEC 14496-12 and 14496-14): where each raw data
/// block is and how big, how much of the decoded timeline the edit list keeps, and the AudioSpecificConfig from the <c>esds</c> box.
/// QuickTime-style <c>wave</c> wrappers and sound description versions 0 to 2 are understood. Fragmented files (no samples in the movie box) are not supported.
/// </summary>
internal sealed class Mp4AacTrack
{
    private const int MaxMovieBytes = 256 << 20;
    private const int MaxPacketBytes = 1 << 18;

    private Mp4AacTrack(AudioSpecificConfig config, long[] offsets, int[] sizes, long startFrame, long frameCount)
    {
        Config = config;
        Offsets = offsets;
        Sizes = sizes;
        StartFrame = startFrame;
        FrameCount = frameCount;
    }

    public AudioSpecificConfig Config { get; }

    /// <summary>Absolute file offset of each raw data block.</summary>
    public long[] Offsets { get; }

    public int[] Sizes { get; }

    /// <summary>Frames at the start of the decoded timeline that are not part of the audio (encoder delay), from the edit list.</summary>
    public long StartFrame { get; }

    /// <summary>Frames of audio after <see cref="StartFrame"/>.</summary>
    public long FrameCount { get; }

    public int PacketCount => Sizes.Length;

    private readonly record struct Box(uint Type, int Start, int End);

    private static uint Type(string name) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(name));

    /// <summary>Finds the movie box, reads it and builds the table. Throws <see cref="NotSupportedException"/> when the file holds no playable AAC audio.</summary>
    public static Mp4AacTrack Read(Stream stream)
    {
        byte[] movie = ReadMovieBox(stream);
        long fileLength = stream.Length;
        uint movieTimescale = 0;
        List<Box> top = Children(movie, 0, movie.Length);
        if (Find(top, "mvhd") is { } mvhd) movieTimescale = ReadTimescale(movie, mvhd);
        foreach (Box trak in top)
        {
            if (trak.Type != Type("trak")) continue;
            Mp4AacTrack? track = TryReadTrack(movie, trak, movieTimescale, fileLength);
            if (track is not null) return track;
        }
        throw new NotSupportedException("The MP4 file holds no AAC track.");
    }

    private static byte[] ReadMovieBox(Stream stream)
    {
        long end = stream.Length;
        Span<byte> header = stackalloc byte[16];
        while (stream.Position + 8 <= end)
        {
            long start = stream.Position;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            int headerSize = 8;
            if (size == 1)
            {
                if (start + 16 > end) break;
                stream.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - start;
            }
            if (size < headerSize || size > end - start) throw new InvalidDataException("An MP4 box is damaged or truncated.");
            if (type == Type("moov"))
            {
                long payload = size - headerSize;
                if (payload > MaxMovieBytes) throw new InvalidDataException("The MP4 movie box is too large.");
                byte[] movie = new byte[payload];
                stream.ReadExactly(movie);
                return movie;
            }
            stream.Seek(start + size, SeekOrigin.Begin);
        }
        throw new NotSupportedException("The MP4 file has no movie box.");
    }

    private static List<Box> Children(byte[] data, int start, int end)
    {
        List<Box> boxes = [];
        int position = start;
        while (position + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 4));
            int headerSize = 8;
            if (size == 1)
            {
                if (position + 16 > end) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }
            if (size < headerSize || size > end - position) break;
            boxes.Add(new Box(type, position + headerSize, (int)(position + size)));
            position += (int)size;
        }
        return boxes;
    }

    private static Box? Find(List<Box> boxes, string type)
    {
        uint wanted = Type(type);
        foreach (Box box in boxes)
        {
            if (box.Type == wanted) return box;
        }
        return null;
    }

    // mvhd and mdhd share the layout of their first fields: version and flags, then times (4 or 8 bytes each) and the timescale.
    private static uint ReadTimescale(byte[] movie, Box header)
    {
        ReadOnlySpan<byte> data = movie.AsSpan(header.Start, header.End - header.Start);
        if (data.Length < 4) throw new InvalidDataException("A MP4 header box is damaged.");
        int at = data[0] == 1 ? 20 : 12;
        if (data.Length < at + 4) throw new InvalidDataException("A MP4 header box is damaged.");
        return BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
    }

    private static long ToFrames(long ticks, uint timescale, int rate) => (long)Math.Round(ticks * (double)rate / timescale);

    private static Mp4AacTrack? TryReadTrack(byte[] movie, Box trak, uint movieTimescale, long fileLength)
    {
        List<Box> trakChildren = Children(movie, trak.Start, trak.End);
        if (Find(trakChildren, "mdia") is not { } mdia) return null;
        List<Box> mdiaChildren = Children(movie, mdia.Start, mdia.End);
        if (Find(mdiaChildren, "hdlr") is not { } hdlr || hdlr.End - hdlr.Start < 12 || BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(hdlr.Start + 8)) != Type("soun")) return null;
        if (Find(mdiaChildren, "minf") is not { } minf) return null;
        List<Box> minfChildren = Children(movie, minf.Start, minf.End);
        if (Find(minfChildren, "stbl") is not { } stbl) return null;
        List<Box> table = Children(movie, stbl.Start, stbl.End);
        if (Find(table, "stsd") is not { } stsd) return null;

        AudioSpecificConfig? config = ReadSampleDescription(movie, stsd);
        if (config is null) return null;
        if (Find(mdiaChildren, "mdhd") is not { } mdhd) throw new InvalidDataException("The AAC track has no media header.");
        uint mediaTimescale = ReadTimescale(movie, mdhd);
        if (mediaTimescale == 0) throw new InvalidDataException("The AAC track has no time scale.");

        if (Find(table, "stsz") is not { } stszBox || Find(table, "stsc") is not { } stscBox || Find(table, "stts") is not { } sttsBox) throw new InvalidDataException("The AAC track has no sample table.");
        Box? chunkBox = Find(table, "stco") ?? Find(table, "co64");
        if (chunkBox is not { } chunks) throw new InvalidDataException("The AAC track has no chunk offsets.");
        bool wide = Find(table, "stco") is null;

        int[] sizes = ReadSizes(movie, stszBox, fileLength);
        long[] offsets = ReadOffsets(movie, stscBox, chunks, wide, sizes, fileLength);
        int count = offsets.Length;
        if (count == 0) throw new NotSupportedException("The AAC track holds no samples (fragmented files are not supported).");
        if (count < sizes.Length) sizes = sizes[..count];

        int rate = config.SampleRate;
        long timeline = (long)count * AacDecoder.FrameLength;
        long mediaFrames = Math.Min(ToFrames(ReadTotalTicks(movie, sttsBox), mediaTimescale, rate), timeline);
        (long start, long length) = ReadEditList(movie, trakChildren, movieTimescale, mediaTimescale, rate, mediaFrames);
        return new Mp4AacTrack(config, offsets, sizes, start, length);
    }

    // The edit list says which part of the decoded timeline is the audio: media_time (in media time scale units) skips the encoder delay and
    // segment_duration (in movie time scale units) ends it. Only a single non-empty edit is understood; any other list plays the whole media.
    private static (long Start, long Length) ReadEditList(byte[] movie, List<Box> trakChildren, uint movieTimescale, uint mediaTimescale, int rate, long mediaFrames)
    {
        if (Find(trakChildren, "edts") is { } edts && Find(Children(movie, edts.Start, edts.End), "elst") is { } elst && movieTimescale != 0)
        {
            ReadOnlySpan<byte> data = movie.AsSpan(elst.Start, elst.End - elst.Start);
            if (data.Length >= 8)
            {
                int version = data[0];
                uint entries = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                int entrySize = version == 1 ? 20 : 12;
                if (entries == 1 && data.Length - 8 >= entrySize)
                {
                    ReadOnlySpan<byte> entry = data[8..];
                    long duration = version == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(entry) : BinaryPrimitives.ReadUInt32BigEndian(entry);
                    long mediaTime = version == 1 ? BinaryPrimitives.ReadInt64BigEndian(entry[8..]) : BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
                    if (mediaTime >= 0 && duration >= 0)
                    {
                        long start = Math.Min(ToFrames(mediaTime, mediaTimescale, rate), mediaFrames);
                        long available = mediaFrames - start;
                        // A duration of 0 means "to the end" in the files that write one; a longer one than the media is cut short.
                        long length = duration == 0 ? available : Math.Min(ToFrames(duration, movieTimescale, rate), available);
                        return (start, length);
                    }
                }
            }
        }
        return (0, mediaFrames);
    }

    private static long ReadTotalTicks(byte[] movie, Box stts)
    {
        ReadOnlySpan<byte> data = movie.AsSpan(stts.Start, stts.End - stts.Start);
        if (data.Length < 8) throw new InvalidDataException("The time-to-sample table is damaged.");
        uint runs = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (data.Length - 8 < (long)runs * 8) throw new InvalidDataException("The time-to-sample table is truncated.");
        long ticks = 0;
        for (int run = 0; run < runs; run++)
        {
            ticks += (long)BinaryPrimitives.ReadUInt32BigEndian(data[(8 + run * 8)..]) * BinaryPrimitives.ReadUInt32BigEndian(data[(12 + run * 8)..]);
        }
        return ticks;
    }

    // The configuration of the first sample entry, or null when that entry is not AAC ('mp4a'). Encrypted audio ('enca') and MP3 in MP4 are refused.
    private static AudioSpecificConfig? ReadSampleDescription(byte[] movie, Box stsd)
    {
        if (stsd.End - stsd.Start < 16) throw new InvalidDataException("The sample description is damaged.");
        List<Box> entries = Children(movie, stsd.Start + 8, stsd.End);
        if (entries.Count == 0) return null;
        Box entry = entries[0];
        if (entry.Type == Type("enca")) throw new NotSupportedException("The MP4 audio is encrypted.");
        if (entry.Type != Type("mp4a")) return null;
        if (entry.End - entry.Start < 28) throw new InvalidDataException("The AAC sample entry is damaged.");
        int version = BinaryPrimitives.ReadUInt16BigEndian(movie.AsSpan(entry.Start + 8));
        int childStart = entry.Start + 28 + version switch { 1 => 16, 2 => 36, _ => 0 };
        Box esds = FindEsds(movie, childStart, entry.End) ?? throw new InvalidDataException("The AAC sample entry has no 'esds' box.");
        return ParseEsds(movie.AsSpan(esds.Start, esds.End - esds.Start));
    }

    private static Box? FindEsds(byte[] movie, int start, int end)
    {
        foreach (Box child in Children(movie, start, end))
        {
            if (child.Type == Type("esds")) return child;
            if (child.Type == Type("wave") && FindEsds(movie, child.Start, child.End) is { } inner) return inner;
        }
        return null;
    }

    // Descriptor length: 1 to 4 bytes of 7 bits, the top bit says another follows (ISO/IEC 14496-1, subclause 8.3.3).
    private static bool ReadDescriptor(ReadOnlySpan<byte> data, ref int position, out byte tag, out int length)
    {
        tag = 0;
        length = 0;
        if (position >= data.Length) return false;
        tag = data[position++];
        for (int i = 0; i < 4; i++)
        {
            if (position >= data.Length) return false;
            byte next = data[position++];
            length = length << 7 | next & 0x7F;
            if ((next & 0x80) == 0) break;
        }
        return length <= data.Length - position;
    }

    private static AudioSpecificConfig ParseEsds(ReadOnlySpan<byte> box)
    {
        if (box.Length < 4) throw new InvalidDataException("The 'esds' box is damaged.");
        ReadOnlySpan<byte> data = box[4..];
        int position = 0;
        if (!ReadDescriptor(data, ref position, out byte tag, out _) || tag != 0x03 || data.Length - position < 3) throw new InvalidDataException("The 'esds' box is damaged.");
        int flags = data[position + 2];
        position += 3;
        if ((flags & 0x80) != 0) position += 2;
        if ((flags & 0x40) != 0 && position < data.Length) position += 1 + data[position];
        if ((flags & 0x20) != 0) position += 2;
        if (position > data.Length || !ReadDescriptor(data, ref position, out tag, out _) || tag != 0x04 || data.Length - position < 13) throw new InvalidDataException("The 'esds' box is damaged.");
        byte objectType = data[position];
        // 0x40: MPEG-4 audio; 0x66 to 0x68: MPEG-2 AAC Main, LC and SSR. Anything else (0x69 and 0x6B are MP3) is not for this decoder.
        if (objectType is not (0x40 or 0x66 or 0x67 or 0x68)) throw new NotSupportedException("The MP4 audio is not AAC.");
        position += 13;
        if (!ReadDescriptor(data, ref position, out tag, out int length) || tag != 0x05) throw new InvalidDataException("The 'esds' box has no decoder configuration.");
        return AudioSpecificConfig.Parse(data.Slice(position, length));
    }

    private static int[] ReadSizes(byte[] movie, Box box, long fileLength)
    {
        ReadOnlySpan<byte> data = movie.AsSpan(box.Start, box.End - box.Start);
        if (data.Length < 12) throw new InvalidDataException("The sample size table is damaged.");
        uint fixedSize = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        if (count > fileLength) throw new InvalidDataException("The sample size table is damaged.");
        int[] sizes = new int[count];
        if (fixedSize != 0)
        {
            if (fixedSize > MaxPacketBytes) throw new InvalidDataException("An AAC packet is implausibly large.");
            Array.Fill(sizes, (int)fixedSize);
            return sizes;
        }
        if (data.Length - 12 < (long)count * 4) throw new InvalidDataException("The sample size table is truncated.");
        for (int i = 0; i < sizes.Length; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data[(12 + i * 4)..]);
            if (size > MaxPacketBytes) throw new InvalidDataException("An AAC packet is implausibly large.");
            sizes[i] = (int)size;
        }
        return sizes;
    }

    private static long[] ReadOffsets(byte[] movie, Box stsc, Box chunkBox, bool wide, int[] sizes, long fileLength)
    {
        ReadOnlySpan<byte> map = movie.AsSpan(stsc.Start, stsc.End - stsc.Start);
        if (map.Length < 8) throw new InvalidDataException("The sample-to-chunk table is damaged.");
        uint runs = BinaryPrimitives.ReadUInt32BigEndian(map[4..]);
        if (map.Length - 8 < (long)runs * 12) throw new InvalidDataException("The sample-to-chunk table is truncated.");

        ReadOnlySpan<byte> chunks = movie.AsSpan(chunkBox.Start, chunkBox.End - chunkBox.Start);
        if (chunks.Length < 8) throw new InvalidDataException("The chunk offset table is damaged.");
        uint chunkCount = BinaryPrimitives.ReadUInt32BigEndian(chunks[4..]);
        int entrySize = wide ? 8 : 4;
        if (chunks.Length - 8 < (long)chunkCount * entrySize) throw new InvalidDataException("The chunk offset table is truncated.");

        List<long> offsets = new(sizes.Length);
        int sample = 0;
        for (int run = 0; run < runs && sample < sizes.Length; run++)
        {
            uint firstChunk = BinaryPrimitives.ReadUInt32BigEndian(map[(8 + run * 12)..]);
            uint perChunk = BinaryPrimitives.ReadUInt32BigEndian(map[(12 + run * 12)..]);
            uint lastChunk = run + 1 < runs ? BinaryPrimitives.ReadUInt32BigEndian(map[(8 + (run + 1) * 12)..]) - 1 : chunkCount;
            if (firstChunk < 1) throw new InvalidDataException("The sample-to-chunk table is damaged.");
            for (uint chunk = firstChunk; chunk <= Math.Min(lastChunk, chunkCount) && sample < sizes.Length; chunk++)
            {
                ReadOnlySpan<byte> entry = chunks[(8 + (int)(chunk - 1) * entrySize)..];
                long position = wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(entry) : BinaryPrimitives.ReadUInt32BigEndian(entry);
                for (uint i = 0; i < perChunk && sample < sizes.Length; i++)
                {
                    // A packet that reaches past the end of the file (a cut-off download) ends the track there.
                    if (position < 0 || position + sizes[sample] > fileLength) return [.. offsets];
                    offsets.Add(position);
                    position += sizes[sample];
                    sample++;
                }
            }
        }
        return [.. offsets];
    }
}
