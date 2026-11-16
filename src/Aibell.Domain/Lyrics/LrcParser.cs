using System.Globalization;
using System.Text;

namespace Aibell.Domain.Lyrics;

/// <summary>
/// Reads and writes the LRC lyrics format: <c>[mm:ss.xx]text</c> lines, several stamps per line, and the <c>[offset:ms]</c> tag.
/// Other <c>[tag:value]</c> header lines (ar, ti, al, by...) are dropped. Text without any timestamp comes back as plain lines.
/// </summary>
public static class LrcParser
{
    public static IReadOnlyList<LyricLine> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        List<(TimeSpan Start, string Text)> timed = [];
        List<string> plain = [];
        long offsetMs = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            List<TimeSpan> stamps = [];
            int index = 0;
            bool metadata = false;
            while (index < line.Length && line[index] == '[')
            {
                int close = line.IndexOf(']', index);
                if (close < 0) break;
                string inner = line.Substring(index + 1, close - index - 1);
                if (TryStamp(inner, out TimeSpan stamp)) stamps.Add(stamp);
                else if (TryOffset(inner, out long offset)) { offsetMs = offset; metadata = true; }
                else if (IsTag(inner)) metadata = true;
                else break;
                index = close + 1;
            }
            string content = line[index..];
            if (stamps.Count > 0) { foreach (TimeSpan stamp in stamps) timed.Add((stamp, StripWordStamps(content).Trim())); }
            else if (!metadata) plain.Add(line.TrimEnd());
        }
        if (timed.Count == 0) return TrimBlankEdges(plain.Select(line => new LyricLine(null, line)).ToList());
        TimeSpan shift = TimeSpan.FromMilliseconds(offsetMs);
        // Positive offset makes the lyrics appear sooner, as the LRC specification defines it.
        return [.. timed.Select(item => new LyricLine(Max(TimeSpan.Zero, item.Start - shift), item.Text)).OrderBy(line => line.Start)];
    }

    public static string Format(IEnumerable<LyricLine> lines)
    {
        StringBuilder builder = new();
        foreach (LyricLine line in lines)
        {
            if (builder.Length > 0) builder.Append('\n');
            if (line.Start is { } start)
            {
                int centis = (int)Math.Round(start.TotalMilliseconds / 10);
                builder.Append(CultureInfo.InvariantCulture, $"[{centis / 6000:00}:{centis / 100 % 60:00}.{centis % 100:00}]");
            }
            builder.Append(line.Text);
        }
        return builder.ToString();
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static List<LyricLine> TrimBlankEdges(List<LyricLine> lines)
    {
        while (lines.Count > 0 && lines[0].Text.Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // Enhanced LRC carries <mm:ss.xx> word stamps inside a line; the line keeps its text without them.
    private static string StripWordStamps(string content)
    {
        if (!content.Contains('<', StringComparison.Ordinal)) return content;
        StringBuilder builder = new();
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '<')
            {
                int close = content.IndexOf('>', i);
                if (close > 0 && TryStamp(content.Substring(i + 1, close - i - 1), out _)) { i = close; continue; }
            }
            builder.Append(content[i]);
        }
        return builder.ToString();
    }

    private static bool TryStamp(string value, out TimeSpan stamp)
    {
        stamp = default;
        int colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !value.All(c => char.IsAsciiDigit(c) || c is ':' or '.')) return false;
        if (!int.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)) return false;
        string rest = value[(colon + 1)..];
        if (!double.TryParse(rest, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds) || rest.Length == 0) return false;
        stamp = TimeSpan.FromSeconds(minutes * 60 + seconds);
        return true;
    }

    private static bool TryOffset(string value, out long milliseconds)
    {
        milliseconds = 0;
        return value.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(value.AsSpan(7).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out milliseconds);
    }

    private static bool IsTag(string value)
    {
        int colon = value.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && value[..colon].All(char.IsAsciiLetter);
    }
}
