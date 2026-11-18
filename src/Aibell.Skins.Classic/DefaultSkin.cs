namespace Aibell.Skins.Classic;

/// <summary>
/// Aibell's own classic skin, drawn in code: the standard layout in Aibell's graphite and blue palette, with a pixel font
/// and digits of its own. It is what Classic Player Mode shows until another skin is chosen, and it fills in whatever
/// another skin leaves out.
/// </summary>
public static class DefaultSkin
{
    public const string Name = "Aibell";

    // Palette.
    internal const uint Body = 0xFF2B3035;
    internal const uint BodyTop = 0xFF353B41;
    internal const uint BodyBottom = 0xFF23272B;
    internal const uint Light = 0xFF5C646C;
    internal const uint Shadow = 0xFF0F1214;
    internal const uint Face = 0xFF3A4147;
    internal const uint FacePressed = 0xFF24292D;
    internal const uint Glyph = 0xFFC9CED3;
    internal const uint Label = 0xFF8A939C;
    internal const uint Accent = 0xFF5AA0F2;
    internal const uint AccentDark = 0xFF2F7DE1;
    internal const uint AccentDeep = 0xFF1D4F8F;
    internal const uint Lcd = 0xFF0A121B;
    internal const uint LcdText = 0xFF8FC3FF;
    internal const uint LcdDim = 0xFF15212F;
    internal const uint TitleTop = 0xFF3D4A5A;
    internal const uint TitleBottom = 0xFF2B3035;
    internal const uint TitleInactive = 0xFF30353A;

    private static readonly Lazy<ClassicSkin> Instance = new(Create);

    /// <summary>The default skin, drawn once.</summary>
    public static ClassicSkin Skin => Instance.Value;

    private static ClassicSkin Create()
    {
        Dictionary<SkinSheet, SkinImage> sheets = new()
        {
            [SkinSheet.Main] = PaintMain(),
            [SkinSheet.ControlButtons] = PaintControlButtons(),
            [SkinSheet.TitleBar] = PaintTitleBar(),
            [SkinSheet.ShuffleRepeat] = PaintShuffleRepeat(),
            [SkinSheet.Text] = PaintText(),
            [SkinSheet.Numbers] = PaintNumbers(),
            [SkinSheet.PlayPause] = PaintPlayPause(),
            [SkinSheet.MonoStereo] = PaintMonoStereo(),
            [SkinSheet.PositionBar] = PaintPositionBar(),
            [SkinSheet.Volume] = PaintVolume(balance: false),
            [SkinSheet.Balance] = PaintVolume(balance: true),
            [SkinSheet.EqualizerMain] = PaintEqualizer(),
            [SkinSheet.EqualizerExtra] = PaintEqualizerExtra(),
            [SkinSheet.Playlist] = PaintPlaylist(),
        };
        PlaylistStyle playlist = new(LcdText, 0xFFFFFFFF, Lcd, AccentDeep, "Arial");
        VisualizerColors visualizer = VisualizerColors.Create(
        [
            Lcd, LcdDim,
            0xFFE8F2FF, 0xFFD2E6FF, 0xFFBCD9FF, 0xFFA6CCFF, 0xFF90BFFC, 0xFF7AB2F8, 0xFF64A5F4, 0xFF5A9CEC,
            0xFF5193E2, 0xFF4889D6, 0xFF407FCA, 0xFF3875BE, 0xFF306BB1, 0xFF2861A4, 0xFF215797, 0xFF1B4D8A,
            0xFFFFFFFF, 0xFFD2E6FF, 0xFFA6CCFF, 0xFF7AB2F8, 0xFF4889D6, 0xFFE8F2FF,
        ]);
        return new ClassicSkin(Name, sheets, hasExtendedNumbers: true, playlist, visualizer, SkinRegions.None);
    }

    // ---- MAIN.BMP -------------------------------------------------------------------------------------------------

