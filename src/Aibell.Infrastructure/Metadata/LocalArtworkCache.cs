using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aibell.Application.Library;

namespace Aibell.Infrastructure.Metadata;

public sealed class LocalArtworkCache(string cacheDirectory) : IArtworkProvider, IDisposable
{
    private const int MaximumImageBytes = 16 * 1024 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    internal static readonly string[] CoverNames =
    [
        "cover.jpg", "cover.jpeg", "cover.png",
        "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.jpeg", "front.png"
    ];
    private readonly string cacheDirectory = Path.GetFullPath(cacheDirectory);
    private readonly SemaphoreSlim accessLock = new(1, 1);

    public async Task<Uri?> GetAsync(Uri mediaLocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaLocation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!mediaLocation.IsAbsoluteUri || !mediaLocation.IsFile || !File.Exists(mediaLocation.LocalPath))
        {
            return null;
        }

        await accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Read(mediaLocation, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            accessLock.Release();
        }
    }

    private Uri? Read(Uri location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] covers = FindCovers(Path.GetDirectoryName(location.LocalPath)!);
        string fingerprint = Fingerprint(location.LocalPath, covers);
        string indexPath = Path.Combine(cacheDirectory, Hash(Encoding.UTF8.GetBytes(location.AbsoluteUri)) + ".json");
        CacheEntry? entry = ReadIndex(indexPath);
        if (entry?.Fingerprint == fingerprint)
        {
            if (entry.ImageHash is null)
            {
                return null;
            }
            if (IsHash(entry.ImageHash))
            {
                string cachedPath = ImagePath(entry.ImageHash);
                if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length <= MaximumImageBytes &&
                    Hash(File.ReadAllBytes(cachedPath)) == entry.ImageHash)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new Uri(cachedPath);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // SPC and RSN files hold no pictures (and ATL cannot read RSN archives): only a cover file beside them counts.
        string extension = Path.GetExtension(location.LocalPath);
        byte[]? image = extension.Equals(".spc", StringComparison.OrdinalIgnoreCase) || extension.Equals(".rsn", StringComparison.OrdinalIgnoreCase)
            ? null
            : new ATL.Track(location.LocalPath).EmbeddedPictures
                .OrderBy(picture => picture.PicType == ATL.PictureInfo.PIC_TYPE.Front ? 0 : 1)
                .Select(picture => picture.PictureData)
                .FirstOrDefault(IsSupportedImage);
        cancellationToken.ThrowIfCancellationRequested();
        if (image is null)
        {
            foreach (string cover in covers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(cover).Length > MaximumImageBytes)
                {
                    continue;
                }
                byte[] candidate = File.ReadAllBytes(cover);
                if (IsSupportedImage(candidate))
                {
                    image = candidate;
                    break;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // A file changed while ATL was reading it: retry on the next request.
        if (fingerprint != Fingerprint(location.LocalPath, FindCovers(Path.GetDirectoryName(location.LocalPath)!)))
        {
            return null;
        }
        Directory.CreateDirectory(cacheDirectory);
        string? imageHash = image is null ? null : Hash(image);
        if (imageHash is not null)
        {
            string imagePath = ImagePath(imageHash);
            if (!File.Exists(imagePath) || new FileInfo(imagePath).Length > MaximumImageBytes ||
                Hash(File.ReadAllBytes(imagePath)) != imageHash)
                WriteAtomically(imagePath, image!, cancellationToken);
        }
        WriteAtomically(indexPath, JsonSerializer.SerializeToUtf8Bytes(new CacheEntry(fingerprint, imageHash)), cancellationToken);
        return imageHash is null ? null : new Uri(ImagePath(imageHash));
    }

    private static string[] FindCovers(string directory)
    {
        string[] files = Directory.GetFiles(directory);
        return CoverNames.SelectMany(name => files
            .Where(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)).ToArray();
    }

    private static string Fingerprint(string mediaPath, IEnumerable<string> covers)
    {
        StringBuilder fingerprint = new();
        foreach (string path in new[] { mediaPath }.Concat(covers))
        {
            FileInfo file = new(path);
            fingerprint.Append(path).Append('\0').Append(file.Length).Append('\0')
                .Append(file.LastWriteTimeUtc.Ticks).Append('\0');
        }
        return Hash(Encoding.UTF8.GetBytes(fingerprint.ToString()));
    }

    private static CacheEntry? ReadIndex(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<CacheEntry>(File.ReadAllBytes(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSupportedImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length is < 8 or > MaximumImageBytes)
        {
            return false;
        }
        return bytes.AsSpan(0, 8).SequenceEqual(PngSignature) ||
            (bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff);
    }

    private static void WriteAtomically(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(temporaryPath, bytes);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsHash(string hash) => hash.Length == 64 && hash.All(char.IsAsciiHexDigit);
    private string ImagePath(string hash) => Path.Combine(cacheDirectory, hash + ".image");
    public void Dispose() => accessLock.Dispose();
    private sealed record CacheEntry(string Fingerprint, string? ImageHash);
}
