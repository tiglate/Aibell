using System.Buffers.Binary;
using System.Text;

namespace Aibell.Playback.PipeWire;

/// <summary>The kind of a choice POD.</summary>
internal enum SpaChoiceType : uint
{
    None = 0,
    Range = 1,
    Step = 2,
    Enum = 3,
    Flags = 4,
}

/// <summary>Well-known property keys used by the PipeWire audio parameters.</summary>
internal static class SpaParamKey
{
    // The format parameters (spa/param/audio/raw.h and spa/param/format.h).
    public const uint MediaType = 1;
    public const uint MediaSubtype = 2;
    public const uint MediaCategory = 3;
    public const uint MediaRole = 4;
    public const uint Format = 5;
    public const uint FrameSize = 6;
    public const uint Rate = 7;
    public const uint Channels = 8;
    public const uint Position = 9;
    public const uint DataType = 10;
    public const uint DataTypeSize = 11;
    public const uint Stride = 12;
    public const uint Buffers = 13;
    public const uint Blocks = 14;
    public const uint Size = 15;
    public const uint Align = 16;
    public const uint Meta = 17;
    public const uint Latency = 18;

    // The object properties (spa/param/props.h).
    public const uint MediaClass = 0x10001;
    public const uint NodeName = 0x10004;
    public const uint NodeDescription = 0x10005;
    public const uint NodeNick = 0x10006;
    public const uint ApplicationName = 0x10007;
}

/// <summary>One property inside an Object POD.</summary>
internal sealed record SpaProperty(uint Key, SpaPropFlags Flags, SpaPod Value);

/// <summary>
/// A SPA POD: the binary data container PipeWire uses for every parameter and event payload.
/// The layout is <c>{ uint32 size; uint32 type; body }</c>. The <c>size</c> field counts the body bytes
/// only, never the eight byte header; a POD then occupies <c>align8(8 + size)</c> bytes on the wire,
/// the extra bytes being the alignment padding before the next POD.
/// </summary>
internal sealed class SpaPod
{
    private const int HeaderSize = 8;

    private SpaPod(SpaPodType type, uint objectType, uint objectId, SpaChoiceType choiceType, byte[] body,
        IReadOnlyList<SpaPod> children, IReadOnlyList<SpaProperty> properties)
    {
        Type = type;
        ObjectType = objectType;
        ObjectId = objectId;
        ChoiceType = choiceType;
        Body = body;
        Children = children;
        Properties = properties;
    }

    public SpaPodType Type { get; }

    /// <summary>For an Object POD, the object type; otherwise zero.</summary>
    public uint ObjectType { get; }

    /// <summary>For an Object POD, the parameter or object id; otherwise zero.</summary>
    public uint ObjectId { get; }

    /// <summary>For a Choice POD, the choice kind; otherwise <see cref="SpaChoiceType.None"/>.</summary>
    public SpaChoiceType ChoiceType { get; }

    /// <summary>The body bytes, excluding the header and the alignment padding.</summary>
    public byte[] Body { get; }

    /// <summary>The children of a Struct POD, or the values of an Array or Choice POD.</summary>
    public IReadOnlyList<SpaPod> Children { get; }

    /// <summary>The properties of an Object POD.</summary>
    public IReadOnlyList<SpaProperty> Properties { get; }

    public static SpaPod Bool(bool value) => new(SpaPodType.Bool, 0, 0, SpaChoiceType.None, Int32Body(value ? 1 : 0), [], []);

    public static SpaPod Id(uint value) => new(SpaPodType.Id, 0, 0, SpaChoiceType.None, UInt32Body(value), [], []);

    public static SpaPod Int(int value) => new(SpaPodType.Int, 0, 0, SpaChoiceType.None, Int32Body(value), [], []);

    public static SpaPod Long(long value) => new(SpaPodType.Long, 0, 0, SpaChoiceType.None, Int64Body(value), [], []);

