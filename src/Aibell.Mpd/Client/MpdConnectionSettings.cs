using System.Text.Json;

namespace Aibell.Mpd.Client;

/// <summary>
/// How the application finds the server. With <see cref="Automatic"/> the endpoint is worked out as MPD's own clients do (environment,
/// the local configuration file, the runtime socket); otherwise <see cref="Host"/> and <see cref="Port"/> are used. The password is
/// never stored.
/// </summary>
public sealed record MpdConnectionSettings(bool Automatic = true, string Host = "localhost", int Port = MpdEndpoint.DefaultPort)
{
    public static MpdConnectionSettings Default { get; } = new();

    /// <summary>Loads the settings; a missing, unreadable or invalid file gives the defaults.</summary>
    public static MpdConnectionSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Default;
            MpdConnectionSettings? loaded = JsonSerializer.Deserialize<MpdConnectionSettings>(File.ReadAllText(path));
            return loaded is { Host.Length: > 0, Port: > 0 and <= 65535 } ? loaded : Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Default;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, overwrite: true);
    }
}
