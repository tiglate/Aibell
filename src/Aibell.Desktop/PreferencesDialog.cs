using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Aibell.Application.Settings;
using Aibell.Desktop.Localization;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Playback;

namespace Aibell.Desktop;

public sealed class PreferencesDialog : Window
{
    public PreferencesDialog(MainWindowViewModel model, Func<Window, Task> addFolder, Func<Window, Task>? reviewMetadataEdits = null,
        Func<Window, string, Task<string?>>? pickPodcastFolder = null, Func<Window, Task<string?>>? pickPluginFolder = null,
        Func<Window, Task<string?>>? pickPluginArchive = null, Func<Window, Task<string?>>? pickSoundFont = null)
    {
        Name = "PreferencesDialog"; Title = Loc.Get("Prefs.Title"); Width = 610; Height = 620; MinWidth = 540; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        PreferencesData initial = model.GetPreferences();
        ComboBox theme = Choice("AppearanceTheme", [Loc.Get("Prefs.ThemeSystem"), Loc.Get("Prefs.ThemeLight"), Loc.Get("Prefs.ThemeDark")], (int)initial.Theme);
        ComboBox repeat = Choice("PlaybackRepeat", [Loc.Get("Prefs.RepeatOff"), Loc.Get("Prefs.RepeatAll"), Loc.Get("Prefs.RepeatOne")], (int)initial.Repeat);
        (PlaybackEngineKind Kind, string Label)[] engineChoices = OperatingSystem.IsLinux()
            ? [
                (PlaybackEngineKind.PipeWire, Loc.Get("Prefs.EnginePipeWire")),
                (PlaybackEngineKind.Alsa, Loc.Get("Prefs.EngineAlsa")),
                (PlaybackEngineKind.Miniaudio, Loc.Get("Prefs.EngineMiniaudio")),
                (PlaybackEngineKind.Vlc, Loc.Get("Prefs.EngineVlc"))
            ]
            : [
                (PlaybackEngineKind.Miniaudio, Loc.Get("Prefs.EngineMiniaudioNative")),
                (PlaybackEngineKind.Managed, Loc.Get("Prefs.EngineManaged")),
                (PlaybackEngineKind.Vlc, Loc.Get("Prefs.EngineVlc"))
            ];
        int selectedEngine = Array.FindIndex(engineChoices, choice => choice.Kind == initial.PlaybackEngine);
        ComboBox engine = Choice("PlaybackEngine", [.. engineChoices.Select(choice => choice.Label)], selectedEngine >= 0 ? selectedEngine : 0);
        ComboBox languageChoice = Choice("AppearanceLanguage", [Loc.Get("Prefs.LanguageSystem"), .. AppLanguages.All.Select(item => item.Name)],
            Math.Max(0, AppLanguages.IndexOf(initial.Language) + 1));
        CheckBox queue = new() { Name = "PreferencesShowQueue", Content = Loc.Get("Prefs.ShowQueue"), IsChecked = initial.ShowPlayQueue };
        CheckBox notifications = new()
        {
            Name = "PreferencesNotifications",
            Content = Loc.Get("Prefs.Notify"),
            IsChecked = initial.ShowNotifications,
            IsEnabled = model.CanShowDesktopNotifications
        };
        ToolTip.SetTip(notifications, model.CanShowDesktopNotifications
            ? Loc.Get("Prefs.NotifyTip")
            : Loc.Get("Prefs.NotifyUnavailable"));
        CheckBox mute = new() { Name = "PreferencesMuted", Content = Loc.Get("Prefs.Mute"), IsChecked = initial.Muted };
        CheckBox shuffle = new() { Name = "PreferencesShuffle", Content = Loc.Get("Prefs.Shuffle"), IsChecked = initial.Shuffle };
        Slider volume = new() { Name = "PreferencesVolume", Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Value = initial.Volume * 100 };
        AutomationProperties.SetName(volume, Loc.Get("Prefs.VolumeAutomation"));
        StackPanel general = new() { Spacing = 14, Margin = new Thickness(16) };
        general.Children.Add(Field(Loc.Get("Prefs.Appearance"), theme));
        general.Children.Add(Field(Loc.Get("Prefs.Language"), languageChoice));
        general.Children.Add(new TextBlock { Text = Loc.Get("Prefs.LanguageRestart"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        general.Children.Add(queue); general.Children.Add(notifications);
        general.Children.Add(new TextBlock { Text = Loc.Get("Prefs.GeneralNote"), TextWrapping = TextWrapping.Wrap });
        StackPanel playback = new() { Spacing = 14, Margin = new Thickness(16) };
        playback.Children.Add(Field(Loc.Get("Prefs.Volume"), volume)); playback.Children.Add(mute); playback.Children.Add(shuffle);
        playback.Children.Add(Field(Loc.Get("Prefs.Repeat"), repeat));
        playback.Children.Add(Field(Loc.Get("Prefs.Engine"), engine));
        playback.Children.Add(new TextBlock { Text = Loc.Get("Prefs.EngineNote"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        TextBox soundFont = new() { Name = "PreferencesSoundFont", Text = initial.SoundFont ?? "", Height = 30, VerticalContentAlignment = VerticalAlignment.Center, PlaceholderText = Loc.Get("Prefs.SoundFontNone") };
        AutomationProperties.SetName(soundFont, Loc.Get("Prefs.SoundFont"));
        Button browseSoundFont = new() { Name = "PreferencesBrowseSoundFont", Content = Loc.Get("Prefs.Browse"), IsEnabled = pickSoundFont is not null };
        Button clearSoundFont = new() { Name = "PreferencesClearSoundFont", Content = Loc.Get("Prefs.SoundFontClear") };
        Grid soundFontRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        soundFontRow.Children.Add(soundFont);
        Grid.SetColumn(browseSoundFont, 1); soundFontRow.Children.Add(browseSoundFont);
        Grid.SetColumn(clearSoundFont, 2); soundFontRow.Children.Add(clearSoundFont);
        playback.Children.Add(Field(Loc.Get("Prefs.SoundFont"), soundFontRow));
        playback.Children.Add(new TextBlock { Text = Loc.Get("Prefs.SoundFontNote"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        StackPanel sources = new() { Spacing = 10, Margin = new Thickness(16) };
        sources.Children.Add(new TextBlock { Text = Loc.Get("Prefs.FoldersNote"), TextWrapping = TextWrapping.Wrap });
        ListBox folders = new()
        {
            Name = "PreferencesFolders",
            Height = 145,
            ItemsSource = model.LibrarySources,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LibrarySourceViewModel>((item, _) =>
                new TextBlock { Text = item?.Path, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4) })
        };
        AutomationProperties.SetName(folders, Loc.Get("Main.MusicFolders")); sources.Children.Add(folders);
        StackPanel folderButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button add = new() { Content = Loc.Get("Prefs.AddFolder"), Name = "PreferencesAddFolder" },
            rescan = new() { Name = "PreferencesRescanFolder", Content = Loc.Get("Main.Rescan"), IsEnabled = false }, remove = new() { Name = "PreferencesRemoveFolder", Content = Loc.Get("Dialog.Remove"), IsEnabled = false };
        folderButtons.Children.Add(add); folderButtons.Children.Add(rescan); folderButtons.Children.Add(remove); sources.Children.Add(folderButtons);
        sources.Children.Add(new TextBlock { Text = Loc.Get("Prefs.FolderChangesNow"), FontSize = 11 });
        Button recovery = new()
        {
            Name = "PreferencesMetadataRecovery",
            Content = Loc.Get("Prefs.ReviewEdits"),
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = model.CanReviewMetadataRecovery && reviewMetadataEdits is not null
        };
        sources.Children.Add(recovery);
        StackPanel podcastTab = new() { Spacing = 10, Margin = new Thickness(16) };
        podcastTab.Children.Add(new TextBlock
        {
            Text = Loc.Get("Prefs.PodcastFolderNote"),
            TextWrapping = TextWrapping.Wrap
        });
        TextBox podcastFolder = new()
        {
            Name = "PreferencesPodcastFolder",
            Text = model.PodcastDownloadRoot,
            IsEnabled = model.CanChoosePodcastFolder,
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(podcastFolder, Loc.Get("Prefs.PodcastFolderAutomation"));
        Button browsePodcasts = new() { Name = "PreferencesBrowsePodcastFolder", Content = Loc.Get("Prefs.Browse"), IsEnabled = model.CanChoosePodcastFolder && pickPodcastFolder is not null };
        Button defaultPodcasts = new() { Name = "PreferencesDefaultPodcastFolder", Content = Loc.Get("Prefs.UseDefault"), IsEnabled = model.CanChoosePodcastFolder };
        AutomationProperties.SetName(browsePodcasts, Loc.Get("Prefs.BrowseAutomation"));
        Grid podcastRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        podcastRow.Children.Add(podcastFolder);
        Grid.SetColumn(browsePodcasts, 1); podcastRow.Children.Add(browsePodcasts);
        Grid.SetColumn(defaultPodcasts, 2); podcastRow.Children.Add(defaultPodcasts);
        podcastTab.Children.Add(Field(Loc.Get("Prefs.DownloadFolder"), podcastRow));
        podcastTab.Children.Add(new TextBlock { Text = Loc.Get("Prefs.FolderChecked"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        CheckBox autoRefresh = new()
        {
            Name = "PreferencesPodcastAutoRefresh",
            Content = Loc.Get("Prefs.AutoRefresh"),
            IsChecked = initial.PodcastAutoRefresh
        };
        AutomationProperties.SetName(autoRefresh, Loc.Get("Prefs.AutoRefreshAutomation"));
        podcastTab.Children.Add(autoRefresh);
        podcastTab.Children.Add(new TextBlock { Text = Loc.Get("Prefs.AutoRefreshNote"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        StackPanel onlineTab = new() { Spacing = 10, Margin = new Thickness(16) };
        CheckBox onlineLookup = new()
        {
            Name = "PreferencesOnlineLookup",
            Content = Loc.Get("Prefs.OnlineLookup"),
            IsChecked = initial.OnlineLookup
        };
        AutomationProperties.SetName(onlineLookup, Loc.Get("Prefs.OnlineLookupAutomation"));
        onlineTab.Children.Add(onlineLookup);
        onlineTab.Children.Add(new TextBlock
        {
            Text = Loc.Get("Prefs.OnlineExplain"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        CheckBox radioCatalog = new()
        {
            Name = "PreferencesRadioCatalog",
            Content = Loc.Get("Prefs.RadioCatalog"),
            IsChecked = initial.RadioCatalog
        };
        AutomationProperties.SetName(radioCatalog, Loc.Get("Prefs.RadioCatalogAutomation"));
        onlineTab.Children.Add(radioCatalog);
        onlineTab.Children.Add(new TextBlock
        {
            Text = Loc.Get("Prefs.RadioCatalogNote"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        PluginsPanel plugins = new(model.PluginAdministration, pickPluginFolder is null ? null : () => pickPluginFolder(this),
            pickPluginArchive is null ? null : () => pickPluginArchive(this));
        folders.SelectionChanged += (_, _) => { rescan.IsEnabled = remove.IsEnabled = folders.SelectedItem is not null; };
        TabControl tabs = new()
        {
            Margin = new Thickness(12),
            ItemsSource = new[]
        {
            new TabItem { Header = Loc.Get("Prefs.Tab.General"), Content = general }, new TabItem { Header = Loc.Get("Prefs.Tab.Playback"), Content = new ScrollViewer { Content = playback, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } },
            new TabItem { Header = Loc.Get("Prefs.Tab.Folders"), Content = sources }, new TabItem { Header = Loc.Get("Prefs.Tab.Podcasts"), Content = podcastTab },
            new TabItem { Header = Loc.Get("Prefs.Tab.Online"), Content = onlineTab }, new TabItem { Header = Loc.Get("Prefs.Tab.Plugins"), Content = plugins }
        }
        };
        TextBlock error = new() { Name = "PreferencesError", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 0, 18, 8) };
        Button save = new() { Name = "SavePreferences", Content = Loc.Get("Dialog.Save"), Width = 92, Height = 30, Classes = { "dialogAccept" } };
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 };
        bool busy = false;
        async Task Execute(Func<Task> action, bool close)
        {
            if (busy) return;
            busy = true; tabs.IsEnabled = false; save.IsEnabled = cancel.IsEnabled = false;
            try { await action(); if (close) { busy = false; Close(true); } }
            catch (Exception exception) { error.Text = exception.Message; }
            finally { busy = false; tabs.IsEnabled = true; save.IsEnabled = cancel.IsEnabled = true; }
        }
        save.Click += async (_, _) => await Execute(() => model.SavePreferencesAsync(new((ApplicationTheme)theme.SelectedIndex,
            queue.IsChecked == true, volume.Value / 100, mute.IsChecked == true, shuffle.IsChecked == true, (RepeatMode)repeat.SelectedIndex, notifications.IsChecked == true,
            RequestedPodcastFolder(), autoRefresh.IsChecked == true, onlineLookup.IsChecked == true,
            engine.SelectedIndex >= 0 && engine.SelectedIndex < engineChoices.Length ? engineChoices[engine.SelectedIndex].Kind : engineChoices[0].Kind,
            languageChoice.SelectedIndex <= 0 ? null : AppLanguages.All[languageChoice.SelectedIndex - 1].Code, radioCatalog.IsChecked == true,
            string.IsNullOrWhiteSpace(soundFont.Text) ? null : soundFont.Text.Trim())), close: true);
        string? RequestedPodcastFolder()
        {
            if (!model.CanChoosePodcastFolder) return null;
            string text = podcastFolder.Text?.Trim() ?? "";
            return text.Length == 0 ? model.PodcastDefaultRoot : text;
        }
        browsePodcasts.Click += async (_, _) => await Execute(async () =>
        {
            string? picked = await pickPodcastFolder!(this, podcastFolder.Text ?? "");
            if (!string.IsNullOrWhiteSpace(picked)) podcastFolder.Text = picked;
        }, false);
        defaultPodcasts.Click += (_, _) => podcastFolder.Text = model.PodcastDefaultRoot;
        browseSoundFont.Click += async (_, _) => await Execute(async () =>
        {
            string? picked = await pickSoundFont!(this);
            if (!string.IsNullOrWhiteSpace(picked)) soundFont.Text = picked;
        }, false);
        clearSoundFont.Click += (_, _) => soundFont.Text = "";
        cancel.Click += (_, _) => Close(false);
        recovery.Click += async (_, _) => await Execute(() => reviewMetadataEdits!(this), false);
        add.Click += async (_, _) => await Execute(() => addFolder(this), false);
        rescan.Click += async (_, _) => await Execute(async () =>
        {
            model.SelectedLibrarySource = folders.SelectedItem as LibrarySourceViewModel;
            await model.RescanSelectedLibrarySourceAsync(); error.Text = model.StatusMessage;
        }, false);
        remove.Click += async (_, _) =>
        {
            if (folders.SelectedItem is not LibrarySourceViewModel source ||
                !await DialogChrome.ConfirmAsync(this, Loc.Get("Dialog.RemoveFolder"),
                    Loc.Format("Prefs.RemoveFolderPrompt", source.Path),
                    Loc.Get("Dialog.Remove"), "ConfirmRemoveMusicFolder")) return;
            await Execute(async () =>
            {
                model.SelectedLibrarySource = source;
                await model.RemoveSelectedLibrarySourceAsync(); error.Text = model.StatusMessage;
            }, false);
        };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Border footer = new()
        {
            Child = buttons,
            Padding = new Thickness(14, 10),
            [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush")
        };
        Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto,51") };
        layout.Children.Add(tabs); Grid.SetRow(error, 1); layout.Children.Add(error); Grid.SetRow(footer, 2); layout.Children.Add(footer); Content = DialogChrome.Wrap(this, layout, () => !busy);
        Closing += (_, args) => args.Cancel = busy;
        KeyDown += (_, args) => { if (args.Key == Key.Escape && !busy) { args.Handled = true; Close(false); } };
    }

    private static ComboBox Choice(string name, string[] items, int selected)
    {
        ComboBox box = new()
        {
            Name = name,
            ItemsSource = items,
            SelectedIndex = selected,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0)
        };
        AutomationProperties.SetName(box, name); return box;
    }
    private static StackPanel Field(string label, Control content)
    {
        AutomationProperties.SetName(content, label);
        StackPanel field = new() { Spacing = 5 }; field.Children.Add(new TextBlock { Text = label }); field.Children.Add(content); return field;
    }
}
