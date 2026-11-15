using Aibell.Mpd.Config;

namespace Aibell.Mpd.Client;

/// <summary>Opens connections to a server; the seam that lets the application's view models run against a fake.</summary>
public interface IMpdConnector
{
    Task<IMpdClient> ConnectAsync(MpdEndpoint endpoint, CancellationToken cancellationToken);

    IMpdChangeSource CreateChangeSource(MpdEndpoint endpoint);

    /// <summary>The endpoint to use for the given settings: the saved host and port, or the automatic choice.</summary>
    MpdEndpoint Locate(MpdConnectionSettings settings, string? password);
}

/// <summary>The real connector: TCP or local sockets, and the automatic endpoint taken from the environment and the local configuration file.</summary>
public sealed class MpdConnector : IMpdConnector
{
    public async Task<IMpdClient> ConnectAsync(MpdEndpoint endpoint, CancellationToken cancellationToken) =>
        await MpdClient.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

    public IMpdChangeSource CreateChangeSource(MpdEndpoint endpoint) => new MpdIdleWatcher(endpoint);

    public MpdEndpoint Locate(MpdConnectionSettings settings, string? password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? pass = string.IsNullOrEmpty(password) ? null : password;
        if (!settings.Automatic) return new MpdEndpoint(settings.Host, settings.Port, pass);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        MpdConfigDocument? configuration = null;
        try
        {
            if (MpdConfigFile.FindExisting(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), home) is { } path) configuration = MpdConfigFile.Load(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable file only means the automatic choice falls back to the defaults.
        }
        MpdEndpoint found = MpdEndpoint.Resolve(Environment.GetEnvironmentVariable, configuration, home, Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), File.Exists);
        return pass is null ? found : found with { Password = pass };
    }
}
