namespace Aibell.Desktop.Localization;

/// <summary>The languages Aibell ships, each named in itself so it can be found whatever language is showing.</summary>
public static class AppLanguages
{
    public sealed record Language(string Code, string Name);

    public static IReadOnlyList<Language> All { get; } =
    [
        new("en", "English"), new("pt-BR", "Português (Brasil)"), new("es", "Español"), new("it", "Italiano"),
        new("fr", "Français"), new("de", "Deutsch"), new("pl", "Polski"), new("ru", "Русский"),
        new("ja", "日本語"), new("zh-Hans", "简体中文"), new("he", "עברית"), new("ar", "العربية")
    ];

    /// <summary>Null (follow the operating system) or one of the shipped languages.</summary>
    public static bool IsValid(string? code) => code is null || IndexOf(code) >= 0;

    public static int IndexOf(string? code)
    {
        for (int index = 0; index < All.Count; index++)
            if (string.Equals(All[index].Code, code, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }
}
