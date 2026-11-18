namespace Aibell.Skins.Classic;

/// <summary>The bitmaps a classic skin is made of.</summary>
public enum SkinSheet
{
    Main,
    ControlButtons,
    TitleBar,
    ShuffleRepeat,
    Text,
    Numbers,
    PlayPause,
    MonoStereo,
    PositionBar,
    Volume,
    Balance,
    EqualizerMain,
    EqualizerExtra,
    Playlist,
}

/// <summary>A rectangle of one sheet.</summary>
public readonly record struct Sprite(SkinSheet Sheet, int X, int Y, int Width, int Height)
{
    public Sprite Offset(int x, int y) => this with { X = X + x, Y = Y + y };
}

/// <summary>
/// Where each piece sits in the classic sheets. The coordinates are the format's, as documented by the Webamp project
/// (MIT licence), so any classic skin lines up.
/// </summary>
public static class Sprites
{
    private static Sprite S(SkinSheet sheet, int x, int y, int width, int height) => new(sheet, x, y, width, height);

    // MAIN.BMP
    public static readonly Sprite MainBackground = S(SkinSheet.Main, 0, 0, 275, 116);

    // TITLEBAR.BMP
    public static readonly Sprite TitleBar = S(SkinSheet.TitleBar, 27, 15, 275, 14);
    public static readonly Sprite TitleBarSelected = S(SkinSheet.TitleBar, 27, 0, 275, 14);
    public static readonly Sprite ShadeBackground = S(SkinSheet.TitleBar, 27, 42, 275, 14);
    public static readonly Sprite ShadeBackgroundSelected = S(SkinSheet.TitleBar, 27, 29, 275, 14);
    public static readonly Sprite OptionsButton = S(SkinSheet.TitleBar, 0, 0, 9, 9);
    public static readonly Sprite OptionsButtonPressed = S(SkinSheet.TitleBar, 0, 9, 9, 9);
    public static readonly Sprite MinimizeButton = S(SkinSheet.TitleBar, 9, 0, 9, 9);
    public static readonly Sprite MinimizeButtonPressed = S(SkinSheet.TitleBar, 9, 9, 9, 9);
    public static readonly Sprite CloseButton = S(SkinSheet.TitleBar, 18, 0, 9, 9);
    public static readonly Sprite CloseButtonPressed = S(SkinSheet.TitleBar, 18, 9, 9, 9);
    public static readonly Sprite ShadeButton = S(SkinSheet.TitleBar, 0, 18, 9, 9);
    public static readonly Sprite ShadeButtonPressed = S(SkinSheet.TitleBar, 9, 18, 9, 9);
    public static readonly Sprite UnshadeButton = S(SkinSheet.TitleBar, 0, 27, 9, 9);
    public static readonly Sprite UnshadeButtonPressed = S(SkinSheet.TitleBar, 9, 27, 9, 9);
    public static readonly Sprite ClutterBar = S(SkinSheet.TitleBar, 304, 0, 8, 43);
    public static readonly Sprite ClutterBarDoubleSelected = S(SkinSheet.TitleBar, 328, 69, 8, 8);
    public static readonly Sprite ShadePositionBackground = S(SkinSheet.TitleBar, 0, 36, 17, 7);
    public static readonly Sprite ShadePositionThumb = S(SkinSheet.TitleBar, 20, 36, 3, 7);
    public static readonly Sprite ShadePositionThumbLeft = S(SkinSheet.TitleBar, 17, 36, 3, 7);
    public static readonly Sprite ShadePositionThumbRight = S(SkinSheet.TitleBar, 23, 36, 3, 7);

    // CBUTTONS.BMP
    public static readonly Sprite Previous = S(SkinSheet.ControlButtons, 0, 0, 23, 18);
    public static readonly Sprite PreviousPressed = S(SkinSheet.ControlButtons, 0, 18, 23, 18);
    public static readonly Sprite Play = S(SkinSheet.ControlButtons, 23, 0, 23, 18);
    public static readonly Sprite PlayPressed = S(SkinSheet.ControlButtons, 23, 18, 23, 18);
    public static readonly Sprite Pause = S(SkinSheet.ControlButtons, 46, 0, 23, 18);
    public static readonly Sprite PausePressed = S(SkinSheet.ControlButtons, 46, 18, 23, 18);
    public static readonly Sprite Stop = S(SkinSheet.ControlButtons, 69, 0, 23, 18);
    public static readonly Sprite StopPressed = S(SkinSheet.ControlButtons, 69, 18, 23, 18);
    public static readonly Sprite Next = S(SkinSheet.ControlButtons, 92, 0, 22, 18);
    public static readonly Sprite NextPressed = S(SkinSheet.ControlButtons, 92, 18, 22, 18);
    public static readonly Sprite Eject = S(SkinSheet.ControlButtons, 114, 0, 22, 16);
    public static readonly Sprite EjectPressed = S(SkinSheet.ControlButtons, 114, 16, 22, 16);

