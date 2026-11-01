using Aibell.Playback.PipeWire;
using NUnit.Framework;

namespace Aibell.Playback.PipeWire.Tests;

/// <summary>
/// Checks that the client knows when it can be used. The answer drives the fallback to another
/// player, so it must be right on a machine without a daemon and on one with it.
/// </summary>
[TestFixture]
public sealed class PipeWireRuntimeTests
{
    [Test]
    public void The_Socket_Path_Is_Null_Without_A_Runtime_Directory()
    {
        string? previousPipeWire = Environment.GetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR");
        string? previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR", null);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);

            Assert.That(PipeWireRuntime.SocketPath(), Is.Null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR", previousPipeWire);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
        }
    }

    [Test]
    public void The_Socket_Path_Follows_The_Runtime_Directory()
    {
        string? previousPipeWire = Environment.GetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR");
        string? previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR", null);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/tmp/aibell-runtime");

            Assert.That(PipeWireRuntime.SocketPath(), Is.EqualTo(Path.Combine("/tmp/aibell-runtime", "pipewire-0")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR", previousPipeWire);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
        }
    }

    [Test]
    public void The_PipeWire_Runtime_Directory_Wins_Over_The_Other_One()
    {
        string? previousPipeWire = Environment.GetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR");
        string? previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR", "/tmp/aibell-pipewire");
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/tmp/aibell-xdg");

            Assert.That(PipeWireRuntime.SocketPath(), Is.EqualTo(Path.Combine("/tmp/aibell-pipewire", "pipewire-0")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR", previousPipeWire);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
        }
    }

    [Test]
    public void There_Is_No_PipeWire_On_A_Machine_That_Is_Not_Linux()
    {
        if (OperatingSystem.IsLinux()) Assert.Ignore("This check only means something elsewhere.");

        Assert.That(PipeWireRuntime.IsAvailable(out string? problem), Is.False);
        Assert.That(problem, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void A_Missing_Socket_Means_There_Is_No_PipeWire()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("PipeWire only runs on Linux.");

        string? previousPipeWire = Environment.GetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR");
        string? previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            // A directory that exists but holds no socket.
            string empty = Path.Combine(Path.GetTempPath(), "aibell-no-pipewire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);
            try
            {
                Environment.SetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR", null);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", empty);

                Assert.That(PipeWireRuntime.IsAvailable(out string? problem), Is.False);
                Assert.That(problem, Is.Not.Null.And.Not.Empty);
            }
            finally
            {
                Directory.Delete(empty, recursive: true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR", previousPipeWire);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
        }
    }
}
