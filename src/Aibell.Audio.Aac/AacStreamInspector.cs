namespace Aibell.Audio.Aac;

public enum AacContainer
{
    Unknown,
    /// <summary>Raw ADTS frames (Icecast/Shoutcast AAC, HLS packed audio).</summary>
    Adts,
    MpegTs,
    Mp4
}

/// <summary>What <see cref="AacStreamInspector.Inspect"/> found out about a stretch of stream.</summary>
public sealed record AacStreamInfo(
    AacContainer Container, string Codec, int ObjectType, int SampleRate, int Channels, bool? Sbr, string? Problem)
{
    /// <summary>True when the stream is plain AAC-LC (or Main) that <see cref="AacDecoder"/> can decode.</summary>
    public bool IsDecodable => Container is AacContainer.Adts or AacContainer.MpegTs && Sbr == false && Problem is null && Codec == "AAC";

    public override string ToString()
    {
        if (Container == AacContainer.Unknown) return "unknown";
        string profile = Codec == "AAC" ? ObjectType switch { 1 => "Main", 2 => "LC", 3 => "SSR", 4 => "LTP", _ => "?" } : "";
        string kind = Codec == "AAC" ? "AAC-" + profile + (Sbr == true ? " + SBR (HE-AAC)" : "") : Codec;
        string layout = SampleRate > 0 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $" {SampleRate / 1000.0:0.###} kHz {Channels} ch") : "";
        return $"{Container} {kind}{layout}" + (Problem is null ? "" : $" ({Problem})");
    }
}

/// <summary>Recognizes AAC containers from the first bytes of a stream and reports the codec details, without playing anything.</summary>
public static class AacStreamInspector
{
    private const int FramesToDecode = 4;

    /// <summary>
    /// Looks at the first bytes of a stream. ADTS needs three consecutive consistent frame headers (a stream may begin mid-frame), so MP3,
    /// whose frames resemble ADTS only in the sync word, is not mistaken for it.
    /// </summary>
    public static AacContainer Sniff(ReadOnlySpan<byte> head)
    {
        int skip = Id3.TagLength(head);
        if (skip > 0)
        {
            if (skip >= head.Length) return AacContainer.Unknown;
            head = head[skip..];
        }
        if (head.Length >= 4 && head[0] == 0x47 && (head.Length <= 188 || head[188] == 0x47) && (head.Length <= 376 || head[376] == 0x47)) return AacContainer.MpegTs;
        if (head.Length >= 12 && (head[4..8].SequenceEqual("ftyp"u8) || head[4..8].SequenceEqual("styp"u8) || head[4..8].SequenceEqual("moof"u8))) return AacContainer.Mp4;
        return FindAdtsChain(head) ? AacContainer.Adts : AacContainer.Unknown;
    }

    private static void DecodeBlocks(AacDecoder decoder, ReadOnlySpan<byte> frame, in AdtsHeader header)
    {
        int offset = header.HeaderLength;
        for (int block = 0; block < header.RawBlocks; block++) decoder.DecodeBlock(frame, header, block, ref offset, out _);
    }

    private static bool FindAdtsChain(ReadOnlySpan<byte> data)
    {
        int limit = Math.Min(data.Length, 4 * AdtsHeader.MaximumFrameLength);
        for (int start = 0; start + AdtsHeader.MinimumHeaderLength <= limit; start++)
        {
            if (data[start] != 0xff) continue;
            int at = start;
            int chain = 0;
            int samplingIndex = -1;
            while (at + AdtsHeader.MinimumHeaderLength <= data.Length && AdtsHeader.TryParse(data[at..], out AdtsHeader header)
                && (samplingIndex < 0 || header.SamplingIndex == samplingIndex))
            {
                samplingIndex = header.SamplingIndex;
                chain++;
                at += header.FrameLength;
                if (chain == 3) return true;
            }
            // A chain that ran into the end of the data (a short head) is accepted when it has at least two links.
            if (chain >= 2 && at + AdtsHeader.MinimumHeaderLength > data.Length) return true;
        }
        return false;
    }

    /// <summary>
    /// Sniffs <paramref name="data"/> (the start of a stream or one HLS segment) and decodes its first frames to find the profile and
    /// whether SBR (HE-AAC) is in use. SBR hides inside ordinary-looking LC frames, so it is looked for in the frames' fill elements.
    /// </summary>
    public static AacStreamInfo Inspect(ReadOnlySpan<byte> data)
    {
        AacContainer container = Sniff(data);
        switch (container)
        {
            case AacContainer.Mp4:
                return new AacStreamInfo(container, "AAC", 0, 0, 0, null, "fragmented MP4 is not supported");
            case AacContainer.Unknown:
                return new AacStreamInfo(container, "", 0, 0, 0, null, null);
        }

        AdtsFrameReader reader = new();
        AacDecoder decoder = new() { RejectSbr = false };
        int decoded = 0;
        AdtsHeader first = default;
        string? problem = null;
        string codec = "AAC";
        void Feed(ReadOnlySpan<byte> elementary)
        {
            while (!elementary.IsEmpty && decoded < FramesToDecode)
            {
                Span<byte> room = reader.GetWriteBuffer();
                int copy = Math.Min(room.Length, elementary.Length);
                elementary[..copy].CopyTo(room);
                reader.Advance(copy);
                elementary = elementary[copy..];
                while (decoded < FramesToDecode && reader.TryRead(out AdtsHeader header, out ReadOnlySpan<byte> payload))
                {
                    if (decoded == 0) first = header;
                    decoded++;
                    try
                    {
                        DecodeBlocks(decoder, reader.LastFrame, header);
                    }
                    catch (AacException exception)
                    {
                        problem ??= exception.Message;
                    }
                }
            }
        }

        if (container == AacContainer.MpegTs)
        {
            TransportStreamDemuxer demuxer = new(Feed, reader.MarkDiscontinuity);
            demuxer.Push(data);
            demuxer.Complete();
            switch (demuxer.Kind)
            {
                case TransportAudioKind.AacAdts:
                    break;
                case TransportAudioKind.MpegAudio:
                    return new AacStreamInfo(container, "MP3", 0, 0, 0, null, null);
                case TransportAudioKind.Ac3:
                    return new AacStreamInfo(container, "AC-3", 0, 0, 0, null, null);
                case TransportAudioKind.AacLatm:
                    return new AacStreamInfo(container, "AAC", 0, 0, 0, null, "AAC in LATM is not supported");
                default:
                    return new AacStreamInfo(container, "", 0, 0, 0, null, "no readable audio stream");
            }
        }
        else
        {
            Feed(data);
        }
        reader.Complete();
        Feed([]);
        while (decoded < FramesToDecode && reader.TryRead(out AdtsHeader header, out ReadOnlySpan<byte> payload))
        {
            if (decoded == 0) first = header;
            decoded++;
            try
            {
                DecodeBlocks(decoder, reader.LastFrame, header);
            }
            catch (AacException exception)
            {
                problem ??= exception.Message;
            }
        }
        if (decoded == 0) return new AacStreamInfo(container, codec, 0, 0, 0, null, "no AAC frames found");
        bool sbr = decoder.SbrSignaled;
        return new AacStreamInfo(container, codec, first.ObjectType, first.SampleRate, first.ChannelConfiguration, sbr, sbr ? null : problem);
    }
}
