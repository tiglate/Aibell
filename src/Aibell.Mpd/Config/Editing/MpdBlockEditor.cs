namespace Aibell.Mpd.Config.Editing;

/// <summary>Edits one <c>name { ... }</c> block: the plugin it uses, that plugin's settings, and any setting the catalog does not know.</summary>
public sealed class MpdBlockEditor
{
    private readonly Action changed;

    internal MpdBlockEditor(MpdConfigBlock block, MpdBlockDefinition? definition, Action changed)
    {
        Block = block;
        Definition = definition;
        this.changed = changed;
    }

    public MpdConfigBlock Block { get; }

    public MpdBlockDefinition? Definition { get; }

    public string BlockName => Block.Name;

    /// <summary>The setting that names the plugin (<c>type</c>, <c>plugin</c> or <c>name</c>), or <c>null</c> for blocks without plugins.</summary>
    public string? PluginKey => Definition?.PluginKey;

    /// <summary>The selected plugin, or <c>null</c> when none is written.</summary>
    public string? Plugin => PluginKey is { } key ? Block.Get(key) : null;

    /// <summary>The encoder of an output that streams or records (<c>vorbis</c> for <c>shout</c> when none is written), or <c>null</c>.</summary>
    public string? Encoder => BlockName == "audio_output" ? Block.Get("encoder") ?? (Plugin == "shout" ? "vorbis" : null) : null;

    /// <summary>A short label for lists: the output's name and plugin, or the plugin alone.</summary>
    public string Title
    {
        get
        {
            string? label = BlockName is "audio_output" or "filter" or "partition" ? Block.Get("name") : null;
            string? plugin = Plugin;
            if (BlockName == "playlist_plugin" || BlockName == "archive_plugin") return plugin ?? BlockName;
            if (plugin is not null && label is not null) return $"{label} ({plugin})";
            return label ?? plugin ?? BlockName;
        }
    }

    public void SetPlugin(string? plugin)
    {
        if (PluginKey is not { } key || string.Equals(plugin, Plugin, StringComparison.Ordinal)) return;
        Block.Set(key, string.IsNullOrEmpty(plugin) ? null : plugin);
        changed();
    }

    /// <summary>The settings to offer for the current plugin, as editors on this block. Rebuild after the plugin or encoder changes.</summary>
    public IReadOnlyList<MpdFieldEditor> Fields
    {
        get
        {
            if (Definition is null) return [];
            return [.. MpdSettingCatalog.FieldsFor(Definition, Plugin, Encoder).Select(definition => new MpdFieldEditor(
                definition,
                () => Block.Get(definition.Name),
                value => Block.Set(definition.Name, value),
                changed))];
        }
    }

    /// <summary>Settings in the block that are neither the plugin name nor a setting the catalog offers for it, in file order.</summary>
    public IReadOnlyList<MpdConfigSetting> OtherSettings
    {
        get
        {
            HashSet<string> known = [.. Fields.Select(item => item.Name)];
            if (PluginKey is { } key) known.Add(key);
            return [.. Block.Settings.Where(setting => !known.Contains(setting.Name))];
        }
    }

    /// <summary>Adds or changes any setting by name, for settings the catalog does not list.</summary>
    public void SetOther(string name, string value)
    {
        Block.Set(name, value);
        changed();
    }

    public void RemoveSetting(MpdConfigSetting setting)
    {
        if (Block.Remove(setting)) changed();
    }
}
