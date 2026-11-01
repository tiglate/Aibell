using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aibell.Desktop.Localization;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

[TestFixture]
public sealed partial class LocalizationTests
{
    private static string Root
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aibell.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private static Dictionary<string, string> Read(string name) =>
        XDocument.Load(Path.Combine(Root, "src", "Aibell.Desktop", "Resources", name)).Root!.Elements("data")
            .ToDictionary(item => (string)item.Attribute("name")!, item => item.Element("value")!.Value, StringComparer.Ordinal);

    private static string[] SourceFiles() =>
        [.. Directory.EnumerateFiles(Path.Combine(Root, "src", "Aibell.Desktop"), "*.*", SearchOption.AllDirectories)
            .Where(file => (file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal)) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))];

    private static HashSet<string> KeysUsedInSource()
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string file in SourceFiles())
        {
            string text = File.ReadAllText(file);
            foreach (Match match in CodeKey().Matches(text)) keys.Add(match.Groups[1].Value);
            foreach (Match match in XamlKey().Matches(text)) keys.Add(match.Groups[1].Value);
            foreach (Match match in PluralKey().Matches(text))
            {
                keys.Add(match.Groups[1].Value + ".One");
                keys.Add(match.Groups[1].Value + ".Other");
            }
        }
        return keys;
    }

    [GeneratedRegex(@"Loc\.(?:Get|Format)\(\s*""([^""]+)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"Loc\.Plural\(\s*""([^""]+)""")]
    private static partial Regex PluralKey();

    [GeneratedRegex(@"\{loc:T\s+([A-Za-z0-9_.]+)\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    [Test]
    public void EveryKeyUsedInSource_ExistsInTheEnglishResources()
    {
        Dictionary<string, string> english = Read("Strings.resx");
        Assert.That(KeysUsedInSource().Where(key => !english.ContainsKey(key)), Is.Empty);
    }

    [Test]
    public void EveryEnglishResource_IsUsedBySomething()
    {
        HashSet<string> used = KeysUsedInSource();
        Assert.That(Read("Strings.resx").Keys.Where(key => !used.Contains(key)), Is.Empty);
    }

    private static string[] Translations() =>
        [.. Directory.EnumerateFiles(Path.Combine(Root, "src", "Aibell.Desktop", "Resources"), "Strings.*.resx")
            .Select(file => Path.GetFileNameWithoutExtension(file)["Strings.".Length..]).Order(StringComparer.Ordinal)];

    private static readonly string[] ExpectedLanguages = ["ar", "de", "es", "fr", "he", "it", "ja", "pl", "pt-BR", "ru", "zh-Hans"];

    [Test]
    public void TheShippedLanguages_AreExactlyTheSupportedOnes() => Assert.That(Translations(), Is.EqualTo(ExpectedLanguages));

    [TestCaseSource(nameof(Translations))]
    public void Translation_HasOnlyKnownKeys(string language)
    {
        Dictionary<string, string> english = Read("Strings.resx");
        Dictionary<string, string> translated = Read($"Strings.{language}.resx");
        IReadOnlyList<string> categories = PluralRules.Categories(CultureInfo.GetCultureInfo(language));

        foreach (string key in translated.Keys)
        {
            if (english.ContainsKey(key)) continue;
            int dot = key.LastIndexOf('.');
            bool pluralVariant = dot > 0 && english.ContainsKey(key[..dot] + ".Other") && categories.Contains(key[(dot + 1)..]);
            Assert.That(pluralVariant, Is.True, $"{language}: unknown key {key}");
        }
    }

    [TestCaseSource(nameof(Translations))]
    public void Translation_CoversEveryEnglishKeyAndEveryPluralCategory(string language)
    {
        Dictionary<string, string> english = Read("Strings.resx");
        HashSet<string> translated = [.. Read($"Strings.{language}.resx").Keys];
        IReadOnlyList<string> categories = PluralRules.Categories(CultureInfo.GetCultureInfo(language));

        List<string> missing = [];
        foreach (string key in english.Keys)
        {
            if (key.EndsWith(".Other", StringComparison.Ordinal))
            {
                string baseKey = key[..^".Other".Length];
                missing.AddRange(categories.Select(category => $"{baseKey}.{category}").Where(name => !translated.Contains(name)));
            }
            else if (!key.EndsWith(".One", StringComparison.Ordinal) && !translated.Contains(key)) missing.Add(key);
        }
        Assert.That(missing, Is.Empty, language);
    }

    [TestCaseSource(nameof(Translations))]
    public void Translation_KeepsThePlaceholders(string language)
    {
        Dictionary<string, string> english = Read("Strings.resx");
        foreach ((string key, string text) in Read($"Strings.{language}.resx"))
        {
            if (english.TryGetValue(key, out string? original) && !key.EndsWith(".One", StringComparison.Ordinal) && !key.EndsWith(".Other", StringComparison.Ordinal))
            {
                Assert.That(Placeholders(text), Is.EqualTo(Placeholders(original)), $"{language}: {key}");
                continue;
            }
            // A plural sentence may spell the number out (no {0}) but never drops another argument or adds an unknown one.
            string baseKey = key[..key.LastIndexOf('.')];
            string[] all = Placeholders(english[baseKey + ".Other"]);
            string[] required = english.TryGetValue(baseKey + ".One", out string? one) ? [.. Placeholders(one).Where(name => name != "0")] : [];
            string[] actual = Placeholders(text);
            Assert.That(actual.Except(all), Is.Empty, $"{language}: {key} has an unknown placeholder");
            Assert.That(required.Except(actual), Is.Empty, $"{language}: {key} lost a placeholder");
        }
    }

    [TestCaseSource(nameof(Translations))]
    public void Translation_DoesNotLeaveEnglishPlaceholdersOrBlankText(string language)
    {
        foreach ((string key, string text) in Read($"Strings.{language}.resx"))
            Assert.That(text, Is.Not.Empty.And.Not.EqualTo("TODO"), $"{language}: {key}");
    }

    [TestCase("ru", 1, "One"), TestCase("ru", 21, "One"), TestCase("ru", 2, "Few"), TestCase("ru", 24, "Few"), TestCase("ru", 5, "Many"),
     TestCase("ru", 11, "Many"), TestCase("ru", 12, "Many"), TestCase("ru", 0, "Many"), TestCase("ru", 101, "One"), TestCase("ru", 112, "Many"),
     TestCase("pl", 1, "One"), TestCase("pl", 21, "Many"), TestCase("pl", 22, "Few"), TestCase("pl", 12, "Many"), TestCase("pl", 5, "Many"),
     TestCase("ar", 0, "Zero"), TestCase("ar", 1, "One"), TestCase("ar", 2, "Two"), TestCase("ar", 3, "Few"), TestCase("ar", 10, "Few"),
     TestCase("ar", 11, "Many"), TestCase("ar", 99, "Many"), TestCase("ar", 100, "Other"), TestCase("ar", 103, "Few"),
     TestCase("he", 1, "One"), TestCase("he", 2, "Two"), TestCase("he", 3, "Other"), TestCase("he", 0, "Other"),
     TestCase("fr", 0, "One"), TestCase("fr", 1, "One"), TestCase("fr", 2, "Other"),
     TestCase("ja", 1, "Other"), TestCase("zh-Hans", 1, "Other"), TestCase("de", 1, "One"), TestCase("de", 0, "Other"),
     TestCase("es", 1, "One"), TestCase("it", 7, "Other"), TestCase("en-US", 1, "One"), TestCase("pt-BR", 2, "Other")]
    public void PluralRules_FollowTheCldrCategories(string language, int count, string expected) =>
        Assert.That(PluralRules.Category(CultureInfo.GetCultureInfo(language), count), Is.EqualTo(expected));

    [Test]
    public void Plural_UsesTheLanguageCategories()
    {
        CultureInfo saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            Assert.That(Loc.Plural("Result.Matching", 1), Does.Contain("1"));
            Assert.That(Loc.Plural("Result.Matching", 3), Does.Not.Contain("!"));
            Assert.That(Loc.Plural("Result.Matching", 5), Does.Not.Contain("!"));
            Assert.That(Loc.Plural("Result.Matching", 3), Is.Not.EqualTo(Loc.Plural("Result.Matching", 5)), "Russian has distinct few and many forms");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja");
            Assert.That(Loc.Plural("Result.Matching", 4), Is.EqualTo(Loc.Plural("Result.Matching", 1).Replace("1", "4", StringComparison.Ordinal)), "Japanese has a single form");
        }
        finally { CultureInfo.CurrentUICulture = saved; }
    }

    [TestCase("ar", true), TestCase("he", true), TestCase("en-US", false), TestCase("ja", false), TestCase("ru", false)]
    public void IsRightToLeft_IsTrueOnlyForArabicAndHebrew(string language, bool expected)
    {
        CultureInfo saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            Assert.That(Loc.IsRightToLeft, Is.EqualTo(expected));
        }
        finally { CultureInfo.CurrentUICulture = saved; }
    }

    [Test]
    public void Get_UsesTheUiCultureAndFallsBackToEnglish()
    {
        CultureInfo saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
            Assert.That(Loc.Get("Plugins.None"), Is.EqualTo("Nenhum plugin está instalado."));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("sv-SE");
            Assert.That(Loc.Get("Plugins.None"), Is.EqualTo("No plugins are installed."), "untranslated languages show English");
        }
        finally { CultureInfo.CurrentUICulture = saved; }
    }

    [Test]
    public void Get_MissingKey_IsVisibleInsteadOfEmpty() => Assert.That(Loc.Get("No.Such.Key"), Is.EqualTo("!No.Such.Key!"));

    [Test]
    public void Plural_PicksTheSingularOnlyForOne()
    {
        Assert.That(Loc.Plural("Result.Matching", 1), Is.EqualTo("1 matching track."));
        Assert.That(Loc.Plural("Result.Matching", 0), Is.EqualTo("0 matching tracks."));
        Assert.That(Loc.Plural("Result.Matching", 7), Is.EqualTo("7 matching tracks."));
    }

    [Test]
    public void Format_FillsPlaceholders() => Assert.That(Loc.Format("Artwork.FoundMany", 3), Is.EqualTo("Found 3 covers. Choose one, then press Save."));

    [Test]
    public void ApplyLanguageOverride_IgnoresBlankAndUnknownNames()
    {
        CultureInfo saved = CultureInfo.CurrentUICulture;
        Loc.ApplyLanguageOverride(null);
        Loc.ApplyLanguageOverride("   ");
        Loc.ApplyLanguageOverride("not a culture!");
        Assert.That(CultureInfo.CurrentUICulture, Is.EqualTo(saved));
    }

    [Test]
    public void AppLanguages_EachChoiceHasATranslation()
    {
        CultureInfo saved = CultureInfo.CurrentUICulture;
        try
        {
            foreach (AppLanguages.Language language in AppLanguages.All.Where(item => item.Code != "en"))
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language.Code);
                Assert.That(Loc.Get("Prefs.LanguageRestart"), Is.Not.EqualTo("The language changes the next time Aibell starts."), language.Code);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
        Assert.That(AppLanguages.IsValid(null), Is.True);
        Assert.That(AppLanguages.IsValid("PT-br"), Is.True);
        Assert.That(AppLanguages.IsValid("xx"), Is.False);
    }

    private static string[] Placeholders(string text) => [.. Placeholder().Matches(text).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal)];
}
