using Aibell.Application.Library;
using Aibell.Desktop.Localization;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Skins.Classic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace Aibell.Desktop.Classic;

/// <summary>
/// Classic Player Mode's window: borderless, sized by the skin, and the owner of everything the skinned surface cannot do
/// itself (menus, file pickers, dialogs). Closing it closes Aibell, as closing the main window does.
/// </summary>
internal sealed class ClassicPlayerWindow : Window, IClassicPlayerHost, IDisposable
{
    private static readonly string[] MusicPatterns =
        ["*.aac", "*.aif", "*.aifc", "*.aiff", "*.alac", "*.flac", "*.m4a", "*.m4b", "*.mid", "*.midi", "*.mp3", "*.ogg", "*.oga", "*.opus", "*.rmi", "*.rsn", "*.spc", "*.wav", "*.wma"];

    private readonly ClassicPlayerViewModel model;
    private readonly ClassicPlayerView view;
    private readonly SkinLibrary skins;
    private readonly SkinGalleryClient? gallery;
    private readonly IDesktopFilePicker picker;
    private readonly IMediaFileDiscovery? discovery;
    private readonly Action switchToStandard;
    private readonly Action exitApplication;
    private bool closingForExit;

    public ClassicPlayerWindow(ClassicPlayerViewModel model, SkinLibrary skins, SkinGalleryClient? gallery, IDesktopFilePicker picker,
        IMediaFileDiscovery? discovery, Action switchToStandard, Action exitApplication)
    {
        this.model = model;
        this.skins = skins;
        this.gallery = gallery;
        this.picker = picker;
        this.discovery = discovery;
        this.switchToStandard = switchToStandard;
        this.exitApplication = exitApplication;
        Name = "ClassicPlayerWindow";
        Title = "Aibell";
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        // Skins are drawn left to right whatever the interface language is.
        FlowDirection = FlowDirection.LeftToRight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        view = new ClassicPlayerView(model, this, skins.Load(model.Preferences.SkinId)) { Name = "ClassicPlayerSurface" };
        view.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        view.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Content = view;
        Styles.Add(new Style(selector => selector.OfType<MenuItem>())
        {
            Setters = { new Setter(MenuItem.PaddingProperty, new Thickness(8, 3)) },
        });
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) => args.DragEffects = args.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, Dropped);
        Activated += (_, _) => view.IsWindowActive = true;
        Deactivated += (_, _) => view.IsWindowActive = false;
        KeyDown += WindowKeyDown;
        Closing += (_, args) =>
        {
            if (closingForExit) return;
            args.Cancel = true;
            exitApplication();
        };
        Closed += (_, _) => Dispose();
        model.Player.PreferencesLoaded += (_, _) => ApplyPreferences(reloadSkin: true);
        ApplyPreferences(reloadSkin: false);
    }

    public void Dispose() => view.Dispose();

    internal ClassicPlayerView Surface => view;

    /// <summary>Closes the window for good (the application is shutting down).</summary>
    public void CloseForExit()
    {
        closingForExit = true;
        Close();
    }

    private void ApplyPreferences(bool reloadSkin)
    {
        if (reloadSkin) view.ChangeSkin(skins.Load(model.Preferences.SkinId));
        LayoutChanged();
    }

    public void LayoutChanged()
    {
        Topmost = model.Preferences.AlwaysOnTop;
        view.InvalidateMeasure();
        // A skin's outline needs a compositor that can show through the window; otherwise the window stays rectangular.
        Geometry? outline = ActualTransparencyLevel == WindowTransparencyLevel.Transparent ? view.Outline() : null;
        view.Clip = outline;
        Background = outline is null ? Brushes.Black : Brushes.Transparent;
    }

    public void BeginMove(PointerPressedEventArgs args) => BeginMoveDrag(args);

    public void Minimize() => WindowState = WindowState.Minimized;

    public void Exit() => exitApplication();

    public void Run(Func<Task> action)
    {
        _ = RunAsync(action);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // The window closed while the action ran.
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidSkinException or HttpRequestException)
        {
            model.Player.StatusMessage = exception.Message;
        }
    }

    // ---- Menus -----------------------------------------------------------------------------------------------------

    private static MenuItem Item(string header, Action action, string? gesture = null, bool? isChecked = null, bool enabled = true)
    {
        // An underscore marks an access key in menus; names of skins and playlists keep theirs.
        MenuItem item = new() { Header = header.Replace("_", "__", StringComparison.Ordinal), IsEnabled = enabled };
        if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
        if (isChecked is bool on)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = on;
        }
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Submenu(string header, IEnumerable<object> items) =>
        new() { Header = header.Replace("_", "__", StringComparison.Ordinal), ItemsSource = items.ToList() };

    private void Show(IEnumerable<object> items)
    {
        List<object> list = [.. items];
        ContextMenu menu = new() { ItemsSource = list, FontSize = 12, FlowDirection = Localization.Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        // Menus are drawn inside the window (see Program: native pop-ups are misplaced under Wayland). A window too small for the
        // menu grows while it is open, see-through where the compositor allows, and shrinks back afterwards.
        const double neededWidth = 340;
        double menuHeight = list.Sum(item => item is Separator ? 11 : 28) + 24;
        bool taller = Bounds.Height < menuHeight;
        if (!taller && Bounds.Width >= neededWidth)
        {
            menu.Open(view);
            return;
        }
        SizeToContent = SizeToContent.Manual;
        Width = Math.Max(Bounds.Width, neededWidth);
        if (taller) Height = view.Bounds.Height + menuHeight;
        if (ActualTransparencyLevel == WindowTransparencyLevel.Transparent)
        {
            Background = Brushes.Transparent;
            TransparencyBackgroundFallback = Brushes.Black;
        }
        menu.Closed += (_, _) =>
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            LayoutChanged();
        };
        // The overlay is laid out for the window as it is when the menu opens, so wait for the new size to arrive. Below a
        // short window the menu hangs under the skin; otherwise it opens at the pointer as usual.
        if (taller)
        {
            menu.Placement = PlacementMode.Bottom;
            menu.PlacementTarget = view;
        }
        OpenWhenResized(menu, Width, Height);
    }

    private void OpenWhenResized(ContextMenu menu, double width, double height)
    {
        bool opened = false;
        void Open()
        {
            if (opened) return;
            opened = true;
            Resized -= OnResized;
            menu.Open(view);
        }
        void OnResized(object? sender, WindowResizedEventArgs args)
        {
            if (args.ClientSize.Width >= width - 1 && args.ClientSize.Height >= height - 1)
                Avalonia.Threading.Dispatcher.UIThread.Post(Open, Avalonia.Threading.DispatcherPriority.Background);
        }
        Resized += OnResized;
        // A window manager that ignores the request still gets its menu, clipped or not.
        Avalonia.Threading.DispatcherTimer.RunOnce(Open, TimeSpan.FromMilliseconds(400));
    }

    public void ShowOptionsMenu()
    {
        Aibell.Application.Settings.ClassicPlayerPreferences preferences = model.Preferences;
        Show(
        [
            Item(Loc.Get("Classic.StandardMode"), switchToStandard, "Ctrl+Shift+M"),
            Submenu(Loc.Get("Classic.Skins"), SkinItems()),
            new Separator(),
            Item(Loc.Get("Classic.OpenFiles"), () => Run(() => OpenFilesAsync(replace: true)), "L"),
            Item(Loc.Get("Classic.AddFromLibrary"), () => Run(SearchLibraryAsync), "J"),
            new Separator(),
            Item(Loc.Get("Classic.ShowEqualizer"), () => view.ChangeLayout(p => p with { ShowEqualizer = !p.ShowEqualizer }), "Alt+G", preferences.ShowEqualizer),
            Item(Loc.Get("Classic.ShowPlaylist"), () => view.ChangeLayout(p => p with { ShowPlaylist = !p.ShowPlaylist }), "Alt+E", preferences.ShowPlaylist),
            Item(Loc.Get("Classic.DoubleSize"), () => view.ChangeLayout(p => p with { DoubleSize = !p.DoubleSize }), "Ctrl+D", preferences.DoubleSize),
            Item(Loc.Get("Classic.AlwaysOnTop"), () => view.ChangeLayout(p => p with { AlwaysOnTop = !p.AlwaysOnTop }), null, preferences.AlwaysOnTop),
            Item(Loc.Get("Classic.RemainingTime"), () => Run(() => model.ToggleRemainingTimeAsync()), "Ctrl+T", preferences.ShowRemainingTime),
            new Separator(),
            Item(Loc.Get("Classic.Shuffle"), () => Run(() => model.ToggleShuffleAsync()), "S", model.Player.IsShuffleEnabled),
            Item(Loc.Get("Classic.Repeat"), () => Run(() => model.ToggleRepeatAsync()), "R", model.Player.CurrentRepeatMode != RepeatMode.Off),
            new Separator(),
            Item(Loc.Get("Menu.Quit"), exitApplication, "Ctrl+Q"),
        ]);
    }

    private IEnumerable<object> SkinItems()
    {
        string current = model.Preferences.SkinId ?? string.Empty;
        foreach (InstalledSkin skin in skins.List())
        {
            InstalledSkin chosen = skin;
            MenuItem item = Item(skin.IsBuiltIn ? Loc.Get("Classic.BuiltInSkin") : skin.Name, () => UseSkin(chosen));
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = skin.Id == current;
            yield return item;
        }
        yield return new Separator();
        yield return Item(Loc.Get("Classic.ManageSkins"), () => Run(ManageSkinsAsync));
    }

    public void UseSkin(InstalledSkin skin)
    {
        view.ChangeSkin(skins.Load(skin.Id));
        string? id = skin.IsBuiltIn ? null : skin.Id;
        Run(() => model.Player.UpdateClassicPlayerAsync(preferences => preferences with { SkinId = id }));
        LayoutChanged();
    }

    private async Task ManageSkinsAsync()
    {
        SkinManagerWindow manager = new(skins, model.Player.IsOnlineLookupEnabled ? gallery : null, picker, model.Preferences.SkinId ?? string.Empty);
        manager.SkinChosen += (_, skin) => UseSkin(skin);
        manager.SkinRemoved += (_, skin) =>
        {
            if ((model.Preferences.SkinId ?? string.Empty) == skin.Id) UseSkin(SkinLibrary.BuiltIn);
        };
        await manager.ShowDialog(this);
    }

    public void ShowPresetsMenu()
    {
        IEqualizerHost equalizer = model.Player;
        List<object> items = [];
        foreach ((string id, double preamp, double[] bands) in EqualizerPresets.All)
        {
            double[] curve = bands;
            double gain = preamp;
            items.Add(Item(Loc.Get($"Equalizer.Preset.{id}"),
                () => Run(() => equalizer.ApplyAsync(equalizer.Current with { IsEnabled = true, Preamp = gain, Bands = curve }, persist: true))));
        }
        items.Add(new Separator());
        items.Add(Item(Loc.Get("Equalizer.Reset"),
            () => Run(() => equalizer.ApplyAsync(equalizer.Current with { Preamp = 0, Bands = new double[EqualizerSettings.BandCount] }, persist: true))));
        Show(items);
    }

    public void ShowSortMenu() => Show(SortItems());

    private List<object> SortItems() =>
    [
        Item(Loc.Get("Classic.SortTitle"), () => model.Sort(ClassicPlaylistSort.Title)),
        Item(Loc.Get("Classic.SortFileName"), () => model.Sort(ClassicPlaylistSort.FileName)),
        Item(Loc.Get("Classic.SortPath"), () => model.Sort(ClassicPlaylistSort.Path)),
        new Separator(),
        Item(Loc.Get("Classic.Reverse"), () => model.Sort(ClassicPlaylistSort.Reverse)),
        Item(Loc.Get("Classic.Randomize"), () => model.Sort(ClassicPlaylistSort.Randomize)),
    ];

    public void ShowMiscMenu() => Show(
    [
        Item(Loc.Get("Classic.AddFromLibrary"), () => Run(SearchLibraryAsync), "J"),
        Submenu(Loc.Get("Classic.LoadLibraryPlaylist"), LibraryPlaylistItems()),
        new Separator(),
        Item(Loc.Get("Classic.RemoveDuplicates"), model.RemoveDuplicates),
    ]);

    public void ShowLoadListMenu()
    {
        List<object> items = [Item(Loc.Get("Classic.LoadPlaylistFile"), () => Run(LoadPlaylistFileAsync)), new Separator()];
        items.AddRange(LibraryPlaylistItems());
        Show(items);
    }

    private List<object> LibraryPlaylistItems()
    {
        List<object> items = [];
        foreach (PlaylistFilterViewModel playlist in model.Player.Playlists.Where(playlist => playlist.Id is not null))
        {
            PlaylistFilterViewModel chosen = playlist;
            items.Add(Item(playlist.Name, () => Run(async () => model.Replace(await model.Player.GetPlaylistTracksAsync(chosen)))));
        }
        if (items.Count == 0) items.Add(Item(Loc.Get("Classic.NoLibraryPlaylists"), () => { }, enabled: false));
        return items;
    }

    public void ShowPlaylistContextMenu()
    {
        bool any = model.SelectedCount > 0;
        Show(
        [
            Item(Loc.Get("Classic.PlaySelected"), () => Run(() => model.PlayEntryAsync(model.FocusIndex)), "Enter", enabled: model.FocusIndex >= 0),
            Item(Loc.Get("Classic.RemoveSelected"), model.RemoveSelected, "Delete", enabled: any),
            Item(Loc.Get("Classic.Crop"), model.Crop, enabled: any),
            Item(Loc.Get("Classic.FileInfo"), ShowFileInfo, enabled: any),
            new Separator(),
            Item(Loc.Get("Classic.AddFiles"), () => Run(() => OpenFilesAsync(replace: false))),
            Item(Loc.Get("Classic.AddFolder"), () => Run(AddFolderAsync)),
            Item(Loc.Get("Classic.AddFromLibrary"), () => Run(SearchLibraryAsync), "J"),
            Submenu(Loc.Get("Classic.LoadLibraryPlaylist"), LibraryPlaylistItems()),
            new Separator(),
            Submenu(Loc.Get("Classic.Sort"), SortItems()),
            Item(Loc.Get("Menu.SelectAll"), model.SelectAll, "Ctrl+A"),
        ]);
    }

    public void ShowFileInfo()
    {
        IReadOnlyList<Track> selected = model.SelectedTracks();
        Track? track = selected.Count > 0 ? selected[0] : model.DisplayedTrack;
        if (track is null) return;
        Run(() => new ClassicFileInfoWindow(track).ShowDialog(this));
    }

    // ---- Files -----------------------------------------------------------------------------------------------------

    public async Task OpenFilesAsync(bool replace)
    {
        IReadOnlyList<Uri> files = await picker.PickFilesAsync(this, new FilePickerOpenOptions
        {
            Title = Loc.Get(replace ? "Classic.OpenFiles" : "Classic.AddFiles"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Loc.Get("Picker.MusicFiles")) { Patterns = MusicPatterns }, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        await AddAsync(await model.Player.ReadTracksAsync(files), replace);
    }

    public async Task AddFolderAsync()
    {
        Uri? folder = await picker.PickFolderAsync(this, new FolderPickerOpenOptions { Title = Loc.Get("Classic.AddFolder"), AllowMultiple = false });
        if (folder is not { IsFile: true } || discovery is null) return;
        List<Uri> files = [];
        await foreach (Uri file in discovery.DiscoverAsync([folder.LocalPath])) files.Add(file);
        files.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.LocalPath, right.LocalPath));
        await AddAsync(await model.Player.ReadTracksAsync(files), replace: false);
    }

    public async Task SearchLibraryAsync()
    {
        LibraryPickerWindow pickerWindow = new(await model.Player.ListLibraryTracksAsync());
        await pickerWindow.ShowDialog(this);
        if (pickerWindow.Chosen.Count == 0) return;
        int first = model.Add(pickerWindow.Chosen);
        if (pickerWindow.PlayChosen) await model.PlayEntryAsync(first);
    }

    public async Task SavePlaylistAsync()
    {
        if (model.Entries.Count == 0) return;
        Uri? file = await picker.PickSaveFileAsync(this, new FilePickerSaveOptions
        {
            Title = Loc.Get("Classic.SavePlaylist"),
            SuggestedFileName = "playlist.m3u8",
            DefaultExtension = "m3u8",
            FileTypeChoices = [new FilePickerFileType("M3U8") { Patterns = ["*.m3u8"] }, new FilePickerFileType("PLS") { Patterns = ["*.pls"] }],
        });
        if (file is not null) await model.Player.WritePlaylistFileAsync(file, model.Tracks());
    }

    private async Task LoadPlaylistFileAsync()
    {
        Uri? file = await picker.PickFileAsync(this, new FilePickerOpenOptions
        {
            Title = Loc.Get("Classic.LoadPlaylistFile"),
            FileTypeFilter = [new FilePickerFileType(Loc.Get("Classic.PlaylistFiles")) { Patterns = ["*.m3u", "*.m3u8", "*.pls"] }],
        });
        if (file is not null) model.Replace(await model.Player.ReadPlaylistFileAsync(file));
    }

    private async Task AddAsync(IReadOnlyList<Track> tracks, bool replace)
    {
        if (tracks.Count == 0) return;
        if (replace)
        {
            model.Replace(tracks);
            await model.PlayEntryAsync(0);
        }
        else
        {
            model.Add(tracks);
        }
    }

    private void Dropped(object? sender, DragEventArgs args)
    {
        IStorageItem[] items = args.DataTransfer.TryGetFiles() ?? [];
        List<Uri> files = [];
        List<string> folders = [];
        foreach (IStorageItem item in items)
        {
            if (item.TryGetLocalPath() is not { } path) continue;
            if (Directory.Exists(path)) folders.Add(path);
            else files.Add(new Uri(path));
        }
        Run(async () =>
        {
            if (folders.Count > 0 && discovery is not null)
                await foreach (Uri file in discovery.DiscoverAsync(folders)) files.Add(file);
            await AddAsync(await model.Player.ReadTracksAsync(files), replace: false);
        });
    }

    private void WindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && args.Key == Key.M)
        {
            args.Handled = true;
            switchToStandard();
        }
        else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.Q)
        {
            args.Handled = true;
            exitApplication();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        LayoutChanged();
        view.Focus();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        LayoutChanged();
    }
}
