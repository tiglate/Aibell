using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace Aibell.Desktop.Services;

/// <summary>Loads small cover thumbnails (podcast episodes, audio CD albums).</summary>
public interface ICoverImageLoader
{
    /// <summary>Returns a decoded thumbnail, or null when the cover is unavailable. Never throws for bad images or network errors.</summary>
    Task<Bitmap?> LoadAsync(Uri url, CancellationToken cancellationToken = default);
}

/// <summary>
/// Downloads each cover at most once into an on-disk cache, bounded in size and time, and keeps decoded thumbnails in a small
/// memory cache. Failures are remembered for the session so a broken link is not retried on every scroll.
/// </summary>
public sealed class CoverImageCache : ICoverImageLoader, IDisposable
{
    public const long MaximumBytes = 5 * 1024 * 1024;
    private const int ThumbnailWidth = 128;
    private const int MemoryLimit = 256;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient http;
    private readonly string directory;
    private readonly SemaphoreSlim concurrency = new(4);
    private readonly object gate = new();
    private readonly Dictionary<string, Task<Bitmap?>> memory = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    public const long DefaultDiskLimitBytes = 256L * 1024 * 1024;
    private readonly long diskLimit;
    private int downloadsSincePrune = PruneEvery - 1;
    private const int PruneEvery = 50;

    public CoverImageCache(HttpClient http, string directory, long diskLimitBytes = DefaultDiskLimitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(diskLimitBytes);
        diskLimit = diskLimitBytes;
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public Task<Bitmap?> LoadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url is not { IsAbsoluteUri: true, Scheme: "http" or "https" }) return Task.FromResult<Bitmap?>(null);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri))).ToLowerInvariant();
        Task<Bitmap?> task;
        lock (gate)
        {
            if (!memory.TryGetValue(key, out task!))
            {
                // The shared load is deliberately not tied to one caller's token: another row may need the same cover.
                task = LoadCoreAsync(url, key);
                memory[key] = task;
                order.Enqueue(key);
                while (order.Count > MemoryLimit) memory.Remove(order.Dequeue());
            }
        }
        return task.WaitAsync(cancellationToken);
    }

    private async Task<Bitmap?> LoadCoreAsync(Uri url, string key)
    {
        try
        {
            await concurrency.WaitAsync().ConfigureAwait(false);
            try
            {
                string path = Path.Combine(directory, key + ".img");
                if (!File.Exists(path) && !await DownloadAsync(url, path).ConfigureAwait(false)) return null;
                try
                {
                    await using FileStream stream = File.OpenRead(path);
                    return await Task.Run(() => Bitmap.DecodeToWidth(stream, ThumbnailWidth)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Not a decodable image: drop it so a corrected cover can be fetched in a later session.
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    return null;
                }
            }
            finally { concurrency.Release(); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }

    private async Task<bool> DownloadAsync(Uri url, string destination)
    {
        using CancellationTokenSource timeout = new(Timeout);
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        string? type = response.Content.Headers.ContentType?.MediaType;
        if (type is null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("svg", StringComparison.OrdinalIgnoreCase)) return false;
        if (response.Content.Headers.ContentLength > MaximumBytes) return false;
        Directory.CreateDirectory(directory);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                byte[] buffer = new byte[16 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaximumBytes) return false;
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }
            File.Move(temporary, destination, overwrite: true);
            PruneIfDue(destination);
            return true;
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // Covers are only a cache: keep the newest files within the disk limit, checked on the first download of a session and
    // then every fifty downloads. A file in use or a race with another process is simply left for the next pass.
    private void PruneIfDue(string keep)
    {
        if (Interlocked.Increment(ref downloadsSincePrune) < PruneEvery) return;
        Interlocked.Exchange(ref downloadsSincePrune, 0);
        try
        {
            FileInfo[] files = new DirectoryInfo(directory).GetFiles("*.img");
            long total = files.Sum(file => file.Length);
            foreach (FileInfo file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (total <= diskLimit) break;
                if (string.Equals(file.FullName, keep, StringComparison.Ordinal)) continue;
                try { total -= file.Length; file.Delete(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => concurrency.Dispose();
}
