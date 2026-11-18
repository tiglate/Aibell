using System.Globalization;
using Aibell.Application.Playback;
using Aibell.Application.Settings;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Aibell.Skins.Classic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Aibell.Desktop.Classic;

/// <summary>The parts of the classic player a surface shows: all three stacked, or one per window.</summary>
[Flags]
internal enum ClassicSections
{
    Main = 1,
    Equalizer = 2,
    Playlist = 4,
    All = Main | Equalizer | Playlist,
}

/// <summary>What the skinned surfaces ask of the windows around them: moving, menus, dialogs.</summary>
internal interface IClassicPlayerHost
{
    /// <summary>A press on a surface's background: true when the host moves the windows itself, following <see cref="DragTo"/>.</summary>
    bool BeginDrag(ClassicPlayerView source, PointerPressedEventArgs args);

    /// <summary>The pointer, in screen pixels, during a drag the host accepted.</summary>
    void DragTo(ClassicPlayerView source, PixelPoint pointer);

    void EndDrag(ClassicPlayerView source);

    /// <summary>The surface the user is working with, so menus open over it.</summary>
    void Activate(ClassicPlayerView source);

    void Minimize();

    void Exit();

    void LayoutChanged();

    void ShowOptionsMenu();

    void ShowPresetsMenu();

    void ShowSortMenu();

    void ShowFileInfo();

    void ShowMiscMenu();

    void ShowLoadListMenu();

    void ShowPlaylistContextMenu();

    Task OpenFilesAsync(bool replace);

    Task AddFolderAsync();

    Task SearchLibraryAsync();

    Task SavePlaylistAsync();

    void Run(Func<Task> action);
}

/// <summary>
/// The skinned player: main window, equalizer and playlist stacked in one surface, drawn from the skin's bitmaps at one or two
/// skin pixels per screen pixel. Every coordinate here is in skin pixels, as the format defines them.
/// </summary>
internal sealed class ClassicPlayerView : Control, IDisposable
{
    public const int SkinWidth = 275;
    private const int MainHeight = 116;
    private const int ShadeHeight = 14;
    private const int PlaylistMinimumHeight = 116;
    private const int PlaylistStep = 29;
    private const int RowHeight = 13;
    private const int VisualizerBars = 19;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    private readonly ClassicPlayerViewModel model;
    private readonly IClassicPlayerHost host;
    private readonly DispatcherTimer timer = new() { Interval = Frame };
    private SkinBitmaps bitmaps;
    private ISpectrumSource? spectrum;
    private readonly float[] fresh = new float[VisualizerBars];
    private readonly float[] levels = new float[VisualizerBars];
    private readonly float[] peaks = new float[VisualizerBars];
    private bool visualizerOn = true;
    private bool active = true;

    private Part pressed;
    private int pressedArgument;
    private bool pressedInside;
    private double dragValue;
    private double dragGrab;
    private int dragRow = -1;
    private int openMenu = -1;
    private int menuHover = -1;
    // Where the press that opened a menu happened: a click without moving leaves the menu open to pick from.
    private Point? menuOpenedAt;
    private int scrollRow;
    private int marqueeOffset;
    private int marqueeTicks;
    private DateTime lastVolumeSend;

    public ClassicPlayerView(ClassicPlayerViewModel model, IClassicPlayerHost host, ClassicSkin skin)
    {
        this.model = model;
        this.host = host;
        bitmaps = new SkinBitmaps(skin);
        Focusable = true;
        ClipToBounds = true;
        // Pixels stay pixels: no smoothing when the skin is drawn at double size.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        FlowDirection = FlowDirection.LeftToRight;
        timer.Tick += (_, _) => Tick();
        model.PlaylistChanged += (_, _) =>
        {
            ClampScroll();
            InvalidateVisual();
        };
    }

    private enum Part
    {
        None,
        WindowDrag,
        MainTitle, Options, Minimize, Shade, Close, ClutterOptions, ClutterTop, ClutterInfo, ClutterDouble, ClutterVisualizer,
        Time, Visualizer, Volume, Balance, EqualizerToggle, PlaylistToggle, Position, Transport, Shuffle, Repeat,
        EqualizerTitle, EqualizerClose, EqualizerShade, EqualizerOn, EqualizerAuto, EqualizerPresets, EqualizerSlider,
        PlaylistTitle, PlaylistClose, PlaylistShade, PlaylistRows, PlaylistScroll, PlaylistMenu, PlaylistMini, PlaylistResize, PlaylistTime,
    }

    public ClassicSkin Skin => bitmaps.Skin;

