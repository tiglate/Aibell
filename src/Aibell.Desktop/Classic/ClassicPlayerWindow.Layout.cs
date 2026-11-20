using Aibell.Application.Settings;
using Aibell.Skins.Classic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Aibell.Desktop.Classic;

/// <summary>
/// The window layouts of Classic Player Mode. Stacked: one window draws all three parts (the default under a tiling compositor). Separate (the default elsewhere): the main
/// window, the equalizer and the playlist are windows of their own that snap together; dragging the main window carries the
/// windows docked to it, dragging another pulls it away. Aibell moves the windows itself during a drag (rather than leaving
/// it to the window manager) so docked windows move in the same instant and never trail behind.
/// </summary>
internal sealed partial class ClassicPlayerWindow
{
    private const int MainIndex = 0, EqualizerIndex = 1, PlaylistIndex = 2;

    private ClassicPlayerView activeSurface = null!;
    private ClassicSectionWindow? equalizerWindow;
    private ClassicSectionWindow? playlistWindow;
    private bool growingForMenu;
    private bool placed;
    private bool applyingLayout;
    private Drag? drag;
    // Where each window is, as far as Aibell knows: what it last asked for, or what the system last reported. A window's
    // own Position lags behind (it reads (0, 0) until the system has placed a newly opened window).
    private readonly PixelPoint[] known = new PixelPoint[3];

    private sealed record Drag(int[] Group, PixelPoint Grab, PixelPoint[] Start);

    /// <summary>
    /// The layout used until the user picks one. A tiling compositor would tile the separate windows apart and ignores where
    /// Aibell asks to put them, so there the player starts as one window. Tests pin it so the machine running them does not matter.
    /// </summary>
    internal static bool SeparateWindowsByDefault { get; set; } = !Services.TitleBarPolicy.IsTilingSession(Environment.GetEnvironmentVariable);

    internal bool Separate => model.Preferences.SeparateWindows ?? SeparateWindowsByDefault;

    /// <summary>The equalizer and playlist windows of the separate layout (null while stacked).</summary>
    internal ClassicSectionWindow? EqualizerWindow => equalizerWindow;

    internal ClassicSectionWindow? PlaylistWindow => playlistWindow;

    // The main, equalizer and playlist windows; the last two are null while stacked.
    private Window?[] Windows => [this, equalizerWindow, playlistWindow];

    private ClassicPlayerView?[] Surfaces => [view, equalizerWindow?.Surface, playlistWindow?.Surface];

