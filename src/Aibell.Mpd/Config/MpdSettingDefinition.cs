namespace Aibell.Mpd.Config;

/// <summary>How a setting's value is edited.</summary>
public enum MpdValueKind
{
    Text,
    Path,
    Boolean,
    WholeNumber,
    RealNumber,
    Choice,
    /// <summary><c>samplerate:bits:channels</c>, each part a number or <c>*</c>.</summary>
    AudioFormat,
    Secret,
}

/// <summary>The sections the settings editor groups top-level settings into.</summary>
public enum MpdSettingGroup
{
    Files,
    Network,
    Permissions,
    Audio,
    ReplayGain,
    Database,
    Limits,
    Process,
    Metadata,
    Zeroconf,
    Proxy,
}

/// <param name="OpenChoices">True when <see cref="Choices"/> are suggestions only (a newer MPD may add more), so any text is accepted.</param>
/// <summary>One setting MPD understands, for a top-level line or a line inside a block.</summary>
public sealed record MpdSettingDefinition(
    string Name,
    MpdValueKind Kind = MpdValueKind.Text,
    MpdSettingGroup Group = MpdSettingGroup.Files,
    string? Default = null,
    IReadOnlyList<string>? Choices = null,
    double? Minimum = null,
    double? Maximum = null,
    bool Repeatable = false,
    bool Deprecated = false,
    bool OpenChoices = false);

/// <summary>A plugin that can be selected inside a block, with the settings it adds.</summary>
public sealed record MpdPluginDefinition(string Name, IReadOnlyList<MpdSettingDefinition> Fields);

/// <summary>A block MPD understands, such as <c>audio_output</c>.</summary>
/// <param name="PluginKey">The setting inside the block that names the plugin (<c>type</c>, <c>plugin</c> or <c>name</c>), or <c>null</c> when there are no plugins.</param>
/// <param name="CommonFields">The settings that every plugin of the block accepts, besides the plugin key itself.</param>
public sealed record MpdBlockDefinition(
    string Name,
    bool Repeatable,
    string? PluginKey,
    IReadOnlyList<MpdSettingDefinition> CommonFields,
    IReadOnlyList<MpdPluginDefinition> Plugins,
    bool Deprecated = false)
{
    public MpdPluginDefinition? FindPlugin(string? name) => Plugins.FirstOrDefault(plugin => plugin.Name == name);
}
