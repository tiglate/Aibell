namespace Aibell.Playback.PipeWire;

/// <summary>The SPA POD type ids used on the wire.</summary>
internal enum SpaPodType : uint
{
    None = 1,
    Bool = 2,
    Id = 3,
    Int = 4,
    Long = 5,
    Float = 6,
    Double = 7,
    String = 8,
    Bytes = 9,
    Rectangle = 10,
    Fraction = 11,
    Bitmap = 12,
    Array = 13,
    Struct = 14,
    Object = 15,
    Sequence = 16,
    Pointer = 17,
    Fd = 18,
    Choice = 19,
    Pod = 20,
}

/// <summary>The object types a POD can carry (the <c>type</c> of an Object POD's body).</summary>
internal static class SpaObjectType
{
    public const uint PropInfo = 0x40001;
    public const uint Props = 0x40002;
    public const uint Format = 0x40003;
    public const uint ParamBuffers = 0x40004;
    public const uint ParamMeta = 0x40005;
    public const uint ParamIO = 0x40006;
    public const uint ParamProfile = 0x40007;
    public const uint ParamRoute = 0x40008;
    public const uint Profiler = 0x40009;
    public const uint ParamLatency = 0x4000a;
    public const uint ParamProcessLatency = 0x4000b;
    public const uint ParamTag = 0x4000c;
}

/// <summary>Flags that can accompany a property inside an Object POD.</summary>
[Flags]
internal enum SpaPropFlags : uint
{
    None = 0,
    ReadOnly = 1u << 0,
    Hardware = 1u << 1,
    HintDict = 1u << 2,
    Mandatory = 1u << 3,
    DontFixate = 1u << 4,
}