    /// <summary>Gives a classic window what every classic window has: menus that fit, dropped files, the mode keys.</summary>
    private void Prepare(Window window, ClassicPlayerView surface)
    {
        window.Styles.Add(new Style(selector => selector.OfType<MenuItem>())
        {
            Setters = { new Setter(MenuItem.PaddingProperty, new Thickness(8, 3)) },
        });
        DragDrop.SetAllowDrop(window, true);
        window.AddHandler(DragDrop.DragOverEvent, (_, args) => args.DragEffects = args.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        window.AddHandler(DragDrop.DropEvent, Dropped);
        window.Activated += (_, _) =>
        {
            surface.IsWindowActive = true;
            activeSurface = surface;
        };
        window.Deactivated += (_, _) => surface.IsWindowActive = false;
        window.KeyDown += WindowKeyDown;
    }

    private Window WindowOf(ClassicPlayerView surface) =>
        ReferenceEquals(surface, equalizerWindow?.Surface) ? equalizerWindow! : ReferenceEquals(surface, playlistWindow?.Surface) ? playlistWindow! : this;

    private int IndexOf(ClassicPlayerView surface) =>
        ReferenceEquals(surface, equalizerWindow?.Surface) ? EqualizerIndex : ReferenceEquals(surface, playlistWindow?.Surface) ? PlaylistIndex : MainIndex;

    private void ChangeSkin(ClassicSkin skin)
    {
        foreach (ClassicPlayerView? surface in Surfaces) surface?.ChangeSkin(skin);
    }

    public void Activate(ClassicPlayerView source) => activeSurface = source;

    public void Minimize()
    {
        // The equalizer and playlist windows belong to this one, so they go and come back with it.
        WindowState = WindowState.Minimized;
    }

    // ---- Layout ----------------------------------------------------------------------------------------------------

    public void LayoutChanged()
    {
        if (applyingLayout || growingForMenu) return;
        applyingLayout = true;
        try
        {
            ClassicPlayerPreferences preferences = model.Preferences;
            // Sizes before the change, so windows docked under one that grows or shrinks can follow its edge.
            PixelRect?[] before = Rects();
            view.Sections = Separate ? ClassicSections.Main : ClassicSections.All;
            if (Separate && IsVisible) EnsureSectionWindows();
            else HideSectionWindows();
            foreach ((Window? window, ClassicPlayerView? surface) in Windows.Zip(Surfaces))
            {
                if (window is null || surface is null) continue;
                window.Topmost = preferences.AlwaysOnTop;
                surface.InvalidateMeasure();
                // A skin's outline needs a compositor that can show through the window; otherwise the window stays rectangular.
                Geometry? outline = window.ActualTransparencyLevel == WindowTransparencyLevel.Transparent ? surface.Outline() : null;
                surface.Clip = outline;
                window.Background = outline is null ? Brushes.Black : Brushes.Transparent;
            }
            if (Separate && placed && IsVisible) FollowSizes(before);
            if (Separate && IsVisible) ShowOrHideSections(preferences);
        }
        finally
        {
            applyingLayout = false;
        }
    }

    private void EnsureSectionWindows()
    {
        ClassicSkin skin = view.Skin;
        equalizerWindow ??= CreateSectionWindow(ClassicSections.Equalizer, skin, "ClassicEqualizerWindow");
        playlistWindow ??= CreateSectionWindow(ClassicSections.Playlist, skin, "ClassicPlaylistWindow");
    }

    private ClassicSectionWindow CreateSectionWindow(ClassicSections section, ClassicSkin skin, string name)
    {
        ClassicPlayerView surface = new(model, this, skin) { Sections = section, Name = name + "Surface" };
        ClassicSectionWindow window = new(surface) { Name = name, Icon = Icon };
        Prepare(window, surface);
        int index = section == ClassicSections.Equalizer ? EqualizerIndex : PlaylistIndex;
        window.PositionChanged += (_, args) => known[index] = args.Point;
        // Closing one from the window manager (Alt+F4) hides it, as its own close button does.
        window.Closing += (_, args) =>
        {
            // Only a close aimed at this window (Alt+F4) hides it; the application or its owner closing must not be saved as one.
            if (window.ClosingForExit || args.CloseReason != WindowCloseReason.WindowClosing) return;
            args.Cancel = true;
            view.ChangeLayout(preferences => section == ClassicSections.Equalizer
                ? preferences with { ShowEqualizer = false }
                : preferences with { ShowPlaylist = false });
        };
        return window;
    }

    private void ShowOrHideSections(ClassicPlayerPreferences preferences)
    {
        if (!placed) return;
        Show(equalizerWindow, preferences.ShowEqualizer, EqualizerIndex);
        Show(playlistWindow, preferences.ShowPlaylist, PlaylistIndex);
    }

    private void Show(ClassicSectionWindow? window, bool visible, int index, PixelPoint? at = null)
    {
        if (window is null || window.IsVisible == visible) return;
        if (!visible)
        {
            window.Hide();
            return;
        }
        PixelPoint position = at ?? SavedOrDocked(index);
        Move(index, position);
        window.Show(this);
        Move(index, position);
    }

    private void HideSectionWindows()
    {
        equalizerWindow?.Hide();
        playlistWindow?.Hide();
    }

    private void DisposeSectionWindows()
    {
        foreach (ClassicSectionWindow? window in new[] { equalizerWindow, playlistWindow })
        {
            if (window is null) continue;
            window.ClosingForExit = true;
            window.Close();
            window.Surface.Dispose();
        }
        equalizerWindow = playlistWindow = null;
    }

    /// <summary>Puts the windows where they were left, or stacks them in the middle of the screen the first time.</summary>
    private void Move(int index, PixelPoint position)
    {
        known[index] = position;
        Windows[index]!.Position = position;
    }

    private void PlaceWindows()
    {
        // The saved layout is needed first; the windows are placed again when it arrives.
        if (!model.Player.ArePreferencesLoaded) return;
        if (!Separate)
        {
            placed = true;
            return;
        }
        EnsureSectionWindows();
        ClassicPlayerPreferences preferences = model.Preferences;
        PixelSize[] sizes = [TargetSize(MainIndex), TargetSize(EqualizerIndex), TargetSize(PlaylistIndex)];
        PixelRect workArea = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        List<PixelSize> stack = [sizes[MainIndex]];
        if (preferences.ShowEqualizer) stack.Add(sizes[EqualizerIndex]);
        if (preferences.ShowPlaylist) stack.Add(sizes[PlaylistIndex]);
        PixelPoint[] layout = ClassicDocking.DefaultLayout(workArea, stack);
        // Positions are worked out here rather than read back: a window's position only updates once the system has moved it.
        PixelPoint main = Reachable(preferences.MainPosition, sizes[MainIndex]) ?? layout[0];
        Move(MainIndex, main);
        placed = true;
        PixelPoint equalizer = Reachable(preferences.EqualizerPosition, sizes[EqualizerIndex]) ?? new PixelPoint(main.X, main.Y + sizes[MainIndex].Height);
        PixelPoint playlist = Reachable(preferences.PlaylistPosition, sizes[PlaylistIndex])
            ?? (preferences.ShowEqualizer ? new PixelPoint(equalizer.X, equalizer.Y + sizes[EqualizerIndex].Height) : new PixelPoint(main.X, main.Y + sizes[MainIndex].Height));
        Show(equalizerWindow, preferences.ShowEqualizer, EqualizerIndex, equalizer);
        Show(playlistWindow, preferences.ShowPlaylist, PlaylistIndex, playlist);
        if (equalizerWindow is { IsVisible: true }) Move(EqualizerIndex, equalizer);
        if (playlistWindow is { IsVisible: true }) Move(PlaylistIndex, playlist);
        Avalonia.Threading.DispatcherTimer.RunOnce(SavePositions, TimeSpan.FromMilliseconds(500));
    }

    // A saved spot, while it is still on a screen.
    private PixelPoint? Reachable(ClassicWindowPosition? saved, PixelSize size)
    {
        if (saved is null) return null;
        PixelPoint point = new(saved.X, saved.Y);
        return ClassicDocking.IsReachable(new PixelRect(point, size), Screens.All.Select(screen => screen.Bounds)) ? point : null;
    }

    // Where a section window opens: where it was left, or else docked under the windows above it.
    private PixelPoint SavedOrDocked(int index)
    {
        ClassicPlayerPreferences preferences = model.Preferences;
        ClassicWindowPosition? saved = index == EqualizerIndex ? preferences.EqualizerPosition : preferences.PlaylistPosition;
        if (Reachable(saved, TargetSize(index)) is { } point) return point;
        PixelRect above = new(known[MainIndex], TargetSize(MainIndex));
        if (index == PlaylistIndex && equalizerWindow is { IsVisible: true }) above = new PixelRect(known[EqualizerIndex], TargetSize(EqualizerIndex));
        return new PixelPoint(above.X, above.Bottom);
    }

    // The size a window is about to take, in screen pixels (its own size may not have caught up yet).
    private PixelSize TargetSize(int index)
    {
        ClassicPlayerView? surface = Surfaces[index];
        Window? window = Windows[index];
        if (surface is null || window is null) return default;
        double scale = surface.Scale * window.RenderScaling;
        return new PixelSize((int)Math.Round(surface.SkinSize.Width * scale), (int)Math.Round(surface.SkinSize.Height * scale));
    }

    private PixelRect?[] Rects() =>
    [
        .. Windows.Select((window, index) => window is { IsVisible: true } && Surfaces[index] is not null
            ? new PixelRect(known[index], PixelSize.FromSize(window.ClientSize, window.RenderScaling))
            : (PixelRect?)null),
    ];

    // A window that grew or shrank (rolled up, double size, a longer playlist) carries the windows docked under it.
    private void FollowSizes(PixelRect?[] before)
    {
        PixelRect?[] rects = [.. before];
        for (int index = 0; index < rects.Length; index++)
        {
            if (rects[index] is not { } old || Windows[index] is null) continue;
            PixelSize size = TargetSize(index);
            if (size == old.Size) continue;
            foreach ((int other, PixelPoint position) in ClassicDocking.FollowResize(rects, index, size))
            {
                Move(other, position);
                rects[other] = new PixelRect(position, rects[other]!.Value.Size);
            }
            rects[index] = new PixelRect(old.Position, size);
        }
        SavePositions();
    }

    private void SavePositions()
    {
        if (!Separate || !placed) return;
        ClassicWindowPosition? At(Window? window, int index) => window is { IsVisible: true } ? new ClassicWindowPosition(known[index].X, known[index].Y) : null;
        ClassicWindowPosition main = new(known[MainIndex].X, known[MainIndex].Y);
        ClassicWindowPosition? equalizer = At(equalizerWindow, EqualizerIndex), playlist = At(playlistWindow, PlaylistIndex);
        Run(() => model.Player.UpdateClassicPlayerAsync(preferences => preferences with
        {
            MainPosition = main,
            EqualizerPosition = equalizer ?? preferences.EqualizerPosition,
            PlaylistPosition = playlist ?? preferences.PlaylistPosition,
        }));
    }

    // ---- Dragging --------------------------------------------------------------------------------------------------

    public bool BeginDrag(ClassicPlayerView source, PointerPressedEventArgs args)
    {
        Window window = WindowOf(source);
        if (!Separate)
        {
            window.BeginMoveDrag(args);
            return false;
        }
        int index = IndexOf(source);
        // The main window carries everything docked to it; the others come away on their own.
        int[] group = index == MainIndex ? [.. ClassicDocking.Group(Rects(), MainIndex)] : [index];
        drag = new Drag(group, source.PointToScreen(args.GetPosition(source)), [.. group.Select(member => known[member])]);
        return true;
    }

    public void DragTo(ClassicPlayerView source, PixelPoint pointer)
    {
        if (drag is null) return;
        PixelRect?[] rects = Rects();
        PixelRect[] moving = [.. drag.Group.Select((member, at) => new PixelRect(drag.Start[at], rects[member]?.Size ?? default))];
        PixelRect[] others = [.. rects.Select((rect, index) => (rect, index)).Where(pair => pair.rect is not null && !drag.Group.Contains(pair.index)).Select(pair => pair.rect!.Value)];
        PixelRect screen = (Screens.ScreenFromPoint(pointer) ?? Screens.Primary)?.WorkingArea ?? new PixelRect(0, 0, 0, 0);
        PixelPoint delta = ClassicDocking.Snap(moving, others, screen, new PixelPoint(pointer.X - drag.Grab.X, pointer.Y - drag.Grab.Y));
        for (int at = 0; at < drag.Group.Length; at++)
            Move(drag.Group[at], new PixelPoint(drag.Start[at].X + delta.X, drag.Start[at].Y + delta.Y));
    }

    public void EndDrag(ClassicPlayerView source)
    {
        drag = null;
        SavePositions();
    }
}

/// <summary>The equalizer or the playlist in a window of its own (the separate layout).</summary>
internal sealed class ClassicSectionWindow : Window
{
    public ClassicSectionWindow(ClassicPlayerView surface)
    {
        Surface = surface;
        Title = "Aibell";
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FlowDirection = FlowDirection.LeftToRight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        surface.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        surface.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Content = surface;
    }

    public ClassicPlayerView Surface { get; }

    /// <summary>Set when the application closes it for good, so the close is not turned into a hide.</summary>
    public bool ClosingForExit { get; set; }
}
