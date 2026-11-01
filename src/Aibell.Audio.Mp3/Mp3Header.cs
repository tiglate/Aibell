namespace Aibell.Audio.Mp3;

/// <summary>The fixed header of an MPEG audio frame (4 bytes; Layer I, II and III, MPEG-1, MPEG-2 and the unofficial MPEG-2.5).</summary>
internal readonly struct Mp3Header
{
    private static readonly short[] Layer1Mpeg1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly short[] Layer2Mpeg1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly short[] Layer3Mpeg1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly short[] Layer1Lsf = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly short[] Layer23Lsf = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] RatesMpeg1 = [44100, 48000, 32000];

    /// <summary>The bits of a header that stay the same from frame to frame: syncword, version, layer, bit rate index and sampling frequency.</summary>
    private const uint StableBits = 0xFFFEFC00;

    public bool IsMpeg1 { get; init; }

    /// <summary>1 for MPEG-1, 2 for MPEG-2, 25 for MPEG-2.5.</summary>
    public int Version { get; init; }

    /// <summary>1, 2 or 3.</summary>
    public int Layer { get; init; }

    public int SampleRate { get; init; }

    /// <summary>Bit rate in bit/s; for a free-format stream the one worked out from its frame length.</summary>
    public int BitRate { get; init; }

    /// <summary>The frame length in bytes without padding, set only for free-format streams (bit rate index 0).</summary>
    public int FreeFormatBytes { get; init; }

    public bool IsFreeFormat { get; init; }

    public bool HasCrc { get; init; }

    public bool Padding { get; init; }

    /// <summary>0 stereo, 1 joint stereo, 2 dual channel, 3 single channel.</summary>
    public int Mode { get; init; }

    public int ModeExtension { get; init; }

    public uint Raw { get; init; }

    public int Channels => Mode == 3 ? 1 : 2;

    public int SamplesPerFrame => Layer == 1 ? 384 : Layer == 2 || IsMpeg1 ? 1152 : 576;

    public int Granules => IsMpeg1 ? 2 : 1;

    public int SideInfoLength => IsMpeg1 ? (Mode == 3 ? 17 : 32) : (Mode == 3 ? 9 : 17);

    /// <summary>The size of one slot: 4 bytes in Layer I, 1 byte otherwise.</summary>
    public int SlotBytes => Layer == 1 ? 4 : 1;

    /// <summary>The length of the whole frame in bytes, the header included; 0 for a free-format stream whose frame length is not known yet.</summary>
    public int FrameLength
    {
        get
        {
            int pad = Padding ? SlotBytes : 0;
            if (IsFreeFormat) return FreeFormatBytes == 0 ? 0 : FreeFormatBytes + pad;
            if (Layer == 1) return 12 * BitRate / SampleRate * 4 + pad;
            return (Layer == 3 && !IsMpeg1 ? 72 : 144) * BitRate / SampleRate + pad;
        }
    }

    /// <summary>The bytes from the start of the frame to the first byte of main data (header, CRC, side information) of a Layer III frame.</summary>
    public int HeaderAndSideLength => 4 + (HasCrc ? 2 : 0) + SideInfoLength;

    /// <summary>
    /// Parses a header; false when it is not a valid header. A free-format header (bit rate index 0) is valid only when <paramref name="freeFormatBytes"/>, the
    /// frame length without padding of the stream, is known.
    /// </summary>
    public static bool TryParse(uint header, out Mp3Header result, int freeFormatBytes = 0)
    {
        result = default;
        if ((header & 0xFFE00000) != 0xFFE00000) return false;
        int versionBits = (int)(header >> 19) & 3;
        if (versionBits == 1) return false;
        int layer = 4 - ((int)(header >> 17) & 3);
        if (layer == 4) return false;
        int bitrateIndex = (int)(header >> 12) & 15;
        int rateIndex = (int)(header >> 10) & 3;
        if (bitrateIndex == 15 || rateIndex == 3) return false;
        bool free = bitrateIndex == 0;
        if (free && freeFormatBytes <= 0) return false;
        bool mpeg1 = versionBits == 3;
        int rate = RatesMpeg1[rateIndex];
        if (versionBits == 2) rate /= 2;
        else if (versionBits == 0) rate /= 4;
        short[] bitrates = mpeg1 ? (layer == 1 ? Layer1Mpeg1 : layer == 2 ? Layer2Mpeg1 : Layer3Mpeg1) : layer == 1 ? Layer1Lsf : Layer23Lsf;
        Mp3Header parsed = new()
        {
            IsMpeg1 = mpeg1,
            Version = versionBits == 3 ? 1 : versionBits == 2 ? 2 : 25,
            Layer = layer,
            SampleRate = rate,
            IsFreeFormat = free,
            FreeFormatBytes = free ? freeFormatBytes : 0,
            HasCrc = ((header >> 16) & 1) == 0,
            Padding = ((header >> 9) & 1) != 0,
            Mode = (int)(header >> 6) & 3,
            ModeExtension = (int)(header >> 4) & 3,
            Raw = header
        };
        int samples = parsed.SamplesPerFrame;
        result = new Mp3Header
        {
            IsMpeg1 = parsed.IsMpeg1,
            Version = parsed.Version,
            Layer = layer,
            SampleRate = rate,
            IsFreeFormat = free,
            FreeFormatBytes = parsed.FreeFormatBytes,
            BitRate = free ? (int)((long)freeFormatBytes * 8 * rate / samples) : bitrates[bitrateIndex] * 1000,
            HasCrc = parsed.HasCrc,
            Padding = parsed.Padding,
            Mode = parsed.Mode,
            ModeExtension = parsed.ModeExtension,
            Raw = header
        };
        return true;
    }

    /// <summary>True when <paramref name="other"/> can follow this frame in one stream (same version, layer and sample rate).</summary>
    public bool IsCompatibleWith(in Mp3Header other) => Version == other.Version && SampleRate == other.SampleRate && Layer == other.Layer;

    /// <summary>
    /// Works out the frame length of a free-format stream: <paramref name="window"/> starts with a free-format header, and the length is the distance to the next header
    /// that has the same stable bits, confirmed by another one after it (or by the end of the data). Returns 0 when no length can be found.
    /// </summary>
    public static int MeasureFreeFormat(ReadOnlySpan<byte> window, bool dataEnds)
    {
        if (window.Length < 4) return 0;
        uint first = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(window);
        if ((first & 0xFFE00000) != 0xFFE00000 || ((first >> 12) & 15) != 0 || ((first >> 10) & 3) == 3 || ((first >> 19) & 3) == 1) return 0;
        int layer = 4 - ((int)(first >> 17) & 3);
        if (layer == 4) return 0;
        int slot = layer == 1 ? 4 : 1;
        int padding0 = ((first >> 9) & 1) != 0 ? slot : 0;
        for (int distance = 8; distance + 4 <= window.Length; distance++)
        {
            uint candidate = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(window[distance..]);
            if ((candidate & StableBits) != (first & StableBits)) continue;
            int bytes = distance - padding0;
            if (bytes < 8 || bytes % slot != 0) continue;
            int next = distance + bytes + (((candidate >> 9) & 1) != 0 ? slot : 0);
            if (next + 4 <= window.Length)
            {
                if ((System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(window[next..]) & StableBits) != (first & StableBits)) continue;
            }
            else if (!dataEnds || next < window.Length) continue;
            return bytes;
        }
        return 0;
    }
}
