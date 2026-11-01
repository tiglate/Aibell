using System.Globalization;
using System.Resources;

namespace Aibell.Desktop.Localization;

/// <summary>
/// Looks up user-visible text in <c>Resources/Strings.resx</c> (English, the neutral language) and its translations, using the
/// UI culture of the current thread, which is the operating system's language unless <c>AIBELL_LANGUAGE</c> overrides it.
/// A key with no translation falls back to English; a key missing everywhere shows as <c>!Key!</c> so the gap is visible and a
/// test (<c>LocalizationTests</c>) fails before it ships.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Resources = new("Aibell.Desktop.Resources.Strings", typeof(Loc).Assembly);

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"!{key}!";

    /// <summary>Looks up <paramref name="key"/> and fills <c>{0}</c>-style placeholders using the regional-format culture.</summary>
    public static string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    /// <summary>
    /// Picks <c>baseKey.&lt;category&gt;</c> using the plural categories of the UI language (<see cref="PluralRules"/>), falling back to
    /// <c>baseKey.Other</c> when the language has no sentence for that category. <paramref name="count"/> is always argument
    /// <c>{0}</c>; further arguments follow.
    /// </summary>
    public static string Plural(string baseKey, int count, params object?[] arguments)
    {
        string key = baseKey + "." + PluralRules.Category(CultureInfo.CurrentUICulture, count);
        string text = Resources.GetString(key, CultureInfo.CurrentUICulture) ?? Get(baseKey + ".Other");
        return string.Format(CultureInfo.CurrentCulture, text, [count, .. arguments]);
    }

    /// <summary>True when the UI language is written right to left (Arabic, Hebrew), so windows should mirror their layout.</summary>
    public static bool IsRightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

    /// <summary>Applies the <c>AIBELL_LANGUAGE</c> override (for example <c>pt-BR</c>) once at startup; unknown names are ignored.</summary>
    public static void ApplyLanguageOverride(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(name.Trim());
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Keep the system language.
        }
    }
}
