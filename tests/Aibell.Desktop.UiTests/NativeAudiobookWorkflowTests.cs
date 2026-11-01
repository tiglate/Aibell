using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Audiobooks;
using Aibell.Application.Library;
using Aibell.Application.Playback;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Playback;
using Aibell.Infrastructure.Audiobooks;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Storage;
using Aibell.Playback.LibVLC;
using LibVLCSharp.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed class NativeAudiobookWorkflowTests
{
    [AvaloniaTest]
    public async Task AddPauseSaveReopenResumeAndRemove_UsesSharedNativePlayerWithoutChangingSourceOrMusicLibrary()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to verify native audiobook playback.");
        string directory = Path.Combine(Path.GetTempPath(), $"aibell-native-audiobook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "Native resume book.wav");
            WriteProbe(source);
            byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            TimeSpan saved;
            await using (Profile profile = new(directory, source))
            {
                await profile.OpenAsync();
                await profile.OpenShelfAsync();
                Assert.That(profile.ShelfModel!.Items, Is.Empty);
                Click(profile.Window, "AddAudiobook");
                await UntilAsync(() => profile.ShelfModel.Items.Count == 1 && !profile.ShelfModel.IsBusy);
                Assert.That(profile.ShelfModel.SelectedItem!.Entry.Duration, Is.EqualTo(TimeSpan.FromSeconds(20)).Within(TimeSpan.FromMilliseconds(1)));
                Assert.That(await profile.Repository.ListAsync(), Is.Empty);
                Click(profile.Window, "PlayAudiobook");
                Assert.That(await profile.Capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(1000));
                await UntilAsync(() => profile.Engine.Snapshot.Status == PlaybackStatus.Playing && !profile.ShelfModel.IsBusy);
                await profile.Model.SeekAsync(5);
                await UntilAsync(() => profile.Player.Time >= 5200 && profile.Engine.Snapshot.Position >= TimeSpan.FromSeconds(5.2));
                await profile.Model.TogglePlaybackAsync();
                await UntilAsync(() => profile.Engine.Snapshot.Status == PlaybackStatus.Paused);
                saved = profile.Engine.Snapshot.Position;
                Assert.That(saved, Is.InRange(TimeSpan.FromSeconds(5.2), TimeSpan.FromSeconds(8)));
                Assert.That(Math.Abs(profile.Player.Time - saved.TotalMilliseconds), Is.LessThan(400), "Save uses the real paused native clock.");
                Assert.That(profile.ShelfModel.SelectedItem.Entry.HasSavedPosition, Is.False, "Pause does not save automatically.");
                Click(profile.Window, "SaveAudiobookPosition");
                await UntilAsync(() => !profile.ShelfModel.IsBusy && profile.ShelfModel.SelectedItem.Entry.HasSavedPosition);
                Assert.That((await profile.Repository.ListShelfAsync()).Single().SavedPosition, Is.EqualTo(saved));
                Assert.That(profile.ShelfModel.CanResume, Is.True);
                Assert.That(profile.Model.QueueItems, Has.Count.EqualTo(1));
                Assert.That(profile.Engine.Snapshot.Track!.Location, Is.EqualTo(new Uri(source)));
                // Leaving the shelf only hides it; coming back shows the same saved state.
                Click(profile.Window, "SelectMusicButton");
                await UntilAsync(() => !profile.Model.IsAudiobooksView);
                await profile.OpenShelfAsync();
                Assert.That(profile.ShelfModel!.Items.Single().Entry.SavedPosition, Is.EqualTo(saved));
                Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(source)), Is.EqualTo(hash));
            }

            // Every service, repository, view model and native player is new here.
            await using (Profile reopened = new(directory, source))
            {
                await reopened.OpenAsync();
                await reopened.OpenShelfAsync();
                Assert.That(reopened.ShelfModel!.Items.Single().Entry.SavedPosition, Is.EqualTo(saved));
                reopened.ShelfModel.SelectedItem = reopened.ShelfModel.Items.Single();
                Click(reopened.Window, "ResumeAudiobook");
                short first = await reopened.Capture.FirstSample.Task.WaitAsync(TimeSpan.FromSeconds(10));
                int expected = 1000 + (int)saved.TotalMilliseconds;
                TestContext.Out.WriteLine($"Persisted native position {saved.TotalMilliseconds:F0} ms; first resumed PCM {first}; expected {expected}..{expected + 5}.");
                Assert.That(first, Is.InRange(expected, expected + 5), "The first decoded sample must identify the persisted source offset; an opening sample is 1000.");
                await UntilAsync(() => reopened.Engine.Snapshot.Status == PlaybackStatus.Playing && !reopened.ShelfModel.IsBusy);
                Assert.That(reopened.Model.QueueItems.Any(item => item.Row.Source.Location == new Uri(source)), Is.True, "The shared queue contains the resumed local book alongside restored requests.");
                Assert.That(reopened.Engine.Snapshot.Track!.Location, Is.EqualTo(new Uri(source)));
                await reopened.Model.TogglePlaybackAsync();
                await UntilAsync(() => reopened.Engine.Snapshot.Status == PlaybackStatus.Paused);
                Assert.That((await reopened.Repository.ListShelfAsync()).Single().SavedPosition, Is.EqualTo(saved), "Resume and pause do not silently update the saved position.");
                Click(reopened.Window, "RemoveAudiobook");
                await UntilAsync(() => reopened.Window.OwnedWindows.Count == 1);
                Click(reopened.Window.OwnedWindows.Single(), "ConfirmRemoveAudiobook");
                await UntilAsync(() => reopened.ShelfModel.Items.Count == 0 && !reopened.ShelfModel.IsBusy);
                Assert.That(await reopened.Repository.ListShelfAsync(), Is.Empty);
                Assert.That(await reopened.Repository.ListAsync(), Is.Empty, "Playback and shelf actions never admit the book to Music.");
                Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(source)), Is.EqualTo(hash));
                // Dispose closes the main owner with its shelf still showing, then drains both.
            }
            await using SqliteTrackRepository fresh = new(Path.Combine(directory, "library.db"));
            Assert.That(await fresh.ListShelfAsync(), Is.Empty);
            Assert.That(await fresh.ListAsync(), Is.Empty);
            Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(source)), Is.EqualTo(hash));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static void WriteProbe(string path)
    {
        using BinaryWriter writer = new(File.Create(path));
        const int frames = 48000 * 20;
        writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * 2);
        // Each millisecond carries its own level, revealing the decoder's source offset.
        for (int frame = 0; frame < frames; frame++) writer.Write((short)(1000 + frame / 48));
    }

    private static void Click(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = window.GetVisualDescendants().OfType<Button>().Single(candidate => candidate.Name == name && candidate.IsVisible);
        Assert.That(button.IsEnabled, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

    private sealed class Profile : IAsyncDisposable
    {
        private readonly JsonApplicationSettingsStore settings;
        private readonly LibraryMonitoringService monitoring;
        public SqliteTrackRepository Repository { get; }
        public LibVlcPlaybackEngine Engine { get; } = new(["--aout=dummy"]);
        public MainWindowViewModel Model { get; }
        public MainWindow Window { get; }
        public AudiobooksViewModel? ShelfModel { get; private set; }
        public MediaPlayer Player { get; }
        public PcmCapture Capture { get; }

        public Profile(string directory, string source)
        {
            Repository = new(Path.Combine(directory, "library.db"));
            settings = new(Path.Combine(directory, "settings.json"));
            AtlMediaMetadataReader reader = new();
            LibraryImportService importer = new(new FileSystemMediaDiscovery(), reader, Repository);
            monitoring = new(Repository, importer, NullLogger<LibraryMonitoringService>.Instance);
            Model = new(importer, Repository, Repository, Repository, Repository, settings,
                monitoring, Engine);
            DelayedFilePicker picker = new(new Uri(source));
            picker.Release.SetResult();
            Window = new(Model, filePicker: picker, audiobooksViewModelFactory: handoff =>
            {
                ShelfModel = new(new AudiobookShelfService(Repository, reader, new AudiobookFiles()), handoff);
                return ShelfModel;
            });
            Engine.SetVolumeAsync(1).GetAwaiter().GetResult();
            Player = (MediaPlayer)typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Engine)!;
            Capture = new(Player);
        }

        public async Task OpenAsync()
        {
            Window.Show();
            await UntilAsync(() => !Model.IsBusy && Model.Playlists.Count > 0);
            await Model.SavePreferencesAsync(Model.GetPreferences() with { Volume = 1, Muted = false, Repeat = RepeatMode.Off });
        }

        public async Task OpenShelfAsync()
        {
            Click(Window, "OpenAudiobooksButton");
            await UntilAsync(() => Model.IsAudiobooksView && ShelfModel is { IsBusy: false });
        }

        public async ValueTask DisposeAsync()
        {
            Capture.Release();
            Window.Close();
            await UntilAsync(() => !Window.IsVisible);
            await Model.WaitForPlaybackOperationsAsync();
            Capture.Dispose();
            Model.Dispose();
            Engine.Dispose();
            monitoring.Dispose();
            settings.Dispose();
            await Repository.DisposeAsync();
        }
    }

    private sealed class PcmCapture : IDisposable
    {
        private readonly MediaPlayer player;
        private readonly CancellationTokenSource closing = new();
        private readonly MediaPlayer.LibVLCAudioPlayCb callback;
        public TaskCompletionSource<short> FirstSample { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PcmCapture(MediaPlayer player)
        {
            this.player = player;
            callback = (_, samples, count, _) =>
            {
                if (count == 0) return;
                FirstSample.TrySetResult(Marshal.ReadInt16(samples));
                // A virtual sink consumes the supplied PCM at its sample rate.
                // Teardown releases the wait before Stop joins native threads.
                closing.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(count / 48000d));
            };
            player.SetAudioFormat("S16N", 48000, 1);
            player.SetAudioCallbacks(callback, null, null, null, null);
        }

        public void Release() => closing.Cancel();

        public void Dispose()
        {
            Release();
            player.Stop();
            GC.KeepAlive(callback);
            closing.Dispose();
        }
    }
}