    private static SkinImage PaintMain()
    {
        Canvas canvas = new(275, 116);
        canvas.VerticalGradient(0, 0, 275, 116, BodyTop, BodyBottom);
        canvas.Bevel(0, 0, 275, 116, Light, Shadow);
        // The display: time, play state and visualizer.
        canvas.Bevel(19, 21, 88, 42, Shadow, Light);
        canvas.Fill(20, 22, 86, 40, Lcd);
        // The time's colon belongs to the background, between the minute and second digits.
        canvas.Fill(72, 30, 2, 2, LcdText);
        canvas.Fill(72, 35, 2, 2, LcdText);
        // Song title, bit rate and sample rate.
        canvas.Bevel(108, 22, 160, 15, Shadow, Light);
        canvas.Fill(109, 23, 158, 13, Lcd);
        canvas.Bevel(108, 40, 21, 12, Shadow, Light);
        canvas.Fill(109, 41, 19, 10, Lcd);
        canvas.Bevel(153, 40, 16, 12, Shadow, Light);
        canvas.Fill(154, 41, 14, 10, Lcd);
        canvas.Text(131, 43, "kbps", Label);
        canvas.Text(171, 43, "khz", Label);
        // Groove under the volume, balance and window buttons.
        canvas.HorizontalLine(10, 68, 255, Shadow);
        canvas.HorizontalLine(10, 69, 255, Light);
        canvas.HorizontalLine(10, 85, 255, Shadow);
        canvas.HorizontalLine(10, 86, 255, Light);
        // The bell, where the original players kept their logo.
        canvas.Icon(253, 91, Accent,
            "......#......",
            ".....###.....",
            "....#####....",
            "...#######...",
            "...#######...",
            "...#######...",
            "..#########..",
            "..#########..",
            ".###########.",
            "#############",
            ".............",
            "......##.....",
            "......##.....");
        return canvas.Image;
    }

    // ---- TITLEBAR.BMP ---------------------------------------------------------------------------------------------

    private static SkinImage PaintTitleBar()
    {
        Canvas canvas = new(344, 87);
        canvas.Fill(0, 0, 344, 87, Body);
        PaintTitle(canvas, 27, 0, "AIBELL", active: true);
        PaintTitle(canvas, 27, 15, "AIBELL", active: false);
        PaintTitle(canvas, 27, 57, "AIBELL", active: true);
        PaintTitle(canvas, 27, 72, "AIBELL", active: false);
        PaintShade(canvas, 27, 29, active: true);
        PaintShade(canvas, 27, 42, active: false);

        string[] options = ["#####", ".....", "#####", ".....", "#####"];
        string[] minimize = [".....", ".....", ".....", ".....", "#####"];
        string[] close = ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"];
        string[] shade = ["..#..", ".###.", "#####", ".....", "#####"];
        string[] unshade = ["#####", ".....", "#####", ".###.", "..#.."];
        TitleButton(canvas, 0, 0, options, pressed: false);
        TitleButton(canvas, 0, 9, options, pressed: true);
        TitleButton(canvas, 9, 0, minimize, pressed: false);
        TitleButton(canvas, 9, 9, minimize, pressed: true);
        TitleButton(canvas, 18, 0, close, pressed: false);
        TitleButton(canvas, 18, 9, close, pressed: true);
        TitleButton(canvas, 0, 18, shade, pressed: false);
        TitleButton(canvas, 9, 18, shade, pressed: true);
        TitleButton(canvas, 0, 27, unshade, pressed: false);
        TitleButton(canvas, 9, 27, unshade, pressed: true);

        // Shaded position slider: groove and three thumbs.
        canvas.Fill(0, 36, 17, 7, Lcd);
        canvas.HorizontalLine(0, 39, 17, LcdDim);
        canvas.Fill(17, 36, 3, 7, Glyph);
        canvas.Fill(20, 36, 3, 7, Accent);
        canvas.Fill(23, 36, 3, 7, Glyph);

        // Clutter bar (options, always on top, info, double size, visualizer) and its lit letters.
        PaintClutterBar(canvas, 304, lit: false);
        PaintClutterBar(canvas, 312, lit: false);
        (int Top, int Height, char Letter, int X)[] letters = [(3, 8, 'O', 304), (11, 7, 'A', 312), (18, 7, 'I', 320), (25, 8, 'D', 328), (33, 7, 'V', 336)];
        foreach ((int top, int height, char letter, int x) in letters)
        {
            int y = 44 + top;
            canvas.Fill(x, y, 8, height, AccentDeep);
            canvas.Text(x + 2, y + (height - 5) / 2, letter.ToString(), 0xFFFFFFFF);
        }
        return canvas.Image;
    }

    private static void PaintTitle(Canvas canvas, int x, int y, string title, bool active)
    {
        if (active) canvas.VerticalGradient(x, y, 275, 14, TitleTop, TitleBottom);
        else canvas.Fill(x, y, 275, 14, TitleInactive);
        canvas.Bevel(x, y, 275, 14, Light, Shadow);
        int width = Canvas.TextWidth(title);
        int start = x + (275 - width) / 2;
        uint ridge = active ? AccentDark : Light;
        foreach (int line in new[] { 5, 8 })
        {
            canvas.HorizontalLine(x + 18, y + line, start - 5 - (x + 18), ridge);
            canvas.HorizontalLine(start + width + 4, y + line, x + 240 - (start + width + 4), ridge);
        }
        canvas.Text(start, y + 4, title, active ? 0xFFFFFFFF : Label);
    }

