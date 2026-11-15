namespace Aibell.Mpd.Config;

/// <summary>The lexical rules of <c>mpd.conf</c>: bare names, double-quoted values in which a backslash escapes the next character, and <c>#</c> comments.</summary>
public static class MpdConfigSyntax
{
    /// <summary>Writes a value the way MPD reads it back.</summary>
    /// <exception cref="ArgumentException">The value contains a line break or a NUL, which a quoted value cannot hold.</exception>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0)
            throw new ArgumentException("A setting value cannot contain a line break.", nameof(value));
        return string.Concat("\"", value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal), "\"");
    }

    /// <summary>True for a name MPD accepts: a letter, then letters, digits and underscores.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsAsciiLetter(name[0])) return false;
        foreach (char ch in name)
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_') return false;
        return true;
    }

    internal static void RequireName(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"'{name}' is not a valid setting name.", nameof(name));
    }

    /// <summary>The characters MPD treats as white space inside a line.</summary>
    internal static bool IsSpace(char ch) => ch is ' ' or '\t' or '\r' or '\f' or '\v';

    /// <summary>Reads a leading name. Returns the index after it, or -1 (with <paramref name="error"/>) when the text is not a name.</summary>
    internal static int ReadName(string text, int start, out string name, out string? error)
    {
        name = string.Empty;
        error = null;
        if (start >= text.Length || !char.IsAsciiLetter(text[start]))
        {
            error = "Letter expected";
            return -1;
        }
        int end = start + 1;
        while (end < text.Length && !IsSpace(text[end]))
        {
            if (!char.IsAsciiLetterOrDigit(text[end]) && text[end] != '_')
            {
                error = "Invalid word character";
                return -1;
            }
            end++;
        }
        name = text[start..end];
        return end;
    }

    /// <summary>
    /// Reads a quoted value starting at <paramref name="start"/>. The closing quote must be followed by white space or the end of the
    /// line. Returns the index after the closing quote, or -1 with <paramref name="error"/>.
    /// </summary>
    internal static int ReadQuoted(string text, int start, out string value, out string? error)
    {
        value = string.Empty;
        error = null;
        if (start >= text.Length || text[start] != '"')
        {
            error = "'\"' expected";
            return -1;
        }
        System.Text.StringBuilder builder = new();
        int index = start + 1;
        while (true)
        {
            if (index >= text.Length)
            {
                error = "Missing closing '\"'";
                return -1;
            }
            char ch = text[index];
            if (ch == '"') break;
            if (ch == '\\')
            {
                index++;
                if (index >= text.Length)
                {
                    error = "Missing closing '\"'";
                    return -1;
                }
                ch = text[index];
            }
            builder.Append(ch);
            index++;
        }
        index++;
        if (index < text.Length && !IsSpace(text[index]))
        {
            error = "Space expected after closing '\"'";
            return -1;
        }
        value = builder.ToString();
        return index;
    }

    /// <summary>The booleans MPD accepts: <c>yes</c>, <c>true</c>, <c>1</c>, <c>no</c>, <c>false</c> and <c>0</c>, in any letter case.</summary>
    public static bool? ParseBoolean(string? value)
    {
        if (value is null) return null;
        if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
        if (value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0") return false;
        return null;
    }
}