    /// <summary>Which parts this surface draws; all of them in the stacked layout, one in each separate window.</summary>
    public ClassicSections Sections
    {
        get => sections;
        set
        {
            sections = value;
            timer.Interval = value.HasFlag(ClassicSections.Main) ? Frame : TimeSpan.FromMilliseconds(100);
            UpdateSpectrum();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private ClassicSections sections = ClassicSections.All;

    private bool Stacked => sections == ClassicSections.All;

    private ClassicPlayerPreferences Preferences => model.Preferences;

    private MainWindowViewModel Player => model.Player;

    public int Scale => Preferences.DoubleSize ? 2 : 1;

    private int MainSectionHeight => !sections.HasFlag(ClassicSections.Main) ? 0 : Preferences.MainShaded ? ShadeHeight : MainHeight;

    private int EqualizerTop => MainSectionHeight;

    // In a separate window the window itself is hidden instead, so the section is always drawn there.
    private int EqualizerSectionHeight => !sections.HasFlag(ClassicSections.Equalizer) || (Stacked && !Preferences.ShowEqualizer) ? 0
        : Preferences.EqualizerShaded ? ShadeHeight : MainHeight;

    private int PlaylistTop => EqualizerTop + EqualizerSectionHeight;

    private int PlaylistSectionHeight => !sections.HasFlag(ClassicSections.Playlist) || (Stacked && !Preferences.ShowPlaylist) ? 0
        : Preferences.PlaylistShaded ? ShadeHeight : PlaylistMinimumHeight + PlaylistStep * Preferences.PlaylistExtraRows;

    /// <summary>The surface's size in skin pixels.</summary>
    public Size SkinSize => new(SkinWidth, PlaylistTop + PlaylistSectionHeight);

    public bool IsWindowActive
    {
        get => active;
        set
        {
            active = value;
            InvalidateVisual();
        }
    }

    public void Dispose()
    {
        timer.Stop();
        spectrum?.SetSpectrumEnabled(false);
        spectrum = null;
        bitmaps.Dispose();
    }

    public void ChangeSkin(ClassicSkin skin)
    {
        SkinBitmaps previous = bitmaps;
        bitmaps = new SkinBitmaps(skin);
        previous.Dispose();
        InvalidateVisual();
    }

    /// <summary>The window outline the skin asks for, in screen pixels, or null for a plain rectangle.</summary>
    public Geometry? Outline()
    {
        SkinRegions regions = Skin.Regions;
        IReadOnlyList<IReadOnlyList<(int X, int Y)>> main = Preferences.MainShaded ? regions.MainShade : regions.Main;
        IReadOnlyList<IReadOnlyList<(int X, int Y)>> equalizer = Preferences.EqualizerShaded ? regions.EqualizerShade : regions.Equalizer;
        if (main.Count == 0 && (equalizer.Count == 0 || EqualizerSectionHeight == 0)) return null;
        GeometryGroup group = new() { FillRule = FillRule.NonZero };
        void Section(IReadOnlyList<IReadOnlyList<(int X, int Y)>> polygons, int top, int height)
        {
            if (height == 0) return;
            if (polygons.Count == 0)
            {
                group.Children.Add(new RectangleGeometry(new Rect(0, top * Scale, SkinWidth * Scale, height * Scale)));
                return;
            }
            foreach (IReadOnlyList<(int X, int Y)> polygon in polygons)
            {
                StreamGeometry geometry = new();
                using (StreamGeometryContext context = geometry.Open())
                {
                    context.BeginFigure(new Point(polygon[0].X * Scale, (polygon[0].Y + top) * Scale), true);
                    foreach ((int x, int y) in polygon.Skip(1)) context.LineTo(new Point(x * Scale, (y + top) * Scale));
                    context.EndFigure(true);
                }
                group.Children.Add(geometry);
            }
        }
        Section(main, 0, MainSectionHeight);
        Section(equalizer, EqualizerTop, EqualizerSectionHeight);
        Section([], PlaylistTop, PlaylistSectionHeight);
        return group;
    }

    protected override Size MeasureOverride(Size availableSize) => new(SkinSize.Width * Scale, SkinSize.Height * Scale);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        timer.Start();
        UpdateSpectrum();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        spectrum?.SetSpectrumEnabled(false);
        spectrum = null;
        base.OnDetachedFromVisualTree(e);
    }

    // ---- Timer -----------------------------------------------------------------------------------------------------

    private void Tick()
    {
        model.Synchronize();
        UpdateSpectrum();
        if (spectrum is { } source && !Preferences.MainShaded)
        {
            bool live = Player.PlaybackSnapshot.Status == PlaybackStatus.Playing && source.IsSpectrumAvailable && source.TryReadSpectrum(fresh);
            if (!live) Array.Clear(fresh);
            for (int bar = 0; bar < VisualizerBars; bar++)
            {
                levels[bar] = fresh[bar] >= levels[bar] ? fresh[bar] : Math.Max(fresh[bar], levels[bar] - 0.06f);
                peaks[bar] = levels[bar] >= peaks[bar] ? levels[bar] : Math.Max(levels[bar], peaks[bar] - 0.012f);
            }
        }
        // The title moves one character every few frames, like the original players.
        if (++marqueeTicks >= 6)
        {
            marqueeTicks = 0;
            marqueeOffset++;
        }
        InvalidateVisual();
    }

    private void UpdateSpectrum()
    {
        ISpectrumSource? wanted = visualizerOn && sections.HasFlag(ClassicSections.Main) && Player.VisualizerEnabled && IsEffectivelyVisible
            ? Player.SpectrumSource : null;
        if (ReferenceEquals(wanted, spectrum)) return;
        spectrum?.SetSpectrumEnabled(false);
        spectrum = wanted;
        spectrum?.SetSpectrumEnabled(true);
        Array.Clear(levels);
        Array.Clear(peaks);
    }

    // ---- Drawing ---------------------------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        using (context.PushTransform(Matrix.CreateScale(Scale, Scale)))
        {
            if (MainSectionHeight > 0) DrawMain(context);
            if (EqualizerSectionHeight > 0)
                using (context.PushTransform(Matrix.CreateTranslation(0, EqualizerTop)))
                    DrawEqualizer(context);
            if (PlaylistSectionHeight > 0)
                using (context.PushTransform(Matrix.CreateTranslation(0, PlaylistTop)))
                    DrawPlaylist(context);
        }
    }

    private void Draw(DrawingContext context, Sprite sprite, double x, double y)
    {
        Bitmap bitmap = bitmaps[sprite.Sheet];
        context.DrawImage(bitmap, new Rect(sprite.X, sprite.Y, sprite.Width, sprite.Height), new Rect(x, y, sprite.Width, sprite.Height));
    }

    // Draws part of a sprite (a tile cut short at an edge).
    private void DrawClipped(DrawingContext context, Sprite sprite, double x, double y, int width, int height) =>
        Draw(context, sprite with { Width = Math.Min(width, sprite.Width), Height = Math.Min(height, sprite.Height) }, x, y);

    private void Text(DrawingContext context, string text, int x, int y, int maximumCharacters = int.MaxValue)
    {
        for (int index = 0; index < Math.Min(text.Length, maximumCharacters); index++)
            Draw(context, Sprites.Character(text[index]), x + index * Sprites.CharacterWidth, y);
    }

    private bool IsPressed(Part part, int argument = 0) => pressed == part && pressedArgument == argument && pressedInside;

    private void DrawMain(DrawingContext context)
    {
        PlaybackSnapshot snapshot = Player.PlaybackSnapshot;
        bool playing = snapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused;
        if (Preferences.MainShaded)
        {
            Draw(context, active ? Sprites.ShadeBackgroundSelected : Sprites.ShadeBackground, 0, 0);
            DrawTitleButtons(context, shaded: true);
            if (model.DisplayTime is { } time) Text(context, MiniTime(time), 127, 4);
            if (playing && snapshot.Track is { Duration.TotalSeconds: > 0 } track)
            {
                double fraction = PositionFraction(snapshot, track);
                Sprite thumb = fraction < 1 / 3.0 ? Sprites.ShadePositionThumbLeft : fraction < 2 / 3.0 ? Sprites.ShadePositionThumb : Sprites.ShadePositionThumbRight;
                Draw(context, thumb, 226 + Math.Round(fraction * 14), 4);
            }
            return;
        }
        Draw(context, Sprites.MainBackground, 0, 0);
        Draw(context, active ? Sprites.TitleBarSelected : Sprites.TitleBar, 0, 0);
        DrawTitleButtons(context, shaded: false);
        Draw(context, Sprites.ClutterBar, 10, 22);
        (Part Part, int Top, int Height, int SheetX, int SheetY, bool Lit)[] clutter =
        [
            (Part.ClutterOptions, 3, 8, 304, 47, false), (Part.ClutterTop, 11, 7, 312, 55, Preferences.AlwaysOnTop),
            (Part.ClutterInfo, 18, 7, 320, 62, false), (Part.ClutterDouble, 25, 8, 328, 69, Preferences.DoubleSize),
            (Part.ClutterVisualizer, 33, 7, 336, 77, false),
        ];
        foreach ((Part part, int top, int height, int sheetX, int sheetY, bool lit) in clutter)
            if (lit || IsPressed(part)) Draw(context, new Sprite(SkinSheet.TitleBar, sheetX, sheetY, 8, height), 10, 22 + top);

        Draw(context, snapshot.Status switch
        {
            PlaybackStatus.Playing => Sprites.PlayingIndicator,
            PlaybackStatus.Paused => Sprites.PausedIndicator,
            _ => Sprites.StoppedIndicator,
        }, 26, 28);
        if (model.DisplayTime is { } shown && (snapshot.Status != PlaybackStatus.Paused || (DateTime.Now.Millisecond / 500) == 0)) DrawTime(context, shown);
        DrawVisualizer(context);

        string marquee = TransientText() ?? model.MarqueeText;
        if (marquee.All(TextFont.Supports)) Text(context, Scroll(marquee, 31), 111, 27, 31);
        else DrawFallbackMarquee(context, marquee);
        Text(context, model.BitRateText.PadLeft(3), 111, 43);
        Text(context, model.SampleRateText.PadLeft(2), 156, 43);
        int channels = model.ChannelIndicator;
        Draw(context, Sprites.Mono(channels == 1), 212, 41);
        Draw(context, Sprites.Stereo(channels == 2), 239, 41);

        double volume = pressed == Part.Volume ? dragValue : Player.Volume;
        Draw(context, Sprites.VolumeFrame((int)Math.Round(volume * 27)), 107, 57);
        Draw(context, IsPressed(Part.Volume) || pressed == Part.Volume ? Sprites.VolumeThumbPressed : Sprites.VolumeThumb, 107 + Math.Round(volume * 51), 58);
        double balance = pressed == Part.Balance ? dragValue : Player.Balance;
        Draw(context, Sprites.BalanceFrame((int)Math.Round(Math.Abs(balance) * 27)), 177, 57);
        Draw(context, pressed == Part.Balance ? Sprites.BalanceThumbPressed : Sprites.BalanceThumb, 177 + Math.Round((balance + 1) / 2 * 24), 58);
        Draw(context, Sprites.EqualizerToggle(Preferences.ShowEqualizer, IsPressed(Part.EqualizerToggle)), 219, 58);
        Draw(context, Sprites.PlaylistToggle(Preferences.ShowPlaylist, IsPressed(Part.PlaylistToggle)), 242, 58);

        Draw(context, Sprites.PositionBackground, 16, 72);
        if (playing && snapshot.Track is { Duration.TotalSeconds: > 0 } current)
        {
            double fraction = pressed == Part.Position ? dragValue : PositionFraction(snapshot, current);
            Draw(context, pressed == Part.Position ? Sprites.PositionThumbPressed : Sprites.PositionThumb, 16 + Math.Round(fraction * 219), 72);
        }

        Sprite[] normal = [Sprites.Previous, Sprites.Play, Sprites.Pause, Sprites.Stop, Sprites.Next];
        Sprite[] down = [Sprites.PreviousPressed, Sprites.PlayPressed, Sprites.PausePressed, Sprites.StopPressed, Sprites.NextPressed];
        for (int button = 0; button < 5; button++) Draw(context, IsPressed(Part.Transport, button) ? down[button] : normal[button], 16 + 23 * button, 88);
        Draw(context, IsPressed(Part.Transport, 5) ? Sprites.EjectPressed : Sprites.Eject, 136, 89);
        Draw(context, Sprites.Shuffle(Player.IsShuffleEnabled, IsPressed(Part.Shuffle)), 164, 89);
        Draw(context, Sprites.Repeat(Player.CurrentRepeatMode != RepeatMode.Off, IsPressed(Part.Repeat)), 210, 89);
    }

