using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using Tmds.DBus.Protocol;

namespace Aibell.Platform.Linux;

internal static class MprisProperties
{
    public const string RootInterface = "org.mpris.MediaPlayer2";
    public const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    public const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    public const string ObjectPath = "/org/mpris/MediaPlayer2";
    public static string TrackPath(Guid id) => "/org/aibell/track/t" + id.ToString("N");

    public static Dictionary<string, VariantValue> Root() => new()
    {
        ["CanQuit"] = false,
        ["CanRaise"] = true,
        ["HasTrackList"] = false,
        ["Identity"] = "Aibell",
        // The desktop file name without ".desktop"; shells use it to show the app icon next to the player.
        ["DesktopEntry"] = "io.github.tiglate.Aibell",
        ["SupportedUriSchemes"] = VariantValue.Array(System.Array.Empty<string>()),
        ["SupportedMimeTypes"] = VariantValue.Array(System.Array.Empty<string>())
    };

    public static Dictionary<string, VariantValue> Player(MediaSessionState state) => new()
    {
        ["PlaybackStatus"] = state.Playback.Status switch { PlaybackStatus.Playing => "Playing", PlaybackStatus.Paused => "Paused", _ => "Stopped" },
        ["LoopStatus"] = state.Repeat switch { RepeatMode.All => "Playlist", RepeatMode.One => "Track", _ => "None" },
        ["Rate"] = 1d,
        ["MinimumRate"] = 1d,
        ["MaximumRate"] = 1d,
        ["Shuffle"] = state.Shuffle,
        ["Volume"] = state.Playback.IsMuted ? 0d : state.Playback.Volume,
        ["Position"] = Math.Max(0, state.Playback.Position.Ticks / 10),
        ["CanPlay"] = state.CanPlay,
        ["CanPause"] = state.CanPause,
        ["CanSeek"] = state.CanSeek,
        ["CanGoNext"] = state.CanGoNext,
        ["CanGoPrevious"] = state.CanGoPrevious,
        ["CanControl"] = true,
        ["Metadata"] = Metadata(state)
    };

    private static VariantValue Metadata(MediaSessionState state)
    {
        Dict<string, VariantValue> result = new();
        if (state.Playback.Track is not { } track) return result;
        TrackMetadata metadata = state.Metadata ?? TrackMetadata.FromTrack(track);
        result.Add("mpris:trackid", new ObjectPath(TrackPath(track.Id)));
        result.Add("mpris:length", Math.Max(0, track.Duration.Ticks / 10));
        result.Add("xesam:url", track.Location.AbsoluteUri);
        result.Add("xesam:title", metadata.Title);
        result.Add("xesam:artist", VariantValue.Array(new[] { metadata.Artist }));
        result.Add("xesam:album", metadata.Album);
        if (metadata.AlbumArtist.Length > 0) result.Add("xesam:albumArtist", VariantValue.Array(new[] { metadata.AlbumArtist }));
        if (metadata.Genre.Length > 0) result.Add("xesam:genre", VariantValue.Array(new[] { metadata.Genre }));
        if (metadata.TrackNumber > 0) result.Add("xesam:trackNumber", metadata.TrackNumber);
        if (state.Artwork is { IsAbsoluteUri: true, IsFile: true } art) result.Add("mpris:artUrl", art.AbsoluteUri);
        return result;
    }

    public static bool SameMetadata(MediaSessionState before, MediaSessionState after) =>
        before.Playback.Track == after.Playback.Track && before.Metadata == after.Metadata && before.Artwork == after.Artwork;

    public static MediaSessionCommand? SetProperty(string name, VariantValue value) => (name, value.Type) switch
    {
        ("Volume", VariantValueType.Double) when double.IsFinite(value.GetDouble()) => new(MediaSessionAction.SetVolume, Volume: Math.Clamp(value.GetDouble(), 0, 1)),
        ("Shuffle", VariantValueType.Bool) => new(MediaSessionAction.SetShuffle, Shuffle: value.GetBool()),
        ("LoopStatus", VariantValueType.String) => new(MediaSessionAction.SetRepeat, Repeat: value.GetString() switch
        {
            "None" => RepeatMode.Off,
            "Track" => RepeatMode.One,
            "Playlist" => RepeatMode.All,
            _ => throw new ArgumentException("Invalid loop status.")
        }),
        ("Rate", VariantValueType.Double) when value.GetDouble() == 0 => new(MediaSessionAction.Pause),
        ("Rate", VariantValueType.Double) when value.GetDouble() == 1 => null,
        _ => throw new ArgumentException("The property is read-only or its value is unsupported.")
    };
}