    // SHUFREP.BMP: index 0 off, 1 off pressed, 2 on, 3 on pressed.
    public static Sprite Repeat(bool on, bool pressed) => S(SkinSheet.ShuffleRepeat, 0, (on ? 30 : 0) + (pressed ? 15 : 0), 28, 15);
    public static Sprite Shuffle(bool on, bool pressed) => S(SkinSheet.ShuffleRepeat, 28, (on ? 30 : 0) + (pressed ? 15 : 0), 47, 15);
    public static Sprite EqualizerToggle(bool on, bool pressed) => S(SkinSheet.ShuffleRepeat, pressed ? 46 : 0, on ? 73 : 61, 23, 12);
    public static Sprite PlaylistToggle(bool on, bool pressed) => S(SkinSheet.ShuffleRepeat, pressed ? 69 : 23, on ? 73 : 61, 23, 12);

    // PLAYPAUS.BMP
    public static readonly Sprite PlayingIndicator = S(SkinSheet.PlayPause, 0, 0, 9, 9);
    public static readonly Sprite PausedIndicator = S(SkinSheet.PlayPause, 9, 0, 9, 9);
    public static readonly Sprite StoppedIndicator = S(SkinSheet.PlayPause, 18, 0, 9, 9);
    public static readonly Sprite WorkingIndicator = S(SkinSheet.PlayPause, 36, 0, 3, 9);
    public static readonly Sprite NotWorkingIndicator = S(SkinSheet.PlayPause, 39, 0, 3, 9);

    // MONOSTER.BMP
    public static Sprite Stereo(bool lit) => S(SkinSheet.MonoStereo, 0, lit ? 0 : 12, 29, 12);
    public static Sprite Mono(bool lit) => S(SkinSheet.MonoStereo, 29, lit ? 0 : 12, 27, 12);

    // NUMBERS.BMP / NUMS_EX.BMP
    public static Sprite Digit(int digit) => S(SkinSheet.Numbers, 9 * Math.Clamp(digit, 0, 9), 0, 9, 13);
    public static readonly Sprite DigitBlank = S(SkinSheet.Numbers, 90, 0, 9, 13);
    public static readonly Sprite MinusExtended = S(SkinSheet.Numbers, 99, 0, 9, 13);
    public static readonly Sprite MinusClassic = S(SkinSheet.Numbers, 20, 6, 5, 1);

    // POSBAR.BMP
    public static readonly Sprite PositionBackground = S(SkinSheet.PositionBar, 0, 0, 248, 10);
    public static readonly Sprite PositionThumb = S(SkinSheet.PositionBar, 248, 0, 29, 10);
    public static readonly Sprite PositionThumbPressed = S(SkinSheet.PositionBar, 278, 0, 29, 10);

    // VOLUME.BMP / BALANCE.BMP: 28 stacked frames 15 pixels apart.
    public const int SliderFrames = 28;
    public static Sprite VolumeFrame(int frame) => S(SkinSheet.Volume, 0, 15 * Math.Clamp(frame, 0, 27), 68, 13);
    public static readonly Sprite VolumeThumb = S(SkinSheet.Volume, 15, 422, 14, 11);
    public static readonly Sprite VolumeThumbPressed = S(SkinSheet.Volume, 0, 422, 14, 11);
    public static Sprite BalanceFrame(int frame) => S(SkinSheet.Balance, 9, 15 * Math.Clamp(frame, 0, 27), 38, 13);
    public static readonly Sprite BalanceThumb = S(SkinSheet.Balance, 15, 422, 14, 11);
    public static readonly Sprite BalanceThumbPressed = S(SkinSheet.Balance, 0, 422, 14, 11);