    private void DrawTitleButtons(DrawingContext context, bool shaded)
    {
        if (IsPressed(Part.Options)) Draw(context, Sprites.OptionsButtonPressed, 6, 3);
        if (IsPressed(Part.Minimize)) Draw(context, Sprites.MinimizeButtonPressed, 244, 3);
        Sprite shade = shaded ? IsPressed(Part.Shade) ? Sprites.UnshadeButtonPressed : Sprites.UnshadeButton
            : IsPressed(Part.Shade) ? Sprites.ShadeButtonPressed : Sprites.ShadeButton;
        if (!shaded || IsPressed(Part.Shade)) Draw(context, shade, 254, 3);
        if (IsPressed(Part.Close)) Draw(context, Sprites.CloseButtonPressed, 264, 3);
        if (!shaded)
        {
            if (!IsPressed(Part.Options)) Draw(context, Sprites.OptionsButton, 6, 3);
            if (!IsPressed(Part.Minimize)) Draw(context, Sprites.MinimizeButton, 244, 3);
            if (!IsPressed(Part.Close)) Draw(context, Sprites.CloseButton, 264, 3);
        }
    }

    private void DrawTime(DrawingContext context, TimeSpan time)
    {
        bool negative = time < TimeSpan.Zero;
        TimeSpan magnitude = negative ? -time : time;
        int minutes = Math.Min(99, (int)magnitude.TotalMinutes), seconds = magnitude.Seconds;
        if (Skin.HasExtendedNumbers) Draw(context, negative ? Sprites.MinusExtended : Sprites.DigitBlank, 36, 26);
        else if (negative) Draw(context, Sprites.MinusClassic, 38, 32);
        Draw(context, Sprites.Digit(minutes / 10), 48, 26);
        Draw(context, Sprites.Digit(minutes % 10), 60, 26);
        Draw(context, Sprites.Digit(seconds / 10), 78, 26);
        Draw(context, Sprites.Digit(seconds % 10), 90, 26);
    }

    private void DrawVisualizer(DrawingContext context)
    {
        if (spectrum is null) return;
        VisualizerColors colors = Skin.Visualizer;
        for (int bar = 0; bar < VisualizerBars; bar++)
        {
            int height = (int)Math.Round(Math.Clamp(levels[bar], 0, 1) * 16);
            int x = 24 + bar * 4;
            for (int row = 16 - height; row < 16; row++)
                context.FillRectangle(Brush(colors.Spectrum(row)), new Rect(x, 43 + row, 3, 1));
            int peak = (int)Math.Round(Math.Clamp(peaks[bar], 0, 1) * 16);
            if (peak > 0) context.FillRectangle(Brush(colors.Peak), new Rect(x, 43 + 16 - peak, 3, 1));
        }
    }

    private readonly Dictionary<uint, IBrush> brushes = [];

    private IBrush Brush(uint color)
    {
        if (!brushes.TryGetValue(color, out IBrush? brush)) brushes[color] = brush = new SolidColorBrush(Color.FromUInt32(color | 0xFF000000));
        return brush;
    }

    private string Scroll(string text, int width)
    {
        if (text.Length <= width) return text;
        string loop = text + "  ***  ";
        int start = marqueeOffset % loop.Length;
        return (loop + loop)[start..(start + width)];
    }

    // Written in the skin's bitmap font, which only has Latin letters, so these stay in English as in the original players.
    private string? TransientText()
    {
        PlaybackSnapshot snapshot = Player.PlaybackSnapshot;
        if (pressed == Part.Position && snapshot.Track is { Duration.TotalSeconds: > 0 } track)
        {
            TimeSpan target = TimeSpan.FromSeconds(dragValue * track.Duration.TotalSeconds);
            return string.Create(CultureInfo.InvariantCulture,
                $"SEEK TO: {ClassicPlayerViewModel.FormatDuration(target)}/{ClassicPlayerViewModel.FormatDuration(track.Duration)} ({(int)Math.Round(dragValue * 100)}%)");
        }
        if (pressed == Part.Volume) return string.Create(CultureInfo.InvariantCulture, $"VOLUME: {(int)Math.Round(dragValue * 100)}%");
        if (pressed == Part.Balance)
        {
            int percent = (int)Math.Round(Math.Abs(dragValue) * 100);
            return percent == 0 ? "BALANCE: CENTER" : string.Create(CultureInfo.InvariantCulture, $"BALANCE: {percent}% {(dragValue < 0 ? "LEFT" : "RIGHT")}");
        }
        return null;
    }

    // Titles in scripts the bitmap font lacks (Japanese, Cyrillic...) are written with a system font in the font's colour.
    private void DrawFallbackMarquee(DrawingContext context, string text)
    {
        Rect area = new(111, 24, 154, 12);
        using (context.PushClip(area))
        {
            FormattedText formatted = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Noto Sans CJK JP, Noto Sans, Arial, sans-serif")), 8, Brush(TextColor()));
            double overflow = formatted.Width - area.Width;
            double shift = overflow <= 0 ? 0 : (marqueeOffset * 5) % (formatted.Width + 30);
            context.DrawText(formatted, new Point(area.X - shift, area.Y + (area.Height - formatted.Height) / 2));
            if (overflow > 0) context.DrawText(formatted, new Point(area.X - shift + formatted.Width + 30, area.Y + (area.Height - formatted.Height) / 2));
        }
    }

