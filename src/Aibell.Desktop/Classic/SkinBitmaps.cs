using Aibell.Skins.Classic;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Aibell.Desktop.Classic;

/// <summary>The sheets of a skin turned into bitmaps the renderer can draw from.</summary>
internal sealed class SkinBitmaps : IDisposable
{
    private readonly Dictionary<SkinSheet, Bitmap> bitmaps = [];

    public SkinBitmaps(ClassicSkin skin)
    {
        Skin = skin ?? throw new ArgumentNullException(nameof(skin));
        foreach (SkinSheet sheet in Enum.GetValues<SkinSheet>()) bitmaps[sheet] = ToBitmap(skin[sheet]);
    }

    public ClassicSkin Skin { get; }

    public Bitmap this[SkinSheet sheet] => bitmaps[sheet];

    public static WriteableBitmap ToBitmap(SkinImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        WriteableBitmap bitmap = new(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using ILockedFramebuffer frame = bitmap.Lock();
        int[] row = new int[image.Width];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                uint pixel = image.Pixels[y * image.Width + x];
                uint alpha = pixel >> 24;
                // 0xAARRGGBB is BGRA in memory on little-endian machines; premultiply for the (rare) transparent pixel.
                row[x] = unchecked((int)(alpha == 255 ? pixel : alpha == 0 ? 0 : Premultiply(pixel, alpha)));
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, image.Width);
        }
        return bitmap;
    }

    private static uint Premultiply(uint pixel, uint alpha)
    {
        uint Channel(int shift) => (((pixel >> shift) & 0xFF) * alpha + 127) / 255;
        return alpha << 24 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    public void Dispose()
    {
        foreach (Bitmap bitmap in bitmaps.Values) bitmap.Dispose();
        bitmaps.Clear();
    }
}
