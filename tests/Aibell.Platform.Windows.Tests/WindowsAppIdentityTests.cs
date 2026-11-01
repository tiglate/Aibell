using System.Runtime.Versioning;
using Microsoft.Win32;
using NUnit.Framework;

namespace Aibell.Platform.Windows.Tests;

[TestFixture]
internal sealed class WindowsAppIdentityTests
{
    [TestCase("")]
    [TestCase("has space")]
    [TestCase(@"back\slash")]
    public void Validate_BadAppId_Throws(string appId) =>
        Assert.Throws<ArgumentException>(() => WindowsAppIdentity.Validate(appId, "Aibell"));

    [Test]
    public void Validate_TooLongAppId_Throws() =>
        Assert.Throws<ArgumentException>(() => WindowsAppIdentity.Validate(new string('a', 129), "Aibell"));

    [Test]
    public void Validate_BlankDisplayName_Throws() =>
        Assert.Throws<ArgumentException>(() => WindowsAppIdentity.Validate("Aibell.Desktop", " "));

    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void Register_SetsProcessIdAndWritesDisplayNameOnce()
    {
        // An isolated key keeps the test off the real HKCU\Software\Classes\AppUserModelId registration.
        string root = @"Software\Aibell.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.That(WindowsAppIdentity.Register("Aibell.Tests", "Aibell Test", root), Is.True);
            Assert.That(WindowsAppIdentity.CurrentProcessId(), Is.EqualTo("Aibell.Tests"));
            Assert.That(ReadDisplayName(root), Is.EqualTo("Aibell Test"));

            Assert.That(WindowsAppIdentity.Register("Aibell.Tests", "Aibell Renamed", root), Is.True);
            Assert.That(ReadDisplayName(root), Is.EqualTo("Aibell Renamed"), "a changed name is rewritten");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Aibell.Tests", throwOnMissingSubKey: false); }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadDisplayName(string root)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{root}\Aibell.Tests");
        return key?.GetValue("DisplayName") as string;
    }
}
