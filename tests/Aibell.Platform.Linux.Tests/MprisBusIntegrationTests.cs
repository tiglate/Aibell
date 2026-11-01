using System.Diagnostics;
using Aibell.Application.Playback;
using NUnit.Framework;
using Tmds.DBus.Protocol;

namespace Aibell.Platform.Linux.Tests;

[TestFixture, Category("LinuxMediaSession")]
public sealed class MprisBusIntegrationTests
{
    private static readonly string[] VolumeProperties = ["Volume"];
    private Process? bus;
    private string address = null!;

    [OneTimeSetUp]
    public async Task StartPrivateBus()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("AIBELL_LINUX_INTEGRATION_TESTS") != "1")
            Assert.Ignore("Enable Linux media-session integration tests with a private dbus-daemon.");
        bus = Process.Start(new ProcessStartInfo("dbus-daemon")
        {
            ArgumentList = { "--session", "--nofork", "--print-address=1" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        try { address = await bus.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("The private bus did not report an address."); }
        catch { await StopPrivateBus(); throw; }
    }

    [OneTimeTearDown]
    public async Task StopPrivateBus()
    {
        if (bus is null) return;
        if (!bus.HasExited) { bus.Kill(); await bus.WaitForExitAsync(); }
        bus.Dispose(); bus = null;
    }

    [Test]
    public async Task PropertiesAndIntrospection_ExposeTypedPlayerAndRootContracts()
    {
        TestController controller = new();
        await using MprisMediaSession session = new(address, Name());
        await session.StartAsync(controller);
        using DBusConnection client = new(address); await client.ConnectAsync();
        Dictionary<string, VariantValue> player = await GetAll(client, session.ServiceName, MprisProperties.PlayerInterface);
        Assert.That(player["PlaybackStatus"].GetString(), Is.EqualTo("Playing"));
        Assert.That(player["Position"].GetInt64(), Is.EqualTo(2_000_000));
        Assert.That(player["Metadata"].GetDictionary<string, VariantValue>()["xesam:title"].GetString(), Is.EqualTo("Library title 日本語"));
        Dictionary<string, VariantValue> root = await GetAll(client, session.ServiceName, MprisProperties.RootInterface);
        Assert.That(root["CanRaise"].GetBool(), Is.True);
        Assert.That(root["HasTrackList"].GetBool(), Is.False);
        string xml = await client.CallMethodAsync(Call(client, session.ServiceName, "org.freedesktop.DBus.Introspectable", "Introspect"),
            static (message, _) => message.GetBodyReader().ReadString());
        System.Xml.Linq.XDocument parsed = System.Xml.Linq.XDocument.Parse(xml);
        Assert.That(parsed.Root!.Elements("interface").Select(element => (string?)element.Attribute("name")),
            Does.Contain(MprisProperties.PlayerInterface));
    }

    [Test]
    public async Task Commands_RouteMethodsPositionsAndWritableProperties()
    {
        TestController controller = new();
        await using MprisMediaSession session = new(address, Name()); await session.StartAsync(controller);
        using DBusConnection client = new(address); await client.ConnectAsync();
        foreach (string method in new[] { "Play", "Pause", "PlayPause", "Stop", "Next", "Previous" })
            await client.CallMethodAsync(Call(client, session.ServiceName, MprisProperties.PlayerInterface, method));
        await client.CallMethodAsync(Call(client, session.ServiceName, MprisProperties.RootInterface, "Raise"));
        await client.CallMethodAsync(Seek(client, session.ServiceName, -1_000_000));
        Guid id = controller.State.Playback.Track!.Id;
        await client.CallMethodAsync(SetPosition(client, session.ServiceName, MprisProperties.TrackPath(id), 3_000_000));
        await client.CallMethodAsync(Set(client, session.ServiceName, "Volume", 0.4d));
        await client.CallMethodAsync(Set(client, session.ServiceName, "Shuffle", true));
        await client.CallMethodAsync(Set(client, session.ServiceName, "LoopStatus", "Playlist"));
        Assert.That(controller.Commands.Select(command => command.Action), Is.EqualTo(new[]
        {
            MediaSessionAction.Play, MediaSessionAction.Pause, MediaSessionAction.PlayPause, MediaSessionAction.Stop,
            MediaSessionAction.Next, MediaSessionAction.Previous, MediaSessionAction.Raise, MediaSessionAction.Seek,
            MediaSessionAction.SetPosition, MediaSessionAction.SetVolume, MediaSessionAction.SetShuffle, MediaSessionAction.SetRepeat
        }));
        Assert.That(controller.Commands[7].Position, Is.EqualTo(-1_000_000));
        Assert.That(controller.Commands[8].TrackId, Is.EqualTo(id));
        Assert.That(controller.Commands[9].Volume, Is.EqualTo(0.4d));
    }

    [Test]
    public async Task Signals_EmitChangedPropertiesAndSeekedWithoutClockSpam()
    {
        TestController controller = new();
        await using MprisMediaSession session = new(address, Name()); await session.StartAsync(controller);
        using DBusConnection client = new(address); await client.ConnectAsync();
        List<Dictionary<string, VariantValue>> changes = [];
        TaskCompletionSource<Dictionary<string, VariantValue>> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable watch = await client.AddMatchAsync(new MatchRule
        { Type = MessageType.Signal, Path = MprisProperties.ObjectPath, Interface = MprisProperties.PropertiesInterface, Member = "PropertiesChanged" },
            static (message, _) => { Reader reader = message.GetBodyReader(); reader.ReadString(); return ReadDictionary(ref reader); },
            notification =>
            {
                if (notification.IsCompletion) return;
                lock (changes) changes.Add(notification.Value);
                changed.TrySetResult(notification.Value);
            }, emitOnCapturedContext: false);
        controller.State = controller.State with { Playback = controller.State.Playback with { Position = TimeSpan.FromSeconds(3) } };
        controller.Publish();
        // Round-trip over the bus after the clock-only update instead of sleeping.
        await GetAll(client, session.ServiceName, MprisProperties.PlayerInterface);
        lock (changes) Assert.That(changes, Is.Empty);
        controller.State = controller.State with { Playback = controller.State.Playback with { Volume = 0.5 } };
        controller.Publish();
        Dictionary<string, VariantValue> signal = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(signal.Keys, Is.EqualTo(VolumeProperties));
        Assert.That(signal["Volume"].GetDouble(), Is.EqualTo(0.5));
        TaskCompletionSource<long> seeked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable seekWatch = await client.AddMatchAsync(new MatchRule
        { Type = MessageType.Signal, Path = MprisProperties.ObjectPath, Interface = MprisProperties.PlayerInterface, Member = "Seeked" },
            static (message, _) => message.GetBodyReader().ReadInt64(),
            notification => { if (!notification.IsCompletion) seeked.TrySetResult(notification.Value); }, emitOnCapturedContext: false);
        controller.State = controller.State with { Playback = controller.State.Playback with { Position = TimeSpan.FromSeconds(4) } };
        controller.PublishSeek(TimeSpan.FromSeconds(4));
        Assert.That(await seeked.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(4_000_000));
        Assert.That((await GetAll(client, session.ServiceName, MprisProperties.PlayerInterface))["Position"].GetInt64(), Is.EqualTo(4_000_000));
    }

    [Test]
    public async Task InvalidPropertiesAndUnknownMethods_ReturnErrorsWithoutChangingState()
    {
        TestController controller = new();
        await using MprisMediaSession session = new(address, Name()); await session.StartAsync(controller);
        using DBusConnection client = new(address); await client.ConnectAsync();
        foreach (MessageBuffer request in new[] { Set(client, session.ServiceName, "Volume", double.NaN),
            Set(client, session.ServiceName, "Position", 1L), Set(client, session.ServiceName, "LoopStatus", "Bad"),
            Call(client, session.ServiceName, MprisProperties.PlayerInterface, "Unknown"),
            Call(client, session.ServiceName, MprisProperties.RootInterface, "Quit") })
            Assert.That(async () => await client.CallMethodAsync(request), Throws.TypeOf<DBusErrorReplyException>());
        Assert.That(controller.Commands, Is.Empty);
        Assert.That((await GetAll(client, session.ServiceName, MprisProperties.PlayerInterface))["PlaybackStatus"].GetString(), Is.EqualTo("Playing"));
    }

    [Test]
    public async Task Dispose_ReleasesNameAndStopsCommandAccess()
    {
        MprisMediaSession session = new(address, Name()); await session.StartAsync(new TestController());
        using DBusConnection client = new(address); await client.ConnectAsync();
        await GetAll(client, session.ServiceName, MprisProperties.PlayerInterface);
        await session.DisposeAsync();
        Assert.That(async () => await GetAll(client, session.ServiceName, MprisProperties.PlayerInterface), Throws.TypeOf<DBusErrorReplyException>());
        await session.DisposeAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Notifications_PrivateService_ReceivesTypedTransientPayloadAndCapabilities(bool markup)
    {
        await using RecordingNotificationServer server = new(address, markup); await server.StartAsync();
        using LinuxDesktopNotifications notifications = new(address);
        await notifications.ShowAsync(new("Now playing", "Artist & <b>tag</b>", "Album", new Uri("file:///cache/cover.png")));
        RecordedNotification note = server.Notes.Single();
        Assert.That(note.App, Is.EqualTo("Aibell")); Assert.That(note.Replaces, Is.Zero);
        Assert.That(note.Summary, Is.EqualTo("Now playing"));
        Assert.That(note.Body, Is.EqualTo(markup ? "Artist &amp; &lt;b&gt;tag&lt;/b&gt;\nAlbum" : "Artist & <b>tag</b>\nAlbum"));
        Assert.That(note.Actions, Is.Empty); Assert.That(note.Timeout, Is.EqualTo(5000));
        Assert.That(note.Hints["suppress-sound"].GetBool(), Is.True);
        Assert.That(note.Hints["transient"].GetBool(), Is.True);
        Assert.That(note.Hints["image-path"].GetString(), Is.EqualTo("file:///cache/cover.png"));
        await notifications.ShowAsync(new("Next", "Artist", "Album"));
        Assert.That(server.Notes, Has.Count.EqualTo(2)); Assert.That(server.CapabilityCalls, Is.EqualTo(2));
    }

    [Test]
    public async Task Notifications_CancelBlockedReply_DrainsClientWithoutWaitingForServer()
    {
        await using RecordingNotificationServer server = new(address, false) { Block = true }; await server.StartAsync();
        using LinuxDesktopNotifications notifications = new(address);
        using CancellationTokenSource cancellation = new();
        Task send = notifications.ShowAsync(new("Title", "Artist", "Album"), cancellation.Token);
        await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        try { Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>()); }
        finally { server.Release.TrySetResult(); }
    }

    [Test]
    public async Task Notifications_ServiceRestart_RechecksMarkupAndUsesNewUniqueOwner()
    {
        using LinuxDesktopNotifications notifications = new(address);
        await using (RecordingNotificationServer first = new(address, true))
        {
            await first.StartAsync(); await notifications.ShowAsync(new("First", "Artist & tag", ""));
            Assert.That(first.Notes.Single().Body, Is.EqualTo("Artist &amp; tag"));
        }
        await using RecordingNotificationServer second = new(address, false);
        await second.StartAsync(); await notifications.ShowAsync(new("Second", "Artist & tag", ""));
        Assert.That(second.Notes.Single().Body, Is.EqualTo("Artist & tag"));
    }

    private static string Name() => "org.mpris.MediaPlayer2.aibell.test" + Guid.NewGuid().ToString("N");
    private static MessageBuffer Call(DBusConnection client, string destination, string iface, string member)
    {
        using MessageWriter writer = client.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: MprisProperties.ObjectPath, @interface: iface, member: member);
        return writer.CreateMessage();
    }
    private static MessageBuffer Seek(DBusConnection client, string destination, long offset)
    {
        using MessageWriter writer = client.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: MprisProperties.ObjectPath, @interface: MprisProperties.PlayerInterface, member: "Seek", signature: "x");
        writer.WriteInt64(offset); return writer.CreateMessage();
    }
    private static MessageBuffer SetPosition(DBusConnection client, string destination, string track, long position)
    {
        using MessageWriter writer = client.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: MprisProperties.ObjectPath, @interface: MprisProperties.PlayerInterface, member: "SetPosition", signature: "ox");
        writer.WriteObjectPath(track); writer.WriteInt64(position); return writer.CreateMessage();
    }
    private static MessageBuffer Set(DBusConnection client, string destination, string property, VariantValue value)
    {
        using MessageWriter writer = client.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: MprisProperties.ObjectPath, @interface: MprisProperties.PropertiesInterface, member: "Set", signature: "ssv");
        writer.WriteString(MprisProperties.PlayerInterface); writer.WriteString(property); writer.WriteVariant(value); return writer.CreateMessage();
    }
    private static Task<Dictionary<string, VariantValue>> GetAll(DBusConnection client, string destination, string iface)
    {
        using MessageWriter writer = client.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: MprisProperties.ObjectPath, @interface: MprisProperties.PropertiesInterface, member: "GetAll", signature: "s");
        writer.WriteString(iface);
        return client.CallMethodAsync(writer.CreateMessage(), static (message, _) => { Reader reader = message.GetBodyReader(); return ReadDictionary(ref reader); });
    }
    private static Dictionary<string, VariantValue> ReadDictionary(ref Reader reader)
    {
        Dictionary<string, VariantValue> result = [];
        ArrayEnd end = reader.ReadDictionaryStart();
        while (reader.HasNext(end)) result.Add(reader.ReadString(), reader.ReadVariantValue());
        return result;
    }
}
