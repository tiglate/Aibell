using System.Globalization;
using Aibell.Mpd.Config;

namespace Aibell.Mpd.Client;

/// <summary>Where an MPD server listens: a TCP host and port, or a local socket path (a leading <c>@</c> names an abstract socket on Linux).</summary>
public sealed record MpdEndpoint(string Host, int Port = MpdEndpoint.DefaultPort, string? Password = null)
{
    public const int DefaultPort = 6600;

    public bool IsLocalSocket => Host.StartsWith('/') || Host.StartsWith('@');

    public override string ToString() => IsLocalSocket ? Host : Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>
    /// Works out where to connect the way MPD's own clients do: <c>MPD_HOST</c> (which may be <c>password@host</c>) and <c>MPD_PORT</c>, then
    /// the server's configuration file, then the per-user runtime socket, then <c>localhost:6600</c>.
    /// </summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="configuration">The server's configuration, or <c>null</c> when none could be read.</param>
    /// <param name="home">The user's home folder, used to expand a leading <c>~</c>.</param>
    /// <param name="runtimeDirectory"><c>XDG_RUNTIME_DIR</c>, or <c>null</c>.</param>
    /// <param name="fileExists">Tells whether a socket file exists.</param>
    public static MpdEndpoint Resolve(Func<string, string?> environment, MpdConfigDocument? configuration, string home, string? runtimeDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileExists);
        string? envHost = environment("MPD_HOST");
        int? envPort = ParsePort(environment("MPD_PORT"));
        if (!string.IsNullOrWhiteSpace(envHost))
        {
            string? password = null;
            string host = envHost;
            int at = host.LastIndexOf('@');
            if (at > 0 && !host.StartsWith('@'))
            {
                password = host[..at];
                host = host[(at + 1)..];
            }
            return new MpdEndpoint(ExpandHome(host, home), envPort ?? DefaultPort, password);
        }
        int port = envPort ?? ParsePort(configuration?.Get("port")) ?? DefaultPort;
        if (configuration is not null)
        {
            string? address = configuration.GetAll("bind_to_address").FirstOrDefault(item => item is not "any" and not "");
            string? local = configuration.GetAll("bind_to_address").FirstOrDefault(item => item.StartsWith('/') || item.StartsWith('~') || item.StartsWith('@'));
            if (local is not null)
            {
                string expanded = ExpandHome(local, home);
                if (expanded.StartsWith('@') || fileExists(expanded)) return new MpdEndpoint(expanded, port);
            }
            if (address is not null && !(address.StartsWith('/') || address.StartsWith('~') || address.StartsWith('@')))
            {
                (string host, int? hostPort) = SplitHostPort(address);
                return new MpdEndpoint(host, hostPort ?? port);
            }
        }
        if (!string.IsNullOrEmpty(runtimeDirectory))
        {
            string socket = Path.Combine(runtimeDirectory, "mpd", "socket");
            if (fileExists(socket)) return new MpdEndpoint(socket, port);
        }
        return new MpdEndpoint("localhost", port);
    }

    private static int? ParsePort(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535 ? port : null;

    private static string ExpandHome(string path, string home)
    {
        if (path == "~") return home;
        return path.StartsWith("~/", StringComparison.Ordinal) && !string.IsNullOrEmpty(home) ? Path.Combine(home, path[2..]) : path;
    }

    /// <summary>Splits <c>host</c>, <c>host:port</c>, <c>[::1]</c> and <c>[::1]:port</c>.</summary>
    internal static (string Host, int? Port) SplitHostPort(string address)
    {
        if (address.StartsWith('['))
        {
            int close = address.IndexOf(']', StringComparison.Ordinal);
            if (close > 0)
            {
                string host = address[1..close];
                string rest = address[(close + 1)..];
                return (host, rest.StartsWith(':') ? ParsePort(rest[1..]) : null);
            }
        }
        int colon = address.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon == address.LastIndexOf(':'))
            return (address[..colon], ParsePort(address[(colon + 1)..]));
        return (address, null);
    }
}
