namespace Aibell.Platform.Windows;

internal enum TaskbarGlyph { Previous, Play, Pause, Next }

/// <summary>
/// Draws the thumbnail-toolbar button images: a white glyph with a thin dark outline, so it reads on both the light and
/// the dark taskbar flyout. Pixels are straight-alpha BGRA, top-down, which is what a 32-bit GDI icon bitmap takes.
/// </summary>
internal static class TaskbarGlyphs
{
    public const int Size = 32;
    private const int Samples = 4;
    private const byte Outline = 0x28;

    public static byte[] Render(TaskbarGlyph glyph)
    {
        double[] coverage = new double[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < Samples; sy++)
                    for (int sx = 0; sx < Samples; sx++)
                        if (Contains(glyph, (x + (sx + 0.5) / Samples) / Size, (y + (sy + 0.5) / Samples) / Size)) hits++;
                coverage[y * Size + x] = hits / (double)(Samples * Samples);
            }

        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double fill = coverage[y * Size + x];
                double ring = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < Size && ny < Size) ring = Math.Max(ring, coverage[ny * Size + nx]);
                    }
                double outline = ring * (1 - fill);
                double alpha = fill + outline;
                if (alpha <= 0) continue;
                byte colour = (byte)Math.Round((255 * fill + Outline * outline) / alpha);
                int at = (y * Size + x) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = colour;
                pixels[at + 3] = (byte)Math.Round(alpha * 255);
            }
        return pixels;
    }

    // Shapes in unit coordinates, y down. Previous mirrors Next.
    private static bool Contains(TaskbarGlyph glyph, double x, double y) => glyph switch
    {
        TaskbarGlyph.Play => InTriangle(x, y, 0.30, 0.16, 0.30, 0.84, 0.82, 0.50),
        TaskbarGlyph.Pause => y is >= 0.18 and <= 0.82 && (x is >= 0.22 and <= 0.42 || x is >= 0.58 and <= 0.78),
        TaskbarGlyph.Next => NextShape(x, y),
        _ => NextShape(1 - x, y)
    };

    private static bool NextShape(double x, double y) =>
        InTriangle(x, y, 0.16, 0.20, 0.16, 0.80, 0.66, 0.50) || (x is >= 0.70 and <= 0.84 && y is >= 0.20 and <= 0.80);

    private static bool InTriangle(double px, double py, double ax, double ay, double bx, double by, double cx, double cy)
    {
        double d1 = Side(px, py, ax, ay, bx, by), d2 = Side(px, py, bx, by, cx, cy), d3 = Side(px, py, cx, cy, ax, ay);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    private static double Side(double px, double py, double ax, double ay, double bx, double by) =>
        (px - bx) * (ay - by) - (ax - bx) * (py - by);
}
