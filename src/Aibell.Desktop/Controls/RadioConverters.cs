using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Aibell.Desktop.Controls;

/// <summary>Binding helpers for the Radio station cards and genre list.</summary>
public static class RadioConverters
{
    /// <summary>True when a station has been given a one-to-five rating.</summary>
    public static readonly FuncValueConverter<int, bool> IsRated = new(rating => rating > 0);

    private static readonly Geometry Globe = Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z M3,12 L21,12 M12,3 C8,7 8,17 12,21 C16,17 16,7 12,3");
    private static readonly Geometry Note = Geometry.Parse("M9,18 L9,5 L19,3 L19,16 M9,18 A3,3 0 1 1 6,15 A3,3 0 0 1 9,18 Z M19,16 A3,3 0 1 1 16,13 A3,3 0 0 1 19,16 Z");
    private static readonly Geometry Bubble = Geometry.Parse("M4,5 L20,5 L20,16 L11,16 L7,20 L7,16 L4,16 Z");
    private static readonly Geometry Wave = Geometry.Parse("M2,12 L5,12 L7,6 L10,18 L13,4 L16,20 L18,12 L22,12");
    private static readonly Geometry Flame = Geometry.Parse("M12,2.5 C13,6 17.5,8.5 17.5,14 A5.5,5.5 0 0 1 6.5,14 C6.5,11.5 8,10 9,8.5 C9.5,10 10.5,10.5 11,10.5 C11.5,8 11,5 12,2.5 Z");
    private static readonly Geometry Keys = Geometry.Parse("M4,5 L20,5 L20,19 L4,19 Z M9.3,12 L9.3,19 M14.7,12 L14.7,19 M7.5,5 L7.5,12 M12,5 L12,12 M16.5,5 L16.5,12");

    /// <summary>The small icon in front of a genre in the genre list; <c>null</c> (the "All stations" row) is a globe.</summary>
    public static readonly FuncValueConverter<string?, Geometry?> GenreIcon = new(genre => IconFor(genre));

    internal static Geometry IconFor(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return Globe;
        string name = genre.ToLowerInvariant();
        if (Has(name, "rock", "metal", "alternative", "punk")) return Flame;
        if (Has(name, "news", "talk", "sport")) return Bubble;
        if (Has(name, "classical", "jazz", "opera", "soundtrack", "piano")) return Keys;
        if (Has(name, "electronic", "ambient", "experimental", "dance", "techno")) return Wave;
        if (Has(name, "world", "folk")) return Globe;
        return Note;
    }

    private static bool Has(string text, params string[] words) => words.Any(text.Contains);
}
