namespace Aibell.Application.Library;

/// <summary>Fetches the full cover image a lookup found. Implementations accept https only and bound size and time.</summary>
public interface ICoverArtDownloader
{
    /// <summary>Returns a validated PNG or JPEG. Throws <see cref="IOException"/> or <see cref="InvalidDataException"/> with a message fit for the user.</summary>
    Task<byte[]> DownloadAsync(Uri source, CancellationToken cancellationToken = default);
}

/// <summary>Stores a cover for an album as a picture file next to its tracks.</summary>
public interface IAlbumCoverWriter
{
    /// <summary>
    /// Writes <c>cover.jpg</c> or <c>cover.png</c> into the folder of <paramref name="trackLocation"/> and returns its path. An existing
    /// cover file is never replaced; the call fails with an <see cref="IOException"/> that names it.
    /// </summary>
    Task<string> SaveAsync(Uri trackLocation, byte[] image, CancellationToken cancellationToken = default);
}