    // EQMAIN.BMP
    public static readonly Sprite EqualizerBackground = S(SkinSheet.EqualizerMain, 0, 0, 275, 116);
    public static readonly Sprite EqualizerTitleBar = S(SkinSheet.EqualizerMain, 0, 149, 275, 14);
    public static readonly Sprite EqualizerTitleBarSelected = S(SkinSheet.EqualizerMain, 0, 134, 275, 14);
    public static readonly Sprite EqualizerClose = S(SkinSheet.EqualizerMain, 0, 116, 9, 9);
    public static readonly Sprite EqualizerClosePressed = S(SkinSheet.EqualizerMain, 0, 125, 9, 9);
    public static Sprite EqualizerOn(bool on, bool pressed) => S(SkinSheet.EqualizerMain, 10 + (on ? 59 : 0) + (pressed ? 118 : 0), 119, 26, 12);
    public static Sprite EqualizerAuto(bool on, bool pressed) => S(SkinSheet.EqualizerMain, 36 + (on ? 59 : 0) + (pressed ? 118 : 0), 119, 32, 12);
    public static readonly Sprite EqualizerPresets = S(SkinSheet.EqualizerMain, 224, 164, 44, 12);
    public static readonly Sprite EqualizerPresetsPressed = S(SkinSheet.EqualizerMain, 224, 176, 44, 12);
    public static readonly Sprite EqualizerGraphBackground = S(SkinSheet.EqualizerMain, 0, 294, 113, 19);
    public static readonly Sprite EqualizerGraphLineColors = S(SkinSheet.EqualizerMain, 115, 294, 1, 19);
    public static readonly Sprite EqualizerPreampLine = S(SkinSheet.EqualizerMain, 0, 314, 113, 1);
    public static readonly Sprite EqualizerSliderThumb = S(SkinSheet.EqualizerMain, 0, 164, 11, 11);
    public static readonly Sprite EqualizerSliderThumbPressed = S(SkinSheet.EqualizerMain, 0, 176, 11, 11);

    /// <summary>One of the 28 slider backgrounds, laid out 14 to a row; 0 is the bottom (lowest gain), 27 the top.</summary>
    public static Sprite EqualizerSliderFrame(int frame)
    {
        frame = Math.Clamp(frame, 0, 27);
        return S(SkinSheet.EqualizerMain, 13 + 15 * (frame % 14), 164 + 65 * (frame / 14), 14, 63);
    }

    // EQ_EX.BMP
    public static readonly Sprite EqualizerShade = S(SkinSheet.EqualizerExtra, 0, 15, 275, 14);
    public static readonly Sprite EqualizerShadeSelected = S(SkinSheet.EqualizerExtra, 0, 0, 275, 14);
    public static readonly Sprite EqualizerShadeButtonPressed = S(SkinSheet.EqualizerExtra, 1, 47, 9, 9);
    public static readonly Sprite EqualizerUnshadeButtonPressed = S(SkinSheet.EqualizerExtra, 1, 38, 9, 9);
    public static readonly Sprite EqualizerShadeClosePressed = S(SkinSheet.EqualizerExtra, 11, 47, 9, 9);

    // PLEDIT.BMP
    public static Sprite PlaylistTopLeft(bool selected) => S(SkinSheet.Playlist, 0, selected ? 0 : 21, 25, 20);
    public static Sprite PlaylistTitle(bool selected) => S(SkinSheet.Playlist, 26, selected ? 0 : 21, 100, 20);
    public static Sprite PlaylistTopTile(bool selected) => S(SkinSheet.Playlist, 127, selected ? 0 : 21, 25, 20);
    public static Sprite PlaylistTopRight(bool selected) => S(SkinSheet.Playlist, 153, selected ? 0 : 21, 25, 20);
    public static readonly Sprite PlaylistLeftTile = S(SkinSheet.Playlist, 0, 42, 12, 29);
    public static readonly Sprite PlaylistRightTile = S(SkinSheet.Playlist, 31, 42, 20, 29);
    public static readonly Sprite PlaylistBottomTile = S(SkinSheet.Playlist, 179, 0, 25, 38);
    public static readonly Sprite PlaylistBottomLeft = S(SkinSheet.Playlist, 0, 72, 125, 38);
    public static readonly Sprite PlaylistBottomRight = S(SkinSheet.Playlist, 126, 72, 150, 38);
    public static readonly Sprite PlaylistVisualizerBackground = S(SkinSheet.Playlist, 205, 0, 75, 38);
    public static readonly Sprite PlaylistScrollHandle = S(SkinSheet.Playlist, 52, 53, 8, 18);
    public static readonly Sprite PlaylistScrollHandlePressed = S(SkinSheet.Playlist, 61, 53, 8, 18);
    public static readonly Sprite PlaylistClosePressed = S(SkinSheet.Playlist, 52, 42, 9, 9);
    public static readonly Sprite PlaylistShadePressed = S(SkinSheet.Playlist, 62, 42, 9, 9);
    public static readonly Sprite PlaylistShadeLeft = S(SkinSheet.Playlist, 72, 42, 25, 14);
    public static readonly Sprite PlaylistShadeTile = S(SkinSheet.Playlist, 72, 57, 25, 14);
    public static Sprite PlaylistShadeRight(bool selected) => S(SkinSheet.Playlist, 99, selected ? 42 : 57, 50, 14);
    public static readonly Sprite PlaylistUnshadePressed = S(SkinSheet.Playlist, 150, 42, 9, 9);

