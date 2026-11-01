using Tmds.DBus.Protocol;

namespace Aibell.Platform.Linux.Tests;

internal sealed record RecordedNotification(string App, uint Replaces, string Summary, string Body, string[] Actions,
    Dictionary<string, VariantValue> Hints, int Timeout);

internal sealed class RecordingNotificationServer(string address, bool markup) : IPathMethodHandler, IAsyncDisposable
{
    private static readonly string[] MarkupCapabilities = ["body", "body-markup"], PlainCapabilities = ["body"];
    private readonly DBusConnection connection = new(address);
    public List<RecordedNotification> Notes { get; } = [];
    public int CapabilityCalls { get; private set; }
    public bool Block { get; init; }
    public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string IPathMethodHandler.Path => LinuxDesktopNotifications.Path;
    bool IPathMethodHandler.HandlesChildPaths => false;

    public async Task StartAsync()
    {
        await connection.ConnectAsync(); connection.AddMethodHandler(this);
        await connection.RequestNameAsync(LinuxDesktopNotifications.Interface);
    }

    ValueTask IPathMethodHandler.HandleMethodAsync(MethodContext context)
    {
        if (context.Request.InterfaceAsString != LinuxDesktopNotifications.Interface) return default;
        if (context.Request.MemberAsString == "GetCapabilities")
        {
            CapabilityCalls++;
            using MessageWriter reply = context.CreateReplyWriter("as");
            reply.WriteArray(markup ? MarkupCapabilities : PlainCapabilities);
            context.Reply(reply.CreateMessage()); return default;
        }
        if (context.Request.MemberAsString != "Notify" || context.Request.SignatureAsString != "susssasa{sv}i") return default;
        Reader reader = context.Request.GetBodyReader();
        string app = reader.ReadString(); uint replaces = reader.ReadUInt32(); reader.ReadString();
        string summary = reader.ReadString(), body = reader.ReadString(); string[] actions = reader.ReadArrayOfString();
        Dictionary<string, VariantValue> hints = [];
        ArrayEnd end = reader.ReadDictionaryStart(); while (reader.HasNext(end)) hints.Add(reader.ReadString(), reader.ReadVariantValue());
        int timeout = reader.ReadInt32();
        Notes.Add(new(app, replaces, summary, body, actions, hints, timeout)); Received.TrySetResult();
        return ReplyAsync(context);
    }

    private async ValueTask ReplyAsync(MethodContext context)
    {
        if (Block) await Release.Task;
        using MessageWriter reply = context.CreateReplyWriter("u"); reply.WriteUInt32((uint)Notes.Count);
        context.Reply(reply.CreateMessage());
    }

    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult();
        try { await connection.ReleaseNameAsync(LinuxDesktopNotifications.Interface); }
        finally { connection.Dispose(); }
    }
}
