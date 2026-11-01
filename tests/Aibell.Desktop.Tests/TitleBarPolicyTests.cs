using Aibell.Desktop.Services;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

[TestFixture]
public sealed class TitleBarPolicyTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.Where(pair => pair.Name == name).Select(pair => pair.Value).FirstOrDefault();

    [TestCase("niri"), TestCase("sway"), TestCase("Hyprland"), TestCase("i3"), TestCase("river:wlroots")]
    public void Linux_TilingDesktop_HidesTheTitleBar(string desktop)
    {
        Assume.That(OperatingSystem.IsLinux());

        Assert.That(TitleBarPolicy.ShouldHideTitleBar(Env(("XDG_CURRENT_DESKTOP", desktop))), Is.True);
    }

    [TestCase("GNOME"), TestCase("KDE"), TestCase("X-Cinnamon"), TestCase("ubuntu:GNOME"), TestCase("XFCE")]
    public void Linux_FullDesktop_KeepsTheTitleBar(string desktop)
    {
        Assume.That(OperatingSystem.IsLinux());

        Assert.That(TitleBarPolicy.ShouldHideTitleBar(Env(("XDG_CURRENT_DESKTOP", desktop))), Is.False);
    }

    [Test]
    public void Linux_CompositorSocket_HidesTheTitleBarEvenWithoutADesktopName()
    {
        Assume.That(OperatingSystem.IsLinux());

        Assert.That(TitleBarPolicy.ShouldHideTitleBar(Env(("NIRI_SOCKET", "/run/user/1000/niri.sock"))), Is.True);
    }

    [Test]
    public void Linux_NoHints_KeepsTheTitleBar()
    {
        Assume.That(OperatingSystem.IsLinux());

        Assert.That(TitleBarPolicy.ShouldHideTitleBar(Env()), Is.False);
    }

    [Test]
    public void Linux_Override_WinsOverDetection()
    {
        Assume.That(OperatingSystem.IsLinux());

        Assert.That(TitleBarPolicy.ShouldHideTitleBar(Env(("XDG_CURRENT_DESKTOP", "niri"), ("AIBELL_TITLE_BAR", "on"))), Is.False);
        Assert.That(TitleBarPolicy.ShouldHideTitleBar(Env(("XDG_CURRENT_DESKTOP", "GNOME"), ("AIBELL_TITLE_BAR", "off"))), Is.True);
    }
}
