using System.Globalization;
using System.Text.RegularExpressions;

namespace Aibell.Mpd.Config.Editing;

/// <summary>Edits one setting of the document: reads its text, writes it back, and says whether the text is something MPD accepts.</summary>
public sealed partial class MpdFieldEditor
{
    private readonly Func<string?> read;
    private readonly Action<string?> write;
    private readonly Action changed;

    internal MpdFieldEditor(MpdSettingDefinition definition, Func<string?> read, Action<string?> write, Action changed)
    {
        Definition = definition;
        this.read = read;
        this.write = write;
        this.changed = changed;
    }

    public MpdSettingDefinition Definition { get; }

    public string Name => Definition.Name;

    /// <summary>The text in the file, or <c>null</c> when the setting is not set (MPD then uses its default).</summary>
    public string? Value => read();

    public bool IsSet => Value is not null;

    /// <summary>What MPD does when the setting is absent, for display next to an empty field.</summary>
    public string? Default => Definition.Default;

    /// <summary>Why the current text would be rejected, or <c>null</c>.</summary>
    public string? Error => Validate(Definition, Value);

    /// <summary>Sets the text. <c>null</c> or empty removes the setting, so the file shows only what was chosen.</summary>
    public void SetValue(string? text)
    {
        string? next = string.IsNullOrEmpty(text) ? null : text;
        if (string.Equals(next, Value, StringComparison.Ordinal)) return;
        write(next);
        changed();
    }

    /// <summary>Sets the text as given, including an empty string, which some settings (an empty permission list) accept as a value.</summary>
    public void SetRaw(string? text)
    {
        if (string.Equals(text, Value, StringComparison.Ordinal)) return;
        write(text);
        changed();
    }

    /// <summary>The booleans as a three-state choice: <c>null</c> for not set.</summary>
    public bool? BooleanValue => MpdConfigSyntax.ParseBoolean(Value);

    public void SetBoolean(bool? value) => SetValue(value is null ? null : value.Value ? "yes" : "no");

    public static string? Validate(MpdSettingDefinition definition, string? text)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (text is null) return null;
        if (text.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0) return "LineBreak";
        switch (definition.Kind)
        {
            case MpdValueKind.WholeNumber:
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole)) return "NotNumber";
                return OutOfRange(definition, whole) ? "OutOfRange" : null;
            case MpdValueKind.RealNumber:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) || double.IsNaN(real) || double.IsInfinity(real)) return "NotNumber";
                return OutOfRange(definition, real) ? "OutOfRange" : null;
            case MpdValueKind.Boolean:
                return MpdConfigSyntax.ParseBoolean(text) is null ? "NotYesNo" : null;
            case MpdValueKind.Choice:
                return definition.OpenChoices || definition.Choices?.Contains(text, StringComparer.Ordinal) == true ? null : "NotChoice";
            case MpdValueKind.AudioFormat:
                return AudioFormatPattern().IsMatch(text) ? null : "NotAudioFormat";
            default:
                return null;
        }
    }

    private static bool OutOfRange(MpdSettingDefinition definition, double value) =>
        definition.Minimum is { } minimum && value < minimum || definition.Maximum is { } maximum && value > maximum;

    // samplerate:bits:channels, each part a number or *; or the short DSD form such as dsd64:2.
    [GeneratedRegex(@"^(?:(?:\*|\d+|dsd\d*)?:(?:\*|8|16|24|32|f|dsd)?:(?:\*|\d+)?|dsd\d*:\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AudioFormatPattern();
}
