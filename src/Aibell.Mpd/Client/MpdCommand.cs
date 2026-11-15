using System.Globalization;

namespace Aibell.Mpd.Client;

/// <summary>Builds protocol command lines with their arguments quoted.</summary>
public static class MpdCommand
{
    /// <summary>Quotes one argument: wraps it in double quotes and escapes backslashes and quotes.</summary>
    /// <exception cref="ArgumentException">The text contains a line break, which would end the command early.</exception>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0) throw new ArgumentException("An MPD argument cannot contain a line break.", nameof(value));
        return string.Concat("\"", value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal), "\"");
    }

    /// <summary>Formats <paramref name="name"/> followed by its arguments. Numbers are written plainly, booleans as 0 or 1, text quoted.</summary>
    public static string Format(string name, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(arguments);
        System.Text.StringBuilder builder = new(name);
        foreach (object? argument in arguments)
        {
            builder.Append(' ');
            builder.Append(argument switch
            {
                null => throw new ArgumentException("An MPD argument cannot be null.", nameof(arguments)),
                bool flag => flag ? "1" : "0",
                int number => number.ToString(CultureInfo.InvariantCulture),
                long number => number.ToString(CultureInfo.InvariantCulture),
                double number => number.ToString("0.###", CultureInfo.InvariantCulture),
                TimeSpan time => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                string text => Quote(text),
                _ => Quote(Convert.ToString(argument, CultureInfo.InvariantCulture) ?? string.Empty),
            });
        }
        return builder.ToString();
    }

    /// <summary>A filter expression <c>(base 'folder')</c> that restricts a search to the songs under a folder of the music directory.</summary>
    public static string BaseFilter(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return $"(base '{folder.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}')";
    }

    /// <summary>A filter expression <c>(tag contains 'text')</c> with the text escaped for MPD's filter syntax. The tag is <c>any</c> or a tag name.</summary>
    public static string ContainsFilter(string tag, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentNullException.ThrowIfNull(text);
        return $"({tag} contains '{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}')";
    }
}