    private static void PaintShade(Canvas canvas, int x, int y, bool active)
    {
        PaintTitle(canvas, x, y, "", active);
        canvas.Fill(x + 16, y + 3, 20, 8, active ? TitleTop : TitleInactive);
        canvas.Text(x + 18, y + 4, "AIB", active ? 0xFFFFFFFF : Label);
        // Mini time and position groove, then the tiny transport icons the shaded window's buttons sit on.
        canvas.Fill(x + 125, y + 3, 33, 8, Lcd);
        canvas.Fill(x + 225, y + 3, 19, 9, Lcd);
        uint glyph = active ? Glyph : Label;
        canvas.Icon(x + 170, y + 4, glyph, "#..#", "#.##", "####", "#.##", "#..#");
        canvas.Icon(x + 179, y + 3, glyph, "#...", "##..", "###.", "####", "###.", "##..", "#...");
        canvas.Icon(x + 188, y + 4, glyph, "##.##", "##.##", "##.##", "##.##", "##.##");
        canvas.Icon(x + 197, y + 4, glyph, "#####", "#####", "#####", "#####", "#####");
        canvas.Icon(x + 206, y + 4, glyph, "#..#", "##.#", "####", "##.#", "#..#");
        canvas.Icon(x + 216, y + 3, glyph, "..#..", ".###.", "#####", ".....", "#####");
    }

    private static void TitleButton(Canvas canvas, int x, int y, string[] icon, bool pressed)
    {
        canvas.Fill(x, y, 9, 9, pressed ? FacePressed : Face);
        if (pressed) canvas.Bevel(x, y, 9, 9, Shadow, Light);
        else canvas.Bevel(x, y, 9, 9, Light, Shadow);
        canvas.Icon(x + 2, y + 2, pressed ? Accent : Glyph, icon);
    }

    private static void PaintClutterBar(Canvas canvas, int x, bool lit)
    {
        canvas.Fill(x, 0, 8, 43, Lcd);
        canvas.Bevel(x, 0, 8, 43, Shadow, Light);
        foreach ((int top, int height, char letter) in new[] { (3, 8, 'O'), (11, 7, 'A'), (18, 7, 'I'), (25, 8, 'D'), (33, 7, 'V') })
            canvas.Text(x + 2, top + (height - 5) / 2, letter.ToString(), lit ? Accent : Label);
    }

    // ---- CBUTTONS.BMP ---------------------------------------------------------------------------------------------

    private static SkinImage PaintControlButtons()
    {
        Canvas canvas = new(136, 36);
        canvas.Fill(0, 0, 136, 36, Body);
        string[] previous = ["#...#..#", "#..##.##", "#.######", "#..##.##", "#...#..#"];
        string[] play = ["#....", "###..", "#####", "###..", "#...."];
        string[] pause = ["##.##", "##.##", "##.##", "##.##", "##.##"];
        string[] stop = ["#####", "#####", "#####", "#####", "#####"];
        string[] next = ["#..#...#", "##.##..#", "######.#", "##.##..#", "#..#...#"];
        string[] eject = ["...#...", "..###..", ".#####.", "#######", ".......", "#######"];
        (int X, int Width, int Height, string[] Icon, bool Accented)[] buttons =
            [(0, 23, 18, previous, false), (23, 23, 18, play, true), (46, 23, 18, pause, false), (69, 23, 18, stop, false), (92, 22, 18, next, false), (114, 22, 16, eject, false)];
        foreach ((int x, int width, int height, string[] icon, bool accented) in buttons)
        {
            for (int state = 0; state < 2; state++)
            {
                bool pressed = state == 1;
                int y = state * height;
                canvas.VerticalGradient(x, y, width, height, pressed ? FacePressed : 0xFF454D54, pressed ? 0xFF1C2024 : Face);
                if (pressed) canvas.Bevel(x, y, width, height, Shadow, Light);
                else canvas.Bevel(x, y, width, height, Light, Shadow);
                int iconWidth = icon[0].Length, iconHeight = icon.Length;
                int shift = pressed ? 1 : 0;
                canvas.Icon(x + (width - iconWidth) / 2 + shift, y + (height - iconHeight) / 2 + shift,
                    accented || pressed ? Accent : Glyph, icon);
            }
        }
        return canvas.Image;
    }

    // ---- SHUFREP.BMP ----------------------------------------------------------------------------------------------

