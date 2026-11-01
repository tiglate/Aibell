using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Aibell.Application.Audiobooks;
using Aibell.Application.Library;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Audiobooks;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Audiobooks;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class AudiobooksLifecycleTests
{
    private string directory = null!;
    private Uri file = null!;
    private SqliteTrackRepository database = null!;
    private BlockingRepository repository = null!;
    private BlockingReader reader = null!;
    private BlockingHandoff handoff = null!;
    private AudiobooksViewModel model = null!;
    private AudiobooksTestWindow? window;
    private readonly List<PendingWork> pending = [];

    [SetUp]
    public void SetUp()
    {
        pending.Clear();
        window = null;
        directory = Path.Combine(Path.GetTempPath(), $"aibell-audiobook-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "book.mp3");
        File.WriteAllBytes(path, [1, 2, 3]);
        file = new(path);
        database = new(Path.Combine(directory, "library.db"));
        repository = new(database);
        reader = new();
        handoff = new() { Current = new(file, TimeSpan.FromMinutes(2), true) };
        model = new(new AudiobookShelfService(repository, reader, new AudiobookFiles()), handoff);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (PendingWork work in pending) work.Release.TrySetResult();
        if (window is not null) { await window.ShutdownAsync(); window.Close(); }
        await database.DisposeAsync();
        Directory.Delete(directory, true);
    }

    [AvaloniaTest]
    [TestCase("play")]
    [TestCase("save")]
    [TestCase("remove")]
    public async Task Close_AcceptedOperationIgnoresCancellation_DrainsWithoutLateUiMutation(string action)
    {
        await SeedAsync();
        Open();
        await UntilAsync(() => !model.IsBusy && model.Items.Count == 1);
        AudiobookItem row = model.Items.Single();
        model.SelectedItem = row;
        PendingWork blocked = NewPending();
        Task operation;
        switch (action)
        {
            case "play": handoff.Play = blocked; operation = model.PlayAsync(row, resume: false); break;
            case "save": repository.Save = blocked; operation = model.SaveCurrentAsync(); break;
            default: repository.Remove = blocked; operation = model.RemoveSelectedAsync(); break;
        }
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string status = model.StatusMessage;
        AudiobookShelfEntry original = row.Entry;
        window!.Close();
        await UntilAsync(() => blocked.Token.IsCancellationRequested);
        Task shutdown = window.ShutdownAsync();
        Assert.Multiple(() =>
        {
            Assert.That(window.IsVisible, Is.True, "Accepted work must drain before closing.");
            Assert.That(shutdown.IsCompleted, Is.False);
            Assert.That(blocked.Token.IsCancellationRequested, Is.True);
        });
        blocked.Release.SetResult();
        await operation;
        await UntilAsync(() => !window.IsVisible);
        await shutdown;
        Assert.Multiple(() =>
        {
            Assert.That(model.StatusMessage, Is.EqualTo(status));
            Assert.That(model.Items, Has.Count.EqualTo(1));
            Assert.That(model.Items.Single(), Is.SameAs(row));
            Assert.That(row.Entry, Is.EqualTo(original));
            Assert.That(model.SelectedItem, Is.SameAs(row));
        });
    }

    [AvaloniaTest]
    public async Task Close_InitialReadIgnoresCancellation_DrainsWithoutPopulatingClosedShelf()
    {
        await SeedAsync();
        PendingWork blocked = NewPending();
        repository.Read = blocked;
        Open();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string status = model.StatusMessage;
        window!.Close();
        await UntilAsync(() => blocked.Token.IsCancellationRequested);
        Assert.That(window.IsVisible, Is.True);
        Assert.That(blocked.Token.IsCancellationRequested, Is.True);
        blocked.Release.SetResult();
        await UntilAsync(() => !window.IsVisible);
        Assert.Multiple(() =>
        {
            Assert.That(model.Items, Is.Empty);
            Assert.That(model.StatusMessage, Is.EqualTo(status));
        });
    }

    [AvaloniaTest]
    public async Task Close_AcceptedMetadataReadIgnoresCancellation_DrainsAndLeavesShelfAndSourceUntouched()
    {
        Open();
        await UntilAsync(() => !model.IsBusy);
        PendingWork blocked = NewPending();
        reader.Read = blocked;
        Task add = model.AddAsync(file);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string status = model.StatusMessage;
        window!.Close();
        await UntilAsync(() => blocked.Token.IsCancellationRequested);
        Assert.That(window.IsVisible, Is.True);
        Assert.That(blocked.Token.IsCancellationRequested, Is.True);
        blocked.Release.SetResult();
        await add;
        await UntilAsync(() => !window.IsVisible);
        Assert.Multiple(() =>
        {
            Assert.That(model.Items, Is.Empty);
            Assert.That(model.StatusMessage, Is.EqualTo(status));
            Assert.That(File.ReadAllBytes(file.LocalPath), Is.EqualTo(new byte[] { 1, 2, 3 }));
        });
        Assert.That(await database.ListShelfAsync(), Is.Empty);
    }

    private Task<bool> SeedAsync() => database.AddToShelfAsync(new(file, "Book", "Author", TimeSpan.FromHours(1), TimeSpan.Zero, DateTimeOffset.UtcNow));

    private PendingWork NewPending()
    {
        PendingWork work = new();
        pending.Add(work);
        return work;
    }

    private void Open()
    {
        window = new(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class PendingWork
    {
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitAsync(CancellationToken token)
        {
            Token = token;
            Entered.TrySetResult();
            // Deliberately ignore cancellation until the underlying operation has drained.
            return Release.Task;
        }
    }

    private sealed class BlockingReader : IMediaMetadataReader
    {
        public PendingWork? Read { get; set; }
        public async Task<MediaMetadata> ReadAsync(Uri location, CancellationToken cancellationToken = default)
        {
            if (Read is { } pending) await pending.WaitAsync(cancellationToken);
            return new("Book", "Author", "", TimeSpan.FromHours(1));
        }
    }

    private sealed class BlockingHandoff : IAudiobookPlaybackHandoff
    {
        public PendingWork? Play { get; set; }
        public AudiobookPlaybackSnapshot? Current { get; set; }
        public async Task PlayAsync(AudiobookPlaybackRequest request, CancellationToken cancellationToken = default)
        {
            if (Play is { } pending) await pending.WaitAsync(cancellationToken);
        }
        public ValueTask<AudiobookPlaybackSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
    }

    private sealed class BlockingRepository(IAudiobookShelfRepository inner) : IAudiobookShelfRepository
    {
        public PendingWork? Read { get; set; }
        public PendingWork? Save { get; set; }
        public PendingWork? Remove { get; set; }
        public async Task<IReadOnlyList<AudiobookShelfEntry>> ListShelfAsync(CancellationToken cancellationToken = default)
        {
            if (Read is { } pending) { await pending.WaitAsync(cancellationToken); return await inner.ListShelfAsync(CancellationToken.None); }
            return await inner.ListShelfAsync(cancellationToken);
        }
        public Task<bool> AddToShelfAsync(AudiobookShelfEntry entry, CancellationToken cancellationToken = default) => inner.AddToShelfAsync(entry, cancellationToken);
        public async Task<bool> SaveShelfPositionAsync(Uri location, TimeSpan position, TimeSpan duration, DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            if (Save is { } pending) { await pending.WaitAsync(cancellationToken); return await inner.SaveShelfPositionAsync(location, position, duration, at, CancellationToken.None); }
            return await inner.SaveShelfPositionAsync(location, position, duration, at, cancellationToken);
        }
        public async Task<bool> RemoveFromShelfAsync(Uri location, CancellationToken cancellationToken = default)
        {
            if (Remove is { } pending) { await pending.WaitAsync(cancellationToken); return await inner.RemoveFromShelfAsync(location, CancellationToken.None); }
            return await inner.RemoveFromShelfAsync(location, cancellationToken);
        }
    }
}
