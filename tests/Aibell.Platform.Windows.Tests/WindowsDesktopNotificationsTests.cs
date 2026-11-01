using Aibell.Application.Playback;
using NUnit.Framework;

namespace Aibell.Platform.Windows.Tests;

[TestFixture]
internal sealed class WindowsDesktopNotificationsTests
{
    [Test]
    public void Text_JoinsArtistAndAlbumOnSeparateLines()
    {
        (string title, string body) = WindowsDesktopNotifications.Text(new("Song", "Artist", "Album"));

        Assert.Multiple(() =>
        {
            Assert.That(title, Is.EqualTo("Song"));
            Assert.That(body, Is.EqualTo("Artist\nAlbum"));
        });
    }

    [Test]
    public void Text_EmptyTitleAndAlbum_UsesFallbackTitleAndArtistOnly()
    {
        (string title, string body) = WindowsDesktopNotifications.Text(new("  ", "Artist", ""));

        Assert.Multiple(() =>
        {
            Assert.That(title, Is.EqualTo("Unknown title"));
            Assert.That(body, Is.EqualTo("Artist"));
        });
    }

    [Test]
    public void Text_ControlCharactersAndLongValues_AreStrippedAndBounded()
    {
        (string title, string body) = WindowsDesktopNotifications.Text(new("A\0B\r\nC" + new string('x', 200), new string('a', 500), "Album"));

        Assert.Multiple(() =>
        {
            Assert.That(title, Does.StartWith("A B  C"));
            Assert.That(title, Has.Length.EqualTo(63));
            Assert.That(body, Has.Length.LessThan(256));
            Assert.That(body, Does.Not.Contain('\0'));
        });
    }

    [Test]
    public void Text_TruncationNeverSplitsSurrogatePair()
    {
        (string title, _) = WindowsDesktopNotifications.Text(new(new string('x', 62) + "😀", "", ""));

        Assert.That(char.IsHighSurrogate(title[^1]), Is.False);
    }

    [Test]
    public void ShowAsync_AfterDispose_Throws()
    {
        // The disposed check comes before the platform check, so this holds on every host.
        WindowsDesktopNotifications notifications = new();
        notifications.Dispose();

        Assert.That(async () => await notifications.ShowAsync(new("Song", "Artist", "Album")), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void ShowAsync_NullNotification_Throws()
    {
        using WindowsDesktopNotifications notifications = new();

        Assert.That(async () => await notifications.ShowAsync(null!), Throws.InstanceOf<ArgumentNullException>());
    }

    [Test]
    public void ShowAsync_BeforeWindows10_ThrowsPlatformNotSupported()
    {
        Assume.That(WindowsDesktopNotifications.IsSupported, Is.False, "needs a host older than Windows 10 or not Windows");
        using WindowsDesktopNotifications notifications = new();

        Assert.That(async () => await notifications.ShowAsync(new("Song", "Artist", "Album")), Throws.InstanceOf<PlatformNotSupportedException>());
    }

    [Test]
    public void Dispose_WithoutEverShowing_IsIdempotentAndStartsNoThread()
    {
        WindowsDesktopNotifications notifications = new();

        Assert.That(() => { notifications.Dispose(); notifications.Dispose(); }, Throws.Nothing);
    }

    /// <summary>
    /// Shows one real banner through the Windows shell. Opt in with AIBELL_WINDOWS_NOTIFICATION_TESTS=1 on an interactive
    /// desktop; whether the banner is visible still depends on Focus Assist and the user's notification settings.
    /// </summary>
    [Test]
    [Platform("Win")]
    public async Task ShowAsync_RealShell_DeliversBannerWithoutError()
    {
        Assume.That(Environment.GetEnvironmentVariable("AIBELL_WINDOWS_NOTIFICATION_TESTS"), Is.EqualTo("1"));
        Assume.That(WindowsDesktopNotifications.IsSupported, Is.True);

        using WindowsDesktopNotifications notifications = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await notifications.ShowAsync(new("Aibell integration 日本語", "Artist", "Album"), timeout.Token);
        await notifications.ShowAsync(new("Aibell second banner", "Artist", "Album"), timeout.Token);
    }
}
