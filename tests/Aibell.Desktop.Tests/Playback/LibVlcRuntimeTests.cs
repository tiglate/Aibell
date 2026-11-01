using Aibell.Playback.LibVLC;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Playback;

[TestFixture]
public sealed class LibVlcRuntimeTests
{
    private string directory = null!;
    [SetUp] public void SetUp() => directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-vlc-path-" + Guid.NewGuid().ToString("N"))).FullName;
    [TearDown] public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public void ResolveDirectory_ExplicitWindowsRuntime_TakesPrecedence()
    {
        File.WriteAllBytes(Path.Combine(directory, "libvlc.dll"), []);
        File.WriteAllBytes(Path.Combine(directory, "libvlccore.dll"), []);
        Assert.That(LibVlcRuntime.ResolveDirectory(true, directory, ""), Is.EqualTo(directory));
    }
    [Test]
    public void ResolveDirectory_InstalledWindowsRuntime_IsDiscoveredWithoutChangingEnvironment()
    {
        string installed = Directory.CreateDirectory(Path.Combine(directory, "VideoLAN", "VLC")).FullName;
        File.WriteAllBytes(Path.Combine(installed, "libvlc.dll"), []);
        File.WriteAllBytes(Path.Combine(installed, "libvlccore.dll"), []);
        Assert.That(LibVlcRuntime.ResolveDirectory(true, null, directory), Is.EqualTo(installed));
        Assert.That(LibVlcRuntime.ResolveDirectory(true, null, ""), Is.Null);
    }
    [Test]
    public void ResolveDirectory_IncompleteOrRelativeOverride_RejectsInsteadOfSilentlyUsingAnotherRuntime()
    {
        Assert.That(() => LibVlcRuntime.ResolveDirectory(true, directory, ""), Throws.TypeOf<DllNotFoundException>());
        Assert.That(() => LibVlcRuntime.ResolveDirectory(true, "relative", ""), Throws.TypeOf<DllNotFoundException>());
    }
    [Test]
    public void ResolveDirectory_NonWindows_UsesPlatformLoader()
        => Assert.That(LibVlcRuntime.ResolveDirectory(false, directory, ""), Is.Null);
}
