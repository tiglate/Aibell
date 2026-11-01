using System.Security;
using Aibell.Application.Playback;
using Tmds.DBus.Protocol;

namespace Aibell.Platform.Linux;

public sealed class LinuxDesktopNotifications(string? address) : IDesktopNotifications, IDisposable
{
    internal const string Interface = "org.freedesktop.Notifications";
    internal const string Path = "/org/freedesktop/Notifications";
    private readonly SemaphoreSlim access = new(1, 1);
    private readonly object gate = new();
    private DBusConnection? connection;
    private bool ready;
    private bool disposed;

    public LinuxDesktopNotifications() : this(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) { }

    public async Task ShowAsync(NowPlayingNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        await access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DBusConnection active;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("No Linux session bus is available.");
                active = connection ??= new DBusConnection(address);
            }
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            Task send = SendCoreAsync(active, notification);
            try { await send.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                ResetConnection();
                try { await send.ConfigureAwait(false); } catch { }
                throw;
            }
        }
        catch { ResetConnection(); throw; }
        finally { access.Release(); }
    }

    private async Task SendCoreAsync(DBusConnection active, NowPlayingNotification notification)
    {
        if (!ready) { await active.ConnectAsync().ConfigureAwait(false); ready = true; }
        // Bind Notify to the same unique owner that described its markup support.
        // A restarted service must not receive text encoded for its predecessor.
        (string[] Capabilities, string Owner) capabilities = await active.CallMethodAsync(Capabilities(active),
            static (message, _) => (message.GetBodyReader().ReadArrayOfString(),
                message.SenderAsString ?? throw new InvalidDataException("The notification service has no bus owner."))).ConfigureAwait(false);
        if (!capabilities.Owner.StartsWith(':')) throw new InvalidDataException("The notification reply has no unique bus owner.");
        bool markup = capabilities.Capabilities.Contains("body-markup", StringComparer.Ordinal);
        await active.CallMethodAsync(Notify(active, capabilities.Owner, notification, markup),
            static (message, _) => message.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
    }

    private static MessageBuffer Capabilities(DBusConnection active)
    {
        using MessageWriter writer = active.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Interface, path: Path, @interface: Interface, member: "GetCapabilities");
        return writer.CreateMessage();
    }

    private static MessageBuffer Notify(DBusConnection active, string owner, NowPlayingNotification notification, bool markup)
    {
        using MessageWriter writer = active.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: owner, path: Path, @interface: Interface, member: "Notify", signature: "susssasa{sv}i");
        writer.WriteString("Aibell"); writer.WriteUInt32(0); writer.WriteString("audio-x-generic");
        (string summary, string body) = Text(notification, markup);
        writer.WriteString(summary); writer.WriteString(body); writer.WriteArray(System.Array.Empty<string>());
        Dictionary<string, VariantValue> hints = new() { ["transient"] = true, ["suppress-sound"] = true, ["category"] = "music" };
        if (notification.Artwork is { IsAbsoluteUri: true, IsFile: true, IsUnc: false } artwork)
            hints["image-path"] = artwork.AbsoluteUri;
        writer.WriteDictionary(hints); writer.WriteInt32(5000);
        return writer.CreateMessage();
    }

    internal static (string Summary, string Body) Text(NowPlayingNotification notification, bool markup)
    {
        string title = Limit(notification.Title, 200);
        string artist = Limit(notification.Artist, 400), album = Limit(notification.Album, 400);
        string body = artist.Length == 0 ? album : album.Length == 0 ? artist : artist + "\n" + album;
        return (title.Length == 0 ? "Unknown title" : title, markup ? SecurityElement.Escape(body)! : body);
        static string Limit(string value, int length)
        {
            string text = value.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= length ? text : text[..length];
        }
    }

    private void ResetConnection()
    {
        lock (gate)
        {
            connection?.Dispose(); connection = null; ready = false;
        }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; ResetConnection(); }
    }
}
