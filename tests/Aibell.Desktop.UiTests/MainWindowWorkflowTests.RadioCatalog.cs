using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Playback;
using Aibell.Application.Radio;
using Aibell.Desktop.Services;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Radio;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    private static readonly string[] ExpectedLogoRequests = ["https://logos.example.invalid/rock-one.png", "https://logos.example.invalid/jazz-uno.png"];
    private static readonly string[] PopularityOrderedCardNames = ["Rock One", "News One", "Jazz Uno", "Rock Due", "Klassiek"];
    private static readonly string[] AllCountriesNames = ["All countries", "Germany", "Italy", "Netherlands"];
    private static readonly string[] RockCardNames = ["Rock One", "Rock Due"];
    private static readonly string[] RockDueCardNames = ["Rock Due"];
    private static readonly string[] AlphabeticalCardNames = ["Jazz Uno", "Klassiek", "News One", "Rock Due", "Rock One"];
    private static readonly string[] AllStationsAndRockGenres = ["All Stations", "Rock"];
    private static readonly string[] AllStationsAndJazzGenres = ["All Stations", "Jazz"];
    private static readonly string[] AllCountriesAndBrazil = ["All countries", "Brazil"];

    private static readonly CuratedStation[] SampleCatalog =
    [
        Sample("de-rock-one", "Rock One", "DE", "Germany", "Rock", "https://logos.example.invalid/rock-one.png"),
        Sample("de-news-one", "News One", "DE", "Germany", "News", null),
        Sample("it-jazz-one", "Jazz Uno", "IT", "Italy", "Jazz", "https://logos.example.invalid/jazz-uno.png"),
        Sample("it-rock-two", "Rock Due", "IT", "Italy", "Rock", null),
        Sample("nl-classic", "Klassiek", "NL", "Netherlands", "Classical", null)
    ];

    private static CuratedStation Sample(string id, string name, string code, string country, string genre, string? logo) =>
        new(id, name, code, country, genre, $"{name} description", new Uri($"https://streams.example.invalid/{id}"),
            logo is null ? null : new Uri(logo), new Uri($"https://www.{id}.example.invalid/"));

    private sealed class FakeCatalog(IReadOnlyList<CuratedStation> stations) : ICuratedStationCatalog
    {
        public IReadOnlyList<CuratedStation> Stations { get; } = stations;
    }

    private sealed class CountingLogoLoader : ICoverImageLoader
    {
        public List<Uri> Requests { get; } = [];
        public Task<Bitmap?> LoadAsync(Uri url, CancellationToken cancellationToken = default)
        {
            Requests.Add(url);
            return Task.FromResult<Bitmap?>(new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul));
        }
    }

    private async Task OpenCatalogAsync(CountingLogoLoader loader, bool enable)
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-catalog-ui-{Guid.NewGuid():N}");
        Open(radio: true, curatedCatalog: new FakeCatalog(SampleCatalog), radioLogos: loader);
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        if (enable) await viewModel.SetRadioCatalogEnabledAsync(true);
        Activate(window.FindControl<Button>("SelectRadioButton")!);
        await UntilAsync(() => viewModel.IsRadioView && !viewModel.IsBusy);
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalogStationTheBuiltInPlayerCannotPlay_ShowsAVisibleVlcTag()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("On Windows the direct HE-AAC station is played through Media Foundation and carries no tag.");
        CuratedStation flagged = Sample("de-he", "Dull FM", "DE", "Germany", "Pop", null) with { UnsupportedFormat = "HE-AAC" };
        directory = Path.Combine(Path.GetTempPath(), $"aibell-radio-tag-ui-{Guid.NewGuid():N}");
        Open(radio: true, curatedCatalog: new FakeCatalog([flagged, Sample("de-ok", "Fine FM", "DE", "Germany", "Pop", null)]), radioLogos: new CountingLogoLoader());
        await UntilAsync(() => !viewModel.IsBusy && viewModel.Playlists.Count > 0);
        await viewModel.SetRadioCatalogEnabledAsync(true);
        Activate(window.FindControl<Button>("SelectRadioButton")!);
        await UntilAsync(() => viewModel.IsRadioView && !viewModel.IsBusy && viewModel.RadioCards.Count == 2);
        Dispatcher.UIThread.RunJobs();

        TextBlock[] tags = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.Text == "HE-AAC · needs VLC" && block.IsEffectivelyVisible)];
        Assert.That(tags, Has.Length.EqualTo(1), "only the HE-AAC station is tagged");
        Assert.That(viewModel.RadioCards.Single(card => card.HasNotice).Name, Is.EqualTo("Dull FM"));
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioFreshProfile_OpensOnTheCatalogOptInThenShowsEmptyMyStationsWithNoNetworkRequests()
    {
        CountingLogoLoader loader = new();
        await OpenCatalogAsync(loader, enable: false);
        Dispatcher.UIThread.RunJobs();
        Assert.That(viewModel.IsRadioCatalogMode, Is.True, "Choosing Radio opens the catalog first.");
        Assert.That(viewModel.ShowRadioCatalogOptIn, Is.True);
        Assert.That(window.FindControl<Button>("RadioCatalogButton")!.Bounds.X, Is.LessThan(window.FindControl<Button>("RadioMyStationsButton")!.Bounds.X),
            "The catalog tab sits left of My stations.");
        Activate(window.FindControl<Button>("RadioMyStationsButton")!);
        await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsRadioCatalogMode, Is.False);
            Assert.That(viewModel.RadioCards, Is.Empty);
            Assert.That(viewModel.IsRadioEmpty, Is.True);
            Assert.That(viewModel.RadioEmptyText, Does.Contain("Add a direct"));
            Assert.That(viewModel.RadioCatalogEnabled, Is.False);
            Assert.That(window.FindControl<Button>("RadioCatalogButton")!.IsVisible, Is.True);
            Assert.That(loader.Requests, Is.Empty);
        });
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalogBeforeOptIn_ShowsTheOptInCardAndNeverCallsTheLogoLoader()
    {
        CountingLogoLoader loader = new();
        await OpenCatalogAsync(loader, enable: false);
        Activate(window.FindControl<Button>("RadioCatalogButton")!);
        await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsRadioCatalogMode, Is.True);
            Assert.That(viewModel.ShowRadioCatalogOptIn, Is.True);
            Assert.That(viewModel.RadioCards, Is.Empty);
            Assert.That(window.FindControl<Button>("EnableRadioCatalogButton")!.IsEffectivelyVisible, Is.True);
            Assert.That(window.FindControl<ListBox>("RadioStationList")!.IsEffectivelyVisible, Is.False);
            Assert.That(window.FindControl<Button>("AddRadioStationButton")!.IsEffectivelyVisible, Is.False);
            Assert.That(loader.Requests, Is.Empty);
        });
        // Even asking for a logo directly is refused until the user has opted in.
        await viewModel.RequestRadioLogoAsync(new RadioCardViewModel(SampleCatalog[0].ToRadioStation(), true, false, 0));
        Assert.That(loader.Requests, Is.Empty);

        Activate(window.FindControl<Button>("EnableRadioCatalogButton")!);
        await UntilAsync(() => viewModel.RadioCatalogEnabled && viewModel.RadioCards.Count == SampleCatalog.Length);
        Assert.That((await settings.LoadAsync()).RadioCatalogEnabled, Is.True);
        Assert.That(viewModel.ShowRadioCatalogOptIn, Is.False);
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalog_LogosAreRequestedOnceAndOnlyOverHttps()
    {
        CountingLogoLoader loader = new();
        await OpenCatalogAsync(loader, enable: true);
        viewModel.SetRadioCatalogMode(true);
        await PumpAsync();
        RadioCardViewModel rock = viewModel.RadioCards.Single(card => card.Name == "Rock One");
        RadioCardViewModel news = viewModel.RadioCards.Single(card => card.Name == "News One");
        // Cards inside the scroll viewport ask for their logo by themselves; asking again changes nothing.
        await UntilAsync(() => rock.HasLogo);
        await viewModel.RequestRadioLogoAsync(rock);
        await viewModel.RequestRadioLogoAsync(rock);
        await viewModel.RequestRadioLogoAsync(news);
        Assert.Multiple(() =>
        {
            Assert.That(loader.Requests.Select(uri => uri.AbsoluteUri), Is.EquivalentTo(ExpectedLogoRequests));
            Assert.That(loader.Requests.Distinct().Count(), Is.EqualTo(loader.Requests.Count));
            Assert.That(rock.HasLogo, Is.True);
            Assert.That(rock.ShowInitials, Is.False);
            Assert.That(news.HasLogo, Is.False);
            Assert.That(news.Initials, Is.EqualTo("NO"));
        });
        // Turning the catalog off forgets the downloaded logos and shows the opt-in card again.
        await viewModel.SetRadioCatalogEnabledAsync(false);
        Assert.Multiple(() =>
        {
            Assert.That(rock.HasLogo, Is.False);
            Assert.That(viewModel.ShowRadioCatalogOptIn, Is.True);
            Assert.That(viewModel.RadioCards, Is.Empty);
        });
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalog_GenreCountrySearchAndSortFilterTheCards()
    {
        await OpenCatalogAsync(new CountingLogoLoader(), enable: true);
        viewModel.SetRadioCatalogMode(true);
        await PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.RadioCards.Select(card => card.Name), Is.EqualTo(PopularityOrderedCardNames), "catalog order is popularity order");
            Assert.That(viewModel.RadioGenres.Select(row => (row.Name, row.Count)), Is.EqualTo(new[]
                { ("All Stations", 5), ("Classical", 1), ("Jazz", 1), ("News", 1), ("Rock", 2) }));
            Assert.That(viewModel.RadioGenreChoices[0].ChoiceName, Is.EqualTo("All genres"));
            Assert.That(viewModel.RadioCountries.Select(row => row.Name), Is.EqualTo(AllCountriesNames));
        });

        viewModel.SelectedRadioGenre = viewModel.RadioGenres.Single(row => row.Genre == "Rock");
        Assert.That(viewModel.RadioCards.Select(card => card.Name), Is.EqualTo(RockCardNames));
        viewModel.SelectedRadioCountry = viewModel.RadioCountries.Single(row => row.Code == "IT");
        Assert.That(viewModel.RadioCards.Select(card => card.Name), Is.EqualTo(RockDueCardNames));
        // The genre list counts stay consistent with the other filters.
        Assert.That(viewModel.RadioGenres.Single(row => row.Genre == "Rock").Count, Is.EqualTo(1));

        viewModel.SelectedRadioGenreChoice = viewModel.RadioGenreChoices[0];
        viewModel.SelectedRadioCountry = viewModel.RadioCountries[0];
        Assert.That(viewModel.RadioCards, Has.Count.EqualTo(5));
        viewModel.RadioSortIndex = 1;
        Assert.That(viewModel.RadioCards.Select(card => card.Name), Is.EqualTo(AlphabeticalCardNames));
        viewModel.RadioSortIndex = 0;

        TextBox box = window.FindControl<TextBox>("RadioSearchBox")!;
        box.Text = "uno";
        await UntilAsync(() => viewModel.RadioCards.Count == 1);
        Assert.That(viewModel.RadioCards.Single().Name, Is.EqualTo("Jazz Uno"));
        box.Text = "germany";
        await UntilAsync(() => viewModel.RadioCards.Count == 2);
        box.Text = "nothing like this";
        await UntilAsync(() => viewModel.RadioCards.Count == 0);
        Assert.That(viewModel.IsRadioEmpty, Is.True);
        Assert.That(viewModel.RadioEmptyText, Is.EqualTo("No stations match your search or filters."));
        box.Text = "";
        await UntilAsync(() => viewModel.RadioCards.Count == 5);

        window.FindControl<TextBox>("RadioGenreSearchBox")!.Text = "ro";
        await UntilAsync(() => viewModel.RadioGenres.Select(row => row.Name).SequenceEqual(AllStationsAndRockGenres));
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalog_StarSavesOnceRemovesAndKeepsMyStationsSeparate()
    {
        await OpenCatalogAsync(new CountingLogoLoader(), enable: true);
        viewModel.SetRadioCatalogMode(true);
        await PumpAsync();
        RadioCardViewModel card = viewModel.RadioCards.Single(item => item.Name == "Jazz Uno");
        Assert.That(card.IsSaved, Is.False);
        await viewModel.ToggleRadioCardSavedAsync(card);
        Assert.Multiple(() =>
        {
            Assert.That(card.IsSaved, Is.True);
            Assert.That(card.StarTip, Is.EqualTo("Remove from My stations"));
            Assert.That(viewModel.StatusMessage, Does.Contain("Jazz Uno"));
        });
        RadioStation saved = (await repository.ListStationsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(saved.Id, Is.EqualTo(SampleCatalog[2].StationId));
            Assert.That(saved.CatalogId, Is.EqualTo("it-jazz-one"));
            Assert.That(saved.CountryCode, Is.EqualTo("IT"));
            Assert.That(saved.LogoUri, Is.EqualTo(SampleCatalog[2].Logo));
        });
        // Saving again from a stale card is idempotent.
        await viewModel.SaveCatalogRadioStationAsync(card);
        Assert.That(await repository.ListStationsAsync(), Has.Count.EqualTo(1));

        viewModel.SetRadioCatalogMode(false);
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.RadioCards.Single().IsCatalogCard, Is.False);
            Assert.That(viewModel.RadioCards.Single().IsSaved, Is.True);
            Assert.That(viewModel.RadioStations.Single().Id, Is.EqualTo(saved.Id));
            Assert.That(window.FindControl<Button>("AddRadioStationButton")!.IsEffectivelyVisible, Is.True);
        });

        viewModel.SetRadioCatalogMode(true);
        await viewModel.ToggleRadioCardSavedAsync(viewModel.RadioCards.Single(item => item.Name == "Jazz Uno"));
        Assert.That(await repository.ListStationsAsync(), Is.Empty);
        Assert.That(viewModel.RadioCards.Single(item => item.Name == "Jazz Uno").IsSaved, Is.False);
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalog_DoubleClickPlaysWithoutSavingAndEnterPlaysTheSelectedCard()
    {
        await OpenCatalogAsync(new CountingLogoLoader(), enable: true);
        viewModel.SetRadioCatalogMode(true);
        await PumpAsync();
        window.UpdateLayout();
        ListBox list = window.FindControl<ListBox>("RadioStationList")!;
        ListBoxItem item = list.GetVisualDescendants().OfType<ListBoxItem>().First(candidate => candidate.DataContext is RadioCardViewModel { Name: "Rock Due" });
        Point point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 12), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        await UntilAsync(() => engine.Snapshot.Status == PlaybackStatus.Playing && !viewModel.IsBusy);
        await viewModel.WaitForPlaybackOperationsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Snapshot.Track!.Location, Is.EqualTo(SampleCatalog[3].Stream));
            Assert.That(viewModel.IsRadioPlayback, Is.True);
            Assert.That(viewModel.DurationText, Is.EqualTo("LIVE"));
        });
        Assert.That(await repository.ListStationsAsync(), Is.Empty, "playing never saves");
        await viewModel.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.Stop));
        await CloseAsync(); await ReleaseServicesAsync();
    }

    // Picks like the user does: with the drop-down open, so the selection changes while the popup is still showing.
    private static async Task PickWithOpenDropDownAsync(ComboBox box, Func<object?, bool> choose)
    {
        box.IsDropDownOpen = true;
        await PumpAsync();
        box.SelectedItem = box.Items.Cast<object?>().First(choose);
        box.IsDropDownOpen = false;
        await PumpAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalog_PickingFromTheThreeFilterBoxes_KeepsTheChoiceVisible()
    {
        await OpenCatalogAsync(new CountingLogoLoader(), enable: true);
        viewModel.SetRadioCatalogMode(true);
        await PumpAsync();
        window.UpdateLayout();
        ComboBox genre = window.FindControl<ComboBox>("RadioGenreChoice")!, country = window.FindControl<ComboBox>("RadioCountryChoice")!,
            sort = window.FindControl<ComboBox>("RadioSortChoice")!;
        await PickWithOpenDropDownAsync(genre, item => item is RadioGenreRow { Genre: "Rock" });
        await PumpAsync();
        Assert.That(genre.SelectedItem, Is.Not.Null, "genre");
        Assert.That(((RadioGenreRow)genre.SelectedItem!).Genre, Is.EqualTo("Rock"));
        await PickWithOpenDropDownAsync(country, item => item is RadioCountryChoice { Code: "DE" });
        await PumpAsync();
        Assert.That(country.SelectedItem, Is.Not.Null, "country");
        Assert.That(((RadioCountryChoice)country.SelectedItem!).Code, Is.EqualTo("DE"));
        Assert.That(genre.SelectedItem, Is.Not.Null, "genre after country");
        await PickWithOpenDropDownAsync(sort, item => item is ComboBoxItem { Content: string name } && name == "Name");
        await PumpAsync();
        Assert.That(sort.SelectedIndex, Is.EqualTo(1), "sort");
        Assert.That(genre.SelectedItem, Is.Not.Null, "genre after sort");
        Assert.That(country.SelectedItem, Is.Not.Null, "country after sort");
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalog_PlayingAStationShowsItsLogoInNowPlayingUntilAnotherWithoutOneStarts()
    {
        await OpenCatalogAsync(new CountingLogoLoader(), enable: true);
        viewModel.SetRadioCatalogMode(true);
        await PumpAsync();
        Image cover = window.FindControl<Image>("NowPlayingCover")!;
        IImage? defaultCover = cover.Source;
        await viewModel.PlayRadioStationAsync(viewModel.RadioCards.Single(item => item.Name == "Jazz Uno").Id);
        await viewModel.WaitForPlaybackOperationsAsync();
        await UntilAsync(() => viewModel.RadioNowPlayingLogo is not null);
        Assert.That(cover.Source, Is.SameAs(viewModel.RadioNowPlayingLogo), "The station logo replaces the cover.");
        await viewModel.PlayRadioStationAsync(viewModel.RadioCards.Single(item => item.Name == "Rock Due").Id);
        await viewModel.WaitForPlaybackOperationsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.RadioNowPlayingLogo, Is.Null);
            Assert.That(cover.Source, Is.SameAs(defaultCover), "A station without a logo restores the normal cover.");
        });
        await viewModel.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.Stop));
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalog_PlayingASavedCopyRecordsHistoryOnTheSavedStation()
    {
        await OpenCatalogAsync(new CountingLogoLoader(), enable: true);
        viewModel.SetRadioCatalogMode(true);
        await PumpAsync();
        RadioCardViewModel card = viewModel.RadioCards.Single(item => item.Name == "Klassiek");
        await viewModel.ToggleRadioCardSavedAsync(card);
        await viewModel.PlayRadioStationAsync(card.Id);
        await viewModel.WaitForPlaybackOperationsAsync();
        Assert.That((await repository.ListStationsAsync()).Single().PlayCount, Is.EqualTo(1));
        await viewModel.ExecuteMediaSessionCommandAsync(new(MediaSessionAction.Stop));
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioMyStations_StillListEditAndFilterSavedStations()
    {
        await OpenCatalogAsync(new CountingLogoLoader(), enable: false);
        RadioStation mine = new(Guid.NewGuid(), "Mine", new Uri("https://radio.example.invalid/mine"), "Jazz", "Me", "My own", Country: "Brazil", CountryCode: "BR");
        await viewModel.SaveRadioStationAsync(mine, true);
        await viewModel.SelectRadioAsync();
        viewModel.SetRadioCatalogMode(false); // Radio itself opens on the catalog.
        window.UpdateLayout();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.RadioStations.Single().Id, Is.EqualTo(mine.Id));
            Assert.That(viewModel.SelectedRadioStation?.Id, Is.EqualTo(mine.Id));
            Assert.That(viewModel.SelectedRadioCard?.Id, Is.EqualTo(mine.Id));
            Assert.That(viewModel.RadioGenres.Select(row => row.Name), Is.EqualTo(AllStationsAndJazzGenres));
            Assert.That(viewModel.RadioCountries.Select(row => row.Name), Is.EqualTo(AllCountriesAndBrazil));
            Assert.That(window.FindControl<ListBox>("RadioStationList")!.GetVisualDescendants().OfType<ListBoxItem>().Single().DataContext, Is.SameAs(viewModel.RadioCards.Single()));
            Assert.That(window.FindControl<Button>("EditRadioStationButton")!.IsEnabled, Is.True);
            Assert.That(window.FindControl<Button>("AddRadioStationButton")!.IsEffectivelyVisible, Is.True);
        });
        await CloseAsync(); await ReleaseServicesAsync();
    }

    [AvaloniaTest]
    public async Task MainWindow_RadioCatalogPreferences_SwitchTurnsTheCatalogOffAndPersistsIt()
    {
        await OpenCatalogAsync(new CountingLogoLoader(), enable: true);
        window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
        await UntilAsync(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
        PreferencesDialog dialog = window.OwnedWindows.OfType<PreferencesDialog>().Single();
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();
        CheckBox box = dialog.GetVisualDescendants().OfType<CheckBox>().Single(item => item.Name == "PreferencesRadioCatalog");
        Assert.That(box.IsChecked, Is.True);
        box.IsChecked = false;
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SavePreferences").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !dialog.IsVisible && !viewModel.RadioCatalogEnabled);
        Assert.That((await settings.LoadAsync()).RadioCatalogEnabled, Is.False);
        viewModel.SetRadioCatalogMode(true);
        Assert.That(viewModel.ShowRadioCatalogOptIn, Is.True);
        await CloseAsync(); await ReleaseServicesAsync();
    }
}
