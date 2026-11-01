using Aibell.Archive.Rar;

namespace Aibell.Audio.Spc;

/// <summary>
/// RSN files: RAR archives that hold a game's SPC files. Each SPC inside is a track, addressed by the archive's file URI with a
/// query naming the entry (<c>file:///music/game.rsn?entry=song_01.spc</c>). A query is used because <see cref="Uri"/> equality ignores
/// fragments but not queries, so the tracks of one archive stay distinct, while <see cref="Uri.LocalPath"/> still names the archive file.
/// </summary>
public static class RsnArchive
{
    public const string Extension = ".rsn";

    private const string EntryKey = "entry=";

    /// <summary>True for a path with the <c>.rsn</c> extension.</summary>
    public static bool IsRsnPath(string path) => Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The location of one track inside an archive.</summary>
    public static Uri CreateLocation(string archivePath, string entryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        return new Uri(new Uri(Path.GetFullPath(archivePath)).AbsoluteUri + "?" + EntryKey + Uri.EscapeDataString(entryName));
    }

    /// <summary>Splits a track location into the archive path and the entry name (empty for a bare archive location).</summary>
    public static bool TryParseLocation(Uri location, out string archivePath, out string entryName)
    {
        archivePath = entryName = "";
        if (!location.IsAbsoluteUri || !location.IsFile || !IsRsnPath(location.LocalPath)) return false;
        archivePath = location.LocalPath;
        string query = location.Query;
        if (query.StartsWith('?') && query[1..].StartsWith(EntryKey, StringComparison.Ordinal)) entryName = Uri.UnescapeDataString(query[(1 + EntryKey.Length)..]);
        return true;
    }

    /// <summary>The names of the SPC files in an archive, in archive order.</summary>
    public static IReadOnlyList<string> ListTracks(Stream archive) => ListTracks(RarArchive.Open(archive));

    private static List<string> ListTracks(RarArchive rar) =>
        [.. rar.Entries.Where(IsTrack).Select(entry => entry.Name)];

    private static bool IsTrack(RarEntry entry) => !entry.IsDirectory && entry.Name.EndsWith(".spc", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bytes of one SPC file. An empty <paramref name="entryName"/> takes the first track.</summary>
    public static byte[] ReadTrack(Stream archive, string entryName) => ReadTrack(archive, entryName, out _, out _);

    /// <summary>As <see cref="ReadTrack(Stream, string)"/>, also returning the track's name and its 1-based position among the tracks.</summary>
    public static byte[] ReadTrack(Stream archive, string entryName, out string name, out int number)
    {
        RarArchive rar = RarArchive.Open(archive);
        number = 0;
        foreach (RarEntry entry in rar.Entries)
        {
            if (!IsTrack(entry)) continue;
            number++;
            if (entryName.Length != 0 && !entry.Name.Equals(entryName, StringComparison.Ordinal)) continue;
            name = entry.Name;
            return rar.Extract(entry);
        }
        throw new InvalidDataException(entryName.Length == 0 ? "The archive holds no SPC files." : $"The archive has no '{entryName}'.");
    }
}
