using Aibell.Infrastructure.Podcasts;

namespace Aibell.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PodcastDownloadStore? podcastDownloads;
    private string podcastDefaultRoot = "";
    // Committed folder choice. Only a successful settings write replaces it, and every write reads it when it actually runs.
    private volatile PodcastFolderState podcastFolderState = new(null, []);

    private sealed record PodcastFolderState(string? Folder, IReadOnlyList<string> History);

    /// <summary>Called once by the composition root with the store that new podcast downloads use.</summary>
    public void ConfigurePodcastDownloads(PodcastDownloadStore store, string defaultRoot)
    {
        ArgumentNullException.ThrowIfNull(store);
        podcastDownloads = store;
        podcastDefaultRoot = Path.GetFullPath(defaultRoot);
    }

    public bool CanChoosePodcastFolder => podcastDownloads is not null;
    public string PodcastDownloadRoot => podcastDownloads?.Root ?? "";
    public string PodcastDefaultRoot => podcastDefaultRoot;

    /// <summary>
    /// Validates and probes a requested folder off the UI thread. Returns null when no change was requested or the folder is already
    /// active. The previously active root joins the retained history so earlier downloads stay playable and deletable.
    /// </summary>
    private async Task<PodcastDownloadConfiguration?> PreparePodcastFolderAsync(string? requested, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        if (podcastDownloads is not { } store) throw new InvalidOperationException("Podcast downloads are unavailable.");
        if (!preferencesLoaded) throw new InvalidOperationException("Preferences are still loading. Try again in a moment.");
        string target = requested.Trim();
        if (Path.IsPathFullyQualified(target) && SamePath(Path.GetFullPath(target), store.Root)) return null;
        string[] history = [store.Root, .. podcastFolderState.History];
        PodcastDownloadConfiguration prepared = await Task.Run(() => PodcastDownloadStore.PrepareConfiguration(target, history), CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return prepared;
    }

    private PodcastFolderState FolderStateFor(PodcastDownloadConfiguration prepared) => new(
        SamePath(prepared.Root, podcastDefaultRoot) ? null : prepared.Root,
        prepared.AllowedRoots.Where(root => !SamePath(root, prepared.Root)).ToArray());

    /// <summary>
    /// The user's folder change as one strict transaction on the shared settings queue: it waits for earlier writes, saves the
    /// proposed folder, and only then commits it and swaps the live store. A failed write propagates and leaves the committed
    /// folder, the live store and the file as they were. The proposed folder is never visible to other writes.
    /// </summary>
    private Task PersistPodcastFolderAsync(PodcastDownloadConfiguration prepared, CancellationToken cancellationToken)
    {
        Task transaction = SavePodcastFolderAsync(pendingPreferencesSave, prepared, cancellationToken);
        pendingPreferencesSave = ObserveQuietly(transaction);
        return transaction;
    }

    private async Task SavePodcastFolderAsync(Task previousSave, PodcastDownloadConfiguration prepared, CancellationToken cancellationToken)
    {
        try { await previousSave; }
        catch { /* Earlier writes already reported their own outcome. */ }
        cancellationToken.ThrowIfCancellationRequested();
        PodcastFolderState proposed = FolderStateFor(prepared);
        await settingsStore.SaveAsync(BuildPreferences(proposed), cancellationToken);
        podcastFolderState = proposed;
        podcastDownloads!.ApplyConfiguration(prepared);
    }

    private static async Task ObserveQuietly(Task task)
    {
        try { await task; }
        catch { /* The caller of the transaction receives the failure. */ }
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
