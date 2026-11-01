using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Aibell.Application.Podcasts;

/// <summary>Turns feed show notes (plain text or HTML) into safe plain text. Nothing is rendered or executed.</summary>
public static partial class PodcastNotes
{
    public const int MaximumLength = 8000;

    [GeneratedRegex(@"<\s*(script|style)\b.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ExecutableBlocks();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr|/ul|/ol|/blockquote)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n+(?=• )")]
    private static partial Regex BeforeBullets();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    public static string ToPlainText(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        string text = ExecutableBlocks().Replace(notes, "");
        text = ListItems().Replace(text, "\n• ");
        text = LineBreaks().Replace(text, "\n");
        text = Tags().Replace(text, "");
        // Decode last, so an encoded "<script>" in the notes stays visible text instead of being stripped.
        text = WebUtility.HtmlDecode(text).Replace("\r\n", "\n").Replace('\r', '\n');
        StringBuilder builder = new();
        foreach (string line in text.Split('\n')) builder.Append(Spaces().Replace(line, " ").Trim()).Append('\n');
        text = BeforeBullets().Replace(builder.ToString(), "\n");
        text = BlankRuns().Replace(text, "\n\n").Trim();
        return text.Length <= MaximumLength ? text : text[..MaximumLength].TrimEnd() + "…";
    }
}
