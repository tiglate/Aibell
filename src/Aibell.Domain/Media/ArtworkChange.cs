namespace Aibell.Domain.Media;

// A null image removes the selected picture. Without a target, a new front cover is added.
public sealed record ArtworkChange(int? PictureIndex = null, string? OriginalHash = null, byte[]? ImageData = null)
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    public const int MaximumImageBytes = 16 * 1024 * 1024;
    public void Validate()
    {
        if (PictureIndex is < 0 || (PictureIndex is null) != (OriginalHash is null) ||
            OriginalHash is not null && (OriginalHash.Length != 64 || !OriginalHash.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("The artwork selection is invalid.");
        if (ImageData is null && PictureIndex is null) throw new ArgumentException("Choose an image or an embedded picture to remove.");
        if (ImageData is { } bytes && (bytes.Length > MaximumImageBytes || !IsSupportedImage(bytes)))
            throw new ArgumentException("Choose a PNG or JPEG image up to 16 MiB.");
    }

    public static bool IsSupportedImage(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 24 && bytes[..8].SequenceEqual(PngSignature) ||
        bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 && bytes[^2] == 255 && bytes[^1] == 217;
}
