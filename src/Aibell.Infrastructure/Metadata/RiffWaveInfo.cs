using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Metadata;

// Classic PCM only. Preserve every byte outside explicitly edited INFO entries,
// including unknown chunks, original date precision and odd-byte padding.
internal sealed record RiffWaveInfo(long Offset, int Length, byte[][] Entries, string OtherChunksHash)
{
    private const int MaximumInfoBytes = 4 * 1024 * 1024;
    // The last 14 bytes of the KSDATAFORMAT_SUBTYPE GUIDs for PCM and IEEE float; the first two bytes hold the format tag.
    private static readonly byte[] SubFormatTail = [0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71];
    private static readonly string[] AmbiguousMetadata = ["bext", "iXML", "axml", "cart", "CSET"];

    public static RiffWaveInfo Read(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        byte[] header = new byte[12];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new NotSupportedException("WAV writing requires classic little-endian RIFF/WAVE.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) + 8L != stream.Length)
            throw new InvalidDataException("The RIFF length does not match the file.");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        List<byte[]> entries = [];
        long infoOffset = -1, dataOffset = -1;
        int infoLength = 0, blockAlign = 0;
        bool foundFormat = false;
        long dataLength = 0;
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = stream.Position;
            if (stream.Length - offset < 8) throw new InvalidDataException("Truncated RIFF chunk header.");
            stream.ReadExactly(header.AsSpan(0, 8));
            string key = Encoding.ASCII.GetString(header, 0, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            long length = 8L + size + (size & 1);
            if (length > stream.Length - offset) throw new InvalidDataException("A RIFF chunk exceeds the file.");
            if (AmbiguousMetadata.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new NotSupportedException("WAV files containing additional metadata systems remain library-only.");
            // id3 bytes are otherwise byte-preservable like any unknown chunk, but the shared
            // stager counts the WAV container itself as one metadata format, so an id3 chunk
            // makes two and the stager rejects the edit whether or not an INFO list is present.
            // Reject here too, so CanWrite does not advertise an edit that would fail downstream.
            if (string.Equals(key, "id3 ", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("WAV files containing an id3 tag remain library-only.");
            bool info = false;
            if (key == "LIST")
            {
                if (size < 4) throw new InvalidDataException("A RIFF LIST has no type.");
                stream.ReadExactly(header.AsSpan(8, 4));
                info = header.AsSpan(8, 4).SequenceEqual("INFO"u8);
            }
            if (info)
            {
                if (infoOffset >= 0 || size > MaximumInfoBytes)
                    throw new NotSupportedException("WAV writing requires one bounded INFO list.");
                infoOffset = offset;
                infoLength = checked((int)length);
                byte[] contents = new byte[size - 4];
                stream.ReadExactly(contents);
                int cursor = 0;
                while (cursor < contents.Length)
                {
                    if (contents.Length - cursor < 8) throw new InvalidDataException("Truncated INFO entry.");
                    uint valueLength = BinaryPrimitives.ReadUInt32LittleEndian(contents.AsSpan(cursor + 4));
                    long entryLength = 8L + valueLength + (valueLength & 1);
                    if (entryLength > contents.Length - cursor) throw new InvalidDataException("An INFO entry exceeds its list.");
                    entries.Add(contents.AsSpan(cursor, (int)entryLength).ToArray());
                    cursor += (int)entryLength;
                }
            }
            else
            {
                if (key == "fmt ")
                {
                    if (foundFormat || size is not (16 or 18 or 40)) throw new NotSupportedException("Unsupported WAV format layout.");
                    foundFormat = true;
                    stream.Position = offset + 8;
                    stream.ReadExactly(buffer.AsSpan(0, (int)size));
                    int format = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
                    int channels = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(2));
                    uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
                    uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8));
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(12));
                    int bits = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(14));
                    // WAVE_FORMAT_EXTENSIBLE (what ffmpeg and most editors write above 16 bits) carries the real format in a GUID.
                    bool extensible = format == 0xFFFE;
                    if (extensible)
                    {
                        if (size != 40 || BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(16)) != 22 ||
                            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(18)) > bits ||
                            !buffer.AsSpan(26, 14).SequenceEqual(SubFormatTail))
                            throw new NotSupportedException("Unsupported WAV format layout.");
                        format = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(24));
                    }
                    else if (size == 40 || size == 18 && BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(16)) != 0)
                        throw new NotSupportedException("Unsupported WAV format layout.");
                    // Only the INFO list is rewritten and the audio bytes are hashed, so PCM and IEEE float are equally safe.
                    bool sampleBits = format == 1 ? bits is 8 or 16 or 24 or 32 : format == 3 && bits is 32 or 64;
                    if (!sampleBits || channels < 1 || channels > (extensible ? 8 : 2) || sampleRate == 0 || blockAlign != channels * (bits / 8) ||
                        byteRate != (ulong)sampleRate * (uint)blockAlign)
                        throw new NotSupportedException("WAV writing supports integer PCM and IEEE float (mono/stereo, or up to eight channels when extensible).");
                }
                if (key == "data")
                {
                    if (dataOffset >= 0 || !foundFormat) throw new NotSupportedException("WAV writing requires one data chunk after its format.");
                    dataOffset = offset;
                    dataLength = size;
                }
                stream.Position = offset;
                long remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, remaining);
                    stream.ReadExactly(buffer.AsSpan(0, count));
                    hash.AppendData(buffer, 0, count);
                    remaining -= count;
                }
            }
            stream.Position = offset + length;
        }
        if (!foundFormat || dataOffset < 0 || dataLength == 0 || dataLength % blockAlign != 0)
            throw new InvalidDataException("WAV audio is missing or not frame-aligned.");
        return new(infoOffset < 0 ? dataOffset : infoOffset, infoLength, entries.ToArray(), Convert.ToHexString(hash.GetHashAndReset()));
    }

    public static bool CanWrite(Stream stream, CancellationToken cancellationToken)
    {
        try { Read(stream, cancellationToken); return true; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        { return false; }
        finally { stream.Position = 0; }
    }

    public void Write(Stream stream, MetadataEdit edit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, string> changes = Changes(edit);
        List<byte[]> edited = [];
        HashSet<string> found = new(StringComparer.Ordinal);
        foreach (byte[] entry in Entries)
        {
            string key = Key(entry);
            if (!changes.TryGetValue(key, out string? value)) { edited.Add(entry); continue; }
            found.Add(key);
            if (value.Length > 0) edited.Add(Entry(key, value));
        }
        foreach ((string key, string value) in changes)
            if (!found.Contains(key) && value.Length > 0 &&
                (key is not ("ITRK" or "TRCK") || Entries.Any(entry => Key(entry) == key)))
                edited.Add(Entry(key, value));
        int payloadLength = checked(4 + edited.Sum(entry => entry.Length));
        if (payloadLength > MaximumInfoBytes) throw new NotSupportedException("The edited WAV INFO list exceeds the size limit.");
        byte[] replacement = edited.Count == 0 ? [] : new byte[8 + payloadLength];
        if (replacement.Length > 0)
        {
            "LIST"u8.CopyTo(replacement);
            BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(4), (uint)payloadLength);
            "INFO"u8.CopyTo(replacement.AsSpan(8));
            int position = 12;
            foreach (byte[] entry in edited) { entry.CopyTo(replacement, position); position += entry.Length; }
        }
        long delta = replacement.Length - (long)Length;
        long oldLength = stream.Length, newLength = checked(oldLength + delta);
        if (newLength - 8 > uint.MaxValue) throw new NotSupportedException("The edited WAV exceeds classic RIFF limits.");
        byte[] buffer = new byte[65536];
        long tailStart = Offset + Length;
        if (delta > 0)
        {
            stream.SetLength(newLength);
            long end = oldLength;
            while (end > tailStart)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, end - tailStart);
                stream.Position = end - count; stream.ReadExactly(buffer.AsSpan(0, count));
                stream.Position = end - count + delta; stream.Write(buffer.AsSpan(0, count));
                end -= count;
            }
        }
        else if (delta < 0)
        {
            long position = tailStart;
            while (position < oldLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, oldLength - position);
                stream.Position = position; stream.ReadExactly(buffer.AsSpan(0, count));
                stream.Position = position + delta; stream.Write(buffer.AsSpan(0, count));
                position += count;
            }
            stream.SetLength(newLength);
        }
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = Offset; stream.Write(replacement);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)(newLength - 8));
        stream.Position = 4; stream.Write(buffer.AsSpan(0, 4));
        stream.Flush();
        RiffWaveInfo saved = Read(stream, cancellationToken);
        if (OtherChunksHash != saved.OtherChunksHash ||
            !Entries.Where(entry => !changes.ContainsKey(Key(entry))).Select(Convert.ToHexString)
                .SequenceEqual(saved.Entries.Where(entry => !changes.ContainsKey(Key(entry))).Select(Convert.ToHexString)))
            throw new InvalidDataException("The edited WAV failed byte-preservation validation.");
    }

    internal static bool IsEditedKey(string key, MetadataEdit edit) => Changes(edit).ContainsKey(key);
    private static string Key(byte[] entry) => Encoding.ASCII.GetString(entry, 0, 4);
    private static Dictionary<string, string> Changes(MetadataEdit edit)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        Add("INAM", edit.Title); Add("IART", edit.Artist); Add("IPRD", edit.Album);
        Add("IGNR", edit.Genre); Add("ICMT", edit.Comment);
        Add("ICRD", Number(edit.Year));
        string? track = Number(edit.TrackNumber);
        Add("IPRT", track); Add("ITRK", track); Add("TRCK", track);
        return result;
        void Add(string key, string? value) { if (value is not null) result.Add(key, value); }
        static string? Number(int? value) => value is null ? null : value == 0 ? "" : value.Value.ToString(CultureInfo.InvariantCulture);
    }
    private static byte[] Entry(string key, string value)
    {
        byte[] text = Encoding.UTF8.GetBytes(value + "\0");
        byte[] entry = new byte[8 + text.Length + (text.Length & 1)];
        Encoding.ASCII.GetBytes(key, entry);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)text.Length);
        text.CopyTo(entry, 8);
        return entry;
    }
}
