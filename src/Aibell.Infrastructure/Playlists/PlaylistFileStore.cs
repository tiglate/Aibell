using System.Globalization;
using System.Text;
using Aibell.Application.Playlists;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Playlists;

public sealed class PlaylistFileStore : IPlaylistFileStore
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumEntries = 10000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<PlaylistFileContent> ReadAsync(Uri source, CancellationToken cancellationToken = default)
    {
        string path = LocalPath(source);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".m3u" or ".m3u8" or ".pls")) throw new NotSupportedException("Choose an M3U, M3U8 or PLS playlist.");
        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length > MaximumBytes) throw new InvalidDataException("The playlist exceeds the 4 MiB limit.");
        // Read through a bounded buffer so a concurrently growing file cannot bypass the limit.
        byte[] buffer = new byte[MaximumBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int count = await input.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > MaximumBytes) throw new InvalidDataException("The playlist exceeds the 4 MiB limit.");
        using MemoryStream bytes = new(buffer, 0, length, writable: false);
        using StreamReader reader = new(bytes, StrictUtf8, detectEncodingFromByteOrderMarks: true);
        string text;
        try { text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("The playlist must use UTF-8 or a recognized Unicode byte-order mark.", exception); }
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        List<(int Entry, string Value)> entries = [];
        if (extension == ".pls") ParsePls(lines, entries);
        else
        {
            foreach (string raw in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = raw.Trim();
                if (line.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("HLS stream manifests cannot be imported as local playlists.");
                if (line.Length == 0 || line[0] == '#') continue;
                entries.Add((entries.Count + 1, line));
                if (entries.Count > MaximumEntries) throw new InvalidDataException("A playlist can contain at most 10,000 entries.");
            }
        }
        List<PlaylistFileEntry> locations = [];
        List<PlaylistFileIssue> issues = [];
        foreach ((int entry, string value) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Uri location = Resolve(path, value);
                if (Path.GetExtension(location.LocalPath).Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(location.LocalPath).Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(location.LocalPath).Equals(".pls", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Nested playlists are not imported.");
                locations.Add(new(entry, location));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or UriFormatException)
            { issues.Add(new(entry, value.Length > 16384 ? value[..256] + "…" : value, exception.Message)); }
        }
        return new(Path.GetFileNameWithoutExtension(path), locations, issues);
    }

    private static void ParsePls(string[] lines, List<(int Entry, string Value)> entries)
    {
        bool header = false;
        SortedDictionary<int, string> ordered = [];
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (!header)
            {
                if (!line.Equals("[playlist]", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The PLS playlist header is missing.");
                header = true; continue;
            }
            int equals = line.IndexOf('=');
            if (equals < 0) continue;
            string key = line[..equals].Trim();
            if (!key.StartsWith("File", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(key.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index is < 1 or > MaximumEntries)
                throw new InvalidDataException("A PLS file index is outside 1–10,000.");
            if (!ordered.TryAdd(index, line[(equals + 1)..].Trim())) throw new InvalidDataException("A PLS file index is duplicated.");
        }
        if (!header) throw new InvalidDataException("The PLS playlist header is missing.");
        entries.AddRange(ordered.Select(pair => (pair.Key, pair.Value)));
    }

    private static Uri Resolve(string playlist, string entry)
    {
        if (entry.Length > 16384) throw new ArgumentException("The entry exceeds the location length limit.");
        if (entry.Length == 0 || entry.Contains('\0')) throw new ArgumentException("The entry has no valid file location.");
        if (entry.StartsWith("\\\\", StringComparison.Ordinal) || entry.StartsWith("//", StringComparison.Ordinal))
            throw new NotSupportedException("Network file paths are not imported.");
        if (entry.Length >= 2 && char.IsAsciiLetter(entry[0]) && entry[1] == ':' &&
            (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(entry)))
            throw new NotSupportedException("This file path belongs to a different operating system or is drive-relative.");
        Uri location;
        if (!entry.Contains("://", StringComparison.Ordinal) && !entry.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            !Path.IsPathFullyQualified(entry))
        {
            string candidate = Path.Combine(Path.GetDirectoryName(playlist)!, entry);
            if (!OperatingSystem.IsWindows() && !File.Exists(candidate))
                candidate = Path.Combine(Path.GetDirectoryName(playlist)!, entry.Replace('\\', '/'));
            location = new Uri(Path.GetFullPath(candidate));
        }
        else if (Uri.TryCreate(entry, UriKind.Absolute, out Uri? absolute)) location = absolute;
        else location = new Uri(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(playlist)!, entry)));
        if (!location.IsFile || location.IsUnc || !string.IsNullOrEmpty(location.Host) && location.Host != "localhost")
            throw new NotSupportedException("Only local audio files are imported; stream and network locations are skipped.");
        if (location.Query.Length > 0 || location.Fragment.Length > 0) throw new ArgumentException("File locations cannot contain URI queries or fragments.");
        if (!Path.IsPathFullyQualified(location.LocalPath)) throw new NotSupportedException("This file path belongs to a different operating system.");
        return location;
    }

    public async Task WriteAsync(Uri destination, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        string path = LocalPath(destination);
        ArgumentNullException.ThrowIfNull(tracks);
        if (!Path.GetExtension(path).Equals(".m3u8", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Export requires the .m3u8 extension.");
        if (tracks.Count > MaximumEntries) throw new ArgumentException("A playlist can contain at most 10,000 entries.", nameof(tracks));
        StringBuilder text = new("#EXTM3U\n");
        foreach (Track track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!track.Location.IsAbsoluteUri || !track.Location.IsFile || track.Location.IsUnc) throw new NotSupportedException("Only local audio files can be exported.");
            if (track.Location.Query.Length > 0 || track.Location.Fragment.Length > 0 || track.Location.AbsoluteUri.Length > 16384)
                throw new NotSupportedException("A track location cannot be represented in the supported playlist profile.");
            string title = (track.Artist + " - " + track.Title).Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
            long seconds = (long)Math.Round(track.Duration.TotalSeconds);
            if (seconds <= 0) seconds = -1;
            text.Append("#EXTINF:").Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(',').Append(title).Append('\n');
            text.Append(track.Location.AbsoluteUri).Append('\n');
            if (text.Length > MaximumBytes) throw new InvalidDataException("The exported playlist exceeds the size limit.");
        }
        byte[] bytes = StrictUtf8.GetBytes(text.ToString());
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The exported playlist exceeds the size limit.");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".aibell-playlist-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (FileStream output = new(temporary, options))
            {
                created = true;
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string LocalPath(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsAbsoluteUri || !location.IsFile || location.IsUnc) throw new ArgumentException("Choose a local playlist file.", nameof(location));
        return Path.GetFullPath(location.LocalPath);
    }
}
