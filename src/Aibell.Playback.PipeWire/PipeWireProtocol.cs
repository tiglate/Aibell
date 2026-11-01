namespace Aibell.Playback.PipeWire;

/// <summary>The parameters a node understands, from the Simple Plugin API (SPA).</summary>
internal static class SpaParams
{
    public const uint Invalid = 0;
    public const uint PropInfo = 1;
    public const uint Props = 2;
    public const uint EnumFormat = 3;
    public const uint Format = 4;
    public const uint Buffers = 5;
    public const uint Meta = 6;
    public const uint Io = 7;
    public const uint EnumProfile = 8;
    public const uint Profile = 9;
    public const uint EnumPortConfig = 10;
    public const uint PortConfig = 11;
    public const uint EnumRoute = 12;
    public const uint Route = 13;
    public const uint Control = 14;
    public const uint Latency = 15;
    public const uint ProcessLatency = 16;
}

/// <summary>The properties of a format object.</summary>
internal static class SpaFormatKeys
{
    public const uint MediaType = 1;
    public const uint MediaSubtype = 2;

    // The audio keys start at 0x10000.
    public const uint Format = 0x10001;
    public const uint Flags = 0x10002;
    public const uint Rate = 0x10003;
    public const uint Channels = 0x10004;
    public const uint Position = 0x10005;
}

/// <summary>The media types a format can carry.</summary>
internal static class SpaMediaType
{
    public const uint Unknown = 0;
    public const uint Audio = 1;
    public const uint Video = 2;
}

/// <summary>The media subtypes a format can carry.</summary>
internal static class SpaMediaSubtype
{
    public const uint Unknown = 0;
    public const uint Raw = 1;
    public const uint Dsp = 2;
}

/// <summary>The raw audio sample formats.</summary>
internal static class SpaAudioFormat
{
    /// <summary>The interleaved formats start at 0x100.</summary>
    public const uint S16Le = 0x100 + 3;

    /// <summary>32 bit floating point interleaved.</summary>
    public const uint F32Le = 0x100 + 27;
}

/// <summary>The channel positions of a stream.</summary>
internal static class SpaAudioPosition
{
    public const uint FrontLeft = 3;
    public const uint FrontRight = 4;
}
