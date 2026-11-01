using Aibell.Playback.PipeWire;
using NUnit.Framework;

namespace Aibell.Playback.PipeWire.Tests;

/// <summary>
/// Checks which format the client picks out of the ones a sink offers. The rule matters: the
/// material was recorded at forty-four point one or forty-eight kilohertz, so a higher rate only
/// means resampling it up and then back down again.
/// </summary>
[TestFixture]
public sealed class FormatChooserTests
{
    private static AudioFormat Format(uint rate, uint channels = 2) =>
        new(rate, channels, SpaAudioFormat.F32Le);

    [Test]
    public void Nothing_On_Offer_Means_No_Format()
    {
        Assert.That(FormatChooser.Choose([]), Is.Null);
    }

    [Test]
    public void The_Forty_Eight_Kilohertz_Format_Is_Picked()
    {
        AudioFormat? chosen = FormatChooser.Choose([Format(44100), Format(48000), Format(96000)]);

        Assert.That(chosen, Is.Not.Null);
        Assert.That(chosen!.Value.SampleRate, Is.EqualTo(48000u));
    }

    [Test]
    public void A_Higher_Rate_Is_Not_Preferred_Over_Forty_Eight_Kilohertz()
    {
        // The rule in one place: a hundred and ninety-two kilohertz does not win.
        AudioFormat? chosen = FormatChooser.Choose([Format(48000), Format(192000)]);

        Assert.That(chosen!.Value.SampleRate, Is.EqualTo(48000u));
    }

    [Test]
    public void The_Rate_Closest_To_Forty_Eight_Kilohertz_Wins()
    {
        // Forty-four kilohertz is three thousand nine hundred away; ninety-six is far more.
        AudioFormat? chosen = FormatChooser.Choose([Format(44100), Format(96000)]);

        Assert.That(chosen!.Value.SampleRate, Is.EqualTo(44100u));
    }

    [Test]
    public void The_Lower_Rate_Wins_An_Exact_Tie()
    {
        // Forty-four and fifty-two are the same distance from forty-eight only when it is
        // forty-eight thousand from each, which is not the case here; use a real tie instead.
        AudioFormat? chosen = FormatChooser.Choose([Format(40000), Format(56000)]);

        Assert.That(chosen!.Value.SampleRate, Is.EqualTo(40000u));
    }

    [Test]
    public void A_Format_With_No_Rate_Is_Not_Used()
    {
        AudioFormat? chosen = FormatChooser.Choose([Format(0), Format(48000)]);

        Assert.That(chosen!.Value.SampleRate, Is.EqualTo(48000u));
    }

    [Test]
    public void A_Format_With_No_Channels_Is_Not_Used()
    {
        AudioFormat? chosen = FormatChooser.Choose([Format(48000, 0), Format(44100)]);

        Assert.That(chosen!.Value.SampleRate, Is.EqualTo(44100u));
    }

    [Test]
    public void Stereo_Is_Preferred_At_The_Same_Rate()
    {
        AudioFormat? chosen = FormatChooser.ChooseWithChannels([Format(48000, 1), Format(48000, 2)]);

        Assert.That(chosen!.Value.Channels, Is.EqualTo(2u));
    }

    [Test]
    public void The_Rate_Wins_Over_The_Channel_Count()
    {
        // A mono format at the right rate beats a stereo format at the wrong one.
        AudioFormat? chosen = FormatChooser.ChooseWithChannels([Format(48000, 1), Format(44100, 2)]);

        Assert.That(chosen!.Value.SampleRate, Is.EqualTo(48000u));
    }

    [Test]
    public void Nothing_Usable_Means_No_Format()
    {
        Assert.That(FormatChooser.ChooseWithChannels([Format(0, 2)]), Is.Null);
    }

    [Test]
    public void The_Preferred_Rate_Can_Be_Changed()
    {
        AudioFormat? chosen = FormatChooser.Choose([Format(48000), Format(96000)], preferredRate: 96000);

        Assert.That(chosen!.Value.SampleRate, Is.EqualTo(96000u));
    }
}
