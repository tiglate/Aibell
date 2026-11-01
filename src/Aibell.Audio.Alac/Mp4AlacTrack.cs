// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 Tiglate Pileser III.
// Part of Aibell.Audio.Alac, which is licensed under the Apache License, Version 2.0 (see LICENSE.Alac and NOTICE).

using System.Buffers.Binary;

namespace Aibell.Audio.Alac;

/// <summary>
/// The sample table of the first ALAC track of an MP4 / M4A file (ISO/IEC 14496-12): where each packet is, how big and how many frames it
/// covers, plus the magic cookie. Written from the specification; QuickTime-style 'wave' wrappers and sound description versions 0 to 2 are understood.
/// Fragmented files (no samples in the movie box) are not supported.
/// </summary>
internal sealed class Mp4AlacTrack
{
    private const int MaxMovieBytes = 256 << 20;

    private Mp4AlacTrack(byte[] cookie, long[] offsets, int[] sizes, long[] startFrames)
    {
        Cookie = cookie;
        Offsets = offsets;
        Sizes = sizes;
        StartFrames = startFrames;
    }

    public byte[] Cookie { get; }

    /// <summary>Absolute file offset of each packet.</summary>
    public long[] Offsets { get; }

    public int[] Sizes { get; }

    /// <summary>First frame of each packet, followed by the total frame count.</summary>
    public long[] StartFrames { get; }

    public int PacketCount => Sizes.Length;

    public long TotalFrames => StartFrames[^1];

    private readonly record struct Box(uint Type, int Start, int End);

    private static uint Type(string name) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(name));

    /// <summary>Finds the movie box, reads it and builds the table. Throws <see cref="NotSupportedException"/> when the file holds no ALAC audio.</summary>
    public static Mp4AlacTrack Read(Stream stream)
    {
        byte[] movie = ReadMovieBox(stream);
        long fileLength = stream.Length;
        foreach (Box trak in Children(movie, 0, movie.Length))
        {
            if (trak.Type != Type("trak")) continue;
            Mp4AlacTrack? track = TryReadTrack(movie, trak, fileLength);
            if (track is not null) return track;
        }
        throw new NotSupportedException("The MP4 file holds no ALAC track.");
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
            if (size < headerSize || start + size > end) throw new InvalidDataException("An MP4 box is damaged or truncated.");
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
            if (size < headerSize || position + size > end) break;
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

    private static Mp4AlacTrack? TryReadTrack(byte[] movie, Box trak, long fileLength)
    {
        List<Box> trakChildren = Children(movie, trak.Start, trak.End);
        if (Find(trakChildren, "mdia") is not { } mdia) return null;
        List<Box> mdiaChildren = Children(movie, mdia.Start, mdia.End);
        if (Find(mdiaChildren, "minf") is not { } minf) return null;
        List<Box> minfChildren = Children(movie, minf.Start, minf.End);
        if (Find(minfChildren, "stbl") is not { } stbl) return null;
        List<Box> table = Children(movie, stbl.Start, stbl.End);
        if (Find(table, "stsd") is not { } stsd) return null;

        byte[]? cookie = ReadSampleDescription(movie, stsd);
        if (cookie is null) return null;
        AlacConfig config = AlacConfig.Parse(cookie);

        if (Find(table, "stsz") is not { } stszBox || Find(table, "stsc") is not { } stscBox || Find(table, "stts") is not { } sttsBox) throw new InvalidDataException("The ALAC track has no sample table.");
        Box? chunkBox = Find(table, "stco") ?? Find(table, "co64");
        if (chunkBox is not { } chunks) throw new InvalidDataException("The ALAC track has no chunk offsets.");
        bool wide = Find(table, "stco") is null;

        int[] sizes = ReadSizes(movie, stszBox, fileLength);
        long[] offsets = ReadOffsets(movie, stscBox, chunks, wide, sizes, fileLength);
        int count = offsets.Length;
        if (count == 0) throw new NotSupportedException("The ALAC track holds no samples (fragmented files are not supported).");
        if (count < sizes.Length) sizes = sizes[..count];
        long[] startFrames = ReadStartFrames(movie, sttsBox, count, config.FrameLength);
        return new Mp4AlacTrack(cookie, offsets, sizes, startFrames);
    }

    // Returns the 'alac' atom (header included, which the cookie parser expects) of the first sample entry, or null when that entry is not ALAC.
    private static byte[]? ReadSampleDescription(byte[] movie, Box stsd)
    {
        if (stsd.End - stsd.Start < 16) throw new InvalidDataException("The sample description is damaged.");
        List<Box> entries = Children(movie, stsd.Start + 8, stsd.End);
        if (entries.Count == 0 || entries[0].Type != Type("alac")) return null;
        Box entry = entries[0];
        if (entry.End - entry.Start < 28) throw new InvalidDataException("The ALAC sample entry is damaged.");
        int version = BinaryPrimitives.ReadUInt16BigEndian(movie.AsSpan(entry.Start + 8));
        int childStart = entry.Start + 28 + version switch { 1 => 16, 2 => 36, _ => 0 };
        return FindCookie(movie, childStart, entry.End) ?? throw new InvalidDataException("The ALAC sample entry has no magic cookie.");
    }

    private static byte[]? FindCookie(byte[] movie, int start, int end)
    {
        foreach (Box child in Children(movie, start, end))
        {
            if (child.Type == Type("alac"))
            {
                int headerStart = child.Start - 8;
                return movie.AsSpan(headerStart, child.End - headerStart).ToArray();
            }
            if (child.Type == Type("wave") && FindCookie(movie, child.Start, child.End) is { } inner) return inner;
        }
        return null;
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
            if (fixedSize > int.MaxValue) throw new InvalidDataException("The sample size table is damaged.");
            Array.Fill(sizes, (int)fixedSize);
            return sizes;
        }
        if (data.Length - 12 < (long)count * 4) throw new InvalidDataException("The sample size table is truncated.");
        for (int i = 0; i < sizes.Length; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data[(12 + i * 4)..]);
            if (size > 1 << 24) throw new InvalidDataException("An ALAC packet is implausibly large.");
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

    private static long[] ReadStartFrames(byte[] movie, Box stts, int count, int defaultDuration)
    {
        ReadOnlySpan<byte> data = movie.AsSpan(stts.Start, stts.End - stts.Start);
        if (data.Length < 8) throw new InvalidDataException("The time-to-sample table is damaged.");
        uint runs = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (data.Length - 8 < (long)runs * 8) throw new InvalidDataException("The time-to-sample table is truncated.");

        long[] starts = new long[count + 1];
        int sample = 0;
        long frame = 0;
        for (int run = 0; run < runs && sample < count; run++)
        {
            uint repeat = BinaryPrimitives.ReadUInt32BigEndian(data[(8 + run * 8)..]);
            uint delta = BinaryPrimitives.ReadUInt32BigEndian(data[(12 + run * 8)..]);
            for (uint i = 0; i < repeat && sample < count; i++)
            {
                starts[sample++] = frame;
                frame += delta;
            }
        }
        for (; sample < count; sample++)
        {
            starts[sample] = frame;
            frame += defaultDuration;
        }
        starts[count] = frame;
        return starts;
    }
}
