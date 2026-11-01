using System.Text.Json;
using System.Text.Json.Serialization;
using Aibell.Application.Settings;
using Aibell.Domain.Playback;

namespace Aibell.Infrastructure.Settings;

public sealed class JsonApplicationSettingsStore(string settingsPath) :
    IApplicationSettingsStore,
    IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new AbsoluteUriConverter() }
    };
    private readonly string settingsPath = Path.GetFullPath(settingsPath);
    private readonly SemaphoreSlim accessLock = new(1, 1);

    public async Task<ApplicationPreferences> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(settingsPath))
            {
                // A new installation uses the player this system prefers.
                return ApplicationPreferences.Default with { PlaybackEngine = PlaybackEngineDefaults.Preferred };
            }

            await using FileStream stream = File.OpenRead(settingsPath);
            byte[] json = await ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);
            ApplicationPreferences? preferences = JsonSerializer.Deserialize<ApplicationPreferences>(json, SerializerOptions);
            if (preferences is null)
            {
                return ApplicationPreferences.Default with { PlaybackEngine = PlaybackEngineDefaults.Preferred };
            }

            // Settings written before the player could be chosen carry no choice at all; they get
            // the player this system prefers rather than the record's own default.
            if (!HasProperty(json, "PlaybackEngine"))
            {
                preferences = preferences with { PlaybackEngine = PlaybackEngineDefaults.Preferred };
            }
            return Normalize(preferences);
        }
        catch (JsonException)
        {
            return ApplicationPreferences.Default with { PlaybackEngine = PlaybackEngineDefaults.Preferred };
        }
        finally
        {
            accessLock.Release();
        }
    }

    /// <summary>Reads the whole file.</summary>
    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream memory = new();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    /// <summary>Whether the settings file names the given property.</summary>
    private static bool HasProperty(byte[] utf8Json, string name)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task SaveAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ApplicationPreferences normalized = Normalize(preferences);
        await accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(settingsPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.tmp");
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        normalized,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, settingsPath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                File.Delete(temporaryPath);
            }

            accessLock.Release();
        }
    }

    private static ApplicationPreferences Normalize(ApplicationPreferences preferences)
    {
        double volume = double.IsFinite(preferences.Volume)
            ? Math.Round(
                Math.Clamp(preferences.Volume, 0, 1),
                2,
                MidpointRounding.AwayFromZero)
            : ApplicationPreferences.Default.Volume;
        RepeatMode repeatMode = Enum.IsDefined(preferences.RepeatMode)
            ? preferences.RepeatMode
            : RepeatMode.Off;
        double queueWidth = double.IsFinite(preferences.PlayQueueWidth)
            ? Math.Clamp(preferences.PlayQueueWidth, 200, 480)
            : ApplicationPreferences.Default.PlayQueueWidth;
        IReadOnlyList<PlayQueueEntry>? queue = preferences.PlayQueue?
            .Where(entry => entry is not null && entry.Id != Guid.Empty &&
                entry.Track is not null && entry.Track.Id != Guid.Empty &&
                entry.Track.Location is { IsAbsoluteUri: true } &&
                entry.Track.Title is not null && entry.Track.Artist is not null &&
                entry.Track.Album is not null)
            .DistinctBy(entry => entry.Id)
            .ToArray();
        return preferences with
        {
            Theme = Enum.IsDefined(preferences.Theme) ? preferences.Theme : ApplicationTheme.System,
            PlaybackEngine = NormalizePlaybackEngine(preferences.PlaybackEngine),
            Language = NormalizeLanguage(preferences.Language),
            SoundFontPath = NormalizeFolder(preferences.SoundFontPath),
            Volume = volume,
            RepeatMode = repeatMode,
            PlayQueueWidth = queueWidth,
            SidebarWidth = double.IsFinite(preferences.SidebarWidth)
                ? Math.Clamp(preferences.SidebarWidth, 160, 480)
                : ApplicationPreferences.Default.SidebarWidth,
            SidebarPlaylistsFraction = double.IsFinite(preferences.SidebarPlaylistsFraction)
                ? Math.Clamp(preferences.SidebarPlaylistsFraction, 0.1, 0.9)
                : ApplicationPreferences.Default.SidebarPlaylistsFraction,
            SidebarLibraryFraction = double.IsFinite(preferences.SidebarLibraryFraction)
                ? Math.Clamp(preferences.SidebarLibraryFraction, 0.1, 0.8)
                : ApplicationPreferences.Default.SidebarLibraryFraction,
            SidebarNowPlayingHeight = double.IsFinite(preferences.SidebarNowPlayingHeight)
                ? Math.Clamp(preferences.SidebarNowPlayingHeight, 120, 600)
                : ApplicationPreferences.Default.SidebarNowPlayingHeight,
            PlayQueue = queue,
            Equalizer = preferences.Equalizer?.Normalized(),
            PodcastPlaybackRate = preferences.PodcastPlaybackRate is 1.0 or 1.25 or 1.5 or 2.0 ? preferences.PodcastPlaybackRate : 1.0,
            PodcastDownloadFolder = NormalizeFolder(preferences.PodcastDownloadFolder),
            PodcastDownloadFolderHistory = preferences.PodcastDownloadFolderHistory?
                .Select(NormalizeFolder).Where(path => path is not null).Cast<string>()
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray()
        };
    }

    private static PlaybackEngineKind NormalizePlaybackEngine(PlaybackEngineKind engine)
    {
        if (!Enum.IsDefined(engine)) return PlaybackEngineDefaults.Preferred;
        if (OperatingSystem.IsLinux() && engine == PlaybackEngineKind.Managed) return PlaybackEngineDefaults.Preferred;
        if (!OperatingSystem.IsLinux() && engine is PlaybackEngineKind.PipeWire or PlaybackEngineKind.Alsa) return PlaybackEngineDefaults.Preferred;
        return engine;
    }

    // A culture name such as "pt-BR"; blank or unknown means "follow the operating system".
    private static string? NormalizeLanguage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try { return System.Globalization.CultureInfo.GetCultureInfo(name.Trim()).Name is { Length: > 0 } culture ? culture : null; }
        catch (System.Globalization.CultureNotFoundException) { return null; }
    }

    private static string? NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public void Dispose()
    {
        accessLock.Dispose();
    }

    private sealed class AbsoluteUriConverter : JsonConverter<Uri>
    {
        public override Uri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? location)
                ? location
                : throw new JsonException("Queue locations must be absolute URIs.");
        }

        public override void Write(Utf8JsonWriter writer, Uri value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.AbsoluteUri);
        }
    }
}
