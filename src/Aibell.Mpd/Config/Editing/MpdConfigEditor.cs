namespace Aibell.Mpd.Config.Editing;

/// <summary>
/// The model behind the settings window: one configuration file, its settings grouped as the window shows them, and load and save.
/// Every edit goes straight into the <see cref="MpdConfigDocument"/>, so the raw text is always current; <see cref="Changed"/> tells the
/// window to refresh its summary.
/// </summary>
public sealed class MpdConfigEditor
{
    private string baseline = string.Empty;

    public MpdConfigEditor() => Document = MpdConfigDocument.CreateEmpty();

    public MpdConfigDocument Document { get; private set; }

    /// <summary>The file being edited, or <c>null</c> for a new, unsaved one.</summary>
    public string? Path { get; private set; }

    /// <summary>True when this user cannot write the file; the window then offers Save As only.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>True when the text differs from what was last loaded or saved.</summary>
    public bool IsDirty => !string.Equals(Document.Serialize(), baseline, StringComparison.Ordinal);

    /// <summary>Raised after any edit.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the whole document was replaced (load, new, or applied raw text), so the window must rebuild its controls.</summary>
    public event EventHandler? Reloaded;

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Load(string path)
    {
        Document = MpdConfigFile.Load(path);
        Path = path;
        IsReadOnly = !MpdConfigFile.CanWrite(path);
        baseline = Document.Serialize();
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts an empty file that will be saved at <paramref name="path"/>.</summary>
    public void StartNew(string path)
    {
        Document = MpdConfigDocument.CreateEmpty();
        Path = path;
        IsReadOnly = !MpdConfigFile.CanWrite(path);
        baseline = string.Empty;
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The file as text.</summary>
    public string Text => Document.Serialize();

    /// <summary>
    /// Replaces the document with edited text; the baseline stays, so the window still knows there are unsaved changes. With
    /// <paramref name="reload"/> false only <see cref="Changed"/> is raised, for a window that is itself showing the text and does not
    /// want its controls rebuilt on every keystroke.
    /// </summary>
    public void SetText(string text, bool reload = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.Equals(text, Text, StringComparison.Ordinal)) return;
        Document = MpdConfigDocument.Parse(text);
        if (reload) Reloaded?.Invoke(this, EventArgs.Empty);
        OnChanged();
    }

    /// <summary>The settings of a group that take one value, as editors.</summary>
    public IReadOnlyList<MpdFieldEditor> Fields(MpdSettingGroup group) =>
        [.. MpdSettingCatalog.Settings.Where(item => item.Group == group && !item.Repeatable).Select(item => new MpdFieldEditor(
            item, () => Document.Get(item.Name), value => Document.Set(item.Name, value), OnChanged))];

    /// <summary>The lines of a repeatable setting (<c>bind_to_address</c>, <c>password</c>, <c>host_permissions</c>).</summary>
    public IReadOnlyList<string> Lines(string name) => Document.GetAll(name);

    public void SetLines(string name, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string[] cleaned = [.. values.Select(value => value.Trim()).Where(value => value.Length > 0)];
        if (cleaned.SequenceEqual(Document.GetAll(name), StringComparer.Ordinal)) return;
        Document.SetAll(name, cleaned);
        OnChanged();
    }

    public IReadOnlyList<MpdBlockEditor> Blocks(string blockName) =>
        [.. Document.BlocksNamed(blockName).Select(block => new MpdBlockEditor(block, MpdSettingCatalog.FindBlock(blockName), OnChanged))];

    /// <summary>Adds a block, with its plugin selected when it has plugins.</summary>
    public MpdBlockEditor AddBlock(string blockName, string? plugin = null)
    {
        MpdBlockDefinition? definition = MpdSettingCatalog.FindBlock(blockName);
        MpdConfigBlock block = Document.AddBlock(blockName);
        if (definition?.PluginKey is { } key && !string.IsNullOrEmpty(plugin)) block.Set(key, plugin);
        OnChanged();
        return new MpdBlockEditor(block, definition, OnChanged);
    }

    public void RemoveBlock(MpdBlockEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (Document.Remove(editor.Block)) OnChanged();
    }

    /// <summary>Moves a block before (-1) or after (+1) its neighbour of the same kind; the order of <c>audio_output</c> blocks is the order clients see.</summary>
    public bool MoveBlock(MpdBlockEditor editor, int offset)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!Document.Move(editor.Block, offset)) return false;
        OnChanged();
        return true;
    }

    /// <summary>Top-level settings the catalog does not know, so every line stays reachable.</summary>
    public IReadOnlyList<MpdConfigSetting> OtherSettings =>
        [.. Document.Settings.Where(setting => setting.Name is not "include" and not "include_optional" && MpdSettingCatalog.FindSetting(setting.Name) is null)];

    /// <summary>Adds or changes a top-level setting by name.</summary>
    public void SetOther(string name, string value)
    {
        Document.Set(name, value);
        OnChanged();
    }

    public void RemoveSetting(MpdConfigSetting setting)
    {
        if (Document.Remove(setting)) OnChanged();
    }

    /// <summary>The <c>include</c> and <c>include_optional</c> lines. They are shown and kept, never merged into this file.</summary>
    public IReadOnlyList<MpdConfigSetting> Includes => [.. Document.Includes];

    /// <summary>Blocks whose name MPD does not know; they stay in the file and are edited as text.</summary>
    public IReadOnlyList<MpdConfigBlock> UnknownBlocks => [.. Document.Blocks.Where(block => MpdSettingCatalog.FindBlock(block.Name) is null)];

    /// <summary>
    /// Everything that would stop MPD from starting or that it would warn about, plus the values of the editors that are out of range.
    /// </summary>
    public IReadOnlyList<MpdConfigDiagnostic> Problems
    {
        get
        {
            List<MpdConfigDiagnostic> problems = [.. Document.Validate()];
            foreach (MpdConfigSetting setting in Document.Settings)
                if (MpdSettingCatalog.FindSetting(setting.Name) is { } definition && MpdFieldEditor.Validate(definition, setting.Value) is { } code)
                    problems.Add(new MpdConfigDiagnostic(setting.Number, MpdConfigSeverity.Error, $"{setting.Name}: {code}", code, setting.Name));
            foreach (MpdConfigBlock block in Document.Blocks)
            {
                if (MpdSettingCatalog.FindBlock(block.Name) is not { } blockDefinition) continue;
                MpdBlockEditor editor = new(block, blockDefinition, OnChanged);
                foreach (MpdFieldEditor item in editor.Fields.Where(item => item.Error is not null))
                    problems.Add(new MpdConfigDiagnostic(block.Number, MpdConfigSeverity.Error, $"{block.Name}/{item.Name}: {item.Error}", item.Error, $"{block.Name}/{item.Name}"));
                if (blockDefinition.PluginKey is { } key && block.Get(key) is null)
                    problems.Add(new MpdConfigDiagnostic(block.Number, MpdConfigSeverity.Error, $"{block.Name}: {key} is missing", "PluginMissing", $"{block.Name}/{key}"));
            }
            return problems;
        }
    }

    /// <summary>True when nothing in <see cref="Problems"/> is an error.</summary>
    public bool CanSave => !Problems.Any(problem => problem.Severity == MpdConfigSeverity.Error);

    /// <summary>Writes the file at <see cref="Path"/>, keeping a backup of what was there.</summary>
    /// <returns>The path of the backup, or <c>null</c> when there was nothing to back up.</returns>
    public string? Save()
    {
        if (Path is null) throw new InvalidOperationException("Choose a file first.");
        return SaveAs(Path);
    }

    /// <summary>Writes the file somewhere else and continues editing that file.</summary>
    public string? SaveAs(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!CanSave) throw new InvalidOperationException("The configuration has errors that would stop MPD from starting.");
        string? backup = MpdConfigFile.Save(Document, path);
        Path = path;
        IsReadOnly = false;
        baseline = Document.Serialize();
        OnChanged();
        return backup;
    }
}
