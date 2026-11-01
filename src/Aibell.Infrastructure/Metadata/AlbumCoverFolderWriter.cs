using Aibell.Application.Library;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Metadata;

/// <summary>
/// Saves an album cover as <c>cover.jpg</c> / <c>cover.png</c> beside the track, where <see cref="LocalArtworkCache"/> already looks.
/// The picture is written to a hidden temporary file and moved into place, so a crash never leaves half a cover, and an existing cover
/// of any recognised name stops the save instead of being overwritten.
/// </summary>
public sealed class AlbumCoverFolderWriter : IAlbumCoverWriter
{
    public Task<string> SaveAsync(Uri trackLocation, byte[] image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackLocation);
        ArgumentNullException.ThrowIfNull(image);
        if (!trackLocation.IsAbsoluteUri || !trackLocation.IsFile) throw new ArgumentException("Covers can only be saved next to files on this computer.", nameof(trackLocation));
        if (!ArtworkChange.IsSupportedImage(image) || image.Length > ArtworkChange.MaximumImageBytes) throw new ArgumentException("The cover must be a PNG or JPEG image.", nameof(image));
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = Path.GetDirectoryName(trackLocation.LocalPath) ?? throw new IOException("The track has no folder.");
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The album folder no longer exists: " + folder);
            foreach (string name in LocalArtworkCache.CoverNames)
                if (File.Exists(Path.Combine(folder, name)))
                    throw new IOException($"This folder already has {name}. Rename or delete it first; Aibell never replaces an existing cover.");
            string target = Path.Combine(folder, image[0] == 0x89 ? "cover.png" : "cover.jpg");
            string temporary = Path.Combine(folder, ".cover-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, image);
                File.Move(temporary, target, overwrite: false);
            }
            catch
            {
                try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Best effort. */ }
                throw;
            }
            return target;
        }, cancellationToken);
    }
}
