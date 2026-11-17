using System.Globalization;
using System.Text.RegularExpressions;

namespace Aibell.Skins.Classic;

/// <summary>The playlist colours and font from pledit.txt.</summary>
public sealed record PlaylistStyle(uint Normal, uint Current, uint NormalBackground, uint SelectedBackground, string FontName)
{
    /// <summary>Used for whatever a skin leaves out: green on black, the look classic players are known for.</summary>
    public static PlaylistStyle Default { get; } = new(0xFF00FF00, 0xFFFFFFFF, 0xFF000000, 0xFF0000C6, "Arial");

    /// <summary>Reads pledit.txt leniently: keys in any case, with or without '#', and anything after the colour ignored.</summary>
    public static PlaylistStyle Parse(string? text, PlaylistStyle? fallback = null)
    {
        PlaylistStyle style = fallback ?? Default;
        if (string.IsNullOrEmpty(text)) return style;
        Dictionary<string, string> values = Ini.Section(text, "Text");
        string font = values.TryGetValue("Font", out string? name) && name.Trim().Length > 0 ? name.Trim() : style.FontName;
        return new(
            Color(values, "Normal") ?? style.Normal,
            Color(values, "Current") ?? style.Current,
            Color(values, "NormalBG") ?? style.NormalBackground,
            Color(values, "SelectedBG") ?? style.SelectedBackground,
            font);
    }

    private static uint? Color(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out string? text)) return null;
        Match match = Ini.HexColor().Match(text);
        return match.Success ? 0xFF000000u | uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    }
}

/// <summary>The 24 visualizer colours from viscolor.txt: background, grid dots, 16 spectrum rows (top first), 5 oscilloscope shades and the peak.</summary>
public sealed class VisualizerColors
{
    public const int Count = 24;

    private VisualizerColors(uint[] colors) => Colors = colors;

    public IReadOnlyList<uint> Colors { get; }

    public uint Background => Colors[0];

    public uint Dots => Colors[1];

    /// <summary>The colour of spectrum row <paramref name="row"/>, counted from the top (0) to the bottom (15).</summary>
    public uint Spectrum(int row) => Colors[2 + Math.Clamp(row, 0, 15)];

    public uint Peak => Colors[23];

    public static VisualizerColors Default { get; } = new(
    [
        0xFF000000, 0xFF181821,
        0xFFEF3110, 0xFFCE2921, 0xFFD65A00, 0xFFD66600, 0xFFD67300, 0xFFC67B08, 0xFFDEA518, 0xFFD6B521,
        0xFFBDDE29, 0xFF94DE21, 0xFF29CE10, 0xFF32BE10, 0xFF239E0A, 0xFF189410, 0xFF168A10, 0xFF158410,
        0xFFFFFFFF, 0xFFD6D6DE, 0xFFADB5BD, 0xFF8C949C, 0xFF6B737B, 0xFFC6C6C6,
    ]);

    public static VisualizerColors Create(IReadOnlyList<uint> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        uint[] all = [.. Default.Colors];
        for (int index = 0; index < Math.Min(colors.Count, Count); index++) all[index] = colors[index];
        return new(all);
    }

    /// <summary>Reads one "r,g,b" triple per line, ignoring comments and junk; missing lines keep the default colours.</summary>
    public static VisualizerColors Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Default;
        List<uint> colors = [];
        foreach (string line in text.Split('\n'))
        {
            MatchCollection numbers = Ini.Number().Matches(line.Split("//")[0]);
            if (numbers.Count < 3) continue;
            int Value(int index) => Math.Clamp(int.Parse(numbers[index].Value, CultureInfo.InvariantCulture), 0, 255);
            colors.Add(BmpDecoder.Opaque(Value(0), Value(1), Value(2)));
            if (colors.Count == Count) break;
        }
        return Create(colors);
    }
}

/// <summary>The window outlines from region.txt, as polygons in skin pixels; an empty list means a plain rectangle.</summary>
public sealed class SkinRegions
{
    public static SkinRegions None { get; } = new(new Dictionary<string, IReadOnlyList<IReadOnlyList<(int X, int Y)>>>());

    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<(int X, int Y)>>> sections;

    private SkinRegions(IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<(int X, int Y)>>> sections) => this.sections = sections;

    public IReadOnlyList<IReadOnlyList<(int X, int Y)>> Main => Get("Normal");

    public IReadOnlyList<IReadOnlyList<(int X, int Y)>> MainShade => Get("WindowShade");

    public IReadOnlyList<IReadOnlyList<(int X, int Y)>> Equalizer => Get("Equalizer");

    public IReadOnlyList<IReadOnlyList<(int X, int Y)>> EqualizerShade => Get("EqualizerWS");

    private IReadOnlyList<IReadOnlyList<(int X, int Y)>> Get(string section) =>
        sections.TryGetValue(section, out var polygons) ? polygons : [];

    public static SkinRegions Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return None;
        Dictionary<string, IReadOnlyList<IReadOnlyList<(int X, int Y)>>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string section in new[] { "Normal", "WindowShade", "Equalizer", "EqualizerWS" })
        {
            Dictionary<string, string> values = Ini.Section(text, section);
            if (!values.TryGetValue("NumPoints", out string? counts) || !values.TryGetValue("PointList", out string? points)) continue;
            int[] sizes = [.. Ini.Number().Matches(counts).Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture))];
            int[] coordinates = [.. Ini.Number().Matches(points).Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture))];
            List<IReadOnlyList<(int X, int Y)>> polygons = [];
            int at = 0;
            foreach (int size in sizes)
            {
                if (size < 3 || at + size * 2 > coordinates.Length) break;
                polygons.Add([.. Enumerable.Range(0, size).Select(index => (coordinates[at + index * 2], coordinates[at + index * 2 + 1]))]);
                at += size * 2;
            }
            if (polygons.Count > 0) result[section] = polygons;
        }
        return new(result);
    }
}

internal static partial class Ini
{
    /// <summary>The key=value pairs of one [section], keys without regard to case; the last duplicate wins.</summary>
    public static Dictionary<string, string> Section(string text, string section)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        bool inside = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.StartsWith('[') && line.Contains(']'))
            {
                inside = string.Equals(line[1..line.IndexOf(']')].Trim(), section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inside || line.StartsWith(';')) continue;
            int equals = line.IndexOf('=');
            if (equals > 0) values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return values;
    }

    [GeneratedRegex(@"#?([0-9A-Fa-f]{6})")]
    public static partial Regex HexColor();

    [GeneratedRegex(@"-?\d+")]
    public static partial Regex Number();
}
