using System.Text;

namespace Aibell.Archive.Rar;

/// <summary>One file or directory of a RAR archive.</summary>
public sealed class RarEntry
{
    internal RarEntry()
    {
    }

    /// <summary>Path inside the archive, with <c>/</c> separators.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>Unpacked size in bytes.</summary>
    public long Size { get; internal set; }

    public long PackedSize { get; internal set; }

    public bool IsDirectory { get; internal set; }

    public DateTime? Modified { get; internal set; }

    /// <summary>The CRC-32 of the unpacked data, when the archive stores one.</summary>
    public uint? Crc32 { get; internal set; }

    internal int Index { get; set; }

    internal int DataOffset { get; set; }

    internal bool IsStored { get; set; }

    /// <summary>True for a file that continues the dictionary of the files before it (solid archives).</summary>
    internal bool ContinuesSolid { get; set; }

    internal bool IsRar5 { get; set; }

    internal int UnpackVersion { get; set; }

    internal bool IsEncrypted { get; set; }

    internal bool IsSplit { get; set; }

    internal long DictionarySize { get; set; }
}

/// <summary>
/// Reads RAR archives (the 1.5-4.x format and RAR 5.0) in memory: stored files and files compressed with the RAR 3.x/4.x and 5.0 algorithms,
/// including solid archives. An instance is not thread-safe (it keeps the decoder state of the solid chain). Encrypted entries, multi-volume sets and the old RAR 1.x/2.x compression are refused with
/// <see cref="NotSupportedException"/>; damaged data raises <see cref="InvalidDataException"/>.
/// </summary>
public sealed class RarArchive
{
    public const long MaxArchiveBytes = 1L << 30;

    /// <summary>The largest file unpacked (into memory).</summary>
    public const long MaxEntryBytes = 1L << 29;

