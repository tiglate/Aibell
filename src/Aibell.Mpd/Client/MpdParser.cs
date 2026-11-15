using System.Globalization;

namespace Aibell.Mpd.Client;

/// <summary>Turns the <c>key: value</c> lines of MPD answers into typed values.</summary>
public static class MpdParser
{
    private static readonly string[] NonTagKeys =
        ["file", "directory", "playlist", "Last-Modified", "Format", "duration", "Time", "Pos", "Id", "Range", "Prio"];

    public static MpdStatus ParseStatus(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        Dictionary<string, string> map = ToMap(pairs);
        int? volume = Integer(map, "volume");
        return new MpdStatus(
            volume is >= 0 ? volume : null,
            Flag(map, "repeat"), Flag(map, "random"),
            map.GetValueOrDefault("single") switch { "1" => MpdSingleMode.On, "oneshot" => MpdSingleMode.OneShot, _ => MpdSingleMode.Off },
            Flag(map, "consume"),
            Integer(map, "playlist") ?? 0, Integer(map, "playlistlength") ?? 0,
            map.GetValueOrDefault("state") switch { "play" => MpdState.Playing, "pause" => MpdState.Paused, _ => MpdState.Stopped },
            Integer(map, "song"), Integer(map, "songid"),
            Seconds(map, "elapsed"), Seconds(map, "duration"), Integer(map, "bitrate"), Seconds(map, "xfade"),
            map.GetValueOrDefault("audio"), map.GetValueOrDefault("error"), map.GetValueOrDefault("partition"),
            map.ContainsKey("updating_db"));
    }

    public static MpdStats ParseStats(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        Dictionary<string, string> map = ToMap(pairs);
        long? update = Long(map, "db_update");
        return new MpdStats(
            Integer(map, "artists") ?? 0, Integer(map, "albums") ?? 0, Integer(map, "songs") ?? 0,
            Seconds(map, "uptime") ?? TimeSpan.Zero, Seconds(map, "db_playtime") ?? TimeSpan.Zero,
            update is { } stamp ? DateTimeOffset.FromUnixTimeSeconds(stamp) : null, Seconds(map, "playtime") ?? TimeSpan.Zero);
    }

    /// <summary>Reads a list of songs; each starts at its <c>file</c> line.</summary>
    public static IReadOnlyList<MpdSong> ParseSongs(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        List<MpdSong> songs = [];
        foreach (IReadOnlyList<KeyValuePair<string, string>> group in Split(pairs, "file")) songs.Add(BuildSong(group));
        return songs;
    }

    /// <summary>Reads the items of a folder: directories, songs and playlist files, in the order MPD sent them.</summary>
    public static IReadOnlyList<MpdEntry> ParseEntries(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        List<MpdEntry> entries = [];
        foreach (IReadOnlyList<KeyValuePair<string, string>> group in Split(pairs, "file", "directory", "playlist"))
        {
            KeyValuePair<string, string> head = group[0];
            DateTimeOffset? modified = Date(group.FirstOrDefault(pair => pair.Key == "Last-Modified").Value);
            entries.Add(head.Key switch
            {
                "file" => new MpdEntry(MpdEntryKind.Song, head.Value, BuildSong(group), modified),
                "directory" => new MpdEntry(MpdEntryKind.Directory, head.Value, null, modified),
                _ => new MpdEntry(MpdEntryKind.Playlist, head.Value, null, modified),
            });
        }
        return entries;
    }

    public static IReadOnlyList<MpdStoredPlaylist> ParsePlaylists(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return [.. Split(pairs, "playlist").Select(group => new MpdStoredPlaylist(group[0].Value, Date(group.FirstOrDefault(pair => pair.Key == "Last-Modified").Value)))];
    }

    public static IReadOnlyList<MpdOutput> ParseOutputs(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        List<MpdOutput> outputs = [];
        foreach (IReadOnlyList<KeyValuePair<string, string>> group in Split(pairs, "outputid"))
        {
            Dictionary<string, string> map = ToMap(group);
            outputs.Add(new MpdOutput(Integer(map, "outputid") ?? 0, map.GetValueOrDefault("outputname") ?? string.Empty, Flag(map, "outputenabled"), map.GetValueOrDefault("plugin")));
        }
        return outputs;
    }

    private static MpdSong BuildSong(IReadOnlyList<KeyValuePair<string, string>> group)
    {
        Dictionary<string, List<string>> tags = new(StringComparer.OrdinalIgnoreCase);
        TimeSpan? duration = null;
        TimeSpan? whole = null;
        int? position = null;
        int? id = null;
        DateTimeOffset? modified = null;
        foreach ((string key, string value) in group.Skip(1))
        {
            switch (key)
            {
                case "duration": duration = ParseSeconds(value); break;
                case "Time": whole = ParseSeconds(value); break;
                case "Pos": position = ParseInt(value); break;
                case "Id": id = ParseInt(value); break;
                case "Last-Modified": modified = Date(value); break;
                default:
                    if (NonTagKeys.Contains(key, StringComparer.Ordinal)) break;
                    if (!tags.TryGetValue(key, out List<string>? values)) tags[key] = values = [];
                    values.Add(value);
                    break;
            }
        }
        return new MpdSong(group[0].Value, tags.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase),
            duration ?? whole, position, id, modified);
    }

    private static IEnumerable<IReadOnlyList<KeyValuePair<string, string>>> Split(IReadOnlyList<KeyValuePair<string, string>> pairs, params string[] starts)
    {
        List<KeyValuePair<string, string>>? current = null;
        foreach (KeyValuePair<string, string> pair in pairs)
        {
            if (starts.Contains(pair.Key, StringComparer.Ordinal))
            {
                if (current is not null) yield return current;
                current = [];
            }
            current?.Add(pair);
        }
        if (current is not null) yield return current;
    }

    private static Dictionary<string, string> ToMap(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach ((string key, string value) in pairs) map[key] = value;
        return map;
    }

    private static int? ParseInt(string? text) => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static TimeSpan? ParseSeconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;

    private static int? Integer(Dictionary<string, string> map, string key) => map.TryGetValue(key, out string? text) ? ParseInt(text) : null;

    private static long? Long(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out string? text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

    private static TimeSpan? Seconds(Dictionary<string, string> map, string key) => map.TryGetValue(key, out string? text) ? ParseSeconds(text) : null;

    private static bool Flag(Dictionary<string, string> map, string key) => map.GetValueOrDefault(key) == "1";

    private static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value) ? value : null;
}
