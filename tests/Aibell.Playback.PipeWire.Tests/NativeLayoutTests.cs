using System.Runtime.InteropServices;
using Aibell.Playback.PipeWire;
using NUnit.Framework;

namespace Aibell.Playback.PipeWire.Tests;

/// <summary>
/// The managed copies of PipeWire's structs must match the C layout. A missing field once shifted pw_buffer.requested, so the
/// stream wrote a fixed 1024 frames per cycle: when another application made the graph quantum 2048 (a screenshot's shutter
/// sound), half of every cycle was silence and the stream then stalled.
/// </summary>
[TestFixture]
public sealed class NativeLayoutTests
{
    [Test]
    public void PwBuffer_MatchesTheCLayout()
    {
        if (IntPtr.Size != 8) Assert.Ignore("The offsets below are for 64-bit systems.");
        // struct pw_buffer { struct spa_buffer *buffer; void *user_data; uint64_t size; uint64_t requested; uint64_t time; };
        Assert.That(Marshal.OffsetOf<NativeMethods.PwBuffer>(nameof(NativeMethods.PwBuffer.Buffer)), Is.EqualTo((nint)0));
        Assert.That(Marshal.OffsetOf<NativeMethods.PwBuffer>(nameof(NativeMethods.PwBuffer.UserData)), Is.EqualTo((nint)8));
        Assert.That(Marshal.OffsetOf<NativeMethods.PwBuffer>(nameof(NativeMethods.PwBuffer.Size)), Is.EqualTo((nint)16));
        Assert.That(Marshal.OffsetOf<NativeMethods.PwBuffer>(nameof(NativeMethods.PwBuffer.Requested)), Is.EqualTo((nint)24));
        Assert.That(Marshal.OffsetOf<NativeMethods.PwBuffer>(nameof(NativeMethods.PwBuffer.Time)), Is.EqualTo((nint)32));
    }

    [Test]
    public void MasterVolumePod_SetsOnlyTheVolumePropertyToOne()
    {
        SpaPod pod = SpaPod.Read(PipeWireStream.MasterVolumePod().Write());
        SpaProperty property = pod.Properties.Single();
        Assert.That(property.Key, Is.EqualTo(PipeWireStream.SpaPropVolume));
        Assert.That(property.Value.AsFloat(), Is.EqualTo(1f));
    }
}