    private static readonly byte[] Signature4 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] Signature5 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private const int MaxSfxScan = 1 << 20;
    private const int MaxWindow = 1 << 28;

    private readonly byte[] data;
    private readonly List<RarEntry> entries = [];
    private Rar3Decoder? decoder3;
    private Rar5Decoder? decoder5;
    private int decoderNext = -1;
    private int windowSize;

    private RarArchive(byte[] data) => this.data = data;

    public IReadOnlyList<RarEntry> Entries => entries;

    /// <summary>True when the stream starts a RAR archive (after an optional self-extractor stub).</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) => head.StartsWith(Signature4) || head.StartsWith(Signature5);

    public static RarArchive Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > MaxArchiveBytes) throw new InvalidDataException("The RAR archive is too large.");
            buffer.Write(chunk, 0, read);
        }
        return Parse(buffer.ToArray());
    }

    public static RarArchive Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var archive = new RarArchive(bytes);
        archive.ReadHeaders();
        return archive;
    }

    // --- Headers --------------------------------------------------------------------------------------------------------------

    private void ReadHeaders()
    {
        int start = FindSignature(out bool rar5);
        if (rar5) ReadRar5(start + Signature5.Length);
        else ReadRar4(start + Signature4.Length);

        long total = 0;
        foreach (RarEntry entry in entries)
        {
            if (!entry.IsStored && !entry.IsDirectory) total += entry.Size;
        }
        long dictionary = entries.Where(e => !e.IsStored && !e.IsDirectory).Select(e => e.DictionarySize).DefaultIfEmpty(1 << 16).Max();
        long wanted = Math.Min(total, dictionary);
        int size = 1 << 16;
        while (size < wanted && size < MaxWindow) size <<= 1;
        windowSize = size;
    }

    private int FindSignature(out bool rar5)
    {
        int limit = Math.Min(data.Length, MaxSfxScan + Signature5.Length);
        for (int i = 0; i + Signature4.Length <= limit; i++)
        {
            if (data[i] != 0x52 || !data.AsSpan(i).StartsWith(Signature4.AsSpan(0, 6))) continue;
            if (i + 8 <= data.Length && data.AsSpan(i).StartsWith(Signature5))
            {
                rar5 = true;
                return i;
            }
            if (data[i + 6] == 0)
            {
                rar5 = false;
                return i;
            }
        }
        throw new InvalidDataException("This is not a RAR archive.");
    }

    private ushort U16(int offset) => (ushort)(data[offset] | data[offset + 1] << 8);

    private uint U32(int offset) => BitConverter.ToUInt32(data, offset);

    private void ReadRar4(int position)
    {
        bool sawMain = false;
        while (position + 7 <= data.Length)
        {
            int crc = U16(position);
            int type = data[position + 2];
            int flags = U16(position + 3);
            int headerSize = U16(position + 5);
            if (headerSize < 7 || position + headerSize > data.Length) throw new InvalidDataException("A RAR header is damaged or truncated.");
            long addSize = 0;
            if ((flags & 0x8000) != 0)
            {
                if (headerSize < 11) throw new InvalidDataException("A RAR header is damaged.");
                addSize = U32(position + 7);
            }
            if ((RarCrc32.Compute(data.AsSpan(position + 2, headerSize - 2)) & 0xFFFF) != crc)
                throw new InvalidDataException("A RAR header checksum is wrong.");

            switch (type)
            {
                case 0x73:
                    if ((flags & 0x80) != 0) throw new NotSupportedException("RAR archives with encrypted headers are not supported.");
                    sawMain = true;
                    break;
                case 0x74:
                    if (!sawMain) throw new InvalidDataException("A RAR file header precedes the archive header.");
                    ReadFileHeader4(position, flags, headerSize);
                    break;
                case 0x7B:
                    return;
            }
            if (type == 0x74) position += headerSize + (int)Math.Min(entries[^1].PackedSize, int.MaxValue / 2);
            else position += headerSize + (int)Math.Min(addSize, int.MaxValue / 2);
        }
    }

    private void ReadFileHeader4(int position, int flags, int headerSize)
    {
        int p = position + 7;
        if (headerSize < 32) throw new InvalidDataException("A RAR file header is too short.");
        long packSize = U32(p);
        long unpSize = U32(p + 4);
        int hostOs = data[p + 8];
        uint fileCrc = U32(p + 9);
        uint dosTime = U32(p + 13);
        int version = data[p + 17];
        int method = data[p + 18];
        int nameSize = U16(p + 19);
        p += 25;
        if ((flags & 0x100) != 0)
        {
            if (p + 8 > position + headerSize) throw new InvalidDataException("A RAR file header is damaged.");
            packSize |= (long)U32(p) << 32;
            unpSize |= (long)U32(p + 4) << 32;
            p += 8;
        }
        if (p + nameSize > position + headerSize) throw new InvalidDataException("A RAR file name is damaged.");
        string name = DecodeName(data.AsSpan(p, nameSize), (flags & 0x200) != 0);
        _ = hostOs;

        var entry = new RarEntry
        {
            Index = entries.Count,
            Name = name.Replace('\\', '/'),
            Size = unpSize,
            PackedSize = packSize,
            IsDirectory = (flags & 0xE0) == 0xE0,
            Modified = DosTime(dosTime),
            Crc32 = fileCrc,
            DataOffset = position + headerSize,
            IsStored = method == 0x30,
            ContinuesSolid = (flags & 0x10) != 0,
            UnpackVersion = version,
            IsEncrypted = (flags & 0x04) != 0,
            IsSplit = (flags & 0x03) != 0,
            DictionarySize = (flags & 0xE0) == 0xE0 ? 0 : 0x10000L << (flags >> 5 & 7)
        };
        if (packSize < 0 || unpSize < 0 || entry.DataOffset + packSize > data.Length) throw new InvalidDataException("A RAR file is truncated.");
        entries.Add(entry);
    }

    private static string DecodeName(ReadOnlySpan<byte> raw, bool unicode)
    {
        int zero = raw.IndexOf((byte)0);
        if (!unicode) return Encoding.Latin1.GetString(zero >= 0 ? raw[..zero] : raw);
        if (zero < 0) return Encoding.UTF8.GetString(raw);

        // "OEM name", a zero byte, then the Unicode form coded against the OEM bytes.
        ReadOnlySpan<byte> oem = raw[..zero];
        int offset = zero + 1;
        int end = raw.Length;
        int high = offset >= end ? 0 : raw[offset++];
        int flagBits = 0, flagByte = 0;
        var result = new StringBuilder();
        while (offset < end)
        {
            if (flagBits == 0)
            {
                flagByte = raw[offset++];
                flagBits = 8;
            }
            flagBits -= 2;
            switch (flagByte >> flagBits & 3)
            {
                case 0:
                    if (offset >= end) continue;
                    result.Append((char)raw[offset++]);
                    break;
                case 1:
                    if (offset >= end) continue;
                    result.Append((char)(high << 8 | raw[offset++]));
                    break;
                case 2:
                    if (offset + 1 >= end)
                    {
                        offset = end;
                        continue;
                    }
                    result.Append((char)(raw[offset + 1] << 8 | raw[offset]));
                    offset += 2;
                    break;
                default:
                    {
                        if (offset >= end) continue;
                        int length = raw[offset++];
                        int extra = 0, highByte = 0;
                        if ((length & 0x80) != 0)
                        {
                            if (offset >= end) continue;
                            extra = raw[offset++];
                            highByte = high;
                        }
                        length = (length & 0x7F) + 2;
                        while (length-- > 0)
                        {
                            int index = result.Length;
                            int oemByte = index < oem.Length ? oem[index] : 0;
                            result.Append((char)(highByte << 8 | oemByte + extra & 0xFF));
                        }
                        break;
                    }
            }
        }
        return result.ToString();
    }

    private static DateTime? DosTime(uint value)
    {
        try
        {
            int seconds = 2 * (int)(value & 0x1F), minutes = (int)(value >> 5 & 0x3F), hours = (int)(value >> 11 & 0x1F);
            int day = (int)(value >> 16 & 0x1F), month = (int)(value >> 21 & 0x0F), year = (int)(value >> 25 & 0x7F) + 1980;
            return new DateTime(year, month, day, hours, minutes, seconds, DateTimeKind.Local);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private int ReadVint(ref int position, int end)
    {
        long value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (position >= end) throw new InvalidDataException("A RAR header is truncated.");
            byte b = data[position++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return (int)Math.Min(value, int.MaxValue);
        }
        throw new InvalidDataException("A RAR number is too long.");
    }

    private long ReadVint64(ref int position, int end)
    {
        long value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (position >= end) throw new InvalidDataException("A RAR header is truncated.");
            byte b = data[position++];
            if (shift < 63) value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("A RAR number is too long.");
    }

    private void ReadRar5(int position)
    {
        while (position + 7 <= data.Length)
        {
            uint crc = U32(position);
            int p = position + 4;
            int sizeStart = p;
            int size = ReadVint(ref p, data.Length);
            int bodyStart = p;
            long headerEnd = (long)bodyStart + size;
            if (size < 2 || headerEnd > data.Length) throw new InvalidDataException("A RAR header is damaged or truncated.");
            if (RarCrc32.Compute(data.AsSpan(sizeStart, (int)(headerEnd - sizeStart))) != crc) throw new InvalidDataException("A RAR header checksum is wrong.");
            int end = (int)headerEnd;
            int type = ReadVint(ref p, end);
            int flags = ReadVint(ref p, end);
            int extraSize = (flags & 1) != 0 ? ReadVint(ref p, end) : 0;
            long dataSize = (flags & 2) != 0 ? ReadVint64(ref p, end) : 0;

            switch (type)
            {
                case 4:
                    throw new NotSupportedException("RAR archives with encrypted headers are not supported.");
                case 5:
                    return;
                case 2:
                    ReadFileHeader5(p, end - extraSize, flags, dataSize, end);
                    break;
            }
            long next = headerEnd + dataSize;
            if (next > data.Length) throw new InvalidDataException("A RAR file is truncated.");
            position = (int)next;
        }
    }

    private void ReadFileHeader5(int p, int end, int headerFlags, long dataSize, int dataStart)
    {
        int fileFlags = ReadVint(ref p, end);
        long unpackedSize = ReadVint64(ref p, end);
        ReadVint64(ref p, end);
        DateTime? modified = null;
        if ((fileFlags & 0x02) != 0)
        {
            if (p + 4 > end) throw new InvalidDataException("A RAR header is truncated.");
            modified = DateTimeOffset.FromUnixTimeSeconds(U32(p)).UtcDateTime;
            p += 4;
        }
        uint? crc = null;
        if ((fileFlags & 0x04) != 0)
        {
            if (p + 4 > end) throw new InvalidDataException("A RAR header is truncated.");
            crc = U32(p);
            p += 4;
        }
        int info = ReadVint(ref p, end);
        ReadVint(ref p, end);
        int nameLength = ReadVint(ref p, end);
        if (nameLength == 0 || p + nameLength > end) throw new InvalidDataException("A RAR file name is damaged.");
        string name = Encoding.UTF8.GetString(data, p, nameLength);

        int version = info & 0x3F;
        int method = info >> 7 & 7;
        int dictionaryShift = info >> 10 & 15;
        bool directory = (fileFlags & 1) != 0;
        var entry = new RarEntry
        {
            Index = entries.Count,
            Name = name.Replace('\\', '/'),
            Size = unpackedSize,
            PackedSize = dataSize,
            IsDirectory = directory,
            Modified = modified,
            Crc32 = crc,
            DataOffset = dataStart,
            IsStored = method == 0,
            ContinuesSolid = (info & 0x40) != 0,
            IsRar5 = true,
            UnpackVersion = version,
            IsSplit = (headerFlags & 0x18) != 0,
            DictionarySize = directory ? 0 : 0x20000L << dictionaryShift
        };
        if ((fileFlags & 0x08) != 0) throw new NotSupportedException("RAR files of unknown size are not supported.");
        if (dataSize < 0 || (long)dataStart + dataSize > data.Length) throw new InvalidDataException("A RAR file is truncated.");
        entries.Add(entry);
    }

    // --- Extraction -----------------------------------------------------------------------------------------------------------

    /// <summary>Unpacks one entry. Files of a solid archive are decoded from the first file of their chain unless the previous file was just decoded.</summary>
    public byte[] Extract(RarEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Index >= entries.Count || !ReferenceEquals(entries[entry.Index], entry)) throw new ArgumentException("The entry belongs to another archive.", nameof(entry));
        if (entry.IsDirectory) return [];
        if (entry.IsEncrypted) throw new NotSupportedException("Encrypted RAR entries are not supported.");
        if (entry.IsSplit) throw new NotSupportedException("RAR files split over several volumes are not supported.");
        if (entry.Size > MaxEntryBytes) throw new NotSupportedException("The RAR file is too large to unpack in memory.");

        byte[] result;
        if (entry.IsStored)
        {
            if (entry.PackedSize != entry.Size) throw new InvalidDataException("A stored RAR file has two different sizes.");
            result = data.AsSpan(entry.DataOffset, (int)entry.Size).ToArray();
        }
        else result = Unpack(entry);

        if (entry.Crc32 is { } expected && RarCrc32.Compute(result) != expected) throw new InvalidDataException($"The data of '{entry.Name}' fails its checksum.");
        return result;
    }

    private byte[] Unpack(RarEntry entry)
    {
        if (entry.IsRar5)
        {
            if (entry.UnpackVersion > 1) throw new NotSupportedException("This RAR 5 compression version is not supported.");
        }
        else if (entry.UnpackVersion < 29 || entry.UnpackVersion > 39) throw new NotSupportedException("RAR archives made by RAR 2.x or older are not supported.");

        // A solid file needs the dictionary left by the compressed files before it; start the chain over unless it was just decoded.
        int previous = PreviousCompressed(entry.Index);
        if (!entry.ContinuesSolid || decoderNext != entry.Index || previous < 0)
        {
            if (entry.ContinuesSolid)
            {
                int first = FirstOfChain(entry.Index);
                decoder3 = null;
                decoder5 = null;
                for (int i = first; i < entry.Index; i++)
                {
                    RarEntry earlier = entries[i];
                    if (earlier.IsStored || earlier.IsDirectory) continue;
                    DecodeOne(earlier);
                }
            }
            else
            {
                decoder3 = null;
                decoder5 = null;
            }
        }
        return DecodeOne(entry);
    }

    private int PreviousCompressed(int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (!entries[i].IsStored && !entries[i].IsDirectory) return i;
        }
        return -1;
    }

    private int FirstOfChain(int index)
    {
        int first = index;
        for (int i = index; i >= 0; i--)
        {
            RarEntry e = entries[i];
            if (e.IsStored || e.IsDirectory) continue;
            first = i;
            if (!e.ContinuesSolid) break;
        }
        return first;
    }

    private byte[] DecodeOne(RarEntry entry)
    {
        bool solid = entry.ContinuesSolid && (entry.IsRar5 ? decoder5 is not null : decoder3 is not null);
        byte[] result;
        if (entry.IsRar5)
        {
            decoder5 ??= new Rar5Decoder(windowSize);
            result = decoder5.Unpack(data, entry.DataOffset, (int)entry.PackedSize, entry.Size, solid);
        }
        else
        {
            decoder3 ??= new Rar3Decoder(Math.Min(windowSize, 1 << 22));
            result = decoder3.Unpack(data, entry.DataOffset, (int)entry.PackedSize, entry.Size, solid);
        }
        decoderNext = NextCompressed(entry.Index);
        return result;
    }

    private int NextCompressed(int index)
    {
        for (int i = index + 1; i < entries.Count; i++)
        {
            if (!entries[i].IsStored && !entries[i].IsDirectory) return i;
        }
        return -1;
    }
}
