using System.Globalization;
using System.Text;
using Aibell.Application.Radio;
using Aibell.Domain.Radio;

namespace Aibell.Infrastructure.Radio;

public sealed class RadioPlaylistFileStore : IRadioPlaylistFileStore
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumEntries = 10000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<RadioPlaylistContent> ReadAsync(Uri source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.IsAbsoluteUri || !source.IsFile || source.IsUnc || source.Query.Length > 0 || source.Fragment.Length > 0 ||
            source.Host.Length > 0 && source.Host != "localhost")
            throw new ArgumentException("Choose a local station playlist file.", nameof(source));
        string path = Path.GetFullPath(source.LocalPath);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".m3u" or ".m3u8" or ".pls"))
            throw new NotSupportedException("Choose an M3U, M3U8 or PLS station playlist.");

        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length > MaximumBytes) throw new InvalidDataException("The station playlist exceeds the 4 MiB limit.");
        byte[] buffer = new byte[MaximumBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int count = await input.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > MaximumBytes) throw new InvalidDataException("The station playlist exceeds the 4 MiB limit.");
        using MemoryStream bytes = new(buffer, 0, length, writable: false);
        using StreamReader reader = new(bytes, StrictUtf8, detectEncodingFromByteOrderMarks: true);
        string text;
        try { text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false); }
        catch (DecoderFallbackException exception)
        { throw new InvalidDataException("The station playlist must use UTF-8 or a recognized Unicode byte-order mark.", exception); }
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        List<(int Entry, string Stream, string? Name)> entries = extension == ".pls"
            ? ReadPls(lines, cancellationToken) : ReadM3u(lines, cancellationToken);

        List<RadioStation> stations = [];
        List<RadioPlaylistIssue> issues = [];
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Uri.TryCreate(entry.Stream, UriKind.Absolute, out Uri? stream))
                    throw new ArgumentException("Use a complete HTTP or HTTPS direct-stream URL.");
                string name = string.IsNullOrWhiteSpace(entry.Name) ? stream.Host : entry.Name.Trim();
                RadioStation station = new(Guid.NewGuid(), name, stream);
                station.Validate();
                stations.Add(station);
            }
            catch (ArgumentException exception) { issues.Add(new(entry.Entry, exception.Message)); }
        }
        return new(stations, issues);
    }

    private static List<(int Entry, string Stream, string? Name)> ReadM3u(string[] lines, CancellationToken cancellationToken)
    {
        List<(int Entry, string Stream, string? Name)> entries = [];
        string? name = null;
        foreach (string raw in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = raw.Trim();
            if (line.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("This file is an HLS manifest, not a station list. Choose an M3U or PLS list of stations; an HLS stream is added by listing its .m3u8 address as a station.");
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = line.IndexOf(',');
                name = comma < 0 ? null : line[(comma + 1)..].Trim();
                continue;
            }
            if (line.Length == 0 || line[0] == '#') continue;
            entries.Add((entries.Count + 1, line, name));
            name = null;
            if (entries.Count > MaximumEntries) throw new InvalidDataException("A station playlist can contain at most 10,000 entries.");
        }
        return entries;
    }

    private static List<(int Entry, string Stream, string? Name)> ReadPls(string[] lines, CancellationToken cancellationToken)
    {
        bool header = false;
        SortedDictionary<int, string> streams = [];
        Dictionary<int, string> names = [];
        foreach (string raw in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (!header)
            {
                if (!line.Equals("[playlist]", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The PLS playlist header is missing.");
                header = true;
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals < 0) continue;
            string key = line[..equals].Trim();
            bool isStream = key.StartsWith("File", StringComparison.OrdinalIgnoreCase);
            bool isName = key.StartsWith("Title", StringComparison.OrdinalIgnoreCase);
            if (!isStream && !isName) continue;
            if (!int.TryParse(key.AsSpan(isStream ? 4 : 5), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index is < 1 or > MaximumEntries)
                throw new InvalidDataException("A PLS entry index is outside 1–10,000.");
            string value = line[(equals + 1)..].Trim();
            bool added = isStream ? streams.TryAdd(index, value) : names.TryAdd(index, value);
            if (!added) throw new InvalidDataException("A PLS entry index is duplicated.");
        }
        if (!header) throw new InvalidDataException("The PLS playlist header is missing.");
        return streams.Select(pair => (pair.Key, pair.Value, names.GetValueOrDefault(pair.Key))).ToList();
    }
}
