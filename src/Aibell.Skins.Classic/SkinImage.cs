namespace Aibell.Skins.Classic;

/// <summary>
/// A decoded skin bitmap: <see cref="Pixels"/> holds one 0xAARRGGBB value per pixel, row by row from the top-left corner.
/// Classic skins have no alpha channel, so every decoded pixel is opaque unless a skin painter made it otherwise.
/// </summary>
public sealed class SkinImage
{
    /// <summary>The largest side accepted from a skin file; real sheets are a few hundred pixels at most.</summary>
    public const int MaximumSide = 4096;

    public SkinImage(int width, int height, uint[]? pixels = null)
    {
        if (width is < 1 or > MaximumSide) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > MaximumSide) throw new ArgumentOutOfRangeException(nameof(height));
        pixels ??= new uint[width * height];
        if (pixels.Length != width * height) throw new ArgumentException("The pixel count does not match the size.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public uint[] Pixels { get; }

    /// <summary>The pixel at (x, y), or transparent black outside the image.</summary>
    public uint this[int x, int y]
    {
        get => x >= 0 && y >= 0 && x < Width && y < Height ? Pixels[y * Width + x] : 0;
        set
        {
            if (x >= 0 && y >= 0 && x < Width && y < Height) Pixels[y * Width + x] = value;
        }
    }

    /// <summary>Copies a rectangle of <paramref name="source"/> to (<paramref name="x"/>, <paramref name="y"/>), clipping to both images.</summary>
    public void Blit(SkinImage source, int sourceX, int sourceY, int width, int height, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(source);
        for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
            {
                int fromX = sourceX + column, fromY = sourceY + row, toX = x + column, toY = y + row;
                if (fromX < 0 || fromY < 0 || fromX >= source.Width || fromY >= source.Height) continue;
                this[toX, toY] = source.Pixels[fromY * source.Width + fromX];
            }
    }

    /// <summary>A copy at least <paramref name="width"/> by <paramref name="height"/>, padding with transparent pixels; this image when it is big enough.</summary>
    public SkinImage AtLeast(int width, int height)
    {
        if (Width >= width && Height >= height) return this;
        SkinImage larger = new(Math.Max(width, Width), Math.Max(height, Height));
        larger.Blit(this, 0, 0, Width, Height, 0, 0);
        return larger;
    }
}
