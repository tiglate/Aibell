using System.Globalization;

namespace Aibell.Desktop.Localization;

/// <summary>
/// The CLDR plural categories the shipped languages need. English-style languages use <c>One</c>/<c>Other</c>; Slavic languages add
/// <c>Few</c> and <c>Many</c>; Arabic uses all six; Japanese and Chinese have only <c>Other</c>. A language that is not listed
/// behaves like English.
/// </summary>
public static class PluralRules
{
    public static IReadOnlyList<string> Categories(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "ja" or "zh" => ["Other"],
        "ru" or "pl" => ["One", "Few", "Many", "Other"],
        "ar" => ["Zero", "One", "Two", "Few", "Many", "Other"],
        "he" => ["One", "Two", "Other"],
        _ => ["One", "Other"]
    };

    public static string Category(CultureInfo culture, int count)
    {
        long n = Math.Abs((long)count);
        long lastTwo = n % 100;
        long last = n % 10;
        return culture.TwoLetterISOLanguageName switch
        {
            "ja" or "zh" => "Other",
            "fr" => n <= 1 ? "One" : "Other",
            "ru" => last == 1 && lastTwo != 11 ? "One"
                : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? "Few"
                : "Many",
            "pl" => n == 1 ? "One"
                : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? "Few"
                : "Many",
            "ar" => n == 0 ? "Zero"
                : n == 1 ? "One"
                : n == 2 ? "Two"
                : lastTwo is >= 3 and <= 10 ? "Few"
                : lastTwo is >= 11 and <= 99 ? "Many"
                : "Other",
            "he" => n == 1 ? "One" : n == 2 ? "Two" : "Other",
            _ => n == 1 ? "One" : "Other"
        };
    }
}
