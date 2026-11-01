using Aibell.Application.Library;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Metadata;

public sealed class AtlTrackFileDetailsReader : ITrackFileDetailsReader
{
    public Task<TrackFileDetails> ReadAsync(Uri location, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsAbsoluteUri || !location.IsFile) throw new ArgumentException("File details require a local file.", nameof(location));
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(location.LocalPath)) throw new FileNotFoundException("The media file is unavailable.", location.LocalPath);
            using FileStream input = new(location.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string sourceHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, cancellationToken));
            input.Position = 0;
            ATL.Track track = new(input, Path.GetExtension(location.LocalPath));
            if (track.DurationMs <= 0) throw new InvalidDataException("The media file could not be read.");
            TrackMetadata metadata = new(Title: track.Title ?? "", Artist: track.Artist ?? "", Album: track.Album ?? "",
                AlbumArtist: track.AlbumArtist ?? "", Genre: track.Genre ?? "", Year: track.Year ?? 0,
                TrackNumber: track.TrackNumber ?? 0, DiscNumber: track.DiscNumber ?? 0, Composer: track.Composer ?? "",
                Comment: track.Comment ?? "", Description: string.IsNullOrEmpty(track.Description) ? track.LongDescription ?? "" : track.Description,
                BeatsPerMinute: track.BPM ?? 0, Rating: (int)Math.Round(Math.Clamp(track.Popularity ?? 0, 0, 1) * 5),
                TrackTotal: track.TrackTotal ?? 0, DiscTotal: track.DiscTotal ?? 0,
                Conductor: track.Conductor ?? "", Grouping: track.Group ?? "", Copyright: track.Copyright ?? "",
                LicenseUri: AtlTagFields.ReadText(track.AdditionalFields, AtlTagFields.LicenseNames) ?? "",
                Keywords: AtlTagFields.ReadText(track.AdditionalFields, AtlTagFields.KeywordNames) ?? "",
                ShowName: AtlTagFields.ReadText(track.AdditionalFields, AtlTagFields.ShowNames) ?? "",
                SeasonNumber: AtlTagFields.ReadInteger(track.AdditionalFields, AtlTagFields.SeasonNames) ?? 0,
                EpisodeNumber: AtlTagFields.ReadInteger(track.AdditionalFields, AtlTagFields.EpisodeNames)
                    ?? 0,
                SortTitle: track.SortTitle ?? "", SortArtist: track.SortArtist ?? "",
                SortAlbumArtist: track.SortAlbumArtist ?? "", SortAlbum: track.SortAlbum ?? "");
            string? restriction = null;
            MpegAudioData? audio = Path.GetExtension(location.LocalPath).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? MpegAudioData.TryRead(input, cancellationToken) : null;
            if (Path.GetExtension(location.LocalPath).Equals(".mp3", StringComparison.OrdinalIgnoreCase) && audio is null)
                restriction = "This MP3 layout has not passed the frame-preservation writing gate. Library editing remains available.";
            else if (track.MetadataFormats.Count > 1 && !AtlMetadataEditStager.IsId3v2WithId3v1(track.MetadataFormats.Select(format => format.Name), audio))
                restriction = "This file holds several tag formats, which have not passed the writing gate. Library editing remains available.";
            if (Path.GetExtension(location.LocalPath).Equals(".wav", StringComparison.OrdinalIgnoreCase) && !RiffWaveInfo.CanWrite(input, cancellationToken))
                restriction = "This WAV layout has not passed the RIFF-preservation writing gate. Library editing remains available.";
            restriction ??= AtlTagFields.UnsafeVendorRestriction(track.AdditionalFields);
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, string> additional = new(track.AdditionalFields);
            if (!string.IsNullOrEmpty(track.SeriesTitle)) additional.TryAdd("Movement name", track.SeriesTitle);
            if (!string.IsNullOrEmpty(track.SeriesPart)) additional.TryAdd("Movement number", track.SeriesPart);
            return new TrackFileDetails(metadata, track.BitDepth, track.AudioFormat.Name,
                track.MetadataFormats.Select(format => format.Name).ToArray(), additional, sourceHash, restriction,
                track.EmbeddedPictures.Select((picture, index) => new EmbeddedArtworkInfo(index,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(picture.PictureData)),
                    picture.PicType == ATL.PictureInfo.PIC_TYPE.Front, picture.PicType.ToString(), picture.PictureData.LongLength)).ToArray());
        }, cancellationToken);
    }
}
