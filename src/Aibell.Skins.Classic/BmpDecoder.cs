using System.Buffers.Binary;
using System.Numerics;

namespace Aibell.Skins.Classic;

/// <summary>
/// Decodes the Windows bitmaps classic skins are made of: 1, 4, 8, 16, 24 and 32 bits per pixel, bottom-up or top-down rows,
/// RLE4/RLE8 compression and bit-field masks, with the OS/2 core header too. Skins are often saved by lenient tools, so sizes
/// and offsets that disagree with the data are tolerated and a short file decodes as far as it goes (the rest stays black).
/// </summary>
public static class BmpDecoder
{
    private const int FileHeaderSize = 14;

    public static SkinImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileHeaderSize + 12 || data[0] != (byte)'B' || data[1] != (byte)'M')
            throw new InvalidDataException("Not a BMP image.");
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
        bool core = headerSize == 12;
        if (!core && (headerSize < 40 || data.Length < FileHeaderSize + 40)) throw new InvalidDataException("Unsupported BMP header.");
        int width, height, bitsPerPixel, compression = 0, colorsUsed = 0;
        if (core)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
            height = BinaryPrimitives.ReadInt16LittleEndian(data[20..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
        }
        else
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]);
            compression = BinaryPrimitives.ReadInt32LittleEndian(data[30..]);
            colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(data[46..]);
        }
        bool topDown = height < 0;
        if (height == int.MinValue) throw new InvalidDataException("Invalid BMP size.");
        height = Math.Abs(height);
        if (width is < 1 or > SkinImage.MaximumSide || height is < 1 or > SkinImage.MaximumSide)
            throw new InvalidDataException("Invalid BMP size.");
        if (bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32)) throw new InvalidDataException("Unsupported BMP bit depth.");

        int position = FileHeaderSize + headerSize;
        uint redMask = 0, greenMask = 0, blueMask = 0;
        if (compression is 3 or 6)
        {
            if (bitsPerPixel is not (16 or 32)) throw new InvalidDataException("Bit-field masks need 16 or 32 bits per pixel.");
            // Version 4 and later headers carry the masks; the plain 40-byte header is followed by them.
            int masks = headerSize >= 52 ? FileHeaderSize + 40 : position;
            if (data.Length < masks + 12) throw new InvalidDataException("Truncated BMP masks.");
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(data[masks..]);
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(data[(masks + 4)..]);
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(data[(masks + 8)..]);
            if (headerSize < 52) position += compression == 6 ? 16 : 12;
        }
        else if (bitsPerPixel == 16)
        {
            (redMask, greenMask, blueMask) = (0x7C00, 0x03E0, 0x001F);
        }
        else if (compression is not (0 or 1 or 2))
        {
            throw new InvalidDataException("Unsupported BMP compression.");
        }
        if ((compression == 1 && bitsPerPixel != 8) || (compression == 2 && bitsPerPixel != 4))
            throw new InvalidDataException("Mismatched BMP compression.");

        uint[] palette = [];
        if (bitsPerPixel <= 8)
        {
            int count = colorsUsed > 0 && colorsUsed <= 1 << bitsPerPixel ? colorsUsed : 1 << bitsPerPixel;
            int entrySize = core ? 3 : 4;
            palette = new uint[1 << bitsPerPixel];
            for (int index = 0; index < count; index++)
            {
                int at = position + index * entrySize;
                if (at + 3 > data.Length) break;
                palette[index] = Opaque(data[at + 2], data[at + 1], data[at]);
            }
            for (int index = count; index < palette.Length; index++) palette[index] = Opaque(0, 0, 0);
            position += count * entrySize;
        }

        // The offset field is trusted only when it points inside the file and past the headers; some skin tools write junk there.
        int offset = BinaryPrimitives.ReadInt32LittleEndian(data[10..]);
        if (offset >= position && offset < data.Length) position = offset;

        SkinImage image = new(width, height);
        Array.Fill(image.Pixels, Opaque(0, 0, 0));
        ReadOnlySpan<byte> pixels = position < data.Length ? data[position..] : [];
        if (compression is 1 or 2) DecodeRunLength(pixels, image, bitsPerPixel, palette, topDown);
        else DecodeRows(pixels, image, bitsPerPixel, palette, topDown, redMask, greenMask, blueMask);
        return image;
    }

    private static void DecodeRows(ReadOnlySpan<byte> pixels, SkinImage image, int bitsPerPixel, uint[] palette, bool topDown,
        uint redMask, uint greenMask, uint blueMask)
    {
        int stride = (image.Width * bitsPerPixel + 31) / 32 * 4;
        for (int row = 0; row < image.Height; row++)
        {
            int start = row * stride;
            if (start >= pixels.Length) break;
            ReadOnlySpan<byte> line = pixels.Slice(start, Math.Min(stride, pixels.Length - start));
            int y = topDown ? row : image.Height - 1 - row;
            for (int x = 0; x < image.Width; x++)
            {
                uint? color = bitsPerPixel switch
                {
                    1 or 4 or 8 => Indexed(line, x, bitsPerPixel, palette),
                    16 => x * 2 + 2 <= line.Length ? Masked(BinaryPrimitives.ReadUInt16LittleEndian(line[(x * 2)..]), redMask, greenMask, blueMask) : null,
                    24 => x * 3 + 3 <= line.Length ? Opaque(line[x * 3 + 2], line[x * 3 + 1], line[x * 3]) : null,
                    _ => x * 4 + 4 <= line.Length ? (redMask | greenMask | blueMask) != 0
                        ? Masked(BinaryPrimitives.ReadUInt32LittleEndian(line[(x * 4)..]), redMask, greenMask, blueMask)
                        : Opaque(line[x * 4 + 2], line[x * 4 + 1], line[x * 4]) : null,
                };
                if (color is not { } value) break;
                image.Pixels[y * image.Width + x] = value;
            }
        }
    }

    private static uint? Indexed(ReadOnlySpan<byte> line, int x, int bitsPerPixel, uint[] palette)
    {
        int bit = x * bitsPerPixel;
        if (bit / 8 >= line.Length) return null;
        int shift = 8 - bitsPerPixel - bit % 8;
        int index = (line[bit / 8] >> shift) & ((1 << bitsPerPixel) - 1);
        return palette[index];
    }

    private static void DecodeRunLength(ReadOnlySpan<byte> pixels, SkinImage image, int bitsPerPixel, uint[] palette, bool topDown)
    {
        int x = 0, row = 0, at = 0;
        void Put(int index)
        {
            if (x < image.Width && row < image.Height)
                image.Pixels[(topDown ? row : image.Height - 1 - row) * image.Width + x] = palette[index];
            x++;
        }
        while (at + 1 < pixels.Length && row < image.Height)
        {
            int count = pixels[at], value = pixels[at + 1];
            at += 2;
            if (count > 0)
            {
                for (int index = 0; index < count; index++)
                    Put(bitsPerPixel == 8 ? value : index % 2 == 0 ? value >> 4 : value & 0x0F);
                continue;
            }
            switch (value)
            {
                case 0:
                    x = 0;
                    row++;
                    break;
                case 1:
                    return;
                case 2:
                    if (at + 1 >= pixels.Length) return;
                    x += pixels[at];
                    row += pixels[at + 1];
                    at += 2;
                    break;
                default:
                    int bytes = bitsPerPixel == 8 ? value : (value + 1) / 2;
                    for (int index = 0; index < value && at + (bitsPerPixel == 8 ? index : index / 2) < pixels.Length; index++)
                    {
                        byte packed = pixels[at + (bitsPerPixel == 8 ? index : index / 2)];
                        Put(bitsPerPixel == 8 ? packed : index % 2 == 0 ? packed >> 4 : packed & 0x0F);
                    }
                    at += bytes + (bytes & 1);
                    break;
            }
        }
    }

    private static uint Masked(uint value, uint redMask, uint greenMask, uint blueMask) =>
        Opaque(Channel(value, redMask), Channel(value, greenMask), Channel(value, blueMask));

    private static byte Channel(uint value, uint mask)
    {
        if (mask == 0) return 0;
        int shift = BitOperations.TrailingZeroCount(mask);
        uint maximum = mask >> shift;
        return (byte)(((value & mask) >> shift) * 255 / maximum);
    }

    internal static uint Opaque(int red, int green, int blue) => 0xFF000000u | (uint)red << 16 | (uint)green << 8 | (uint)blue;
}
