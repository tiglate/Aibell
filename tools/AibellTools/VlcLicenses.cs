using System.Buffers.Binary;
using System.Text;

namespace AibellTools;

/// <summary>
/// Reads the license a VLC 3.x plugin declares about itself. Every plugin DLL exports <c>vlc_entry_license__3_0_0f</c>, which on x64
/// compiles to <c>lea rax,[rip+disp32]; ret</c> pointing at the license text, so the text is found by parsing the PE export table
/// without running the code. Anything unexpected (no export, other code shape, not a PE32+) is reported as unknown, which callers
/// must treat as not allowed.
/// </summary>
internal static class VlcLicenses
{
    private const string LgplPrefix = "Licensed under the terms of the GNU Lesser General Public License";
    private const string GplPrefix = "Licensed under the terms of the GNU General Public License";
    private static readonly byte[] ExportPrefix = Encoding.ASCII.GetBytes("vlc_entry_license__");
    private const int MaxBytes = 64 * 1024 * 1024;

    public enum Kind { Lgpl, Gpl, Unknown }

    public static string? PluginLicense(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaxBytes) return null;
        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        try
        {
            return Parse(data);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static string? Parse(byte[] data)
    {
        if (data.Length < 0x100 || data[0] != (byte)'M' || data[1] != (byte)'Z') return null;
        int pe = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x3C));
        if (!data.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8)) return null;
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pe + 6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pe + 20));
        int optional = pe + 24;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(optional)) != 0x20B) return null; // PE32+ (x64) only
        uint exportRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(optional + 112));
        int firstSection = optional + optionalSize;
        List<(uint Address, uint Size, uint Raw)> sections = [];
        for (int index = 0; index < sectionCount; index++)
        {
            int at = firstSection + (40 * index);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 8)), address = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 12));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 16)), raw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 20));
            sections.Add((address, Math.Max(virtualSize, rawSize), raw));
        }
        int? Offset(uint rva)
        {
            foreach ((uint address, uint size, uint raw) in sections)
                if (address <= rva && rva < address + size) return (int)(rva - address + raw);
            return null;
        }
        int? export = Offset(exportRva);
        if (export is null) return null;
        uint nameCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(export.Value + 24));
        uint functions = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(export.Value + 28));
        uint names = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(export.Value + 32));
        uint ordinals = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(export.Value + 36));
        for (int index = 0; index < nameCount; index++)
        {
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Need(Offset(names)) + (4 * index)));
            int nameAt = Need(Offset(nameRva));
            if (!data.AsSpan(nameAt).StartsWith(ExportPrefix)) continue;
            int ordinal = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Need(Offset(ordinals)) + (2 * index)));
            uint functionRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Need(Offset(functions)) + (4 * ordinal)));
            int codeAt = Need(Offset(functionRva));
            ReadOnlySpan<byte> code = data.AsSpan(codeAt, 8);
            if (code[0] != 0x48 || code[1] != 0x8D || code[2] != 0x05 || code[7] != 0xC3) return null;
            int displacement = BinaryPrimitives.ReadInt32LittleEndian(code[3..]);
            int? textAt = Offset((uint)(functionRva + 7 + displacement));
            if (textAt is null) return null;
            int end = Array.IndexOf(data, (byte)0, textAt.Value);
            return end < 0 ? null : Encoding.ASCII.GetString(data, textAt.Value, end - textAt.Value);
        }
        return null;
    }

    private static int Need(int? offset) => offset ?? throw new ArgumentException("Address outside the file sections.");

    public static Kind Classify(string? license) =>
        license is null ? Kind.Unknown : license.StartsWith(LgplPrefix, StringComparison.Ordinal) ? Kind.Lgpl
        : license.StartsWith(GplPrefix, StringComparison.Ordinal) ? Kind.Gpl : Kind.Unknown;
}
