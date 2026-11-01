using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Aibell.Infrastructure.Metadata;

// Strict MPEG Layer III frame inspection. Unknown layouts stay read-only.
// Audio hashing includes the complete frame bytes, including Xing/LAME data.
internal sealed record MpegAudioData(string Hash, TimeSpan Duration, int SampleRate, int Channels, int BitRate, bool HasId3v1 = false)
{
    private static readonly int[] SampleRates = [44100, 48000, 32000];
    private static readonly int[] Mpeg1BitRates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2BitRates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    public static MpegAudioData Read(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek || !stream.CanRead) throw new ArgumentException("MP3 inspection requires a readable seekable stream.", nameof(stream));
        stream.Position = 0;
        Span<byte> tag = stackalloc byte[10]; stream.ReadExactly(tag);
        if (!tag[..3].SequenceEqual("ID3"u8) || tag[3] is not (3 or 4) || tag[4] != 0 || tag[5] != 0 ||
            (tag[6] | tag[7] | tag[8] | tag[9]) >= 128)
            throw new NotSupportedException("MP3 writing requires a single ordinary ID3v2.3/2.4 header without extra flags.");
        long offset = 10L + (tag[6] << 21) + (tag[7] << 14) + (tag[8] << 7) + tag[9];
        if (offset >= stream.Length) throw new InvalidDataException("The ID3 tag exceeds the audio file.");
        // A 128-byte ID3v1 footer ("TAG") after the last frame is allowed and kept out of the audio fingerprint. Extended "TAG+"
        // blocks, APE and Lyrics3 footers are not, so they still stop the frame walk as trailing data.
        long end = stream.Length;
        bool id3v1 = false;
        if (end - 128 > offset)
        {
            stream.Position = end - 128;
            Span<byte> footer = stackalloc byte[3]; stream.ReadExactly(footer);
            id3v1 = footer.SequenceEqual("TAG"u8);
            if (id3v1) end -= 128;
        }
        stream.Position = offset;
        using IncrementalHash audioHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[4096];
        int frames = 0, sampleRate = 0, samplesPerFrame = 0, channels = 0, version = -1;
        uint? declaredFrames = null;
        while (stream.Position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(buffer.AsSpan(0, 4));
            uint header = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            int currentVersion = (int)((header >> 19) & 3), layer = (int)((header >> 17) & 3);
            int rateIndex = (int)((header >> 10) & 3), bitIndex = (int)((header >> 12) & 15);
            if ((header >> 21) != 0x7ff || currentVersion == 1 || layer != 1 || rateIndex == 3 || bitIndex is 0 or 15)
                throw new NotSupportedException("The MP3 contains an unsupported frame or trailing data.");
            int rate = SampleRates[rateIndex] / (currentVersion == 3 ? 1 : currentVersion == 2 ? 2 : 4);
            int currentChannels = ((header >> 6) & 3) == 3 ? 1 : 2;
            if (frames == 0) { sampleRate = rate; channels = currentChannels; version = currentVersion; samplesPerFrame = version == 3 ? 1152 : 576; }
            else if (sampleRate != rate || channels != currentChannels || version != currentVersion)
                throw new NotSupportedException("MP3 stream parameters change between frames.");
            int bitRate = (currentVersion == 3 ? Mpeg1BitRates : Mpeg2BitRates)[bitIndex];
            int length = (currentVersion == 3 ? 144 : 72) * bitRate * 1000 / rate + (int)((header >> 9) & 1);
            if (length < 4 || length > buffer.Length) throw new InvalidDataException("Invalid MP3 frame length.");
            stream.ReadExactly(buffer.AsSpan(4, length - 4));
            if (stream.Position > end) throw new InvalidDataException("The last MP3 frame is truncated.");
            audioHash.AppendData(buffer, 0, length);
            if (frames == 0)
            {
                int xing = 4 + (((header >> 16) & 1) == 0 ? 2 : 0) + (version == 3 ? channels == 1 ? 17 : 32 : channels == 1 ? 9 : 17);
                if (xing + 12 <= length && (buffer.AsSpan(xing, 4).SequenceEqual("Xing"u8) || buffer.AsSpan(xing, 4).SequenceEqual("Info"u8)))
                {
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(xing + 4));
                    if ((flags & 1) != 0) declaredFrames = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(xing + 8));
                }
            }
            frames++;
        }
        if (frames < 2 || declaredFrames is { } count && (count == 0 || count != frames && count != frames - 1))
            throw new InvalidDataException("The MP3 frame count is inconsistent.");
        double seconds = (declaredFrames ?? (uint)frames) * (double)samplesPerFrame / sampleRate;
        return new(Convert.ToHexString(audioHash.GetHashAndReset()), TimeSpan.FromSeconds(seconds), sampleRate, channels,
            (int)Math.Round((end - offset) * 8 / seconds / 1000), id3v1);
    }

    public static MpegAudioData? TryRead(Stream stream, CancellationToken cancellationToken)
    {
        try { return Read(stream, cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        { return null; }
        finally { stream.Position = 0; }
    }
}