    // The colour of the bitmap font: the brightest pixel of the letter A.
    private uint TextColor()
    {
        SkinImage text = Skin[SkinSheet.Text];
        uint best = 0xFFFFFFFF;
        int brightest = -1;
        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 5; x++)
            {
                uint pixel = text[x, y];
                int light = (int)(((pixel >> 16) & 0xFF) + ((pixel >> 8) & 0xFF) + (pixel & 0xFF));
                if (light > brightest)
                {
                    brightest = light;
                    best = pixel;
                }
            }
        return best;
    }

    private static string MiniTime(TimeSpan time)
    {
        bool negative = time < TimeSpan.Zero;
        TimeSpan magnitude = negative ? -time : time;
        return string.Create(CultureInfo.InvariantCulture, $"{(negative ? "-" : " ")}{Math.Min(99, (int)magnitude.TotalMinutes):00}:{magnitude.Seconds:00}");
    }

    private static double PositionFraction(PlaybackSnapshot snapshot, Track track) =>
        Math.Clamp(snapshot.Position.TotalSeconds / track.Duration.TotalSeconds, 0, 1);

    private void DrawEqualizer(DrawingContext context)
    {
        EqualizerSettings settings = CurrentEqualizer();
        if (Preferences.EqualizerShaded)
        {
            Draw(context, active ? Sprites.EqualizerShadeSelected : Sprites.EqualizerShade, 0, 0);
            if (IsPressed(Part.EqualizerShade)) Draw(context, Sprites.EqualizerUnshadeButtonPressed, 254, 3);
            if (IsPressed(Part.EqualizerClose)) Draw(context, Sprites.EqualizerShadeClosePressed, 264, 3);
            double volume = pressed == Part.Volume ? dragValue : Player.Volume;
            int part = volume < 1 / 3.0 ? 0 : volume < 2 / 3.0 ? 1 : 2;
            Draw(context, new Sprite(SkinSheet.EqualizerExtra, 1 + part * 3, 30, 3, 7), 61 + Math.Round(volume * 94), 4);
            double balance = pressed == Part.Balance ? dragValue : Player.Balance;
            int side = balance < -1 / 3.0 ? 0 : balance < 1 / 3.0 ? 1 : 2;
            Draw(context, new Sprite(SkinSheet.EqualizerExtra, 11 + side * 3, 30, 3, 7), 164 + Math.Round((balance + 1) / 2 * 40), 4);
            return;
        }
        Draw(context, Sprites.EqualizerBackground, 0, 0);
        Draw(context, active ? Sprites.EqualizerTitleBarSelected : Sprites.EqualizerTitleBar, 0, 0);
        if (IsPressed(Part.EqualizerShade)) Draw(context, Sprites.EqualizerShadeButtonPressed, 254, 3);
        if (IsPressed(Part.EqualizerClose)) Draw(context, Sprites.EqualizerClosePressed, 264, 3);
        Draw(context, Sprites.EqualizerOn(settings.IsEnabled, IsPressed(Part.EqualizerOn)), 14, 18);
        Draw(context, Sprites.EqualizerAuto(Player.IsEqualizerAuto, IsPressed(Part.EqualizerAuto)), 40, 18);
        Draw(context, IsPressed(Part.EqualizerPresets) ? Sprites.EqualizerPresetsPressed : Sprites.EqualizerPresets, 217, 18);
        DrawGraph(context, settings);
        for (int slider = 0; slider <= EqualizerSettings.BandCount; slider++)
        {
            double gain = pressed == Part.EqualizerSlider && pressedArgument == slider ? dragValue
                : slider == 0 ? settings.Preamp : settings.Bands[slider - 1];
            double level = (gain + EqualizerSettings.MaximumGain) / (2 * EqualizerSettings.MaximumGain);
            int x = SliderX(slider);
            Draw(context, Sprites.EqualizerSliderFrame((int)Math.Round(level * 27)), x, 38);
            Sprite thumb = pressed == Part.EqualizerSlider && pressedArgument == slider ? Sprites.EqualizerSliderThumbPressed : Sprites.EqualizerSliderThumb;
            Draw(context, thumb, x + 1, 38 + Math.Round((1 - level) * 51));
        }
    }

    private static int SliderX(int slider) => slider == 0 ? 21 : 78 + (slider - 1) * 18;

    private void DrawGraph(DrawingContext context, EqualizerSettings settings)
    {
        Draw(context, Sprites.EqualizerGraphBackground, 86, 17);
        SkinImage sheet = Skin[SkinSheet.EqualizerMain];
        double preampY = 9 - settings.Preamp / EqualizerSettings.MaximumGain * 9;
        Draw(context, Sprites.EqualizerPreampLine, 86, 17 + Math.Clamp(Math.Round(preampY), 0, 18));
        // A smooth curve through the ten bands, each pixel coloured by its height as the skin's colour strip says.
        double[] bands = [.. settings.Bands];
        int? previous = null;
        for (int x = 0; x < 113; x++)
        {
            double position = x / 112.0 * (bands.Length - 1);
            int left = Math.Min((int)position, bands.Length - 2);
            double t = position - left;
            double eased = (1 - Math.Cos(t * Math.PI)) / 2;
            double gain = bands[left] * (1 - eased) + bands[left + 1] * eased;
            int y = Math.Clamp((int)Math.Round(9 - gain / EqualizerSettings.MaximumGain * 9), 0, 18);
            int from = previous is int last ? Math.Min(last, y) : y, to = previous is int end ? Math.Max(end, y) : y;
            for (int row = from; row <= to; row++)
                context.FillRectangle(Brush(sheet[115, 294 + row]), new Rect(86 + x, 17 + row, 1, 1));
            previous = y;
        }
    }

    private const int PlaylistWidth = SkinWidth;

    private int VisibleRows => Math.Max(1, (PlaylistSectionHeight - 38 - 23) / RowHeight);

    private int MaximumScroll => Math.Max(0, model.Entries.Count - VisibleRows);

    private void ClampScroll() => scrollRow = Math.Clamp(scrollRow, 0, MaximumScroll);

    private void DrawPlaylist(DrawingContext context)
    {
        int width = PlaylistWidth, height = PlaylistSectionHeight;
        if (Preferences.PlaylistShaded)
        {
            Draw(context, Sprites.PlaylistShadeLeft, 0, 0);
            for (int x = 25; x < width - 50; x += 25) DrawClipped(context, Sprites.PlaylistShadeTile, x, 0, width - 50 - x, 14);
            Draw(context, Sprites.PlaylistShadeRight(active), width - 50, 0);
            if (IsPressed(Part.PlaylistShade)) Draw(context, Sprites.PlaylistUnshadePressed, width - 20, 3);
            if (IsPressed(Part.PlaylistClose)) Draw(context, Sprites.PlaylistClosePressed, width - 11, 3);
            if (model.DisplayedTrack is { } track)
            {
                string title = ClassicPlayerViewModel.DescribeTrack(track);
                Text(context, title, 5, 4, (width - 85) / 5);
                Text(context, ClassicPlayerViewModel.FormatDuration(track.Duration).PadLeft(5), width - 57, 4);
            }
            return;
        }
        // Frame: title bar, sides and bottom.
        Draw(context, Sprites.PlaylistTopLeft(active), 0, 0);
        int titleX = (width - 100) / 2;
        for (int x = 25; x < titleX; x += 25) DrawClipped(context, Sprites.PlaylistTopTile(active), x, 0, titleX - x, 20);
        Draw(context, Sprites.PlaylistTitle(active), titleX, 0);
        for (int x = titleX + 100; x < width - 25; x += 25) DrawClipped(context, Sprites.PlaylistTopTile(active), x, 0, width - 25 - x, 20);
        Draw(context, Sprites.PlaylistTopRight(active), width - 25, 0);
        for (int y = 20; y < height - 38; y += 29)
        {
            DrawClipped(context, Sprites.PlaylistLeftTile, 0, y, 12, height - 38 - y);
            DrawClipped(context, Sprites.PlaylistRightTile, width - 20, y, 20, height - 38 - y);
        }
        Draw(context, Sprites.PlaylistBottomLeft, 0, height - 38);
        for (int x = 125; x < width - 150; x += 25) DrawClipped(context, Sprites.PlaylistBottomTile, x, height - 38, width - 150 - x, 38);
        Draw(context, Sprites.PlaylistBottomRight, width - 150, height - 38);
        if (IsPressed(Part.PlaylistShade)) Draw(context, Sprites.PlaylistShadePressed, width - 21, 3);
        if (IsPressed(Part.PlaylistClose)) Draw(context, Sprites.PlaylistClosePressed, width - 11, 3);

        DrawRows(context, width, height);

        // Scroll handle.
        int trackTop = 20, trackLength = height - 38 - 20 - 18;
        double handle = MaximumScroll == 0 ? 0 : scrollRow / (double)MaximumScroll;
        Draw(context, pressed == Part.PlaylistScroll ? Sprites.PlaylistScrollHandlePressed : Sprites.PlaylistScrollHandle,
            width - 15, trackTop + Math.Round(handle * Math.Max(0, trackLength)));

        // Running time and the mini time display.
        Text(context, model.RunningTimeText, width - 143, height - 28, 18);
        if (model.DisplayTime is { } time) Text(context, MiniTime(time), width - 84, height - 15);

        if (openMenu >= 0) DrawMenu(context, height);
    }

    private void DrawRows(DrawingContext context, int width, int height)
    {
        PlaylistStyle style = Skin.Playlist;
        Rect area = new(12, 20, width - 32, height - 58);
        context.FillRectangle(Brush(style.NormalBackground), area);
        using (context.PushClip(area))
        {
            Typeface typeface = new(new FontFamily(style.FontName + ", Arial, Liberation Sans, DejaVu Sans, sans-serif"));
            for (int row = 0; row < VisibleRows + 1; row++)
            {
                int index = scrollRow + row;
                if (index >= model.Entries.Count) break;
                double y = 22 + row * RowHeight;
                if (model.IsSelected(index)) context.FillRectangle(Brush(style.SelectedBackground), new Rect(12, y, width - 32, RowHeight));
                if (index == model.FocusIndex && IsKeyboardFocusWithin)
                    context.DrawRectangle(new Pen(Brush(style.Normal), 0.5), new Rect(12.5, y + 0.5, width - 33, RowHeight - 1));
                IBrush color = Brush(index == model.CurrentIndex ? style.Current : style.Normal);
                Track track = model.Entries[index].Track;
                FormattedText duration = new(ClassicPlayerViewModel.FormatDuration(track.Duration), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 9, color);
                double durationX = width - 23 - duration.Width;
                FormattedText title = new($"{index + 1}. {ClassicPlayerViewModel.DescribeTrack(track)}", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 9, color)
                {
                    MaxTextWidth = Math.Max(1, durationX - 16),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(title, new Point(14, y + (RowHeight - title.Height) / 2));
                context.DrawText(duration, new Point(durationX, y + (RowHeight - duration.Height) / 2));
            }
        }
    }

    // The skinned pop-up menus of the playlist's bottom buttons: a bar, then items stacked upwards from the button.
    private static readonly (int X, bool FromRight, Sprite Bar, Sprite[] Items)[] Menus =
    [
        (14, false, Sprites.AddMenuBar, [Sprites.AddFile, Sprites.AddDirectory, Sprites.AddUrl]),
        (43, false, Sprites.RemoveMenuBar, [Sprites.RemoveSelected, Sprites.RemoveCrop, Sprites.RemoveAll, Sprites.RemoveMisc]),
        (72, false, Sprites.SelectMenuBar, [Sprites.SelectAll, Sprites.SelectNone, Sprites.SelectInvert]),
        (101, false, Sprites.MiscMenuBar, [Sprites.MiscOptions, Sprites.MiscFileInfo, Sprites.MiscSort]),
        (44, true, Sprites.ListMenuBar, [Sprites.ListLoad, Sprites.ListSave, Sprites.ListNew]),
    ];

    private static int MenuX(int menu) => Menus[menu].FromRight ? PlaylistWidth - Menus[menu].X : Menus[menu].X;

    private void DrawMenu(DrawingContext context, int height)
    {
        (int _, bool _, Sprite bar, Sprite[] items) = Menus[openMenu];
        int x = MenuX(openMenu), bottom = height - 12;
        int top = bottom - items.Length * 18;
        Draw(context, bar with { Height = items.Length * 18 }, x - 3, top);
        for (int item = 0; item < items.Length; item++)
        {
            Sprite sprite = item == menuHover ? Sprites.Highlighted(items[item]) : items[item];
            Draw(context, sprite, x, bottom - (item + 1) * 18);
        }
    }

    // ---- Hit testing -----------------------------------------------------------------------------------------------

    private static bool In(Point point, double x, double y, double width, double height) =>
        point.X >= x && point.Y >= y && point.X < x + width && point.Y < y + height;

    private (Part Part, int Argument) HitTest(Point point)
    {
        if (point.Y < MainSectionHeight) return HitMain(point);
        if (point.Y < PlaylistTop) return HitEqualizer(point - new Point(0, EqualizerTop));
        if (point.Y < PlaylistTop + PlaylistSectionHeight) return HitPlaylist(point - new Point(0, PlaylistTop));
        return (Part.None, 0);
    }

    private (Part, int) HitMain(Point p)
    {
        if (In(p, 6, 3, 9, 9)) return (Part.Options, 0);
        if (In(p, 244, 3, 9, 9)) return (Part.Minimize, 0);
        if (In(p, 254, 3, 9, 9)) return (Part.Shade, 0);
        if (In(p, 264, 3, 9, 9)) return (Part.Close, 0);
        if (Preferences.MainShaded)
        {
            (double X, double Width)[] buttons = [(169, 7), (176, 10), (186, 9), (195, 9), (204, 10), (215, 10)];
            for (int button = 0; button < buttons.Length; button++)
                if (In(p, buttons[button].X, 2, buttons[button].Width, 10)) return (Part.Transport, button);
            if (In(p, 226, 4, 17, 7)) return (Part.Position, 1);
            if (In(p, 127, 4, 30, 6)) return (Part.Time, 0);
            return (Part.MainTitle, 0);
        }
        if (p.Y < 14) return (Part.MainTitle, 0);
        if (In(p, 10, 25, 8, 8)) return (Part.ClutterOptions, 0);
        if (In(p, 10, 33, 8, 7)) return (Part.ClutterTop, 0);
        if (In(p, 10, 40, 8, 7)) return (Part.ClutterInfo, 0);
        if (In(p, 10, 47, 8, 8)) return (Part.ClutterDouble, 0);
        if (In(p, 10, 55, 8, 7)) return (Part.ClutterVisualizer, 0);
        if (In(p, 36, 26, 63, 13)) return (Part.Time, 0);
        if (In(p, 24, 43, 76, 16)) return (Part.Visualizer, 0);
        if (In(p, 107, 57, 68, 13)) return (Part.Volume, 0);
        if (In(p, 177, 57, 38, 13)) return (Part.Balance, 0);
        if (In(p, 219, 58, 23, 12)) return (Part.EqualizerToggle, 0);
        if (In(p, 242, 58, 23, 12)) return (Part.PlaylistToggle, 0);
        if (In(p, 16, 72, 248, 10)) return (Part.Position, 0);
        for (int button = 0; button < 5; button++)
            if (In(p, 16 + 23 * button, 88, button == 4 ? 22 : 23, 18)) return (Part.Transport, button);
        if (In(p, 136, 89, 22, 16)) return (Part.Transport, 5);
        if (In(p, 164, 89, 46, 15)) return (Part.Shuffle, 0);
        if (In(p, 210, 89, 28, 15)) return (Part.Repeat, 0);
        // Everything else (the background, the logo) moves the window, as in the original.
        return (Part.MainTitle, 0);
    }

    private (Part, int) HitEqualizer(Point p)
    {
        if (In(p, 254, 3, 9, 9)) return (Part.EqualizerShade, 0);
        if (In(p, 264, 3, 9, 9)) return (Part.EqualizerClose, 0);
        if (Preferences.EqualizerShaded)
        {
            if (In(p, 61, 4, 97, 7)) return (Part.Volume, 1);
            if (In(p, 164, 4, 43, 7)) return (Part.Balance, 1);
            return (Part.EqualizerTitle, 0);
        }
        if (p.Y < 14) return (Part.EqualizerTitle, 0);
        if (In(p, 14, 18, 26, 12)) return (Part.EqualizerOn, 0);
        if (In(p, 40, 18, 32, 12)) return (Part.EqualizerAuto, 0);
        if (In(p, 217, 18, 44, 12)) return (Part.EqualizerPresets, 0);
        for (int slider = 0; slider <= EqualizerSettings.BandCount; slider++)
            if (In(p, SliderX(slider), 38, 14, 63)) return (Part.EqualizerSlider, slider);
        return (Part.EqualizerTitle, 0);
    }

    private (Part, int) HitPlaylist(Point p)
    {
        int width = PlaylistWidth, height = PlaylistSectionHeight;
        if (Preferences.PlaylistShaded)
        {
            if (In(p, width - 20, 3, 9, 9)) return (Part.PlaylistShade, 0);
            if (In(p, width - 11, 3, 9, 9)) return (Part.PlaylistClose, 0);
            return (Part.PlaylistTitle, 0);
        }
        if (openMenu >= 0) return (Part.PlaylistMenu, openMenu);
        if (In(p, width - 21, 3, 9, 9)) return (Part.PlaylistShade, 0);
        if (In(p, width - 11, 3, 9, 9)) return (Part.PlaylistClose, 0);
        if (p.Y < 20) return (Part.PlaylistTitle, 0);
        if (In(p, 12, 20, width - 32, height - 58)) return (Part.PlaylistRows, 0);
        if (In(p, width - 15, 20, 8, height - 58)) return (Part.PlaylistScroll, 0);
        for (int menu = 0; menu < Menus.Length; menu++)
            if (In(p, MenuX(menu), height - 30, 22, 18)) return (Part.PlaylistMenu, menu);
        for (int button = 0; button < 6; button++)
            if (In(p, width - 147 + 10 * button, height - 16, 10, 10)) return (Part.PlaylistMini, button);
        if (In(p, width - 143, height - 28, 90, 8) || In(p, width - 84, height - 15, 30, 8)) return (Part.PlaylistTime, 0);
        if (In(p, width - 20, height - 20, 20, 20)) return (Part.PlaylistResize, 0);
        return (Part.PlaylistTitle, 0);
    }

    // ---- Input -----------------------------------------------------------------------------------------------------

    private Point SkinPoint(PointerEventArgs args)
    {
        Point point = args.GetPosition(this);
        return new Point(point.X / Scale, point.Y / Scale);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        host.Activate(this);
        Point point = SkinPoint(e);
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        (Part part, int argument) = HitTest(point);
        if (properties.IsRightButtonPressed)
        {
            e.Handled = true;
            if (part == Part.PlaylistRows)
            {
                int index = RowAt(point);
                if (index >= 0 && !model.IsSelected(index)) model.Select(index);
                host.ShowPlaylistContextMenu();
            }
            else host.ShowOptionsMenu();
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (openMenu >= 0 && part != Part.PlaylistMenu)
        {
            CloseMenu();
            return;
        }
        pressed = part;
        pressedArgument = argument;
        pressedInside = true;
        switch (part)
        {
            case Part.MainTitle or Part.EqualizerTitle or Part.PlaylistTitle:
                pressed = Part.None;
                if (e.ClickCount == 2) ToggleShadeFor(part);
                else if (host.BeginDrag(this, e))
                {
                    pressed = Part.WindowDrag;
                    e.Pointer.Capture(this);
                }
                return;
            case Part.Volume:
                dragValue = Player.Volume;
                dragGrab = 0;
                UpdateVolume(point, argument);
                break;
            case Part.Balance:
                if (!Player.CanUseBalance)
                {
                    pressed = Part.None;
                    return;
                }
                UpdateBalance(point, argument);
                break;
            case Part.Position:
                if (Player.PlaybackSnapshot is not { Track.Duration.TotalSeconds: > 0, Status: PlaybackStatus.Playing or PlaybackStatus.Paused })
                {
                    pressed = Part.None;
                    return;
                }
                Player.BeginSeek();
                UpdatePosition(point, argument);
                break;
            case Part.EqualizerSlider:
                UpdateEqualizerSlider(point, argument, persist: false);
                break;
            case Part.PlaylistRows:
                PressRow(point, e);
                break;
            case Part.PlaylistScroll:
                UpdateScroll(point);
                break;
            case Part.PlaylistMenu:
                menuOpenedAt = openMenu < 0 ? point : null;
                openMenu = argument;
                menuHover = -1;
                break;
            case Part.PlaylistResize:
                dragGrab = point.Y - PlaylistTop - PlaylistSectionHeight;
                break;
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = SkinPoint(e);
        if (pressed == Part.None)
        {
            // An open menu highlights the item under the pointer even between clicks.
            if (openMenu >= 0)
            {
                menuHover = MenuItemAt(point - new Point(0, PlaylistTop));
                InvalidateVisual();
            }
            return;
        }
        switch (pressed)
        {
            case Part.WindowDrag:
                host.DragTo(this, this.PointToScreen(e.GetPosition(this)));
                return;
            case Part.Volume:
                UpdateVolume(point, pressedArgument);
                break;
            case Part.Balance:
                UpdateBalance(point, pressedArgument);
                break;
            case Part.Position:
                UpdatePosition(point, pressedArgument);
                break;
            case Part.EqualizerSlider:
                UpdateEqualizerSlider(point, pressedArgument, persist: false);
                break;
            case Part.PlaylistRows:
                DragRows(point);
                break;
            case Part.PlaylistScroll:
                UpdateScroll(point);
                break;
            case Part.PlaylistMenu:
                menuHover = MenuItemAt(point - new Point(0, PlaylistTop));
                break;
            case Part.PlaylistResize:
                int rows = (int)Math.Round((point.Y - PlaylistTop - dragGrab - PlaylistMinimumHeight) / PlaylistStep);
                rows = Math.Clamp(rows, 0, ClassicPlayerPreferences.MaximumExtraRows);
                if (rows != Preferences.PlaylistExtraRows) ChangeLayout(preferences => preferences with { PlaylistExtraRows = rows });
                break;
            default:
                (Part part, int argument) = HitTest(point);
                pressedInside = part == pressed && argument == pressedArgument;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Part part = pressed;
        int argument = pressedArgument;
        bool inside = pressedInside;
        pressed = Part.None;
        e.Pointer.Capture(null);
        Point point = SkinPoint(e);
        switch (part)
        {
            case Part.WindowDrag:
                host.EndDrag(this);
                return;
            case Part.Volume:
                double volume = dragValue;
                host.Run(() => Player.ChangeVolumeAsync(volume));
                break;
            case Part.Balance:
                double balance = dragValue;
                host.Run(() => Player.SetBalanceAsync(balance, persist: true));
                break;
            case Part.Position:
                double fraction = dragValue;
                host.Run(async () =>
                {
                    if (Player.PlaybackSnapshot.Track is { Duration.TotalSeconds: > 0 } track) await Player.SeekAsync(fraction * track.Duration.TotalSeconds);
                    Player.EndSeek();
                });
                break;
            case Part.EqualizerSlider:
                UpdateEqualizerSlider(point, argument, persist: true);
                break;
            case Part.PlaylistRows:
                dragRow = -1;
                break;
            case Part.PlaylistMenu:
                if (menuOpenedAt is { } opened && Math.Abs(opened.X - point.X) < 3 && Math.Abs(opened.Y - point.Y) < 3)
                {
                    menuOpenedAt = null;
                    break;
                }
                int item = MenuItemAt(point - new Point(0, PlaylistTop));
                int menu = openMenu;
                CloseMenu();
                if (item >= 0) ActivateMenu(menu, item);
                break;
            default:
                if (inside) Click(part, argument);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point point = SkinPoint(e);
        if (point.Y >= PlaylistTop && !Preferences.PlaylistShaded)
        {
            scrollRow -= (int)Math.Round(e.Delta.Y * 3);
            ClampScroll();
        }
        else
        {
            double volume = Math.Clamp(Player.Volume + e.Delta.Y * 0.04, 0, 1);
            host.Run(() => Player.ChangeVolumeAsync(volume));
        }
        e.Handled = true;
        InvalidateVisual();
    }

    private void Click(Part part, int argument)
    {
        switch (part)
        {
            case Part.Options or Part.ClutterOptions: host.ShowOptionsMenu(); break;
            case Part.Minimize: host.Minimize(); break;
            case Part.Shade: ChangeLayout(preferences => preferences with { MainShaded = !preferences.MainShaded }); break;
            case Part.Close: host.Exit(); break;
            case Part.ClutterTop: ChangeLayout(preferences => preferences with { AlwaysOnTop = !preferences.AlwaysOnTop }); break;
            case Part.ClutterInfo: host.ShowFileInfo(); break;
            case Part.ClutterDouble: ChangeLayout(preferences => preferences with { DoubleSize = !preferences.DoubleSize }); break;
            case Part.ClutterVisualizer or Part.Visualizer:
                visualizerOn = !visualizerOn;
                UpdateSpectrum();
                break;
            case Part.Time or Part.PlaylistTime: host.Run(() => model.ToggleRemainingTimeAsync()); break;
            case Part.EqualizerToggle: ChangeLayout(preferences => preferences with { ShowEqualizer = !preferences.ShowEqualizer }); break;
            case Part.PlaylistToggle: ChangeLayout(preferences => preferences with { ShowPlaylist = !preferences.ShowPlaylist }); break;
            case Part.Transport or Part.PlaylistMini: Transport(argument); break;
            case Part.Shuffle: host.Run(() => model.ToggleShuffleAsync()); break;
            case Part.Repeat: host.Run(() => model.ToggleRepeatAsync()); break;
            case Part.EqualizerClose: ChangeLayout(preferences => preferences with { ShowEqualizer = false }); break;
            case Part.EqualizerShade: ChangeLayout(preferences => preferences with { EqualizerShaded = !preferences.EqualizerShaded }); break;
            case Part.EqualizerOn:
                EqualizerSettings settings = CurrentEqualizer();
                host.Run(() => Equalizer.ApplyAsync(settings with { IsEnabled = !settings.IsEnabled }, persist: true));
                break;
            case Part.EqualizerPresets: host.ShowPresetsMenu(); break;
            case Part.EqualizerAuto:
                bool auto = !Player.IsEqualizerAuto;
                host.Run(() => Player.SetEqualizerAutoAsync(auto));
                break;
            case Part.PlaylistClose: ChangeLayout(preferences => preferences with { ShowPlaylist = false }); break;
            case Part.PlaylistShade: ChangeLayout(preferences => preferences with { PlaylistShaded = !preferences.PlaylistShaded }); break;
        }
    }

    /// <summary>Previous, Play, Pause, Stop, Next, Eject.</summary>
    public void Transport(int button)
    {
        switch (button)
        {
            case 0: host.Run(() => model.PreviousAsync()); break;
            case 1: host.Run(() => model.PlayAsync()); break;
            case 2: host.Run(() => model.PauseAsync()); break;
            case 3: host.Run(() => model.StopAsync()); break;
            case 4: host.Run(() => model.NextAsync()); break;
            case 5: host.Run(() => host.OpenFilesAsync(replace: true)); break;
        }
    }

    private void ToggleShadeFor(Part title)
    {
        switch (title)
        {
            case Part.MainTitle: ChangeLayout(preferences => preferences with { MainShaded = !preferences.MainShaded }); break;
            case Part.EqualizerTitle: ChangeLayout(preferences => preferences with { EqualizerShaded = !preferences.EqualizerShaded }); break;
            default: ChangeLayout(preferences => preferences with { PlaylistShaded = !preferences.PlaylistShaded }); break;
        }
    }

    public void ChangeLayout(Func<ClassicPlayerPreferences, ClassicPlayerPreferences> change)
    {
        host.Run(() => Player.UpdateClassicPlayerAsync(change));
        ClampScroll();
        InvalidateMeasure();
        InvalidateVisual();
        host.LayoutChanged();
    }

    private void UpdateVolume(Point point, int argument)
    {
        dragValue = argument == 1 ? Math.Clamp((point.X - 61 - 1.5) / 94, 0, 1) : Math.Clamp((point.X - 107 - 7) / 51, 0, 1);
        // Volume follows the drag, a few times a second.
        if (DateTime.UtcNow - lastVolumeSend > TimeSpan.FromMilliseconds(60))
        {
            lastVolumeSend = DateTime.UtcNow;
            double volume = dragValue;
            host.Run(() => Player.ChangeVolumeAsync(volume));
        }
    }

    // Balance follows the drag like volume does; the centre snaps (see MainWindowViewModel.SetBalanceAsync).
    private void UpdateBalance(Point point, int argument)
    {
        double value = argument == 1 ? (point.X - 164 - 1.5) / 40 * 2 - 1 : (point.X - 177 - 7) / 24 * 2 - 1;
        value = Math.Clamp(value, -1, 1);
        dragValue = Math.Abs(value) < 0.05 ? 0 : value;
        if (DateTime.UtcNow - lastVolumeSend > TimeSpan.FromMilliseconds(60))
        {
            lastVolumeSend = DateTime.UtcNow;
            double balance = dragValue;
            host.Run(() => Player.SetBalanceAsync(balance, persist: false));
        }
    }

    private void UpdatePosition(Point point, int argument) =>
        dragValue = argument == 1 ? Math.Clamp((point.X - 226 - 1.5) / 14, 0, 1) : Math.Clamp((point.X - 16 - 14.5) / 219, 0, 1);

    private IEqualizerHost Equalizer => Player;

    private EqualizerSettings CurrentEqualizer() => Equalizer.Current.Normalized();

    private void UpdateEqualizerSlider(Point point, int slider, bool persist)
    {
        double level = Math.Clamp(1 - (point.Y - EqualizerTop - 38 - 5.5) / 51, 0, 1);
        double gain = Math.Round((level * 2 - 1) * EqualizerSettings.MaximumGain, 1);
        if (Math.Abs(gain) < 0.6) gain = 0;
        dragValue = gain;
        EqualizerSettings settings = CurrentEqualizer();
        if (slider == 0) settings = settings with { Preamp = gain };
        else
        {
            double[] bands = [.. settings.Bands];
            bands[slider - 1] = gain;
            settings = settings with { Bands = bands };
        }
        // Moving a slider is what turns an off equalizer on, as in the original.
        host.Run(() => Equalizer.ApplyAsync(settings with { IsEnabled = true }, persist));
    }

    private int RowAt(Point point)
    {
        double y = point.Y - PlaylistTop - 22;
        if (y < 0) return -1;
        int index = scrollRow + (int)(y / RowHeight);
        return index < model.Entries.Count ? index : -1;
    }

    private void PressRow(Point point, PointerPressedEventArgs e)
    {
        int index = RowAt(point);
        if (index < 0)
        {
            model.SelectNone();
            return;
        }
        if (e.ClickCount == 2)
        {
            host.Run(() => model.PlayEntryAsync(index));
            return;
        }
        bool toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control), range = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // A plain click on a selected row keeps the selection, so several rows can be dragged together.
        if (toggle || range || !model.IsSelected(index)) model.Select(index, toggle, range);
        dragRow = model.IsSelected(index) ? index : -1;
    }

    private void DragRows(Point point)
    {
        if (dragRow < 0) return;
        double y = point.Y - PlaylistTop - 22;
        int target = Math.Clamp(scrollRow + (int)Math.Floor(y / RowHeight), 0, Math.Max(0, model.Entries.Count - 1));
        if (target == dragRow) return;
        model.MoveSelected(target - dragRow);
        dragRow = target;
    }

    private void UpdateScroll(Point point)
    {
        double y = point.Y - PlaylistTop - 20 - 9;
        double length = Math.Max(1, PlaylistSectionHeight - 38 - 20 - 18);
        scrollRow = (int)Math.Round(Math.Clamp(y / length, 0, 1) * MaximumScroll);
    }

    private int MenuItemAt(Point point)
    {
        if (openMenu < 0) return -1;
        int x = MenuX(openMenu), bottom = PlaylistSectionHeight - 12;
        int count = Menus[openMenu].Items.Length;
        if (point.X < x || point.X >= x + 22 || point.Y >= bottom || point.Y < bottom - count * 18) return -1;
        return (int)((bottom - point.Y) / 18);
    }

    private void CloseMenu()
    {
        openMenu = -1;
        menuHover = -1;
        InvalidateVisual();
    }

    private void ActivateMenu(int menu, int item)
    {
        switch (menu, item)
        {
            case (0, 0): host.Run(() => host.OpenFilesAsync(replace: false)); break;
            case (0, 1): host.Run(host.AddFolderAsync); break;
            // The slot the original kept for web addresses adds from the library instead.
            case (0, 2): host.Run(host.SearchLibraryAsync); break;
            case (1, 0): model.RemoveSelected(); break;
            case (1, 1): model.Crop(); break;
            case (1, 2): model.RemoveAll(); break;
            case (1, 3): model.RemoveDuplicates(); break;
            case (2, 0): model.SelectAll(); break;
            case (2, 1): model.SelectNone(); break;
            case (2, 2): model.InvertSelection(); break;
            case (3, 0): host.ShowMiscMenu(); break;
            case (3, 1): host.ShowFileInfo(); break;
            case (3, 2): host.ShowSortMenu(); break;
            case (4, 0): host.ShowLoadListMenu(); break;
            case (4, 1): host.Run(host.SavePlaylistAsync); break;
            case (4, 2): model.RemoveAll(); break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        host.Activate(this);
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Z when !control && !alt: Transport(0); break;
            case Key.X when !control && !alt: Transport(1); break;
            case Key.C when !control && !alt: Transport(2); break;
            case Key.V when !control && !alt: Transport(3); break;
            case Key.B when !control && !alt: Transport(4); break;
            case Key.L when !control && !alt: Transport(5); break;
            case Key.J when !control && !alt: host.Run(host.SearchLibraryAsync); break;
            case Key.S when !control && !alt: host.Run(() => model.ToggleShuffleAsync()); break;
            case Key.R when !control && !alt: host.Run(() => model.ToggleRepeatAsync()); break;
            case Key.D when control: ChangeLayout(preferences => preferences with { DoubleSize = !preferences.DoubleSize }); break;
            case Key.T when control: host.Run(() => model.ToggleRemainingTimeAsync()); break;
            case Key.A when control: model.SelectAll(); break;
            case Key.G when alt: ChangeLayout(preferences => preferences with { ShowEqualizer = !preferences.ShowEqualizer }); break;
            case Key.E when alt: ChangeLayout(preferences => preferences with { ShowPlaylist = !preferences.ShowPlaylist }); break;
            case Key.Delete: model.RemoveSelected(); break;
            case Key.Enter when model.FocusIndex >= 0: host.Run(() => model.PlayEntryAsync(model.FocusIndex)); break;
            case Key.Up or Key.Down when alt && model.SelectedCount > 0: model.MoveSelected(e.Key == Key.Up ? -1 : 1); break;
            case Key.Up or Key.Down when model.Entries.Count > 0:
                int focus = Math.Clamp(model.FocusIndex + (e.Key == Key.Up ? -1 : 1), 0, model.Entries.Count - 1);
                model.Select(focus, toggle: false, range: shift);
                if (focus < scrollRow) scrollRow = focus;
                else if (focus >= scrollRow + VisibleRows) scrollRow = focus - VisibleRows + 1;
                break;
            case Key.Left or Key.Right when Player.PlaybackSnapshot.Track is not null:
                double target = Player.PlaybackSnapshot.Position.TotalSeconds + (e.Key == Key.Left ? -5 : 5);
                host.Run(() => Player.SeekAsync(Math.Max(0, target)));
                break;
            case Key.Add or Key.OemPlus:
                double up = Math.Min(1, Player.Volume + 0.02);
                host.Run(() => Player.ChangeVolumeAsync(up));
                break;
            case Key.Subtract or Key.OemMinus:
                double down = Math.Max(0, Player.Volume - 0.02);
                host.Run(() => Player.ChangeVolumeAsync(down));
                break;
            default:
                e.Handled = false;
                break;
        }
        InvalidateVisual();
    }
}
