using Aibell.Desktop.Localization;
using Aibell.Desktop.Services;
using Aibell.Mpd.Config;
using Aibell.Mpd.Config.Editing;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Aibell.Desktop;

/// <summary>
/// Edits the configuration file of an MPD that runs on this computer. Every control is generated from <see cref="MpdSettingCatalog"/>, so a
/// setting MPD supports is always reachable; setting names are MPD's own identifiers and are shown as they are. Edits go straight into
/// the text of the file (see <see cref="MpdConfigEditor"/>), so comments, order and unknown lines survive, and nothing is written until Save.
/// </summary>
public sealed class MpdConfigWindow : Window
{
    private readonly MpdConfigEditor editor = new();
    private readonly IDesktopFilePicker? picker;
    private readonly MpdServiceController service;
    private readonly string defaultPath;
    private readonly Func<CancellationToken, Task<MpdVersionInfo?>> discover;
    private MpdVersionInfo? installed;
    private bool canRestart;
    private bool busy;
    private bool suppress;
    private bool rawEdited;
    private string syncedText = "";

    private readonly TextBlock pathText = new() { Name = "MpdConfigPath", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock notice = new() { Name = "MpdConfigNotice", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock status = new() { Name = "MpdConfigStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 0, 18, 6) };
    private readonly ListBox problems = new() { Name = "MpdConfigProblems", MaxHeight = 90, Margin = new Thickness(18, 0, 18, 6) };
    private readonly TabControl tabs = new() { Name = "MpdConfigTabs", Margin = new Thickness(12, 6, 12, 0) };
    private TextBox rawText = NewTextBox();
    private readonly Button save = new() { Name = "SaveMpdConfig", Width = 92, Height = 30, Classes = { "dialogAccept" } };
    private readonly Button saveAs = new() { Name = "SaveMpdConfigAs", Height = 30, MinWidth = 92 };
    private readonly Button restart = new() { Name = "RestartMpd", Height = 30, MinWidth = 110, IsVisible = false };
    private readonly Button open = new() { Name = "OpenMpdConfig", Height = 30 };
    private readonly Button reload = new() { Name = "ReloadMpdConfig", Height = 30 };
    private readonly Button create = new() { Name = "NewMpdConfig", Height = 30 };

    private static TextBox NewTextBox() => new() { Name = "MpdConfigText", AcceptsReturn = true, FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.NoWrap };

    public MpdConfigWindow(IDesktopFilePicker? picker = null, MpdServiceController? service = null, string? configPath = null,
        Func<CancellationToken, Task<MpdVersionInfo?>>? discoverVersion = null)
    {
        discover = discoverVersion ?? (token => MpdVersionInfo.DiscoverAsync("mpd", token));
        this.picker = picker;
        this.service = service ?? new MpdServiceController();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        defaultPath = MpdConfigFile.DefaultNewPath(configHome, home);
        Name = "MpdConfigWindow"; Title = Loc.Get("MpdConfig.Title"); Width = 860; Height = 720; MinWidth = 640; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");

        save.Content = Loc.Get("Dialog.Save");
        saveAs.Content = Loc.Get("MpdConfig.SaveAs");
        restart.Content = Loc.Get("MpdConfig.Restart");
        open.Content = Loc.Get("MpdConfig.Open");
        reload.Content = Loc.Get("MpdConfig.Reload");
        create.Content = Loc.Get("MpdConfig.NewFile");
        Button close = new() { Content = Loc.Get("Common.Close"), Width = 92, Height = 30 };

        Grid fileBar = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(18, 12, 18, 4) };
        StackPanel names = new() { Spacing = 2 };
        names.Children.Add(new TextBlock { Text = Loc.Get("MpdConfig.File"), FontSize = 11 });
        names.Children.Add(pathText);
        fileBar.Children.Add(names);
        Grid.SetColumn(open, 1); fileBar.Children.Add(open);
        Grid.SetColumn(reload, 2); fileBar.Children.Add(reload);
        Grid.SetColumn(create, 3); fileBar.Children.Add(create);
        notice.Margin = new Thickness(18, 0, 18, 4);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(restart); buttons.Children.Add(saveAs); buttons.Children.Add(save); buttons.Children.Add(close);
        Border footer = new() { Child = buttons, Padding = new Thickness(14, 10), [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush") };
        StackPanel top = new();
        top.Children.Add(fileBar); top.Children.Add(notice);
        StackPanel bottom = new();
        bottom.Children.Add(problems); bottom.Children.Add(status);
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,51") };
        layout.Children.Add(top);
        Grid.SetRow(tabs, 1); layout.Children.Add(tabs);
        Grid.SetRow(bottom, 2); layout.Children.Add(bottom);
        Grid.SetRow(footer, 3); layout.Children.Add(footer);
        Content = DialogChrome.Wrap(this, layout, () => !busy);

        tabs.SelectionChanged += (_, args) =>
        {
            if (suppress || !rawEdited || args.Source != tabs || tabs.SelectedItem is TabItem { Name: "MpdTextTab" }) return;
            Rebuild();
        };
        editor.Reloaded += (_, _) => Rebuild();
        editor.Changed += (_, _) => Refresh();
        save.Click += async (_, _) => await SaveAsync(editor.Path);
        saveAs.Click += async (_, _) => await SaveAsAsync();
        open.Click += async (_, _) => await OpenAsync();
        reload.Click += async (_, _) => await ReloadAsync();
        create.Click += async (_, _) => await NewAsync();
        restart.Click += async (_, _) => await RestartAsync();
        close.Click += (_, _) => Close();
        Closing += OnClosing;
        KeyDown += (_, args) => { if (args.Key == Key.Escape && !busy) { args.Handled = true; Close(); } };
        Opened += async (_, _) => await InitializeAsync(configPath);
    }

    private bool closeConfirmed;

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (busy) { args.Cancel = true; return; }
        ApplyRawText();
        if (closeConfirmed || !editor.IsDirty) return;
        args.Cancel = true;
        if (await ConfirmDiscardAsync()) { closeConfirmed = true; Close(); }
    }

    private async Task InitializeAsync(string? configPath)
    {
        string? path = configPath ?? MpdConfigFile.FindExisting(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (path is not null && File.Exists(path)) LoadFile(path);
        else if (path is not null) editor.StartNew(path);
        else
        {
            Rebuild();
            status.Text = Loc.Format("MpdConfig.NoFileFound", defaultPath);
        }
        installed = await discover(CancellationToken.None);
        if (installed is not null) Rebuild();
        canRestart = await service.IsUserServiceAvailableAsync();
        Refresh();
    }

    private void LoadFile(string path)
    {
        try
        {
            editor.Load(path);
            status.Text = "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status.Text = Loc.Format("MpdConfig.LoadFailed", path, exception.Message);
        }
    }

    // File actions

    private async Task<bool> ConfirmDiscardAsync() =>
        !editor.IsDirty || await DialogChrome.ConfirmAsync(this, Loc.Get("MpdConfig.UnsavedTitle"),
            Loc.Format("MpdConfig.UnsavedPrompt", editor.Path ?? Loc.Get("MpdConfig.Untitled")), Loc.Get("MpdConfig.Discard"), "ConfirmDiscardMpdConfig");

    private async Task OpenAsync()
    {
        if (picker is null || busy || !await ConfirmDiscardAsync()) return;
        Uri? chosen = await picker.PickFileAsync(this, new FilePickerOpenOptions
        {
            Title = Loc.Get("MpdConfig.Open"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc.Get("MpdConfig.ConfigFiles")) { Patterns = ["*.conf", "mpd.conf", "*"] }]
        });
        if (chosen is { IsFile: true }) LoadFile(chosen.LocalPath);
    }

    private async Task ReloadAsync()
    {
        if (editor.Path is not { } path || busy || !await ConfirmDiscardAsync()) return;
        if (File.Exists(path)) LoadFile(path);
        else editor.StartNew(path);
    }

    private async Task NewAsync()
    {
        if (busy || !await ConfirmDiscardAsync()) return;
        editor.StartNew(defaultPath);
        status.Text = "";
    }

    private async Task SaveAsAsync()
    {
        if (picker is null || busy) return;
        Uri? chosen = await picker.PickSaveFileAsync(this, new FilePickerSaveOptions
        {
            Title = Loc.Get("MpdConfig.SaveAs"),
            SuggestedFileName = "mpd.conf",
            DefaultExtension = "conf",
            ShowOverwritePrompt = true
        });
        if (chosen is { IsFile: true }) await SaveAsync(chosen.LocalPath);
    }

    private async Task SaveAsync(string? path)
    {
        if (path is null) { await SaveAsAsync(); return; }
        ApplyRawText();
        busy = true;
        try
        {
            // Small file, written on the UI thread: saving raises Changed, which refreshes controls.
            string? backup = editor.SaveAs(path);
            status.Text = backup is null ? Loc.Format("MpdConfig.Saved", path) : Loc.Format("MpdConfig.SavedBackup", path, backup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            status.Text = Loc.Format("MpdConfig.SaveFailed", exception.Message);
        }
        finally
        {
            busy = false;
            Refresh();
        }
    }

    private async Task RestartAsync()
    {
        if (busy || editor.IsDirty) return;
        if (!await DialogChrome.ConfirmAsync(this, Loc.Get("MpdConfig.Restart"), Loc.Get("MpdConfig.RestartConfirm"), Loc.Get("MpdConfig.Restart"), "ConfirmRestartMpd")) return;
        busy = true;
        restart.IsEnabled = false;
        try
        {
            string problem = await service.RestartAsync();
            status.Text = problem.Length == 0 ? Loc.Get("MpdConfig.RestartDone") : Loc.Format("MpdConfig.RestartFailed", problem);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            status.Text = Loc.Format("MpdConfig.RestartFailed", exception.Message);
        }
        finally
        {
            busy = false;
            Refresh();
        }
    }

    /// <summary>Feeds what is typed in the Text tab into the document, without rebuilding the other tabs until the user leaves it.</summary>
    private void ApplyRawText()
    {
        if (suppress || !rawEdited) return;
        editor.SetText(rawText.Text ?? "", reload: false);
    }

    // Summary

    private void Refresh()
    {
        string name = editor.Path ?? Loc.Get("MpdConfig.Untitled");
        pathText.Text = editor.IsDirty ? name + " *" : name;
        IReadOnlyList<MpdConfigDiagnostic> found = editor.Problems;
        problems.ItemsSource = found.Select(Describe).ToList();
        problems.IsVisible = found.Count > 0;
        bool saveable = editor.CanSave;
        save.IsEnabled = !busy && saveable && !editor.IsReadOnly && editor.Path is not null && editor.IsDirty;
        saveAs.IsEnabled = !busy && saveable && picker is not null;
        open.IsEnabled = !busy && picker is not null;
        reload.IsEnabled = !busy && editor.Path is not null;
        create.IsEnabled = !busy;
        restart.IsVisible = canRestart;
        restart.IsEnabled = !busy && !editor.IsDirty;
        List<string> notes = [Loc.Get("MpdConfig.RestartNote")];
        if (editor.IsReadOnly && editor.Path is not null) notes.Add(Loc.Get("MpdConfig.ReadOnly"));
        if (installed is { Version.Length: > 0 }) notes.Add(Loc.Format("MpdConfig.Installed", installed.Version));
        notice.Text = string.Join(" ", notes);
        if (!suppress && !rawEdited && !string.Equals(rawText.Text, editor.Text, StringComparison.Ordinal)) rawText.Text = syncedText = editor.Text;
    }

    private static string Describe(MpdConfigDiagnostic item)
    {
        string text = item.Code switch
        {
            "NotNumber" => Loc.Format("MpdConfig.Error.NotNumber", item.Subject),
            "OutOfRange" => Loc.Format("MpdConfig.Error.OutOfRange", item.Subject),
            "NotYesNo" => Loc.Format("MpdConfig.Error.NotYesNo", item.Subject),
            "NotChoice" => Loc.Format("MpdConfig.Error.NotChoice", item.Subject),
            "NotAudioFormat" => Loc.Format("MpdConfig.Error.NotAudioFormat", item.Subject),
            "LineBreak" => Loc.Format("MpdConfig.Error.LineBreak", item.Subject),
            "PluginMissing" => Loc.Format("MpdConfig.Error.PluginMissing", item.Subject),
            _ => item.Message,
        };
        return item.Line > 0 ? Loc.Format("MpdConfig.ProblemLine", item.Line, text) : text;
    }

    private static string? ErrorText(MpdFieldEditor field) => field.Error switch
    {
        null => null,
        "NotNumber" => Loc.Format("MpdConfig.Error.NotNumber", field.Name),
        "OutOfRange" => Loc.Format("MpdConfig.Error.OutOfRange", field.Name),
        "NotYesNo" => Loc.Format("MpdConfig.Error.NotYesNo", field.Name),
        "NotChoice" => Loc.Format("MpdConfig.Error.NotChoice", field.Name),
        "NotAudioFormat" => Loc.Format("MpdConfig.Error.NotAudioFormat", field.Name),
        _ => Loc.Format("MpdConfig.Error.LineBreak", field.Name),
    };

    // Building the tabs

    private void Rebuild()
    {
        int selected = Math.Max(0, tabs.SelectedIndex);
        rawEdited = false;
        suppress = true;
        try
        {
            TabItem text = new() { Name = "MpdTextTab", Header = Loc.Get("MpdConfig.TabText"), Content = BuildTextTab() };
            tabs.ItemsSource = new[]
            {
                Tab(Loc.Get("MpdConfig.TabFiles"), BuildFilesTab()),
                Tab(Loc.Get("MpdConfig.TabNetwork"), BuildNetworkTab()),
                Tab(Loc.Get("MpdConfig.TabAudio"), BuildAudioTab()),
                Tab(Loc.Get("MpdConfig.TabOutputs"), new BlockPanel(this, "audio_output")),
                Tab(Loc.Get("MpdConfig.TabPlugins"), BuildPluginsTab()),
                Tab(Loc.Get("MpdConfig.TabAdvanced"), BuildAdvancedTab()),
                text,
            };
            tabs.SelectedIndex = Math.Min(selected, 6);
        }
        finally
        {
            suppress = false;
        }
        Refresh();
    }

    private static TabItem Tab(string header, Control content) => new()
    {
        Header = header,
        Content = content is ScrollViewer ? content : new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
    };

    private static StackPanel Page() => new() { Spacing = 12, Margin = new Thickness(16) };

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) };

    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    private void AddGroup(StackPanel page, string heading, MpdSettingGroup group, Func<MpdFieldEditor, bool>? only = null)
    {
        page.Children.Add(Heading(heading));
        foreach (MpdFieldEditor field in editor.Fields(group).Where(item => only?.Invoke(item) != false)) page.Children.Add(BuildField(field));
    }

    private StackPanel BuildFilesTab()
    {
        StackPanel page = Page();
        AddGroup(page, Loc.Get("MpdConfig.GroupFiles"), MpdSettingGroup.Files);
        AddGroup(page, Loc.Get("MpdConfig.GroupDatabase"), MpdSettingGroup.Database);
        AddGroup(page, Loc.Get("MpdConfig.GroupMetadata"), MpdSettingGroup.Metadata, field => !field.Definition.Deprecated);
        return page;
    }

    private StackPanel BuildNetworkTab()
    {
        StackPanel page = Page();
        page.Children.Add(Heading(Loc.Get("MpdConfig.GroupNetwork")));
        page.Children.Add(BuildLines("bind_to_address"));
        foreach (MpdFieldEditor field in editor.Fields(MpdSettingGroup.Network)) page.Children.Add(BuildField(field));
        page.Children.Add(Heading(Loc.Get("MpdConfig.GroupPermissions")));
        page.Children.Add(Hint(Loc.Get("MpdConfig.PermissionsNote")));
        foreach (MpdFieldEditor field in editor.Fields(MpdSettingGroup.Permissions)) page.Children.Add(BuildPermissions(field));
        page.Children.Add(BuildLines("host_permissions"));
        page.Children.Add(BuildLines("password"));
        AddGroup(page, Loc.Get("MpdConfig.GroupZeroconf"), MpdSettingGroup.Zeroconf);
        return page;
    }

    private StackPanel BuildAudioTab()
    {
        StackPanel page = Page();
        AddGroup(page, Loc.Get("MpdConfig.GroupAudio"), MpdSettingGroup.Audio, field => !field.Definition.Deprecated);
        AddGroup(page, Loc.Get("MpdConfig.GroupReplayGain"), MpdSettingGroup.ReplayGain);
        return page;
    }

    private ScrollViewer BuildPluginsTab()
    {
        string[] blocks = ["decoder", "input", "input_cache", "archive_plugin", "playlist_plugin", "resampler", "filter", "database", "neighbors", "partition"];
        TabControl inner = new() { Name = "MpdPluginTabs", Margin = new Thickness(8) };
        inner.ItemsSource = blocks.Select(block => new TabItem { Header = block, Content = new BlockPanel(this, block) }).ToArray();
        return new ScrollViewer { Content = inner, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private StackPanel BuildAdvancedTab()
    {
        StackPanel page = Page();
        AddGroup(page, Loc.Get("MpdConfig.GroupDaemon"), MpdSettingGroup.Process);
        AddGroup(page, Loc.Get("MpdConfig.GroupLimits"), MpdSettingGroup.Limits);
        page.Children.Add(Heading(Loc.Get("MpdConfig.GroupDeprecated")));
        foreach (MpdFieldEditor field in editor.Fields(MpdSettingGroup.Audio).Concat(editor.Fields(MpdSettingGroup.Metadata)).Where(item => item.Definition.Deprecated)
                     .Concat(editor.Fields(MpdSettingGroup.Proxy)))
            page.Children.Add(BuildField(field));
        page.Children.Add(Heading(Loc.Get("MpdConfig.GroupOther")));
        page.Children.Add(Hint(Loc.Get("MpdConfig.OtherNote")));
        page.Children.Add(BuildOtherSettings(() => editor.OtherSettings, (name, value) => editor.SetOther(name, value), setting => editor.RemoveSetting(setting)));
        page.Children.Add(Heading(Loc.Get("MpdConfig.GroupIncludes")));
        page.Children.Add(Hint(Loc.Get("MpdConfig.IncludesNote")));
        foreach (MpdConfigSetting include in editor.Includes)
            page.Children.Add(new TextBlock { Text = $"{include.Name}  {include.Value}", FontFamily = new FontFamily("monospace"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (editor.UnknownBlocks.Count > 0)
            page.Children.Add(Hint(Loc.Format("MpdConfig.UnknownBlocks", string.Join(", ", editor.UnknownBlocks.Select(block => block.Name).Distinct()))));
        return page;
    }

    private Grid BuildTextTab()
    {
        // A fresh box each time: the old one still belongs to the grid of the tab that is being replaced.
        rawText = NewTextBox();
        rawText.Text = syncedText = editor.Text;
        rawText.TextChanged += (_, _) =>
        {
            // Avalonia reports TextChanged some time after Text was set, also for text this window set itself: only text that differs
            // from what was shown (or what the document holds) came from the user.
            string typed = rawText.Text ?? "";
            if (suppress || typed == syncedText || typed == editor.Text) return;
            rawEdited = true;
            ApplyRawText();
        };
        Grid grid = new() { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(12) };
        grid.Children.Add(Hint(Loc.Get("MpdConfig.TextNote")));
        Grid.SetRow(rawText, 1); grid.Children.Add(rawText);
        rawText.Margin = new Thickness(0, 8, 0, 0);
        return grid;
    }

    // Field controls

    internal Control BuildField(MpdFieldEditor field)
    {
        StackPanel row = new() { Spacing = 4, Name = "MpdField_" + field.Name };
        TextBlock label = new() { Text = field.Name, FontFamily = new FontFamily("monospace") };
        if (field.Definition.Deprecated) label.Opacity = 0.7;
        row.Children.Add(label);
        TextBlock error = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#D52268")), TextWrapping = TextWrapping.Wrap };
        void ShowError() { error.Text = ErrorText(field) ?? ""; error.IsVisible = error.Text.Length > 0; }
        Control input = field.Definition.Kind switch
        {
            MpdValueKind.Boolean => BuildBoolean(field, ShowError),
            MpdValueKind.Choice => BuildChoice(field, ShowError),
            MpdValueKind.Path => BuildPath(field, ShowError),
            _ => BuildText(field, ShowError, secret: field.Definition.Kind == MpdValueKind.Secret),
        };
        AutomationProperties.SetName(input, field.Name);
        row.Children.Add(input);
        row.Children.Add(error);
        ShowError();
        return row;
    }

    private static ComboBox Combo(string name, IEnumerable<string> items, int selected) => new()
    {
        Name = name,
        ItemsSource = items.ToList(),
        SelectedIndex = selected,
        Height = 30,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private ComboBox BuildBoolean(MpdFieldEditor field, Action showError)
    {
        ComboBox box = Combo("MpdValue_" + field.Name, [NotSet(field), Loc.Get("MpdConfig.Yes"), Loc.Get("MpdConfig.No")], field.BooleanValue switch { true => 1, false => 2, _ => 0 });
        box.SelectionChanged += (_, _) =>
        {
            if (suppress) return;
            field.SetBoolean(box.SelectedIndex switch { 1 => true, 2 => false, _ => null });
            showError();
        };
        if (field.IsSet && field.BooleanValue is null) box.ItemsSource = new[] { NotSet(field), Loc.Get("MpdConfig.Yes"), Loc.Get("MpdConfig.No"), field.Value! };
        if (field.IsSet && field.BooleanValue is null) box.SelectedIndex = 3;
        return box;
    }

    private ComboBox BuildChoice(MpdFieldEditor field, Action showError)
    {
        List<string> items = [NotSet(field), .. field.Definition.Choices ?? []];
        if (field.Value is { } current && !items.Contains(current)) items.Add(current);
        ComboBox box = Combo("MpdValue_" + field.Name, items, field.Value is null ? 0 : items.IndexOf(field.Value));
        box.SelectionChanged += (_, _) =>
        {
            if (suppress || box.SelectedIndex < 0) return;
            field.SetValue(box.SelectedIndex == 0 ? null : items[box.SelectedIndex]);
            showError();
        };
        return box;
    }

    private static string NotSet(MpdFieldEditor field) =>
        field.Default is { } fallback ? Loc.Format("MpdConfig.NotSetDefault", fallback) : Loc.Get("MpdConfig.NotSet");

    private TextBox BuildText(MpdFieldEditor field, Action showError, bool secret = false)
    {
        TextBox box = new()
        {
            Name = "MpdValue_" + field.Name,
            Text = field.Value ?? "",
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = field.Default is { } fallback ? Loc.Format("MpdConfig.DefaultValue", fallback) : null,
        };
        if (secret) { box.PasswordChar = '•'; box.Classes.Add("revealPasswordButton"); }
        box.TextChanged += (_, _) =>
        {
            if (suppress) return;
            field.SetValue(box.Text?.Trim());
            showError();
        };
        return box;
    }

    private Grid BuildPath(MpdFieldEditor field, Action showError)
    {
        TextBox box = BuildText(field, showError);
        Button browse = new() { Name = "MpdBrowse_" + field.Name, Content = Loc.Get("Prefs.Browse"), IsEnabled = picker is not null };
        AutomationProperties.SetName(browse, field.Name);
        browse.Click += async (_, _) =>
        {
            if (picker is null) return;
            Uri? chosen = field.Name.EndsWith("directory", StringComparison.Ordinal)
                ? await picker.PickFolderAsync(this, new FolderPickerOpenOptions { Title = field.Name, AllowMultiple = false })
                : await picker.PickSaveFileAsync(this, new FilePickerSaveOptions { Title = field.Name, ShowOverwritePrompt = false });
            if (chosen is { IsFile: true }) box.Text = chosen.LocalPath;
        };
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(box);
        Grid.SetColumn(browse, 1); grid.Children.Add(browse);
        return grid;
    }

    private StackPanel BuildLines(string name)
    {
        StackPanel panel = new() { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = name, FontFamily = new FontFamily("monospace") });
        TextBox box = new()
        {
            Name = "MpdLines_" + name,
            AcceptsReturn = true,
            MinHeight = 60,
            MaxHeight = 120,
            TextWrapping = TextWrapping.NoWrap,
            Text = string.Join("\n", editor.Lines(name)),
        };
        if (name == "password") { box.FontFamily = new FontFamily("monospace"); }
        AutomationProperties.SetName(box, name);
        box.TextChanged += (_, _) =>
        {
            if (suppress) return;
            editor.SetLines(name, (box.Text ?? "").Split('\n', StringSplitOptions.TrimEntries));
        };
        panel.Children.Add(box);
        panel.Children.Add(Hint(Loc.Get("MpdConfig.OneValuePerLine")));
        return panel;
    }

    private StackPanel BuildPermissions(MpdFieldEditor field)
    {
        StackPanel panel = new() { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = field.Name, FontFamily = new FontFamily("monospace") });
        string[] granted = (field.Value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        CheckBox useDefault = new() { Name = "MpdPermissionDefault_" + field.Name, Content = NotSet(field), IsChecked = !field.IsSet };
        WrapPanel boxes = new() { Orientation = Orientation.Horizontal, ItemSpacing = 12 };
        List<CheckBox> checks = [];
        foreach (string permission in MpdSettingCatalog.Permissions)
        {
            CheckBox box = new() { Name = $"MpdPermission_{field.Name}_{permission}", Content = permission, IsChecked = granted.Contains(permission), IsEnabled = field.IsSet };
            checks.Add(box);
            boxes.Children.Add(box);
        }
        void Write()
        {
            if (suppress) return;
            foreach (CheckBox box in checks) box.IsEnabled = useDefault.IsChecked != true;
            if (useDefault.IsChecked == true) { field.SetValue(null); return; }
            field.SetRaw(string.Join(",", checks.Where(box => box.IsChecked == true).Select(box => (string)box.Content!)));
        }
        useDefault.IsCheckedChanged += (_, _) => Write();
        foreach (CheckBox box in checks) box.IsCheckedChanged += (_, _) => Write();
        panel.Children.Add(useDefault);
        panel.Children.Add(boxes);
        return panel;
    }

    internal Control BuildOtherSettings(Func<IReadOnlyList<MpdConfigSetting>> list, Action<string, string> set, Action<MpdConfigSetting> remove)
    {
        StackPanel panel = new() { Spacing = 6 };
        StackPanel rows = new() { Spacing = 4 };
        void Fill()
        {
            rows.Children.Clear();
            foreach (MpdConfigSetting setting in list())
            {
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("180,*,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = setting.Name, FontFamily = new FontFamily("monospace"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                TextBox value = new() { Text = setting.Value ?? "", Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(value, setting.Name);
                value.LostFocus += (_, _) => { if (!suppress && value.Text != setting.Value && !string.IsNullOrEmpty(value.Text)) set(setting.Name, value.Text); };
                Grid.SetColumn(value, 1); row.Children.Add(value);
                Button delete = new() { Content = Loc.Get("Dialog.Remove") };
                delete.Click += (_, _) => { remove(setting); Fill(); };
                Grid.SetColumn(delete, 2); row.Children.Add(delete);
                rows.Children.Add(row);
            }
        }
        Fill();
        panel.Children.Add(rows);
        TextBox newName = new() { Name = "MpdNewSettingName", PlaceholderText = Loc.Get("MpdConfig.SettingName"), Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        TextBox newValue = new() { Name = "MpdNewSettingValue", PlaceholderText = Loc.Get("MpdConfig.SettingValue"), Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        Button add = new() { Name = "MpdAddSetting", Content = Loc.Get("MpdConfig.AddSetting") };
        TextBlock problem = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#D52268")), IsVisible = false };
        add.Click += (_, _) =>
        {
            string name = newName.Text?.Trim() ?? "";
            if (!MpdConfigSyntax.IsValidName(name) || string.IsNullOrEmpty(newValue.Text) || newValue.Text.AsSpan().IndexOfAny('\n', '\r') >= 0)
            {
                problem.Text = Loc.Get("MpdConfig.InvalidSetting");
                problem.IsVisible = true;
                return;
            }
            problem.IsVisible = false;
            set(name, newValue.Text);
            newName.Text = newValue.Text = "";
            Fill();
        };
        Grid addRow = new() { ColumnDefinitions = new ColumnDefinitions("180,*,Auto"), ColumnSpacing = 8 };
        addRow.Children.Add(newName);
        Grid.SetColumn(newValue, 1); addRow.Children.Add(newValue);
        Grid.SetColumn(add, 2); addRow.Children.Add(add);
        panel.Children.Add(addRow);
        panel.Children.Add(problem);
        return panel;
    }

    internal MpdConfigEditor Editor => editor;

    internal MpdVersionInfo? Installed => installed;

    internal bool Suppressed { get => suppress; set => suppress = value; }

    /// <summary>The list-and-form editor for one kind of block (outputs, decoders, …).</summary>
    private sealed class BlockPanel : Grid
    {
        private readonly MpdConfigWindow owner;
        private readonly string blockName;
        private readonly MpdBlockDefinition definition;
        private readonly ListBox list = new() { MinHeight = 160 };
        private readonly ContentControl form = new();
        private readonly Button add = new();
        private readonly Button remove = new();
        private readonly Button up = new();
        private readonly Button down = new();
        private readonly TextBlock none = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        private IReadOnlyList<MpdBlockEditor> blocks = [];

        public BlockPanel(MpdConfigWindow owner, string blockName)
        {
            this.owner = owner;
            this.blockName = blockName;
            definition = MpdSettingCatalog.FindBlock(blockName)!;
            Name = "MpdBlocks_" + blockName;
            ColumnDefinitions = new ColumnDefinitions("240,*");
            RowDefinitions = new RowDefinitions("*");
            ColumnSpacing = 12;
            Margin = new Thickness(8);
            AutomationProperties.SetName(list, blockName);
            list.Name = "MpdBlockList_" + blockName;
            add.Name = "MpdAddBlock_" + blockName; add.Content = Loc.Get("MpdConfig.AddBlock");
            remove.Name = "MpdRemoveBlock_" + blockName; remove.Content = Loc.Get("Dialog.Remove");
            up.Name = "MpdBlockUp_" + blockName; up.Content = Loc.Get("MpdConfig.MoveUp");
            down.Name = "MpdBlockDown_" + blockName; down.Content = Loc.Get("MpdConfig.MoveDown");
            none.Text = Loc.Get("MpdConfig.NoBlocks");
            StackPanel left = new() { Spacing = 8 };
            left.Children.Add(list);
            left.Children.Add(none);
            WrapPanel buttons = new() { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
            buttons.Children.Add(add); buttons.Children.Add(remove);
            if (definition.Repeatable) { buttons.Children.Add(up); buttons.Children.Add(down); }
            left.Children.Add(buttons);
            Children.Add(left);
            Grid.SetColumn(form, 1); Children.Add(form);
            list.SelectionChanged += (_, _) => { if (!owner.suppress) ShowForm(); };
            remove.Click += (_, _) => Remove();
            up.Click += (_, _) => Move(-1);
            down.Click += (_, _) => Move(1);
            if (definition.PluginKey is null) add.Click += (_, _) => Add(null);
            else add.Flyout = BuildPluginMenu();
            owner.editor.Changed += (_, _) => RefreshTitles();
            Load(0);
        }

        private MenuFlyout BuildPluginMenu()
        {
            MenuFlyout menu = new();
            foreach (MpdPluginDefinition plugin in definition.Plugins)
            {
                bool supported = owner.installed?.Supports(blockName, plugin.Name) != false;
                MenuItem item = new() { Header = plugin.Name, IsEnabled = supported };
                if (!supported) ToolTip.SetTip(item, Loc.Format("MpdConfig.PluginUnavailable", plugin.Name));
                string chosen = plugin.Name;
                item.Click += (_, _) => Add(chosen);
                menu.Items.Add(item);
            }
            return menu;
        }

        private void Load(int selected)
        {
            owner.suppress = true;
            try
            {
                blocks = owner.editor.Blocks(blockName);
                list.ItemsSource = blocks.Select(item => item.Title).ToList();
                list.SelectedIndex = blocks.Count == 0 ? -1 : Math.Clamp(selected, 0, blocks.Count - 1);
            }
            finally
            {
                owner.suppress = false;
            }
            ShowForm();
        }

        private void RefreshTitles()
        {
            if (blocks.Count == 0 || owner.suppress) return;
            int selected = list.SelectedIndex;
            IReadOnlyList<MpdBlockEditor> current = owner.editor.Blocks(blockName);
            if (current.Count != blocks.Count) return;
            owner.suppress = true;
            try
            {
                blocks = current;
                list.ItemsSource = blocks.Select(item => item.Title).ToList();
                list.SelectedIndex = selected;
            }
            finally
            {
                owner.suppress = false;
            }
        }

        private void UpdateButtons()
        {
            bool has = list.SelectedIndex >= 0;
            none.IsVisible = blocks.Count == 0;
            add.IsEnabled = definition.Repeatable || blocks.Count == 0;
            remove.IsEnabled = has;
            up.IsEnabled = has && list.SelectedIndex > 0;
            down.IsEnabled = has && list.SelectedIndex < blocks.Count - 1;
        }

        private void Add(string? plugin)
        {
            owner.editor.AddBlock(blockName, plugin);
            Load(int.MaxValue);
        }

        private void Remove()
        {
            int index = list.SelectedIndex;
            if (index < 0) return;
            owner.editor.RemoveBlock(blocks[index]);
            Load(index);
        }

        private void Move(int offset)
        {
            int index = list.SelectedIndex;
            if (index < 0 || !owner.editor.MoveBlock(blocks[index], offset)) return;
            Load(index + offset);
        }

        private void ShowForm()
        {
            UpdateButtons();
            int index = list.SelectedIndex;
            if (index < 0 || index >= blocks.Count) { form.Content = null; return; }
            MpdBlockEditor block = blocks[index];
            StackPanel panel = new() { Spacing = 10 };
            if (block.PluginKey is { } key)
            {
                List<string> plugins = [.. definition.Plugins.Where(item => owner.installed?.Supports(blockName, item.Name) != false || item.Name == block.Plugin).Select(item => item.Name)];
                if (block.Plugin is { } current && !plugins.Contains(current)) plugins.Add(current);
                StackPanel field = new() { Spacing = 4 };
                field.Children.Add(new TextBlock { Text = key, FontFamily = new FontFamily("monospace") });
                ComboBox pluginBox = Combo("MpdPlugin_" + blockName, plugins, block.Plugin is null ? -1 : plugins.IndexOf(block.Plugin));
                AutomationProperties.SetName(pluginBox, key);
                pluginBox.SelectionChanged += (_, _) =>
                {
                    if (owner.suppress || pluginBox.SelectedIndex < 0) return;
                    block.SetPlugin(plugins[pluginBox.SelectedIndex]);
                    ShowForm();
                };
                field.Children.Add(pluginBox);
                if (block.Plugin is { } selectedPlugin && owner.installed?.Supports(blockName, selectedPlugin) == false)
                    field.Children.Add(new TextBlock { Text = Loc.Format("MpdConfig.PluginUnavailable", selectedPlugin), FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D52268")) });
                panel.Children.Add(field);
            }
            foreach (MpdFieldEditor field in block.Fields)
            {
                Control row = owner.BuildField(field);
                // The encoder decides which settings follow, so choosing one redraws the form.
                if (field.Name == "encoder" && row is StackPanel { Children: [_, ComboBox encoderBox, ..] })
                    encoderBox.SelectionChanged += (_, _) => { if (!owner.suppress) ShowForm(); };
                panel.Children.Add(row);
            }
            panel.Children.Add(Heading(Loc.Get("MpdConfig.OtherSettings")));
            panel.Children.Add(owner.BuildOtherSettings(() => block.OtherSettings, (name, value) => block.SetOther(name, value), setting => block.RemoveSetting(setting)));
            form.Content = panel;
        }
    }
}
