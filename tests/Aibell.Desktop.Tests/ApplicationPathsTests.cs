using NUnit.Framework;

namespace Aibell.Desktop.Tests;

[TestFixture]
public sealed class ApplicationPathsTests
{
    [Test]
    public void ResolveDataDirectory_ExplicitAbsoluteProfile_DoesNotUseDefault()
    {
        string profile = Path.Combine(Path.GetTempPath(), "aibell-isolated-profile");
        Assert.That(ApplicationPaths.ResolveDataDirectory(profile, ""), Is.EqualTo(profile));
    }
    [Test]
    public void ResolveDataDirectory_Default_UsesPlatformDataFolder()
    {
        string local = Path.GetTempPath();
        Assert.That(ApplicationPaths.ResolveDataDirectory(null, local), Is.EqualTo(Path.Combine(local, "Aibell")));
    }
    [Test]
    public void ResolveDataDirectory_UnavailableDefaultOrRelativeOverride_RejectsRelativeFallback()
    {
        Assert.That(() => ApplicationPaths.ResolveDataDirectory("relative", Path.GetTempPath()), Throws.TypeOf<ArgumentException>());
        Assert.That(() => ApplicationPaths.ResolveDataDirectory(null, ""), Throws.TypeOf<InvalidOperationException>());
    }
}