    private static SkinImage PaintShuffleRepeat()
    {
        Canvas canvas = new(92, 85);
        canvas.Fill(0, 0, 92, 85, Body);
        string[] loop = [".####..", "#....#.", "#...###", "#....#.", "#......", ".#####."];
        for (int state = 0; state < 4; state++)
        {
            bool on = state >= 2, pressed = state % 2 == 1;
            int y = state * 15;
            ToggleFace(canvas, 0, y, 28, 15, pressed);
            canvas.Icon(5 + (pressed ? 1 : 0), y + 4 + (pressed ? 1 : 0), Glyph, loop);
            Led(canvas, 20, y + 6, on);
            ToggleFace(canvas, 28, y, 47, 15, pressed);
            canvas.Text(28 + 5 + (pressed ? 1 : 0), y + 5 + (pressed ? 1 : 0), "SHUFFLE", Glyph);
            Led(canvas, 28 + 40, y + 6, on);
        }
        for (int state = 0; state < 4; state++)
        {
            bool on = state % 2 == 1, pressed = state >= 2;
            int y = on ? 73 : 61;
            int x = pressed ? 46 : 0;
            ToggleFace(canvas, x, y, 23, 12, pressed);
            canvas.Text(x + 4, y + 4, "EQ", Glyph);
            Led(canvas, x + 16, y + 4, on);
            ToggleFace(canvas, x + 23, y, 23, 12, pressed);
            canvas.Text(x + 27, y + 4, "PL", Glyph);
            Led(canvas, x + 39, y + 4, on);
        }
        return canvas.Image;
    }

    private static void ToggleFace(Canvas canvas, int x, int y, int width, int height, bool pressed)
    {
        canvas.VerticalGradient(x, y, width, height, pressed ? FacePressed : 0xFF454D54, pressed ? 0xFF1C2024 : Face);
        if (pressed) canvas.Bevel(x, y, width, height, Shadow, Light);
        else canvas.Bevel(x, y, width, height, Light, Shadow);
    }

    private static void Led(Canvas canvas, int x, int y, bool on)
    {
        canvas.Fill(x, y, 3, 3, on ? Accent : LcdDim);
        if (on) canvas[x, y] = 0xFFE8F2FF;
    }

    // ---- TEXT.BMP -------------------------------------------------------------------------------------------------

    private static SkinImage PaintText()
    {
        Canvas canvas = new(155, 18);
        canvas.Fill(0, 0, 155, 18, Lcd);
        string[] rows = [TextFont.Row0, TextFont.Row1, TextFont.Row2];
        for (int row = 0; row < rows.Length; row++)
            for (int column = 0; column < rows[row].Length; column++)
                canvas.Glyph(column * Sprites.CharacterWidth, row * Sprites.CharacterHeight, rows[row][column], LcdText);
        return canvas.Image;
    }

    // ---- NUMS_EX.BMP ----------------------------------------------------------------------------------------------

    private static readonly string[] SevenSegment =
    [
        "abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg",
    ];

    private static SkinImage PaintNumbers()
    {
        Canvas canvas = new(108, 13);
        canvas.Fill(0, 0, 108, 13, Lcd);
        for (int digit = 0; digit < 10; digit++) Segments(canvas, digit * 9, SevenSegment[digit]);
        Segments(canvas, 90, "");
        Segments(canvas, 99, "g");
        return canvas.Image;
    }

    private static void Segments(Canvas canvas, int x, string lit)
    {
        // Unlit segments are left out: at one pixel per skin pixel their ghosts made a 0 read as an A.
        void Segment(char name, int left, int top, int width, int height)
        {
            if (lit.Contains(name, StringComparison.Ordinal)) canvas.Fill(x + left, top, width, height, LcdText);
        }
        Segment('a', 2, 0, 5, 2);
        Segment('f', 1, 2, 2, 4);
        Segment('b', 6, 2, 2, 4);
        Segment('g', 2, 6, 5, 1);
        Segment('e', 1, 7, 2, 4);
        Segment('c', 6, 7, 2, 4);
        Segment('d', 2, 11, 5, 2);
    }

    // ---- PLAYPAUS.BMP and MONOSTER.BMP ----------------------------------------------------------------------------

    private static SkinImage PaintPlayPause()
    {
        Canvas canvas = new(42, 9);
        canvas.Fill(0, 0, 42, 9, Lcd);
        canvas.Icon(2, 1, Accent, "#....", "##...", "###..", "####.", "#####", "####.", "###..", "##...", "#....");
        canvas.Icon(11, 2, LcdText, "##.##", "##.##", "##.##", "##.##", "##.##");
        canvas.Icon(20, 2, LcdText, "#####", "#####", "#####", "#####", "#####");
        canvas.Fill(36, 2, 2, 5, LcdDim);
        canvas.Fill(39, 2, 2, 5, Accent);
        return canvas.Image;
    }

