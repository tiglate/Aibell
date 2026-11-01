using System.Globalization;
using Aibell.Application.Library;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Metadata;

public sealed class AtlMediaMetadataReader : IMediaMetadataReader
{
    static AtlMediaMetadataReader()
    {
        ATL.Settings.OutputStacktracesToConsole = false;
    }

    public Task<MediaMetadata> ReadAsync(
        Uri location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!location.IsFile)
        {
            throw new ArgumentException("ATL metadata reading requires a local file URI.", nameof(location));
        }

        string path = location.LocalPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The media file does not exist.", path);
        }

        return Task.Run(() => Read(location, path, cancellationToken), cancellationToken);
    }

    private static MediaMetadata Read(Uri location, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetExtension(path).Equals(".spc", StringComparison.OrdinalIgnoreCase)) return ReadSpc(path, cancellationToken);
        if (Aibell.Audio.Spc.RsnArchive.IsRsnPath(path)) return ReadRsn(location, path, cancellationToken);
        ATL.Track track = new(path);
        MpegAudioData? mpeg = null;
        if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            using FileStream audio = File.OpenRead(path);
            mpeg = MpegAudioData.TryRead(audio, cancellationToken);
        }
        FileInfo file = new(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (track.DurationMs <= 0)
        {
            throw new InvalidDataException($"ATL could not read audio data from '{path}'.");
        }

        return new MediaMetadata(
            EmptyToNull(track.Title),
            EmptyToNull(track.Artist),
            EmptyToNull(track.Album),
            mpeg?.Duration ?? TimeSpan.FromMilliseconds(track.DurationMs),
            EmptyToNull(track.Genre),
            EmptyToNull(track.AlbumArtist),
            (int)(track.TrackNumber ?? 0),
            track.Year ?? 0,
            (int)Math.Round(Math.Clamp(track.Popularity ?? 0, 0, 1) * 5),
            mpeg?.BitRate ?? Math.Max(0, (int)Math.Round(Convert.ToDouble(track.Bitrate, CultureInfo.InvariantCulture))),
            Math.Max(0, Convert.ToDouble(track.BPM, CultureInfo.InvariantCulture)),
            Math.Max(0, track.ChannelsArrangement.NbChannels),
            EmptyToNull(track.Comment),
            EmptyToNull(track.Composer),
            file.LastWriteTimeUtc,
            EmptyToNull(track.Description) ?? EmptyToNull(track.LongDescription),
            (int)(track.DiscNumber ?? 0),
            AtlTagFields.ReadInteger(track.AdditionalFields, AtlTagFields.EpisodeNames) ?? 0,
            AtlTagFields.ReadText(track.AdditionalFields, AtlTagFields.KeywordNames),
            Math.Max(0, (int)Math.Round(track.SampleRate)),
            AtlTagFields.ReadInteger(track.AdditionalFields, AtlTagFields.SeasonNames) ?? 0,
            AtlTagFields.ReadText(track.AdditionalFields, AtlTagFields.ShowNames),
            file.Length);
    }

    // ATL does not know SPC files; the emulator's own parser reads the ID666 and xid6 tags.
    private static MediaMetadata ReadSpc(string path, CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        Aibell.Audio.Spc.SpcFile spc;
        using (FileStream stream = file.OpenRead()) spc = Aibell.Audio.Spc.SpcFile.Open(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return SpcMetadata.From(spc, file.LastWriteTimeUtc, file.Length, Path.GetFileNameWithoutExtension(path));
    }

    // One SPC inside an RSN archive (a RAR file); the location's query names it.
    private static MediaMetadata ReadRsn(Uri location, string path, CancellationToken cancellationToken)
    {
        // A location that is not an RSN track location leaves the entry name empty, which selects the archive's first track.
        if (!Aibell.Audio.Spc.RsnArchive.TryParseLocation(location, out _, out string entryName)) entryName = "";
        FileInfo file = new(path);
        byte[] bytes;
        string name;
        int number;
        using (FileStream stream = file.OpenRead()) bytes = Aibell.Audio.Spc.RsnArchive.ReadTrack(stream, entryName, out name, out number);
        cancellationToken.ThrowIfCancellationRequested();
        var spc = Aibell.Audio.Spc.SpcFile.Parse(bytes);
        return SpcMetadata.From(spc, file.LastWriteTimeUtc, file.Length, Path.GetFileNameWithoutExtension(name), Path.GetFileNameWithoutExtension(path), number);
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
