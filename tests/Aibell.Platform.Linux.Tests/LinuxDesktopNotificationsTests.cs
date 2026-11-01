using Aibell.Application.Playback;
using NUnit.Framework;

namespace Aibell.Platform.Linux.Tests;

[TestFixture]
public sealed class LinuxDesktopNotificationsTests
{
    [Test]
    public void Text_MarkupSupport_EscapesUntrustedBodyAndKeepsSummaryPlain()
    {
        NowPlayingNotification note = new("Title <tag>\nnext", "Artist & <b>name</b>", "Album");
        var marked = LinuxDesktopNotifications.Text(note, markup: true);
        var plain = LinuxDesktopNotifications.Text(note, markup: false);
        Assert.That(marked.Summary, Is.EqualTo("Title <tag> next"));
        Assert.That(marked.Body, Is.EqualTo("Artist &amp; &lt;b&gt;name&lt;/b&gt;\nAlbum"));
        Assert.That(plain.Body, Is.EqualTo("Artist & <b>name</b>\nAlbum"));
    }

    [Test]
    public void Text_EmptyOrVeryLongTags_UsesBoundedTextAndFallback()
    {
        Assert.That(LinuxDesktopNotifications.Text(new("", "", ""), true).Summary, Is.EqualTo("Unknown title"));
        var text = LinuxDesktopNotifications.Text(new(new string('t', 10000), new string('a', 10000), new string('b', 10000)), false);
        Assert.That(text.Summary, Has.Length.EqualTo(200)); Assert.That(text.Body.Length, Is.LessThanOrEqualTo(801));
    }

    [Test]
    public async Task ShowAsync_PreCancelledOrMissingBus_DoesNotCreateAnExternalConnection()
    {
        using LinuxDesktopNotifications notifications = new("");
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.That(async () => await notifications.ShowAsync(new("Title", "Artist", "Album"), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(async () => await notifications.ShowAsync(new("Title", "Artist", "Album")), Throws.TypeOf<InvalidOperationException>());
        notifications.Dispose();
        Assert.That(async () => await notifications.ShowAsync(new("Title", "Artist", "Album")), Throws.TypeOf<ObjectDisposedException>());
        await Task.CompletedTask;
    }
}
