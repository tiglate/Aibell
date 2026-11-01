using Aibell.Application.Playback;
using Tmds.DBus.Protocol;

namespace Aibell.Platform.Linux;

public sealed class MprisMediaSession(string? address, string? serviceName = null) : IMediaSession, IDisposable, IPathMethodHandler
{
    public MprisMediaSession() : this(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) { }

    private readonly object gate = new();
    private DBusConnection? connection;
    private IMediaSessionController? controller;
    private MediaSessionState? state;
    private Dictionary<string, VariantValue> properties = [];
    private bool started;
    private bool disposed;
    public string ServiceName { get; } = serviceName ?? $"org.mpris.MediaPlayer2.aibell.instance{Environment.ProcessId}";
    string IPathMethodHandler.Path => MprisProperties.ObjectPath;
    bool IPathMethodHandler.HandlesChildPaths => false;

    public async Task StartAsync(IMediaSessionController controller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("The media session has already started.");
            started = true;
            this.controller = controller;
            state = controller.State;
            properties = MprisProperties.Player(state);
            controller.StateChanged += OnStateChanged;
            controller.Seeked += OnSeeked;
        }
        if (string.IsNullOrWhiteSpace(address)) { Dispose(); throw new InvalidOperationException("No Linux session bus is available."); }
        DBusConnection candidate = new(address);
        try
        {
            await candidate.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            candidate.AddMethodHandler(this);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                connection = candidate;
            }
            await candidate.RequestNameAsync(ServiceName).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            candidate.Dispose();
            Dispose();
            throw;
        }
    }

    private void OnStateChanged(object? sender, EventArgs eventArgs)
    {
        lock (gate)
        {
            if (disposed || controller is null) return;
            MediaSessionState next = controller.State;
            Dictionary<string, VariantValue> updated = MprisProperties.Player(next);
            if (state is not null && MprisProperties.SameMetadata(state, next)) updated["Metadata"] = properties["Metadata"];
            Dictionary<string, VariantValue> changed = updated.Where(pair => pair.Key is not ("Position" or "CanControl") &&
                (!properties.TryGetValue(pair.Key, out VariantValue previous) || previous != pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            state = next;
            properties = updated;
            if (connection is null || changed.Count == 0) return;
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(path: MprisProperties.ObjectPath, @interface: MprisProperties.PropertiesInterface,
                member: "PropertiesChanged", signature: "sa{sv}as");
            writer.WriteString(MprisProperties.PlayerInterface);
            writer.WriteDictionary(changed);
            writer.WriteArray(System.Array.Empty<string>());
            connection.TrySendMessage(writer.CreateMessage());
        }
    }

    private void OnSeeked(object? sender, TimeSpan position)
    {
        OnStateChanged(sender, EventArgs.Empty);
        lock (gate)
        {
            if (connection is null || disposed) return;
            using MessageWriter writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(path: MprisProperties.ObjectPath, @interface: MprisProperties.PlayerInterface,
                member: "Seeked", signature: "x");
            writer.WriteInt64(Math.Max(0, position.Ticks / 10));
            connection.TrySendMessage(writer.CreateMessage());
        }
    }

    ValueTask IPathMethodHandler.HandleMethodAsync(MethodContext context)
    {
        try
        {
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml([MprisIntrospection.Xml]);
                return default;
            }
            Message request = context.Request;
            if (request.InterfaceAsString == MprisProperties.PropertiesInterface) return HandleProperties(context);
            if (request.InterfaceAsString == MprisProperties.RootInterface && request.MemberAsString == "Raise" && request.SignatureAsString.Length == 0)
                return ExecuteAsync(context, new(MediaSessionAction.Raise));
            if (request.InterfaceAsString != MprisProperties.PlayerInterface) return default;
            Reader reader = request.GetBodyReader();
            MediaSessionCommand? command = (request.MemberAsString, request.SignatureAsString) switch
            {
                ("Play", "") => new(MediaSessionAction.Play),
                ("Pause", "") => new(MediaSessionAction.Pause),
                ("PlayPause", "") => new(MediaSessionAction.PlayPause),
                ("Stop", "") => new(MediaSessionAction.Stop),
                ("Next", "") => new(MediaSessionAction.Next),
                ("Previous", "") => new(MediaSessionAction.Previous),
                ("Seek", "x") => new(MediaSessionAction.Seek, Position: reader.ReadInt64()),
                ("SetPosition", "ox") => Position(reader.ReadObjectPathAsString(), reader.ReadInt64()),
                _ => null
            };
            return command is null ? default : ExecuteAsync(context, command);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            ReplyError(context, "org.freedesktop.DBus.Error.InvalidArgs", exception.Message);
            return default;
        }
    }

    private ValueTask HandleProperties(MethodContext context)
    {
        Message request = context.Request;
        Reader reader = request.GetBodyReader();
        string? name = request.MemberAsString;
        if ((name, request.SignatureAsString) is not (("Get", "ss") or ("GetAll", "s") or ("Set", "ssv"))) return default;
        string iface = reader.ReadString();
        Dictionary<string, VariantValue> available;
        lock (gate)
        {
            available = iface switch
            {
                MprisProperties.RootInterface => MprisProperties.Root(),
                MprisProperties.PlayerInterface => properties,
                _ => throw new ArgumentException("Unknown media interface.")
            };
        }
        if (name == "GetAll")
        {
            using MessageWriter writer = context.CreateReplyWriter("a{sv}");
            writer.WriteDictionary(available); context.Reply(writer.CreateMessage()); return default;
        }
        string property = reader.ReadString();
        if (!available.TryGetValue(property, out VariantValue value))
        {
            ReplyError(context, "org.freedesktop.DBus.Error.UnknownProperty", "Unknown media property."); return default;
        }
        if (name == "Get")
        {
            using MessageWriter writer = context.CreateReplyWriter("v");
            writer.WriteVariant(value); context.Reply(writer.CreateMessage()); return default;
        }
        if (iface != MprisProperties.PlayerInterface || property is not ("LoopStatus" or "Rate" or "Shuffle" or "Volume"))
        {
            ReplyError(context, "org.freedesktop.DBus.Error.PropertyReadOnly", "This property is read-only."); return default;
        }
        MediaSessionCommand? command = MprisProperties.SetProperty(property, reader.ReadVariantValue());
        if (command is not null) return ExecuteAsync(context, command);
        ReplyEmpty(context); return default;
    }

    private static MediaSessionCommand Position(string path, long position)
    {
        string suffix = path.StartsWith("/org/aibell/track/t", StringComparison.Ordinal) ? path["/org/aibell/track/t".Length..] : "";
        return new(MediaSessionAction.SetPosition, Position: position,
            TrackId: Guid.TryParseExact(suffix, "N", out Guid id) ? id : null);
    }

    private async ValueTask ExecuteAsync(MethodContext context, MediaSessionCommand command)
    {
        try
        {
            IMediaSessionController active;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                active = controller!;
            }
            await active.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false);
            ReplyEmpty(context);
        }
        catch (Exception exception)
        {
            ReplyError(context, "org.mpris.MediaPlayer2.Player.Error.Failed", exception.Message);
        }
    }

    private void ReplyError(MethodContext context, string name, string message)
    {
        try { context.ReplyError(name, message); }
        catch (ObjectDisposedException) when (disposed) { }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        using MessageWriter writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (controller is not null)
            {
                controller.StateChanged -= OnStateChanged;
                controller.Seeked -= OnSeeked;
            }
            connection?.Dispose();
            connection = null;
            controller = null;
        }
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
