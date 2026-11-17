namespace Aibell.Skins.Classic;

/// <summary>
/// A loaded classic skin: its bitmaps, playlist colours, visualizer colours and window outlines. Anything the skin lacks or
/// cannot be decoded comes from the fallback skin (Aibell's own), and a bitmap smaller than the format expects is laid over
/// the fallback's, so a partial or older skin still draws every part.
/// </summary>
public sealed class ClassicSkin
{
    private static readonly Dictionary<SkinSheet, string[]> FileNames = new()
    {
        [SkinSheet.Main] = ["main.bmp"],
        [SkinSheet.ControlButtons] = ["cbuttons.bmp"],
        [SkinSheet.TitleBar] = ["titlebar.bmp"],
        [SkinSheet.ShuffleRepeat] = ["shufrep.bmp"],
        [SkinSheet.Text] = ["text.bmp"],
        // The extended digits have their own minus sign; skins that ship both are drawn with them.
        [SkinSheet.Numbers] = ["nums_ex.bmp", "numbers.bmp"],
        [SkinSheet.PlayPause] = ["playpaus.bmp"],
        [SkinSheet.MonoStereo] = ["monoster.bmp"],
        [SkinSheet.PositionBar] = ["posbar.bmp"],
        [SkinSheet.Volume] = ["volume.bmp"],
        // A skin without a balance bitmap uses its volume bitmap, as the original players did.
        [SkinSheet.Balance] = ["balance.bmp", "volume.bmp"],
        [SkinSheet.EqualizerMain] = ["eqmain.bmp"],
        [SkinSheet.EqualizerExtra] = ["eq_ex.bmp"],
        [SkinSheet.Playlist] = ["pledit.bmp"],
    };

    private readonly Dictionary<SkinSheet, SkinImage> sheets;

    public ClassicSkin(string name, IReadOnlyDictionary<SkinSheet, SkinImage> sheets, bool hasExtendedNumbers,
        PlaylistStyle playlist, VisualizerColors visualizer, SkinRegions regions)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        foreach (SkinSheet sheet in Enum.GetValues<SkinSheet>())
            if (!sheets.ContainsKey(sheet)) throw new ArgumentException($"The {sheet} bitmap is missing.", nameof(sheets));
        Name = name;
        this.sheets = new(sheets);
        HasExtendedNumbers = hasExtendedNumbers;
        Playlist = playlist;
        Visualizer = visualizer;
        Regions = regions;
    }

    public string Name { get; }

    public bool HasExtendedNumbers { get; }

    public PlaylistStyle Playlist { get; }

    public VisualizerColors Visualizer { get; }

    public SkinRegions Regions { get; }

    public SkinImage this[SkinSheet sheet] => sheets[sheet];

    /// <summary>The minus sign of the time display, which the two digit bitmaps keep in different places.</summary>
    public Sprite MinusSign => HasExtendedNumbers ? Sprites.MinusExtended : Sprites.MinusClassic;

    /// <summary>
    /// The colour behind the song title, read from the text bitmap the way the original players did; the playlist's
    /// shaded bar and other text strips use it to fill gaps.
    /// </summary>
    public uint TextBackground => sheets[SkinSheet.Text][150, 4];

    /// <summary>Reads a skin archive, taking whatever is missing or unreadable from <paramref name="fallback"/>.</summary>
    public static ClassicSkin Load(SkinArchive archive, ClassicSkin fallback, string name)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(fallback);
        if (archive.IsModernSkin) throw new InvalidSkinException(InvalidSkinReason.ModernSkin);
        if (!archive.Has("main.bmp")) throw new InvalidSkinException(InvalidSkinReason.NotAClassicSkin);
        Dictionary<SkinSheet, SkinImage> sheets = [];
        bool extended = false;
        foreach ((SkinSheet sheet, string[] files) in FileNames)
        {
            SkinImage? image = null;
            foreach (string file in files)
            {
                if (archive.Get(file) is not { } bytes) continue;
                try
                {
                    image = BmpDecoder.Decode(bytes);
                }
                catch (InvalidDataException)
                {
                    continue;
                }
                if (sheet == SkinSheet.Numbers) extended = file == "nums_ex.bmp";
                break;
            }
            sheets[sheet] = image is null ? fallback[sheet] : Complete(image, fallback[sheet]);
            if (image is null && sheet == SkinSheet.Numbers) extended = fallback.HasExtendedNumbers;
        }
        if (archive.Get("main.bmp") is { } main && !TryDecode(main))
            throw new InvalidSkinException(InvalidSkinReason.NotAClassicSkin);
        return new ClassicSkin(name, sheets, extended,
            PlaylistStyle.Parse(archive.GetText("pledit.txt"), fallback.Playlist),
            archive.Has("viscolor.txt") ? VisualizerColors.Parse(archive.GetText("viscolor.txt")) : fallback.Visualizer,
            SkinRegions.Parse(archive.GetText("region.txt")));
    }

    private static bool TryDecode(byte[] bytes)
    {
        try
        {
            BmpDecoder.Decode(bytes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    // A bitmap smaller than the fallback's is laid over a copy of it, so the parts the skin left out still look like something.
    private static SkinImage Complete(SkinImage image, SkinImage fallback)
    {
        if (image.Width >= fallback.Width && image.Height >= fallback.Height) return image;
        SkinImage completed = new(Math.Max(image.Width, fallback.Width), Math.Max(image.Height, fallback.Height));
        completed.Blit(fallback, 0, 0, fallback.Width, fallback.Height, 0, 0);
        completed.Blit(image, 0, 0, image.Width, image.Height, 0, 0);
        return completed;
    }
}
