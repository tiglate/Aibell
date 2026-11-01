using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Podcasts;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class PodcastsRatingInteractionTests
{
    [AvaloniaTest]
    public async Task RatingFlyout_ClickingUnselectedDuplicateTitleRow_RatesItsIdentityAndClearsIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aibell-podcast-rating-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteTrackRepository repository = new(Path.Combine(directory, "profile.db"));
            Guid subscription = Guid.NewGuid();
            await repository.AddSubscriptionAsync(new(subscription, new Uri("https://example.invalid/feed"), "Show", "", DateTimeOffset.UtcNow),
                [new(subscription, "first", "Identical title", new Uri("https://example.invalid/first.mp3")),
                 new(subscription, "second", "Identical title", new Uri("https://example.invalid/second.mp3"))]);
            PodcastService service = new(repository, new Offline(), new Offline(), new PodcastDownloadStore(Path.Combine(directory, "downloads")));
            using PodcastsViewModel model = new(service);
            PodcastsWindow window = new(model) { Width = 1000, Height = 700 };
            try
            {
                window.Show();
                await UntilAsync(() => model.Episodes.Count == 2);
                PodcastEpisodeItem first = model.Episodes.Single(item => item.EpisodeId == "first");
                PodcastEpisodeItem second = model.Episodes.Single(item => item.EpisodeId == "second");
                model.SelectedEpisode = first;
                Dispatcher.UIThread.RunJobs();
                Button rating = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RateEpisode" && ReferenceEquals(button.DataContext, second));
                OpenFlyoutByPointer(window, rating);
                MenuFlyout menu = (MenuFlyout)rating.Flyout!;
                MenuItem stars = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "5"));
                stars.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Hide();
                await UntilAsync(() => second.Episode.Rating == 5);
                Assert.That(first.Episode.Rating, Is.Zero);
                Assert.That((await repository.ListEpisodesAsync(subscription)).Single(item => item.EpisodeId == "second").Rating, Is.EqualTo(5));
                model.SearchText = "IDENTICAL";
                Dispatcher.UIThread.RunJobs();
                rating = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RateEpisode" && ReferenceEquals(button.DataContext, second));
                OpenFlyoutByPointer(window, rating);
                menu = (MenuFlyout)rating.Flyout!;
                MenuItem clear = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "0"));
                clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Hide();
                await UntilAsync(() => second.Episode.Rating == 0);
                Assert.That((await repository.ListEpisodesAsync(subscription)).All(item => item.Rating == 0), Is.True);
                model.SelectedEpisode = first;
                ListBox episodes = window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "PodcastEpisodeList");
                episodes.Focus();
                episodes.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D4 });
                await UntilAsync(() => first.Episode.Rating == 4);
                Assert.That(second.Episode.Rating, Is.Zero);
                episodes.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.NumPad0 });
                await UntilAsync(() => first.Episode.Rating == 0);
                Assert.That((await repository.ListEpisodesAsync(subscription)).All(item => item.Rating == 0), Is.True);
                Assert.That(await repository.ListAsync(), Is.Empty);
            }
            finally { await window.ShutdownAsync(); window.Close(); Dispatcher.UIThread.RunJobs(); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static void OpenFlyoutByPointer(Window window, Button button)
    {
        Assert.That(button.IsVisible && button.IsEnabled, Is.True);
        Avalonia.Point point = button.TranslatePoint(new Rect(button.Bounds.Size).Center, window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.That(((MenuFlyout)button.Flyout!).IsOpen, Is.True, "An actual row-button pointer click must open its rating choices.");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Offline : IPodcastFeedReader, IPodcastEpisodeDownloader
    {
        public Task<PodcastFeedResult> ReadAsync(PodcastFeedRequest request, CancellationToken cancellationToken = default) => throw new AssertionException("Rating must not fetch feeds.");
        public Task<PodcastDownloadResult> DownloadAsync(PodcastDownloadRequest request, CancellationToken cancellationToken = default) => throw new AssertionException("Rating must not download media.");
    }
}