    public static SpaPod Float(float value) => new(SpaPodType.Float, 0, 0, SpaChoiceType.None, SingleBody(value), [], []);

    public static SpaPod Double(double value) => new(SpaPodType.Double, 0, 0, SpaChoiceType.None, DoubleBody(value), [], []);

    public static SpaPod Fd(int value) => new(SpaPodType.Fd, 0, 0, SpaChoiceType.None, Int64Body(value), [], []);

    public static SpaPod String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] body = new byte[bytes.Length + 1]; // the NUL is part of the body
        Array.Copy(bytes, body, bytes.Length);
        return new SpaPod(SpaPodType.String, 0, 0, SpaChoiceType.None, body, [], []);
    }

    /// <summary>A Struct: its children follow one another on the wire.</summary>
    public static SpaPod Struct(params SpaPod[] children) =>
        new(SpaPodType.Struct, 0, 0, SpaChoiceType.None, [], children, []);

    /// <summary>
    /// A dictionary: a Struct of the number of entries followed by the key and value strings.
    /// </summary>
    public static SpaPod Dictionary(IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<KeyValuePair<string, string>> items = [.. entries];
        List<SpaPod> children = [Int(items.Count)];
        foreach (KeyValuePair<string, string> entry in items)
        {
            children.Add(String(entry.Key));
            children.Add(String(entry.Value));
        }
        return new SpaPod(SpaPodType.Struct, 0, 0, SpaChoiceType.None, [], children, []);
    }

    /// <summary>An Object: a type and an id followed by its properties.</summary>
    public static SpaPod Object(uint objectType, uint id, params SpaProperty[] properties) =>
        new(SpaPodType.Object, objectType, id, SpaChoiceType.None, [], [], properties);

    public static SpaPod Choice(SpaChoiceType choiceType, params SpaPod[] values) =>
        new(SpaPodType.Choice, 0, 0, choiceType, [], values, []);

    /// <summary>An Array: an element template followed by the values.</summary>
    public static SpaPod ArrayPod(SpaPod element, params SpaPod[] values) =>
        new(SpaPodType.Array, 0, 0, SpaChoiceType.None, [], [element, .. values], []);

    public bool AsBool()
    {
        Require(SpaPodType.Bool);
        return ReadInt32(Body) != 0;
    }

    public uint AsId()
    {
        Require(SpaPodType.Id);
        return ReadUInt32(Body);
    }

    public int AsInt()
    {
        Require(SpaPodType.Int);
        return ReadInt32(Body);
    }

    public long AsLong()
    {
        Require(SpaPodType.Long);
        RequireBody(8);
        return BinaryPrimitives.ReadInt64LittleEndian(Body.AsSpan(0, 8));
    }

    public float AsFloat()
    {
        Require(SpaPodType.Float);
        RequireBody(4);
        return BinaryPrimitives.ReadSingleLittleEndian(Body.AsSpan(0, 4));
    }

    public double AsDouble()
    {
        Require(SpaPodType.Double);
        RequireBody(8);
        return BinaryPrimitives.ReadDoubleLittleEndian(Body.AsSpan(0, 8));
    }

    public string AsString()
    {
        Require(SpaPodType.String);
        int end = Array.IndexOf(Body, (byte)0);
        return end < 0 ? Encoding.UTF8.GetString(Body) : Encoding.UTF8.GetString(Body, 0, end);
    }

    public int AsFd()
    {
        Require(SpaPodType.Fd);
        RequireBody(8);
        return (int)BinaryPrimitives.ReadInt64LittleEndian(Body.AsSpan(0, 8));
    }

    /// <summary>Serializes the POD into the bytes that go on the wire.</summary>
    public byte[] Write()
    {
        byte[] body = BuildBody();
        byte[] bytes = new byte[AllocatedSize(body.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)Type);
        Array.Copy(body, 0, bytes, HeaderSize, body.Length);
        return bytes;
    }

    /// <summary>Parses the POD at the start of <paramref name="bytes"/>.</summary>
    public static SpaPod Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
            throw new InvalidDataException("A POD needs at least an eight byte header.");

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (size > int.MaxValue)
            throw new InvalidDataException("A POD cannot be larger than two gigabytes.");
        if (bytes.Length < HeaderSize + size)
            throw new InvalidDataException("The POD body is truncated.");

        CheckPadding(bytes, (int)size);

        SpaPodType podType = (SpaPodType)type;
        byte[] body = bytes.Slice(HeaderSize, (int)size).ToArray();
        return podType switch
        {
            SpaPodType.Struct => ParseStruct(body),
            SpaPodType.Array => ParseArray(body),
            SpaPodType.Choice => ParseChoice(body),
            SpaPodType.Object => ParseObject(body),
            _ => new SpaPod(podType, 0, 0, SpaChoiceType.None, body, [], []),
        };
    }

    /// <summary>The number of bytes a POD with the given body size occupies on the wire.</summary>
    public static int AllocatedSize(int bodySize) => Align8(HeaderSize + bodySize);

    private static int Align8(int value) => (value + 7) & ~7;

    /// <summary>The bytes a child POD of the given body size takes, header and padding included.</summary>
    private static void CheckPadding(ReadOnlySpan<byte> bytes, int bodySize)
    {
        int end = AllocatedSize(bodySize);
        if (end > bytes.Length) return; // the caller only handed over the body, not the padding
        for (int i = HeaderSize + bodySize; i < end; i++)
        {
            if (bytes[i] != 0)
                throw new InvalidDataException("The padding after a POD is not zero.");
        }
    }

    private static byte[] Int32Body(int value)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, value);
        return body;
    }

    private static byte[] UInt32Body(uint value)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, value);
        return body;
    }

    private static byte[] Int64Body(long value)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(body, value);
        return body;
    }

    private static byte[] SingleBody(float value)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(body, value);
        return body;
    }

    private static byte[] DoubleBody(double value)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(body, value);
        return body;
    }

    private static int ReadInt32(byte[] body)
    {
        if (body.Length < 4)
            throw new InvalidDataException("The POD body is too short for an integer.");
        return BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0, 4));
    }

    private static uint ReadUInt32(byte[] body)
    {
        if (body.Length < 4)
            throw new InvalidDataException("The POD body is too short for an integer.");
        return BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
    }

    private byte[] BuildBody()
    {
        switch (Type)
        {
            case SpaPodType.Struct:
                {
                    List<byte> body = [];
                    foreach (SpaPod child in Children)
                    {
                        body.AddRange(child.Write());
                    }
                    return [.. body];
                }
            case SpaPodType.Array:
                {
                    // The body is the element template followed by the raw element values.
                    List<byte> body = [];
                    SpaPod template = Children[0];
                    byte[] header = new byte[HeaderSize];
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)template.Body.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)template.Type);
                    body.AddRange(header);
                    for (int i = 1; i < Children.Count; i++)
                    {
                        body.AddRange(Children[i].Body);
                    }
                    return [.. body];
                }
            case SpaPodType.Choice:
                {
                    // The body is the choice kind, the flags, the element template and the raw values.
                    List<byte> body = [];
                    byte[] header = new byte[16];
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)ChoiceType);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4),
                        Children.Count > 0 ? (uint)Children[0].Body.Length : 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4),
                        Children.Count > 0 ? (uint)Children[0].Type : 0);
                    body.AddRange(header);
                    foreach (SpaPod child in Children)
                    {
                        body.AddRange(child.Body);
                    }
                    return [.. body];
                }
            case SpaPodType.Object:
                {
                    // The body is the object type and id, then the properties.
                    List<byte> body = [];
                    byte[] header = new byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), ObjectType);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), ObjectId);
                    body.AddRange(header);
                    foreach (SpaProperty property in Properties)
                    {
                        byte[] prop = new byte[8];
                        BinaryPrimitives.WriteUInt32LittleEndian(prop.AsSpan(0, 4), property.Key);
                        BinaryPrimitives.WriteUInt32LittleEndian(prop.AsSpan(4, 4), (uint)property.Flags);
                        body.AddRange(prop);
                        body.AddRange(property.Value.Write());
                    }
                    return [.. body];
                }
            default:
                return Body;
        }
    }

    private static SpaPod ParseStruct(byte[] body)
    {
        List<SpaPod> children = [];
        int offset = 0;
        while (offset < body.Length)
        {
            children.Add(ReadChild(body, ref offset));
        }
        return new SpaPod(SpaPodType.Struct, 0, 0, SpaChoiceType.None, body, children, []);
    }

    private static SpaPod ParseArray(byte[] body)
    {
        if (body.Length < HeaderSize)
            throw new InvalidDataException("An array POD is missing its element template.");
        uint elementSize = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
        uint elementType = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4, 4));
        List<SpaPod> values = ReadRawValues(body, HeaderSize, (int)elementSize, (SpaPodType)elementType);
        SpaPod template = new((SpaPodType)elementType, 0, 0, SpaChoiceType.None, new byte[elementSize], [], []);
        List<SpaPod> children = [template, .. values];
        return new SpaPod(SpaPodType.Array, 0, 0, SpaChoiceType.None, body, children, []);
    }

    private static SpaPod ParseChoice(byte[] body)
    {
        if (body.Length < 16)
            throw new InvalidDataException("A choice POD is missing its header.");
        SpaChoiceType choiceType = (SpaChoiceType)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
        uint elementSize = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8, 4));
        uint elementType = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12, 4));
        List<SpaPod> values = ReadRawValues(body, 16, (int)elementSize, (SpaPodType)elementType);
        return new SpaPod(SpaPodType.Choice, 0, 0, choiceType, body, values, []);
    }

    private static SpaPod ParseObject(byte[] body)
    {
        if (body.Length < 8)
            throw new InvalidDataException("An object POD is missing its header.");
        uint objectType = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
        uint objectId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4, 4));
        int offset = 8;
        List<SpaProperty> properties = [];
        while (offset < body.Length)
        {
            if (offset + 8 > body.Length)
                throw new InvalidDataException("An object property is truncated.");
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));
            SpaPropFlags flags = (SpaPropFlags)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 4, 4));
            offset += 8;
            SpaPod value = ReadChild(body, ref offset);
            properties.Add(new SpaProperty(key, flags, value));
        }
        return new SpaPod(SpaPodType.Object, objectType, objectId, SpaChoiceType.None, body, [], properties);
    }

    /// <summary>Reads the raw element values that follow a template.</summary>
    private static List<SpaPod> ReadRawValues(byte[] body, int offset, int elementSize, SpaPodType elementType)
    {
        List<SpaPod> values = [];
        if (elementSize <= 0) return values;
        while (offset + elementSize <= body.Length)
        {
            byte[] raw = body.AsSpan(offset, elementSize).ToArray();
            values.Add(new SpaPod(elementType, 0, 0, SpaChoiceType.None, raw, [], []));
            offset += elementSize;
        }
        return values;
    }

    /// <summary>Reads one child POD and advances <paramref name="offset"/> past it.</summary>
    private static SpaPod ReadChild(byte[] body, ref int offset)
    {
        if (offset + HeaderSize > body.Length)
            throw new InvalidDataException("A child POD is truncated.");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));
        if (size > int.MaxValue || offset + HeaderSize + (int)size > body.Length)
            throw new InvalidDataException("A child POD runs past the end of its parent.");
        SpaPod child = Read(body.AsSpan(offset, AllocatedSize((int)size)));
        offset += AllocatedSize((int)size);
        return child;
    }

    private void Require(SpaPodType expected)
    {
        if (Type != expected)
            throw new InvalidDataException($"This POD is a {Type}, not a {expected}.");
    }

    private void RequireBody(int length)
    {
        if (Body.Length < length)
            throw new InvalidDataException("The POD body is too short for this value.");
    }
}
