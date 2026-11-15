namespace Aibell.Mpd.Config;

/// <summary>One thing in an <c>mpd.conf</c> file: a line of text, a setting or a <c>name { ... }</c> block.</summary>
public abstract class MpdConfigEntry
{
}

/// <summary>
/// A line of the file together with its line ending. Everything the editor does not understand stays a plain line, so saving a
/// file never loses comments, blank lines or text the parser could not read.
/// </summary>
public class MpdConfigLine : MpdConfigEntry
{
    internal MpdConfigLine(string text, string lineEnding, int number)
    {
        Text = text;
        LineEnding = lineEnding;
        Number = number;
    }

    /// <summary>The line without its line ending.</summary>
    public string Text { get; internal set; }

    /// <summary>The line ending that follows <see cref="Text"/>: <c>"\n"</c>, <c>"\r\n"</c> or empty for a last line without one.</summary>
    public string LineEnding { get; internal set; }

    /// <summary>The 1-based line number when the file was read; 0 for lines added since.</summary>
    public int Number { get; }

    /// <summary>The reason MPD would reject this line, or <c>null</c> when it is blank, a comment or understood.</summary>
    public string? Error { get; internal set; }

    public bool IsBlank => string.IsNullOrWhiteSpace(Text);

    public bool IsComment => Text.AsSpan().TrimStart().StartsWith("#", StringComparison.Ordinal);
}

/// <summary>A <c>name "value"</c> line, including <c>include</c> and <c>include_optional</c>.</summary>
public sealed class MpdConfigSetting : MpdConfigLine
{
    private string? value;
    private int valueStart = -1;
    private int valueLength;

    internal MpdConfigSetting(string text, string lineEnding, int number, string name, string? value, int valueStart, int valueLength)
        : base(text, lineEnding, number)
    {
        Name = name;
        this.value = value;
        this.valueStart = valueStart;
        this.valueLength = valueLength;
    }

    internal MpdConfigSetting(string indent, string name, string? value, string lineEnding)
        : base(string.Empty, lineEnding, 0)
    {
        Name = name;
        Indent = indent;
        Text = indent + name;
        if (value is not null) Value = value;
    }

    private string Indent { get; } = string.Empty;

    public string Name { get; }

    /// <summary>The unquoted value; <c>null</c> for a repeatable setting written without a value, which clears the earlier ones.</summary>
    public string? Value
    {
        get => value;
        set
        {
            if (string.Equals(this.value, value, StringComparison.Ordinal) && (value is null || valueStart >= 0)) return;
            this.value = value;
            Error = null;
            if (value is null)
            {
                Text = Indent + Name;
                valueStart = -1;
                valueLength = 0;
            }
            else if (valueStart >= 0)
            {
                string quoted = MpdConfigSyntax.Quote(value);
                Text = string.Concat(Text.AsSpan(0, valueStart), quoted, Text.AsSpan(valueStart + valueLength));
                valueLength = quoted.Length;
            }
            else
            {
                string quoted = MpdConfigSyntax.Quote(value);
                Text = $"{(Text.Length > 0 ? Text.TrimEnd() : Indent + Name)} {quoted}";
                valueStart = Text.Length - quoted.Length;
                valueLength = quoted.Length;
            }
        }
    }
}

/// <summary>A <c>name { ... }</c> block such as <c>audio_output</c>. Only settings, comments and blank lines may appear inside.</summary>
public sealed class MpdConfigBlock : MpdConfigEntry
{
    internal MpdConfigBlock(string name, MpdConfigLine open)
    {
        Name = name;
        Open = open;
    }

    public string Name { get; }

    internal MpdConfigLine Open { get; }

    internal MpdConfigLine? Close { get; set; }

    internal List<MpdConfigLine> Lines { get; } = [];

    /// <summary>The 1-based line where the block opens; 0 for a block added since the file was read.</summary>
    public int Number => Open.Number;

    /// <summary>False when the file ended before the closing brace.</summary>
    public bool IsClosed => Close is not null;

    /// <summary>The settings in the block, in file order.</summary>
    public IEnumerable<MpdConfigSetting> Settings => Lines.OfType<MpdConfigSetting>();

    /// <summary>The value of a setting, or <c>null</c> when it is absent.</summary>
    public string? Get(string name) => Settings.FirstOrDefault(item => item.Name == name)?.Value;

    /// <summary>Every value of a setting, in order.</summary>
    public IReadOnlyList<string> GetAll(string name) => [.. Settings.Where(item => item.Name == name && item.Value is not null).Select(item => item.Value!)];

    /// <summary>Sets a setting, adding it after the last one when it is new; <c>null</c> removes it.</summary>
    public void Set(string name, string? value)
    {
        MpdConfigSyntax.RequireName(name);
        MpdConfigSetting? existing = Settings.FirstOrDefault(item => item.Name == name);
        if (value is null)
        {
            Lines.RemoveAll(line => line is MpdConfigSetting setting && setting.Name == name);
            return;
        }
        if (existing is not null)
        {
            existing.Value = value;
            return;
        }
        string ending = Open.LineEnding.Length > 0 ? Open.LineEnding : "\n";
        MpdConfigSetting added = new("    ", name, value, ending);
        int last = Lines.FindLastIndex(line => line is MpdConfigSetting);
        Lines.Insert(last + 1, added);
    }

    /// <summary>Removes one setting line.</summary>
    public bool Remove(MpdConfigSetting setting) => Lines.Remove(setting);
}