    // Playlist menus: a bar on the left, then the items stacked upwards.
    public static readonly Sprite AddMenuBar = S(SkinSheet.Playlist, 48, 111, 3, 54);
    public static readonly Sprite AddUrl = S(SkinSheet.Playlist, 0, 111, 22, 18);
    public static readonly Sprite AddDirectory = S(SkinSheet.Playlist, 0, 130, 22, 18);
    public static readonly Sprite AddFile = S(SkinSheet.Playlist, 0, 149, 22, 18);
    public static readonly Sprite RemoveMenuBar = S(SkinSheet.Playlist, 100, 111, 3, 72);
    public static readonly Sprite RemoveAll = S(SkinSheet.Playlist, 54, 111, 22, 18);
    public static readonly Sprite RemoveCrop = S(SkinSheet.Playlist, 54, 130, 22, 18);
    public static readonly Sprite RemoveSelected = S(SkinSheet.Playlist, 54, 149, 22, 18);
    public static readonly Sprite RemoveMisc = S(SkinSheet.Playlist, 54, 168, 22, 18);
    public static readonly Sprite SelectMenuBar = S(SkinSheet.Playlist, 150, 111, 3, 54);
    public static readonly Sprite SelectInvert = S(SkinSheet.Playlist, 104, 111, 22, 18);
    public static readonly Sprite SelectNone = S(SkinSheet.Playlist, 104, 130, 22, 18);
    public static readonly Sprite SelectAll = S(SkinSheet.Playlist, 104, 149, 22, 18);
    public static readonly Sprite MiscMenuBar = S(SkinSheet.Playlist, 200, 111, 3, 54);
    public static readonly Sprite MiscSort = S(SkinSheet.Playlist, 154, 111, 22, 18);
    public static readonly Sprite MiscFileInfo = S(SkinSheet.Playlist, 154, 130, 22, 18);
    public static readonly Sprite MiscOptions = S(SkinSheet.Playlist, 154, 149, 22, 18);
    public static readonly Sprite ListMenuBar = S(SkinSheet.Playlist, 250, 111, 3, 54);
    public static readonly Sprite ListNew = S(SkinSheet.Playlist, 204, 111, 22, 18);
    public static readonly Sprite ListSave = S(SkinSheet.Playlist, 204, 130, 22, 18);
    public static readonly Sprite ListLoad = S(SkinSheet.Playlist, 204, 149, 22, 18);

    /// <summary>A menu item's highlighted look sits 23 pixels to the right of its normal one.</summary>
    public static Sprite Highlighted(Sprite item) => item.Offset(23, 0);

    // TEXT.BMP: 5x6 cells, 31 to a row.
    public const int CharacterWidth = 5;
    public const int CharacterHeight = 6;

    /// <summary>The cell for <paramref name="character"/>; anything the font lacks is drawn as a space.</summary>
    public static Sprite Character(char character)
    {
        (int row, int column) = TextFont.Cell(character);
        return S(SkinSheet.Text, column * CharacterWidth, row * CharacterHeight, CharacterWidth, CharacterHeight);
    }
}

/// <summary>The character layout of TEXT.BMP.</summary>
public static class TextFont
{
    public const string Row0 = "abcdefghijklmnopqrstuvwxyz\"@";
    public const string Row1 = "0123456789….:()-'!_+\\/[]^&%,=$#";
    public const string Row2 = "ÅÖÄ?*";

    private const int SpaceRow = 0;
    private const int SpaceColumn = 30;

    public static (int Row, int Column) Cell(char character)
    {
        char lower = char.ToLowerInvariant(character);
        int index = Row0.IndexOf(lower, StringComparison.Ordinal);
        if (index >= 0) return (0, index);
        switch (character)
        {
            case '<' or '{': return (1, Row1.IndexOf('[', StringComparison.Ordinal));
            case '>' or '}': return (1, Row1.IndexOf(']', StringComparison.Ordinal));
            case 'å': return (2, 0);
            case 'ö': return (2, 1);
            case 'ä': return (2, 2);
        }
        index = Row1.IndexOf(character, StringComparison.Ordinal);
        if (index >= 0) return (1, index);
        index = Row2.IndexOf(character, StringComparison.Ordinal);
        if (index >= 0) return (2, index);
        // Accented letters fall back to their base letter so titles stay readable.
        string plain = RemoveAccent(lower);
        index = plain.Length == 1 ? Row0.IndexOf(plain[0], StringComparison.Ordinal) : -1;
        return index >= 0 ? (0, index) : (SpaceRow, SpaceColumn);
    }

    /// <summary>Whether the bitmap font can show <paramref name="character"/> (itself or as its unaccented letter).</summary>
    public static bool Supports(char character) => character == ' ' || Cell(character) != (SpaceRow, SpaceColumn);

    private static string RemoveAccent(char character)
    {
        string decomposed = character.ToString().Normalize(System.Text.NormalizationForm.FormD);
        return new string([.. decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)]);
    }
}
