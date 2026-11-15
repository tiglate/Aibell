using System.Text;

namespace Aibell.Mpd.Config;

public enum MpdConfigSeverity { Warning, Error }

/// <summary>A problem found in a configuration file. <see cref="Line"/> is 1-based, or 0 when the problem is not tied to a line.</summary>
/// <param name="Message">An English description, as MPD itself would word it.</param>
/// <param name="Code">For problems with a value, a short code (<c>NotNumber</c>, <c>OutOfRange</c>, …) the window turns into translated text.</param>
/// <param name="Subject">What the problem is about, for example <c>port</c> or <c>audio_output/device</c>.</param>
public sealed record MpdConfigDiagnostic(int Line, MpdConfigSeverity Severity, string Message, string? Code = null, string? Subject = null);

/// <summary>
/// An <c>mpd.conf</c> file that can be edited and written back without disturbing the rest: comments, blank lines, line endings,
/// column alignment, trailing comments and anything not understood are kept exactly. A line is only rewritten when its value changes.
/// </summary>
public sealed class MpdConfigDocument
{
    private const char ByteOrderMark = '﻿';

    private readonly List<MpdConfigEntry> entries = [];
    private readonly List<MpdConfigDiagnostic> diagnostics = [];
    private bool hasByteOrderMark;

    private MpdConfigDocument(string newLine) => NewLine = newLine;

    /// <summary>Every entry, in file order.</summary>
    public IReadOnlyList<MpdConfigEntry> Entries => entries;

    /// <summary>Problems found while reading the file. <see cref="Validate"/> adds checks against what MPD accepts.</summary>
    public IReadOnlyList<MpdConfigDiagnostic> Diagnostics => diagnostics;

    /// <summary>The line ending used for lines added to the file: the one the file already uses most.</summary>
    public string NewLine { get; }

    public static MpdConfigDocument CreateEmpty() => new("\n");

