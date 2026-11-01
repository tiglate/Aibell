using Aibell.Playback.Alsa;
using NUnit.Framework;

namespace Aibell.Playback.Alsa.Tests;

/// <summary>
/// Checks the ALSA output. The device itself can only be opened where there is sound hardware, so
/// the checks that need one are skipped elsewhere; the ones that do not are always run.
/// </summary>
[TestFixture]
public sealed class AlsaOutputTests
{
    [Test]
    public void Drain_CompletesAfterRenderedFramesAreWritten()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("ALSA only runs on Linux.");
        using var completed = new ManualResetEventSlim();
        using var output = new AlsaOutput(samples => samples.Fill(0.25f), "null");
        output.RequestDrain(completed.Set);
        output.Start();
        Assert.That(completed.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(output.FramesWritten, Is.GreaterThan(0));
        Assert.That(output.Failure, Is.Null);
    }

    [Test]
    public void A_Device_That_Does_Not_Exist_Cannot_Be_Opened()
    {
        InvalidOperationException? failure = null;
        try
        {
            using var output = new AlsaOutput(_ => { }, "aibell-no-such-device");
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }
        Assert.That(failure, Is.Not.Null, "a device that does not exist must not open");
        Assert.That(failure!.Message, Is.Not.Empty);
    }

    [Test]
    public void The_Error_Text_Comes_From_The_Library()
    {
        Assert.That(AlsaNative.Error(0), Is.EqualTo("no error"));
        // A negative code is the way the library reports a failure.
        Assert.That(AlsaNative.Error(-2), Is.Not.EqualTo("no error"));
    }

    [Test]
    public void The_Default_Device_Can_Be_Opened_Where_There_Is_Sound_Hardware()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("ALSA only runs on Linux.");
        try
        {
            using var output = new AlsaOutput(_ => { });
            Assert.That(output.SampleRate, Is.GreaterThan(0u));
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore("There is no sound device on this machine.");
        }
    }

    [Test]
    public void Writing_Before_Starting_Does_Nothing()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("ALSA only runs on Linux.");
        try
        {
            using var output = new AlsaOutput(_ => { });
            // Nothing should throw: the device is not running yet.
            Assert.DoesNotThrow(() => output.Write(new float[512]));
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore("There is no sound device on this machine.");
        }
    }

    [Test]
    public void A_Disposed_Device_Refuses_To_Be_Written_To()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("ALSA only runs on Linux.");
        AlsaOutput? output = null;
        try
        {
            output = new AlsaOutput(_ => { });
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore("There is no sound device on this machine.");
            return;
        }
        output.Dispose();
        Assert.Throws<ObjectDisposedException>(() => output.Write(new float[512]));
    }

    [Test]
    public void Disposing_Twice_Is_Harmless()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("ALSA only runs on Linux.");
        AlsaOutput? output = null;
        try
        {
            output = new AlsaOutput(_ => { });
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore("There is no sound device on this machine.");
            return;
        }
        output.Dispose();
        Assert.DoesNotThrow(output.Dispose);
    }
}
