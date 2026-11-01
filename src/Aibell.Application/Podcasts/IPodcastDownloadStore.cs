namespace Aibell.Application.Podcasts;

/// <summary>
/// Physical storage for downloaded episodes. The adapter owns the selected download folder and previously selected roots, and never follows links.
/// Optional safe deletion is exposed separately through IPodcastDownloadDeletionStore.
/// </summary>
public interface IPodcastDownloadStore
{
    /// <summary>
    /// Creates (without following links) the subscription folder and returns the exclusive destination for a new file.
    /// Throws <see cref="IOException"/> when the folder is unsafe or a file already exists at the destination.
    /// </summary>
    Uri PrepareDestination(Guid subscriptionId, string fileName);

    /// <summary>Returns the file when the path is an existing regular file inside the root with no link in any ancestor; otherwise null.</summary>
    Uri? ResolveExisting(string? path);
}
