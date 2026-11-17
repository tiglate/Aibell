using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Aibell.Skins.Classic.Tests;

/// <summary>Builds BMP files and skin archives in memory, so no third-party skin art is needed.</summary>
internal static class SkinTestData
{
    /// <summary>A 24-bit bottom-up BMP filled by <paramref name="pixel"/> (x, y) => 0xRRGGBB.</summary>
    public static byte[] Bmp24(int width, int height, Func<int, int, uint> pixel)
    {
        int stride = (width * 3 + 3) / 4 * 4;
        byte[] data = new byte[54 + stride * height];
        Header(data, width, height, 24, 54, compression: 0, colorsUsed: 0);
        for (int row = 0; row < height; row++)
        {
            int y = height - 1 - row;
            for (int x = 0; x < width; x++)
            {
                uint color = pixel(x, y);
                int at = 54 + row * stride + x * 3;
                data[at] = (byte)color;
                data[at + 1] = (byte)(color >> 8);
                data[at + 2] = (byte)(color >> 16);
            }
        }
        return data;
    }

    /// <summary>An indexed BMP; <paramref name="pixels"/> holds packed rows bottom-up, exactly as stored.</summary>
    public static byte[] Indexed(int width, int height, int bitsPerPixel, uint[] palette, byte[] pixels, int compression = 0, bool topDown = false)
    {
        int offset = 54 + palette.Length * 4;
        byte[] data = new byte[offset + pixels.Length];
        Header(data, width, topDown ? -height : height, bitsPerPixel, offset, compression, palette.Length);
        for (int index = 0; index < palette.Length; index++)
        {
            data[54 + index * 4] = (byte)palette[index];
            data[54 + index * 4 + 1] = (byte)(palette[index] >> 8);
            data[54 + index * 4 + 2] = (byte)(palette[index] >> 16);
        }
        pixels.CopyTo(data, offset);
        return data;
    }

    public static void Header(byte[] data, int width, int height, int bitsPerPixel, int offset, int compression, int colorsUsed)
    {
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), offset);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(28), (short)bitsPerPixel);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(30), compression);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(46), colorsUsed);
    }

    public static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in files)
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }
        return stream.ToArray();
    }

    public static byte[] Text(string text) => Encoding.Latin1.GetBytes(text);

    /// <summary>A minimal classic skin: a red main window and nothing else.</summary>
    public static byte[] MinimalSkin(string folder = "") =>
        Zip(($"{folder}MAIN.BMP", Bmp24(275, 116, (_, _) => 0xFF0000)));
}
