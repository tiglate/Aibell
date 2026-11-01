using System.Globalization;

namespace Aibell.Infrastructure.Metadata;

internal static class AtlTagFields
{
    public static string? ReadText(IEnumerable<KeyValuePair<string, string>> fields, params string[] names)
    {
        foreach (KeyValuePair<string, string> field in fields)
        {
            if (names.Any(name => Matches(field.Key, name)) && !string.IsNullOrWhiteSpace(field.Value))
                return field.Value.Trim();
        }
        return null;
    }

    public static int? ReadInteger(IEnumerable<KeyValuePair<string, string>> fields, params string[] names)
        => ParseInteger(ReadText(fields, names));

    public static int? ParseInteger(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : null;

    public static readonly string[] ShowNames = ["SHOW", "SHOWTITLE", "TVSHOW"];
    public static readonly string[] SeasonNames = ["SEASONNUMBER", "TVSEASON", "SEASON"];
    public static readonly string[] EpisodeNames = ["EPISODENUMBER", "TVEPISODENUM", "EPISODE", "TVEPISODE"];

    /// <summary>
    /// ATL 7.17 writes the Vorbis comment header's vendor string with its length counted in characters, not bytes. When the encoder name
    /// holds non-ASCII text (libVorbis 1.3.5, the most common one, is "Xiph.Org libVorbis I 20150105 (⛄⛄⛄⛄)") the header comes out
    /// misaligned: <c>Save()</c> reports success and every tag in the file is gone. The staged-copy validation would catch it, but the file
    /// should not be offered for writing at all. Returns the reason, or null when the vendor string is safe.
    /// </summary>
    public static string? UnsafeVendorRestriction(IEnumerable<KeyValuePair<string, string>> fields) =>
        fields.Any(field => field.Key.Equals("VORBIS-VENDOR", StringComparison.OrdinalIgnoreCase) && field.Value.Any(character => character > 127))
            ? "This file's encoder name (for example libVorbis 1.3.5) contains characters that the tag library cannot rewrite without erasing the tags, so it cannot be written safely. Library editing remains available."
            : null;

    public static string? NumberText(int? value) => value is null ? null : value == 0 ? "" : value.Value.ToString(CultureInfo.InvariantCulture);

    public static readonly string[] KeywordNames = ["KEYWORDS", "PODCASTKEYWORDS", "KEYWORD"];
    public static readonly string[] LicenseNames = ["LICENSE", "LICENSEURI", "LICENSEURL"];

    public static bool MatchesAny(string key, string[] names) => names.Any(name => Matches(key, name));

    public static void WriteText(IDictionary<string, string> fields, string value, string[] names)
    {
        string[] keys = fields.Keys.Where(key => MatchesAny(key, names)).ToArray();
        foreach (string key in keys) fields[key] = value;
        if (keys.Length == 0 && value.Length > 0) fields[names[0]] = value;
    }

    private static bool Matches(string key, string name)
        => string.Concat(key.Where(char.IsLetterOrDigit)).Equals(
            string.Concat(name.Where(char.IsLetterOrDigit)), StringComparison.OrdinalIgnoreCase);
}