    private static SkinImage PaintMonoStereo()
    {
        Canvas canvas = new(56, 24);
        canvas.Fill(0, 0, 56, 24, Body);
        for (int row = 0; row < 2; row++)
        {
            uint color = row == 0 ? LcdText : LcdDim;
            canvas.Fill(0, row * 12, 56, 12, Lcd);
            canvas.Text(0, row * 12 + 3, "STEREO", color);
            canvas.Text(29 + 4, row * 12 + 3, "MONO", color);
        }
        return canvas.Image;
    }

    // ---- POSBAR.BMP -----------------------------------------------------------------------------------------------

    private static SkinImage PaintPositionBar()
    {
        Canvas canvas = new(307, 10);
        canvas.Fill(0, 0, 307, 10, Body);
        canvas.Fill(1, 3, 246, 4, Lcd);
        canvas.HorizontalLine(1, 2, 246, Shadow);
        canvas.HorizontalLine(1, 7, 246, Light);
        for (int state = 0; state < 2; state++)
        {
            int x = 248 + state * 30;
            canvas.VerticalGradient(x, 0, 29, 10, state == 0 ? 0xFF6C747C : Accent, state == 0 ? Face : AccentDark);
            canvas.Bevel(x, 0, 29, 10, state == 0 ? 0xFF8A939C : 0xFFBCD9FF, Shadow);
            for (int grip = 0; grip < 3; grip++) canvas.VerticalLine(x + 12 + grip * 2, 3, 4, state == 0 ? Shadow : AccentDeep);
        }
        return canvas.Image;
    }

    // ---- VOLUME.BMP and BALANCE.BMP -------------------------------------------------------------------------------

    private static SkinImage PaintVolume(bool balance)
    {
        int width = balance ? 47 : 68;
        Canvas canvas = new(width, 433);
        canvas.Fill(0, 0, width, 433, Body);
        int left = balance ? 9 : 0, trackWidth = balance ? 38 : 68;
        for (int frame = 0; frame < Sprites.SliderFrames; frame++)
        {
            int y = frame * 15;
            canvas.Fill(left, y + 4, trackWidth, 5, Lcd);
            canvas.HorizontalLine(left, y + 3, trackWidth, Shadow);
            canvas.HorizontalLine(left, y + 9, trackWidth, Light);
            double level = frame / 27.0;
            uint color = Blend(AccentDeep, Accent, level);
            if (balance)
            {
                int half = trackWidth / 2, reach = (int)Math.Round(level * (half - 1));
                canvas.Fill(left + half - reach, y + 5, reach * 2 + 1, 3, frame == 0 ? LcdDim : color);
            }
            else
            {
                canvas.Fill(left + 1, y + 5, Math.Max(1, (int)Math.Round(level * (trackWidth - 2))), 3, color);
            }
        }
        for (int state = 0; state < 2; state++)
        {
            int x = state == 0 ? 15 : 0;
            canvas.VerticalGradient(x, 422, 14, 11, state == 0 ? 0xFF6C747C : Accent, state == 0 ? Face : AccentDark);
            canvas.Bevel(x, 422, 14, 11, state == 0 ? 0xFF8A939C : 0xFFBCD9FF, Shadow);
            canvas.VerticalLine(x + 6, 425, 5, Shadow);
            canvas.VerticalLine(x + 7, 425, 5, state == 0 ? Light : 0xFFBCD9FF);
        }
        return canvas.Image;
    }

    // ---- EQMAIN.BMP -----------------------------------------------------------------------------------------------

    private static readonly string[] BandLabels = ["31", "62", "125", "250", "500", "1K", "2K", "4K", "8K", "16K"];

