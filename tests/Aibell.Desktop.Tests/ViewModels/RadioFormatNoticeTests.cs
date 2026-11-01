using Aibell.Desktop.ViewModels;
using Aibell.Domain.Radio;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.ViewModels;

[TestFixture]
public sealed class RadioFormatNoticeTests
{
    private static readonly Uri Direct = new("https://radio.example.invalid/live.aac");
    private static readonly Uri Hls = new("https://radio.example.invalid/live/index.m3u8");

    [Test]
    public void NoFormat_NoNotice() => Assert.That(RadioFormatNotice.For(null, Direct, vlcInUse: false, windows: false), Is.Null);

    [Test]
    public void VlcInUse_NoNotice() => Assert.That(RadioFormatNotice.For("HE-AAC", Direct, vlcInUse: true, windows: false), Is.Null);

    [Test]
    public void HeAacOnLinux_SaysVlcOrWindows()
    {
        RadioFormatNotice? notice = RadioFormatNotice.For("HE-AAC", Direct, vlcInUse: false, windows: false);
        Assert.That(notice, Is.EqualTo(new RadioFormatNotice("HE-AAC", WindowsAlternative: true)));
    }

    [Test]
    public void HeAacOnWindowsPlayedDirectly_IsNotFlaggedBecauseMediaFoundationPlaysIt() =>
        Assert.That(RadioFormatNotice.For("HE-AAC", Direct, vlcInUse: false, windows: true), Is.Null);

    [Test]
    public void HeAacOverHls_IsFlaggedEvenOnWindowsAndOffersNoWindowsAlternative()
    {
        RadioFormatNotice? notice = RadioFormatNotice.For("HE-AAC", Hls, vlcInUse: false, windows: true);
        Assert.That(notice, Is.EqualTo(new RadioFormatNotice("HE-AAC", WindowsAlternative: false)));
    }

    [TestCase("Opus")]
    [TestCase("fMP4")]
    public void OtherFormats_NeedVlcEverywhere(string format)
    {
        Assert.That(RadioFormatNotice.For(format, Direct, vlcInUse: false, windows: true), Is.EqualTo(new RadioFormatNotice(format, false)));
        Assert.That(RadioFormatNotice.For(format, Direct, vlcInUse: false, windows: false), Is.EqualTo(new RadioFormatNotice(format, false)));
    }

    [Test]
    public void Card_ShowsTheChipAndItsTip()
    {
        RadioStation station = new(Guid.NewGuid(), "Test FM", Direct, "Pop", "", "");
        RadioCardViewModel card = new(station, true, false, 0, RadioFormatNotice.For("HE-AAC", Direct, false, false));
        Assert.That(card.HasNotice, Is.True);
        Assert.That(card.NoticeText, Is.EqualTo("HE-AAC · needs VLC"));
        Assert.That(card.NoticeTip, Does.Contain("Windows").And.Contain("VLC"));
        Assert.That(new RadioCardViewModel(station, true, false, 0).HasNotice, Is.False);
    }
}
