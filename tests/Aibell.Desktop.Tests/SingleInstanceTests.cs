using Aibell.Desktop.Services;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

[TestFixture]
public sealed class SingleInstanceTests
{
    private static readonly string[] TwoFiles = ["/music/a song.mp3", "/music/b.flac"];
    private static readonly string[] EarlyFile = ["/music/early.ogg"];
    private string directory = "";

    [SetUp]
    public void CreateDirectory() => Directory.CreateDirectory(directory = Path.Combine(Path.GetTempPath(), "aibell-instance-" + Guid.NewGuid().ToString("N")));

    [TearDown]
    public void DeleteDirectory() => Directory.Delete(directory, recursive: true);

    [Test]
    public void TryBecomePrimary_WhileAnotherInstanceHoldsTheDirectory_ReturnsNull()
    {
        using SingleInstance? first = SingleInstance.TryBecomePrimary(directory);
        Assert.That(first, Is.Not.Null);
        Assert.That(SingleInstance.TryBecomePrimary(directory), Is.Null);
    }

    [Test]
    public void TryBecomePrimary_AfterThePrimaryExits_ClaimsTheDirectory()
    {
        SingleInstance.TryBecomePrimary(directory)!.Dispose();
        using SingleInstance? second = SingleInstance.TryBecomePrimary(directory);
        Assert.That(second, Is.Not.Null);
    }

    [Test]
    public async Task TrySend_DeliversTheLinesToTheHandler()
    {
        using SingleInstance primary = SingleInstance.TryBecomePrimary(directory)!;
        TaskCompletionSource<IReadOnlyList<string>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SetHandler(received.SetResult);
        Assert.That(SingleInstance.TrySend(directory, TwoFiles, TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(TwoFiles));
    }

    [Test]
    public async Task SetHandler_DeliversMessagesThatArrivedEarlier()
    {
        using SingleInstance primary = SingleInstance.TryBecomePrimary(directory)!;
        Assert.That(SingleInstance.TrySend(directory, EarlyFile, TimeSpan.FromSeconds(5)), Is.True);
        await Task.Delay(500);
        TaskCompletionSource<IReadOnlyList<string>> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SetHandler(received.SetResult);
        Assert.That(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(EarlyFile));
    }

    [Test]
    public void TrySend_WithNoInstanceListening_GivesUp()
    {
        Assert.That(SingleInstance.TrySend(directory, [], TimeSpan.FromMilliseconds(300)), Is.False);
    }
}