    public static MpdConfigDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool bom = text.Length > 0 && text[0] == ByteOrderMark;
        if (bom) text = text[1..];
        List<(string Text, string Ending)> rows = Split(text);
        int crlf = rows.Count(row => row.Ending == "\r\n");
        int lf = rows.Count(row => row.Ending == "\n");
        MpdConfigDocument document = new(crlf > lf ? "\r\n" : "\n") { hasByteOrderMark = bom };
        document.Read(rows);
        return document;
    }

    private static List<(string Text, string Ending)> Split(string text)
    {
        List<(string, string)> rows = [];
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                rows.Add((text[start..], string.Empty));
                break;
            }
            bool carriage = newline > start && text[newline - 1] == '\r';
            int end = carriage ? newline - 1 : newline;
            rows.Add((text[start..end], carriage ? "\r\n" : "\n"));
            start = newline + 1;
        }
        return rows;
    }

    private void Read(List<(string Text, string Ending)> rows)
    {
        MpdConfigBlock? block = null;
        for (int index = 0; index < rows.Count; index++)
        {
            (string text, string ending) = rows[index];
            int number = index + 1;
            string trimmed = text.TrimStart(' ', '\t', '\r', '\f', '\v');
            bool plain = trimmed.Length == 0 || trimmed[0] == '#';
            if (block is not null)
            {
                if (plain) block.Lines.Add(new MpdConfigLine(text, ending, number));
                else if (trimmed[0] == '}') block = Close(block, text, ending, number, trimmed);
                else block.Lines.Add(ParseSettingLine(text, ending, number));
                continue;
            }
            if (plain)
            {
                entries.Add(new MpdConfigLine(text, ending, number));
                continue;
            }
            string indentless = trimmed;
            int nameEnd = MpdConfigSyntax.ReadName(indentless, 0, out string name, out string? nameError);
            if (nameEnd < 0)
            {
                entries.Add(Report(new MpdConfigLine(text, ending, number), nameError!));
                continue;
            }
            int afterSpace = nameEnd;
            while (afterSpace < indentless.Length && MpdConfigSyntax.IsSpace(indentless[afterSpace])) afterSpace++;
            if (afterSpace < indentless.Length && indentless[afterSpace] == '{')
            {
                MpdConfigLine open = new(text, ending, number);
                string rest = indentless[(afterSpace + 1)..].TrimStart(' ', '\t', '\r', '\f', '\v');
                if (rest.Length > 0 && rest[0] != '#') Report(open, "Unknown tokens after '{'");
                block = new MpdConfigBlock(name, open);
                entries.Add(block);
                continue;
            }
            entries.Add(ParseSettingLine(text, ending, number));
        }
        if (block is not null) Report(block.Open, "Expected '}' before end-of-file");
    }

    private MpdConfigBlock? Close(MpdConfigBlock block, string text, string ending, int number, string trimmed)
    {
        MpdConfigLine close = new(text, ending, number);
        string rest = trimmed[1..].TrimStart(' ', '\t', '\r', '\f', '\v');
        if (rest.Length > 0 && rest[0] != '#') Report(close, "Unknown tokens after '}'");
        block.Close = close;
        return null;
    }

    private MpdConfigLine Report(MpdConfigLine line, string message)
    {
        line.Error = message;
        diagnostics.Add(new MpdConfigDiagnostic(line.Number, MpdConfigSeverity.Error, message));
        return line;
    }

    private MpdConfigLine ParseSettingLine(string text, string ending, int number)
    {
        int start = 0;
        while (start < text.Length && MpdConfigSyntax.IsSpace(text[start])) start++;
        int nameEnd = MpdConfigSyntax.ReadName(text, start, out string name, out string? error);
        if (nameEnd < 0) return Fail(text, ending, number, error!);
        int index = nameEnd;
        while (index < text.Length && MpdConfigSyntax.IsSpace(text[index])) index++;
        string? value = null;
        int valueStart = -1;
        int valueLength = 0;
        if (index >= text.Length || text[index] == '#')
        {
            bool optionalValue = MpdSettingCatalog.FindSetting(name)?.Repeatable == true;
            if (!optionalValue) return Fail(text, ending, number, "Value missing");
        }
        else
        {
            int end = MpdConfigSyntax.ReadQuoted(text, index, out string parsed, out error);
            if (end < 0) return Fail(text, ending, number, error!);
            value = parsed;
            valueStart = index;
            valueLength = end - index;
            int tail = end;
            while (tail < text.Length && MpdConfigSyntax.IsSpace(text[tail])) tail++;
            if (tail < text.Length && text[tail] != '#') return Fail(text, ending, number, "Unknown tokens after value");
        }
        return new MpdConfigSetting(text, ending, number, name, value, valueStart, valueLength);
    }

    private MpdConfigLine Fail(string text, string ending, int number, string message) =>
        Report(new MpdConfigLine(text, ending, number), message);

    /// <summary>The whole file as text. An unedited document gives back exactly what was read.</summary>
    public string Serialize()
    {
        StringBuilder builder = new();
        if (hasByteOrderMark) builder.Append(ByteOrderMark);
        foreach (MpdConfigEntry entry in entries) Append(builder, entry);
        return builder.ToString();
    }

    public override string ToString() => Serialize();

    private static void Append(StringBuilder builder, MpdConfigEntry entry)
    {
        switch (entry)
        {
            case MpdConfigLine line:
                builder.Append(line.Text).Append(line.LineEnding);
                break;
            case MpdConfigBlock block:
                builder.Append(block.Open.Text).Append(block.Open.LineEnding);
                foreach (MpdConfigLine line in block.Lines) builder.Append(line.Text).Append(line.LineEnding);
                if (block.Close is { } close) builder.Append(close.Text).Append(close.LineEnding);
                break;
        }
    }

    /// <summary>The top-level settings, in file order.</summary>
    public IEnumerable<MpdConfigSetting> Settings => entries.OfType<MpdConfigSetting>();

    /// <summary>The <c>include</c> and <c>include_optional</c> lines. They are kept as they are and never merged into the file.</summary>
    public IEnumerable<MpdConfigSetting> Includes => Settings.Where(setting => setting.Name is "include" or "include_optional");

    public IEnumerable<MpdConfigBlock> Blocks => entries.OfType<MpdConfigBlock>();

    public IEnumerable<MpdConfigBlock> BlocksNamed(string name) => Blocks.Where(block => block.Name == name);

    /// <summary>The effective value of a setting: the last one in the file, as MPD uses. <c>null</c> when absent.</summary>
    public string? Get(string name) => Settings.LastOrDefault(setting => setting.Name == name)?.Value;

    /// <summary>The values of a repeatable setting. A line with no value clears the ones before it, as MPD does.</summary>
    public IReadOnlyList<string> GetAll(string name)
    {
        List<string> values = [];
        foreach (MpdConfigSetting setting in Settings.Where(item => item.Name == name))
        {
            if (setting.Value is null) values.Clear();
            else values.Add(setting.Value);
        }
        return values;
    }

    /// <summary>
    /// Gives a setting one value. The last line that sets it is changed, or a line is added after the other top-level settings.
    /// <c>null</c> removes the setting; commented-out examples are left alone.
    /// </summary>
    public void Set(string name, string? value)
    {
        MpdConfigSyntax.RequireName(name);
        List<MpdConfigSetting> existing = [.. Settings.Where(setting => setting.Name == name)];
        if (value is null)
        {
            foreach (MpdConfigSetting setting in existing) entries.Remove(setting);
            return;
        }
        if (existing.Count > 0)
        {
            existing[^1].Value = value;
            return;
        }
        Insert(new MpdConfigSetting(string.Empty, name, value, NewLine), AfterLastSetting());
    }

    /// <summary>Replaces all lines of a repeatable setting with <paramref name="values"/>, reusing the existing lines in order.</summary>
    public void SetAll(string name, IReadOnlyList<string> values)
    {
        MpdConfigSyntax.RequireName(name);
        ArgumentNullException.ThrowIfNull(values);
        List<MpdConfigSetting> existing = [.. Settings.Where(setting => setting.Name == name)];
        for (int index = 0; index < existing.Count; index++)
        {
            if (index < values.Count) existing[index].Value = values[index];
            else entries.Remove(existing[index]);
        }
        int kept = Math.Min(existing.Count, values.Count);
        int position = kept > 0 ? entries.IndexOf(existing[kept - 1]) + 1 : AfterLastSetting();
        for (int index = kept; index < values.Count; index++)
            Insert(new MpdConfigSetting(string.Empty, name, values[index], NewLine), position++);
    }

    /// <summary>Adds a block at the end of the file.</summary>
    public MpdConfigBlock AddBlock(string name)
    {
        MpdConfigSyntax.RequireName(name);
        EnsureLastLineEnds();
        if (entries.Count > 0 && !LastLineIsBlank()) entries.Add(new MpdConfigLine(string.Empty, NewLine, 0));
        MpdConfigBlock block = new(name, new MpdConfigLine($"{name} {{", NewLine, 0)) { Close = new MpdConfigLine("}", NewLine, 0) };
        entries.Add(block);
        return block;
    }

    /// <summary>Removes a block, and the blank line that separated it, so adding and removing a block leaves the file as it was. Comments above it stay.</summary>
    public bool Remove(MpdConfigBlock block)
    {
        int index = entries.IndexOf(block);
        if (index < 0) return false;
        entries.RemoveAt(index);
        if (index > 0 && entries[index - 1] is MpdConfigLine { IsBlank: true } && (index >= entries.Count || entries[index] is MpdConfigLine { IsBlank: true }))
            entries.RemoveAt(index - 1);
        return true;
    }

    /// <summary>Removes one top-level setting line.</summary>
    public bool Remove(MpdConfigSetting setting) => entries.Remove(setting);

    /// <summary>Swaps a block with the nearest block of the same name before it (<paramref name="offset"/> -1) or after it (+1).</summary>
    public bool Move(MpdConfigBlock block, int offset)
    {
        int from = entries.IndexOf(block);
        if (from < 0 || offset == 0) return false;
        int step = Math.Sign(offset);
        for (int index = from + step; index >= 0 && index < entries.Count; index += step)
        {
            if (entries[index] is not MpdConfigBlock other || other.Name != block.Name) continue;
            (entries[from], entries[index]) = (entries[index], entries[from]);
            return true;
        }
        return false;
    }

    private bool LastLineIsBlank() => entries[^1] is MpdConfigLine { IsBlank: true };

    private int AfterLastSetting()
    {
        int last = entries.FindLastIndex(entry => entry is MpdConfigSetting);
        if (last >= 0) return last + 1;
        int firstBlock = entries.FindIndex(entry => entry is MpdConfigBlock);
        return firstBlock >= 0 ? firstBlock : entries.Count;
    }

    private void Insert(MpdConfigEntry entry, int position)
    {
        if (position >= entries.Count) EnsureLastLineEnds();
        entries.Insert(position, entry);
    }

    private void EnsureLastLineEnds()
    {
        MpdConfigLine? last = entries.Count == 0 ? null : entries[^1] switch
        {
            MpdConfigLine line => line,
            MpdConfigBlock block => block.Close ?? block.Lines.LastOrDefault() ?? block.Open,
            _ => null
        };
        if (last is { LineEnding.Length: 0 }) last.LineEnding = NewLine;
    }

    /// <summary>
    /// The problems MPD itself would report: unknown settings (a warning, since a newer MPD may know more than the catalog), a block that may appear once but appears twice, a setting repeated inside
    /// a block, and the read errors found while parsing.
    /// </summary>
    public IReadOnlyList<MpdConfigDiagnostic> Validate()
    {
        List<MpdConfigDiagnostic> found = [.. diagnostics];
        foreach (MpdConfigSetting setting in Settings)
        {
            if (setting.Name is "include" or "include_optional")
            {
                if (setting.Value is null) found.Add(new MpdConfigDiagnostic(setting.Number, MpdConfigSeverity.Error, "Value missing"));
                continue;
            }
            MpdSettingDefinition? definition = MpdSettingCatalog.FindSetting(setting.Name);
            if (definition is null)
                found.Add(new MpdConfigDiagnostic(setting.Number, MpdConfigSeverity.Warning, $"Unrecognized parameter: {setting.Name}; this MPD version may not accept it"));
            else if (definition.Deprecated)
                found.Add(new MpdConfigDiagnostic(setting.Number, MpdConfigSeverity.Warning, $"{setting.Name} is deprecated"));
        }
        foreach (IGrouping<string, MpdConfigBlock> group in Blocks.GroupBy(block => block.Name))
        {
            MpdBlockDefinition? definition = MpdSettingCatalog.FindBlock(group.Key);
            if (definition is null)
            {
                foreach (MpdConfigBlock block in group)
                    found.Add(new MpdConfigDiagnostic(block.Number, MpdConfigSeverity.Warning, $"Unrecognized parameter: {block.Name}; this MPD version may not accept it"));
                continue;
            }
            if (!definition.Repeatable && group.Count() > 1)
                found.Add(new MpdConfigDiagnostic(group.ElementAt(1).Number, MpdConfigSeverity.Error, $"{group.Key} may appear only once"));
            foreach (MpdConfigBlock block in group)
            {
                if (definition.Deprecated) found.Add(new MpdConfigDiagnostic(block.Number, MpdConfigSeverity.Warning, $"{block.Name} is deprecated"));
                foreach (IGrouping<string, MpdConfigSetting> duplicate in block.Settings.GroupBy(setting => setting.Name).Where(item => item.Count() > 1))
                    found.Add(new MpdConfigDiagnostic(duplicate.ElementAt(1).Number, MpdConfigSeverity.Error, $"{duplicate.Key} is duplicate"));
            }
        }
        return found;
    }
}
