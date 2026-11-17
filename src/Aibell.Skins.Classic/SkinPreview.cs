namespace Aibell.Skins.Classic;

/// <summary>A still picture of a skin's main window, as it looks while a track plays, for choosing between skins.</summary>
public static class SkinPreview
{
    public static SkinImage Render(ClassicSkin skin, string title = "AIBELL - CLASSIC PLAYER MODE")
    {
        ArgumentNullException.ThrowIfNull(skin);
        SkinImage image = new(275, 116);
        void Draw(Sprite sprite, int x, int y) => image.Blit(skin[sprite.Sheet], sprite.X, sprite.Y, sprite.Width, sprite.Height, x, y);
        void Text(string text, int x, int y, int count)
        {
            for (int index = 0; index < Math.Min(text.Length, count); index++) Draw(Sprites.Character(text[index]), x + index * Sprites.CharacterWidth, y);
        }
        Draw(Sprites.MainBackground, 0, 0);
        Draw(Sprites.TitleBarSelected, 0, 0);
        Draw(Sprites.OptionsButton, 6, 3);
        Draw(Sprites.MinimizeButton, 244, 3);
        Draw(Sprites.ShadeButton, 254, 3);
        Draw(Sprites.CloseButton, 264, 3);
        Draw(Sprites.ClutterBar, 10, 22);
        Draw(Sprites.PlayingIndicator, 26, 28);
        if (skin.HasExtendedNumbers) Draw(Sprites.DigitBlank, 36, 26);
        Draw(Sprites.Digit(0), 48, 26);
        Draw(Sprites.Digit(1), 60, 26);
        Draw(Sprites.Digit(4), 78, 26);
        Draw(Sprites.Digit(2), 90, 26);
        Text($"1. {title}", 111, 27, 31);
        Text("320", 111, 43, 3);
        Text("44", 156, 43, 2);
        Draw(Sprites.Mono(false), 212, 41);
        Draw(Sprites.Stereo(true), 239, 41);
        Draw(Sprites.VolumeFrame(21), 107, 57);
        Draw(Sprites.VolumeThumb, 107 + 40, 58);
        Draw(Sprites.BalanceFrame(0), 177, 57);
        Draw(Sprites.BalanceThumb, 177 + 12, 58);
        Draw(Sprites.EqualizerToggle(true, false), 219, 58);
        Draw(Sprites.PlaylistToggle(true, false), 242, 58);
        Draw(Sprites.PositionBackground, 16, 72);
        Draw(Sprites.PositionThumb, 16 + 70, 72);
        Draw(Sprites.Previous, 16, 88);
        Draw(Sprites.Play, 39, 88);
        Draw(Sprites.Pause, 62, 88);
        Draw(Sprites.Stop, 85, 88);
        Draw(Sprites.Next, 108, 88);
        Draw(Sprites.Eject, 136, 89);
        Draw(Sprites.Shuffle(false, false), 164, 89);
        Draw(Sprites.Repeat(true, false), 210, 89);
        return image;
    }
}
