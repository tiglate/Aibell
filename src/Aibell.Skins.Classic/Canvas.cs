namespace Aibell.Skins.Classic;

/// <summary>A tiny pixel painter for the default skin: rectangles, bevels, gradients, icons and Aibell's 5x6 pixel font.</summary>
internal sealed class Canvas(int width, int height)
{
    public SkinImage Image { get; } = new(width, height);

    public uint this[int x, int y]
    {
        get => Image[x, y];
        set => Image[x, y] = value;
    }

    public void Fill(int x, int y, int width, int height, uint color)
    {
        for (int row = y; row < y + height; row++)
            for (int column = x; column < x + width; column++)
                Image[column, row] = color;
    }

    public void HorizontalLine(int x, int y, int length, uint color) => Fill(x, y, length, 1, color);

    public void VerticalLine(int x, int y, int length, uint color) => Fill(x, y, 1, length, color);

    /// <summary>A one-pixel frame: <paramref name="topLeft"/> on the top and left edges, <paramref name="bottomRight"/> on the others.</summary>
    public void Bevel(int x, int y, int width, int height, uint topLeft, uint bottomRight)
    {
        HorizontalLine(x, y, width, topLeft);
        VerticalLine(x, y, height, topLeft);
        HorizontalLine(x, y + height - 1, width, bottomRight);
        VerticalLine(x + width - 1, y, height, bottomRight);
    }

    public void VerticalGradient(int x, int y, int width, int height, uint top, uint bottom)
    {
        for (int row = 0; row < height; row++)
            HorizontalLine(x, y + row, width, DefaultSkin.Blend(top, bottom, height == 1 ? 0 : row / (double)(height - 1)));
    }

    /// <summary>Paints the '#' cells of <paramref name="rows"/>.</summary>
    public void Icon(int x, int y, uint color, params string[] rows)
    {
        for (int row = 0; row < rows.Length; row++)
            for (int column = 0; column < rows[row].Length; column++)
                if (rows[row][column] == '#') Image[x + column, y + row] = color;
    }

    public void Glyph(int x, int y, char character, uint color)
    {
        if (Font.TryGetValue(char.ToLowerInvariant(character), out string[]? rows) || Font.TryGetValue(character, out rows))
            Icon(x, y, color, rows);
    }

    /// <summary>Writes <paramref name="text"/> in the skin font, five pixels to a character.</summary>
    public void Text(int x, int y, string text, uint color)
    {
        for (int index = 0; index < text.Length; index++) Glyph(x + index * Sprites.CharacterWidth, y, text[index], color);
    }

    public static int TextWidth(string text) => text.Length == 0 ? 0 : text.Length * Sprites.CharacterWidth - 1;

    // Aibell's own pixel font: capitals four pixels wide (M and W use five), drawn for this skin.
    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['a'] = [".##.", "#..#", "####", "#..#", "#..#"],
        ['b'] = ["###.", "#..#", "###.", "#..#", "###."],
        ['c'] = [".###", "#...", "#...", "#...", ".###"],
        ['d'] = ["###.", "#..#", "#..#", "#..#", "###."],
        ['e'] = ["####", "#...", "###.", "#...", "####"],
        ['f'] = ["####", "#...", "###.", "#...", "#..."],
        ['g'] = [".###", "#...", "#.##", "#..#", ".###"],
        ['h'] = ["#..#", "#..#", "####", "#..#", "#..#"],
        ['i'] = ["###", ".#.", ".#.", ".#.", "###"],
        ['j'] = ["..##", "...#", "...#", "#..#", ".##."],
        ['k'] = ["#..#", "#.#.", "##..", "#.#.", "#..#"],
        ['l'] = ["#...", "#...", "#...", "#...", "####"],
        ['m'] = ["#...#", "##.##", "#.#.#", "#...#", "#...#"],
        ['n'] = ["#..#", "##.#", "#.##", "#..#", "#..#"],
        ['o'] = [".##.", "#..#", "#..#", "#..#", ".##."],
        ['p'] = ["###.", "#..#", "###.", "#...", "#..."],
        ['q'] = [".##.", "#..#", "#..#", "#.#.", ".#.#"],
        ['r'] = ["###.", "#..#", "###.", "#.#.", "#..#"],
        ['s'] = [".###", "#...", ".##.", "...#", "###."],
        ['t'] = ["###", ".#.", ".#.", ".#.", ".#."],
        ['u'] = ["#..#", "#..#", "#..#", "#..#", ".##."],
        ['v'] = ["#..#", "#..#", "#..#", ".##.", ".##."],
        ['w'] = ["#...#", "#...#", "#.#.#", "##.##", "#...#"],
        ['x'] = ["#..#", "#..#", ".##.", "#..#", "#..#"],
        ['y'] = ["#.#", "#.#", ".#.", ".#.", ".#."],
        ['z'] = ["####", "...#", ".##.", "#...", "####"],
        ['"'] = ["#.#", "#.#"],
        ['@'] = [".##.", "#.##", "#.##", "#...", ".##."],
        ['0'] = ["###", "#.#", "#.#", "#.#", "###"],
        ['1'] = [".#.", "##.", ".#.", ".#.", "###"],
        ['2'] = ["###", "..#", "###", "#..", "###"],
        ['3'] = ["###", "..#", ".##", "..#", "###"],
        ['4'] = ["#.#", "#.#", "###", "..#", "..#"],
        ['5'] = ["###", "#..", "###", "..#", "###"],
        ['6'] = ["###", "#..", "###", "#.#", "###"],
        ['7'] = ["###", "..#", "..#", ".#.", ".#."],
        ['8'] = ["###", "#.#", "###", "#.#", "###"],
        ['9'] = ["###", "#.#", "###", "..#", "###"],
        ['…'] = [".....", ".....", ".....", ".....", "#.#.#"],
        ['.'] = [".", ".", ".", ".", "#"],
        [':'] = [".", "#", ".", "#", "."],
        ['('] = [".#", "#.", "#.", "#.", ".#"],
        [')'] = ["#.", ".#", ".#", ".#", "#."],
        ['-'] = ["...", "...", "###"],
        ['\''] = ["#", "#"],
        ['!'] = ["#", "#", "#", ".", "#"],
        ['_'] = ["....", "....", "....", "....", "####"],
        ['+'] = ["...", ".#.", "###", ".#."],
        ['\\'] = ["#..", "#..", ".#.", "..#", "..#"],
        ['/'] = ["..#", "..#", ".#.", "#..", "#.."],
        ['['] = ["##", "#.", "#.", "#.", "##"],
        [']'] = ["##", ".#", ".#", ".#", "##"],
        ['^'] = [".#.", "#.#"],
        ['&'] = [".#..", "#.#.", ".#..", "#.#.", ".#.#"],
        ['%'] = ["#..#", "...#", "..#.", ".#..", "#..#"],
        [','] = ["..", "..", "..", ".#", "#."],
        ['='] = ["...", "###", "...", "###"],
        ['$'] = [".###", "#.#.", ".##.", ".#.#", "###."],
        ['#'] = [".#.#", "####", ".#.#", "####", ".#.#"],
        ['Å'] = [".##.", ".##.", "#..#", "####", "#..#"],
        ['Ö'] = ["#..#", ".##.", "#..#", "#..#", ".##."],
        ['Ä'] = ["#..#", ".##.", "#..#", "####", "#..#"],
        ['?'] = ["###", "..#", ".##", "...", ".#."],
        ['*'] = ["...", "#.#", ".#.", "#.#"],
    };
}