    private static SkinImage PaintEqualizer()
    {
        Canvas canvas = new(275, 315);
        canvas.Fill(0, 0, 275, 315, Body);
        canvas.VerticalGradient(0, 0, 275, 116, BodyTop, BodyBottom);
        canvas.Bevel(0, 0, 275, 116, Light, Shadow);
        canvas.Bevel(85, 16, 115, 21, Shadow, Light);
        canvas.Text(45, 38, "+12DB", Label);
        canvas.Text(45, 66, "+0DB", Label);
        canvas.Text(45, 97, "-12DB", Label);
        canvas.Text(13, 105, "PREAMP", Label);
        for (int band = 0; band < BandLabels.Length; band++)
        {
            int center = 78 + band * 18 + 7;
            canvas.Text(center - Canvas.TextWidth(BandLabels[band]) / 2, 105, BandLabels[band], Label);
        }
        // Title bars with their baked-in shade and close buttons.
        foreach ((int y, bool active) in new[] { (134, true), (149, false) })
        {
            PaintTitle(canvas, 0, y, "EQUALIZER", active);
            TitleButton(canvas, 254, y + 3, ["..#..", ".###.", "#####", ".....", "#####"], pressed: false);
            TitleButton(canvas, 264, y + 3, ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"], pressed: false);
        }
        TitleButton(canvas, 0, 116, ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"], pressed: false);
        TitleButton(canvas, 0, 125, ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"], pressed: true);
        for (int state = 0; state < 4; state++)
        {
            bool on = state % 2 == 1, pressed = state >= 2;
            int offset = (on ? 59 : 0) + (pressed ? 118 : 0);
            ToggleFace(canvas, 10 + offset, 119, 26, 12, pressed);
            Led(canvas, 10 + offset + 4, 123, on);
            canvas.Text(10 + offset + 10, 122, "ON", Glyph);
            ToggleFace(canvas, 36 + offset, 119, 32, 12, pressed);
            Led(canvas, 36 + offset + 4, 123, on);
            canvas.Text(36 + offset + 10, 122, "AUTO", Glyph);
        }
        for (int frame = 0; frame < Sprites.SliderFrames; frame++)
        {
            Sprite slot = Sprites.EqualizerSliderFrame(frame);
            canvas.Fill(slot.X, slot.Y, 14, 63, Body);
            canvas.Fill(slot.X + 5, slot.Y + 1, 4, 61, Lcd);
            canvas.VerticalLine(slot.X + 4, slot.Y + 1, 61, Shadow);
            canvas.VerticalLine(slot.X + 9, slot.Y + 1, 61, Light);
            int filled = (int)Math.Round(frame / 27.0 * 59);
            canvas.Fill(slot.X + 6, slot.Y + 61 - filled, 2, filled + 1, Blend(AccentDeep, Accent, frame / 27.0));
        }
        for (int state = 0; state < 2; state++)
        {
            int y = 164 + state * 12;
            canvas.VerticalGradient(0, y, 11, 11, state == 0 ? 0xFF6C747C : Accent, state == 0 ? Face : AccentDark);
            canvas.Bevel(0, y, 11, 11, state == 0 ? 0xFF8A939C : 0xFFBCD9FF, Shadow);
            canvas.HorizontalLine(3, y + 5, 5, Shadow);
        }
        for (int state = 0; state < 2; state++)
        {
            int y = 164 + state * 12;
            ToggleFace(canvas, 224, y, 44, 12, state == 1);
            canvas.Text(224 + 5 + state, y + 4 + state, "PRESETS", Glyph);
        }
        canvas.Fill(0, 294, 113, 19, Lcd);
        for (int x = 0; x < 113; x += 4) canvas[x, 303] = LcdDim;
        for (int row = 0; row < 19; row++) canvas[115, 294 + row] = Blend(0xFFE8F2FF, AccentDeep, row / 18.0);
        canvas.HorizontalLine(0, 314, 113, LcdDim);
        return canvas.Image;
    }

    private static SkinImage PaintEqualizerExtra()
    {
        Canvas canvas = new(275, 82);
        canvas.Fill(0, 0, 275, 82, Body);
        foreach ((int y, bool active) in new[] { (0, true), (15, false) })
        {
            PaintTitle(canvas, 0, y, "", active);
            canvas.Text(6, y + 4, "EQ", active ? 0xFFFFFFFF : Label);
            canvas.Fill(60, y + 4, 99, 6, Lcd);
            canvas.Fill(163, y + 4, 45, 6, Lcd);
            TitleButton(canvas, 254, y + 3, ["#####", ".....", "#####", ".###.", "..#.."], pressed: false);
            TitleButton(canvas, 264, y + 3, ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"], pressed: false);
        }
        for (int part = 0; part < 6; part++) canvas.Fill(1 + part * 3 + (part >= 3 ? 1 : 0), 30, 3, 7, part % 3 == 1 ? Accent : Glyph);
        TitleButton(canvas, 1, 38, ["#####", ".....", "#####", ".###.", "..#.."], pressed: true);
        TitleButton(canvas, 1, 47, ["..#..", ".###.", "#####", ".....", "#####"], pressed: true);
        TitleButton(canvas, 11, 38, ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"], pressed: false);
        TitleButton(canvas, 11, 47, ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"], pressed: true);
        return canvas.Image;
    }

    // ---- PLEDIT.BMP -----------------------------------------------------------------------------------------------

    private static SkinImage PaintPlaylist()
    {
        Canvas canvas = new(280, 186);
        canvas.Fill(0, 0, 280, 186, Body);
        string[] close = ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"];
        string[] shade = ["..#..", ".###.", "#####", ".....", "#####"];
        foreach ((int y, bool active) in new[] { (0, true), (21, false) })
        {
            uint top = active ? TitleTop : TitleInactive;
            canvas.VerticalGradient(0, y, 25, 20, top, TitleBottom);
            canvas.VerticalGradient(26, y, 100, 20, top, TitleBottom);
            canvas.VerticalGradient(127, y, 25, 20, top, TitleBottom);
            canvas.VerticalGradient(153, y, 25, 20, top, TitleBottom);
            uint ridge = active ? AccentDark : Light;
            foreach (int x in new[] { 0, 127 })
                foreach (int line in new[] { 7, 10 }) canvas.HorizontalLine(x, y + line, 25, ridge);
            foreach (int line in new[] { 7, 10 }) canvas.HorizontalLine(153, y + line, 3, ridge);
            canvas.HorizontalLine(0, y, 178, Light);
            canvas.VerticalLine(0, y, 20, Light);
            canvas.VerticalLine(177, y, 20, Shadow);
            const string title = "AIBELL PLAYLIST";
            canvas.Text(26 + (100 - Canvas.TextWidth(title)) / 2, y + 7, title, active ? 0xFFFFFFFF : Label);
            canvas.Fill(12, y + 19, 154, 1, Shadow);
            TitleButton(canvas, 153 + 4, y + 3, shade, pressed: false);
            TitleButton(canvas, 153 + 14, y + 3, close, pressed: false);
        }
        // Sides: the left border and the right border with its scroll track.
        canvas.Fill(0, 42, 12, 29, Body);
        canvas.VerticalLine(0, 42, 29, Light);
        canvas.VerticalLine(11, 42, 29, Shadow);
        canvas.Fill(31, 42, 20, 29, Body);
        canvas.VerticalLine(31, 42, 29, Light);
        canvas.VerticalLine(50, 42, 29, Shadow);
        canvas.Fill(36, 42, 8, 29, Lcd);
        TitleButton(canvas, 52, 42, close, pressed: true);
        TitleButton(canvas, 62, 42, shade, pressed: true);
        TitleButton(canvas, 150, 42, ["#####", ".....", "#####", ".###.", "..#.."], pressed: true);
        for (int state = 0; state < 2; state++)
        {
            int x = 52 + state * 9;
            canvas.VerticalGradient(x, 53, 8, 18, state == 0 ? 0xFF6C747C : Accent, state == 0 ? Face : AccentDark);
            canvas.Bevel(x, 53, 8, 18, state == 0 ? 0xFF8A939C : 0xFFBCD9FF, Shadow);
            for (int grip = 0; grip < 3; grip++) canvas.HorizontalLine(x + 2, 58 + grip * 2, 4, Shadow);
        }
        // Shaded playlist bar.
        foreach ((int x, int y, int width) in new[] { (72, 42, 25), (72, 57, 25), (99, 42, 50), (99, 57, 50) })
        {
            canvas.VerticalGradient(x, y, width, 14, y == 42 ? TitleTop : TitleInactive, TitleBottom);
            canvas.HorizontalLine(x, y, width, Light);
            canvas.HorizontalLine(x, y + 13, width, Shadow);
        }
        canvas.VerticalLine(72, 42, 14, Light);
        canvas.VerticalLine(72, 57, 14, Light);
        foreach (int y in new[] { 42, 57 })
        {
            canvas.VerticalLine(148, y, 14, Shadow);
            TitleButton(canvas, 99 + 30, y + 3, ["#####", ".....", "#####", ".###.", "..#.."], pressed: false);
            TitleButton(canvas, 99 + 39, y + 3, close, pressed: false);
        }
        PaintPlaylistBottom(canvas);
        PaintPlaylistMenus(canvas);
        return canvas.Image;
    }

    private static void PaintPlaylistBottom(Canvas canvas)
    {
        // Bottom tile, left corner (menu buttons) and right corner (running time, transport, list button, grip).
        canvas.VerticalGradient(179, 0, 25, 38, BodyTop, BodyBottom);
        canvas.HorizontalLine(179, 37, 25, Shadow);
        canvas.HorizontalLine(179, 0, 25, Shadow);
        // The visualizer panel a wide playlist shows in its bottom bar, with a window onto the spectrum at (2, 12).
        canvas.VerticalGradient(205, 0, 75, 38, BodyTop, BodyBottom);
        canvas.HorizontalLine(205, 0, 75, Shadow);
        canvas.HorizontalLine(205, 37, 75, Shadow);
        canvas.Bevel(206, 11, 74, 18, Shadow, Light);
        canvas.Fill(207, 12, 72, 16, Lcd);
        canvas.VerticalGradient(0, 72, 125, 38, BodyTop, BodyBottom);
        canvas.VerticalLine(0, 72, 38, Light);
        canvas.HorizontalLine(0, 109, 125, Shadow);
        canvas.HorizontalLine(0, 72, 125, Shadow);
        string[] labels = ["ADD", "REM", "SEL", "MISC"];
        for (int index = 0; index < labels.Length; index++)
        {
            int x = 14 + index * 29;
            ToggleFace(canvas, x, 72 + 8, 22, 18, pressed: false);
            canvas.Text(x + (22 - Canvas.TextWidth(labels[index])) / 2, 72 + 8 + 7, labels[index], Glyph);
        }
        canvas.VerticalGradient(126, 72, 150, 38, BodyTop, BodyBottom);
        canvas.HorizontalLine(126, 72, 150, Shadow);
        canvas.HorizontalLine(126, 109, 150, Shadow);
        canvas.VerticalLine(275, 72, 38, Shadow);
        canvas.Fill(126 + 6, 72 + 9, 90, 9, Lcd);
        canvas.Fill(126 + 62, 72 + 21, 30, 9, Lcd);
        // Mini transport: previous, play, pause, stop, next, eject, ten pixels each.
        int miniX = 126 + 3, miniY = 72 + 22;
        canvas.Icon(miniX + 2, miniY + 2, Glyph, "#..#", "#.##", "####", "#.##", "#..#");
        canvas.Icon(miniX + 13, miniY + 1, Glyph, "#...", "##..", "###.", "####", "###.", "##..", "#...");
        canvas.Icon(miniX + 22, miniY + 2, Glyph, "##.##", "##.##", "##.##", "##.##", "##.##");
        canvas.Icon(miniX + 32, miniY + 2, Glyph, "#####", "#####", "#####", "#####", "#####");
        canvas.Icon(miniX + 43, miniY + 2, Glyph, "#..#", "##.#", "####", "##.#", "#..#");
        canvas.Icon(miniX + 52, miniY + 1, Glyph, "..#..", ".###.", "#####", ".....", "#####");
        int list = 126 + 150 - 44;
        ToggleFace(canvas, list, 72 + 8, 22, 18, pressed: false);
        canvas.Text(list + (22 - Canvas.TextWidth("LIST")) / 2, 72 + 15, "LIST", Glyph);
        for (int step = 0; step < 4; step++)
            for (int dot = 0; dot <= step; dot++)
                canvas[126 + 146 - step * 3 + dot * 3, 72 + 26 + step * 3] = Light;
    }

    private static void PaintPlaylistMenus(Canvas canvas)
    {
        (Sprite Item, string Label)[] items =
        [
            (Sprites.AddUrl, "LIB"), (Sprites.AddDirectory, "DIR"), (Sprites.AddFile, "FILE"),
            (Sprites.RemoveAll, "ALL"), (Sprites.RemoveCrop, "CROP"), (Sprites.RemoveSelected, "SEL"), (Sprites.RemoveMisc, "DUP"),
            (Sprites.SelectInvert, "INV"), (Sprites.SelectNone, "NONE"), (Sprites.SelectAll, "ALL"),
            (Sprites.MiscSort, "SORT"), (Sprites.MiscFileInfo, "INFO"), (Sprites.MiscOptions, "OPTS"),
            (Sprites.ListNew, "NEW"), (Sprites.ListSave, "SAVE"), (Sprites.ListLoad, "LOAD"),
        ];
        foreach ((Sprite item, string label) in items)
        {
            for (int state = 0; state < 2; state++)
            {
                Sprite at = state == 0 ? item : Sprites.Highlighted(item);
                canvas.Fill(at.X, at.Y, at.Width, at.Height, state == 0 ? Face : AccentDeep);
                canvas.Bevel(at.X, at.Y, at.Width, at.Height, state == 0 ? Light : Accent, Shadow);
                canvas.Text(at.X + (at.Width - Canvas.TextWidth(label)) / 2, at.Y + 7, label, state == 0 ? Glyph : 0xFFFFFFFF);
            }
        }
        foreach (Sprite bar in new[] { Sprites.AddMenuBar, Sprites.RemoveMenuBar, Sprites.SelectMenuBar, Sprites.MiscMenuBar, Sprites.ListMenuBar })
        {
            canvas.Fill(bar.X, bar.Y, bar.Width, bar.Height, AccentDark);
            canvas.VerticalLine(bar.X, bar.Y, bar.Height, Accent);
        }
    }

    internal static uint Blend(uint from, uint to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        int Mix(int shift) => (int)Math.Round(((from >> shift) & 0xFF) * (1 - amount) + ((to >> shift) & 0xFF) * amount);
        return BmpDecoder.Opaque(Mix(16), Mix(8), Mix(0));
    }
}
