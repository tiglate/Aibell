using Aibell.Audio.Spc;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Metadata;

/// <summary>Library metadata for the tags of an SPC file: the game is the album and the song the title.</summary>
internal static class SpcMetadata
{
    public static MediaMetadata From(SpcFile spc, DateTimeOffset modified, long size, string fallbackTitle, string? fallbackAlbum = null, int fallbackTrack = 0)
    {
        SpcTags tags = spc.Tags;
        (TimeSpan play, TimeSpan fade) = spc.ResolveLengths();
        return new MediaMetadata(
            tags.Title ?? fallbackTitle,
            tags.Artist,
            tags.Game ?? tags.OstTitle ?? fallbackAlbum,
            play + fade,
            Genre: "Video Game Music",
            TrackNumber: tags.TrackNumber != 0 ? tags.TrackNumber : fallbackTrack,
            Year: tags.Year,
            Channels: 2,
            Comment: tags.Comments,
            Composer: tags.Artist,
            DateModified: modified,
            DiscNumber: tags.DiscNumber,
            SampleRate: SpcDecoder.NativeSampleRate,
            FileSize: size);
    }
}
