using Aibell.Application.Settings;
using Aibell.Domain.Playback;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Settings;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Settings;

[TestFixture]
public sealed class JsonApplicationSettingsStoreTests
{
    [Test]
    public async Task SaveAndLoadAsync_Equalizer_RoundTripsAndIsNormalized()
    {
        using JsonApplicationSettingsStore store = new(settingsPath);
        await store.SaveAsync(ApplicationPreferences.Default with
        {
            Equalizer = new EqualizerSettings { IsEnabled = true, Preamp = -3, Bands = [6, 5, 4, 2, 0, 0, 0, 0, 0, 99] }
        });

        EqualizerSettings loaded = (await store.LoadAsync()).Equalizer!;

        Assert.That(loaded.IsEnabled, Is.True);
        Assert.That(loaded.Preamp, Is.EqualTo(-3));
        Assert.That(loaded.Bands, Is.EqualTo(new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 12 }));
    }

    [Test]
    public async Task LoadAsync_OlderFileWithoutRadioCatalog_KeepsTheCatalogOff()
    {
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(settingsPath, "{ \"Volume\": 0.5 }");
        using JsonApplicationSettingsStore store = new(settingsPath);

        Assert.That((await store.LoadAsync()).RadioCatalogEnabled, Is.False);
        Assert.That(ApplicationPreferences.Default.RadioCatalogEnabled, Is.False);
    }

    [Test]
    public async Task SaveAndLoadAsync_RadioCatalogChoice_RoundTrips()
    {
        using JsonApplicationSettingsStore store = new(settingsPath);
        await store.SaveAsync(ApplicationPreferences.Default with { RadioCatalogEnabled = true });
        Assert.That((await store.LoadAsync()).RadioCatalogEnabled, Is.True);
        await store.SaveAsync(ApplicationPreferences.Default with { RadioCatalogEnabled = false });
        Assert.That((await store.LoadAsync()).RadioCatalogEnabled, Is.False);
    }

    [Test]
    public async Task LoadAsync_FileWithoutEqualizer_LoadsWithNoEqualizer()
    {
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(settingsPath, "{ \"Volume\": 0.5 }");
        using JsonApplicationSettingsStore store = new(settingsPath);

        ApplicationPreferences loaded = await store.LoadAsync();

        Assert.That(loaded.Volume, Is.EqualTo(0.5));
        Assert.That(loaded.Equalizer, Is.Null);
    }

    [Test]
    public async Task SaveAndLoadAsync_RepeatedQueueTracks_RetainsOrderIdentityAndMetadata()
    {
        Track track = new(Guid.NewGuid(), new Uri(Path.Combine(Path.GetTempPath(), "external # ü.flac")),
            "External", "Artist", "Album", TimeSpan.FromMinutes(3), Rating: 4);
        PlayQueueEntry[] entries = [new(Guid.NewGuid(), track), new(Guid.NewGuid(), track)];
        using (JsonApplicationSettingsStore store = new(settingsPath))
        {
            await store.SaveAsync(ApplicationPreferences.Default with { PlayQueue = entries });
        }
        using JsonApplicationSettingsStore reopened = new(settingsPath);

        Assert.That((await reopened.LoadAsync()).PlayQueue, Is.EqualTo(entries));
        Assert.That(Directory.GetFiles(temporaryDirectory), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task LoadAsync_InvalidQueueEntries_KeepsValidRequestsAndOtherPreferences()
    {
        Track track = new(Guid.NewGuid(), new Uri("file:///music/external.flac"),
            "External", "Artist", "Album", TimeSpan.FromMinutes(3));
        PlayQueueEntry valid = new(Guid.NewGuid(), track);
        PlayQueueEntry repeated = new(Guid.NewGuid(), track);
        using JsonApplicationSettingsStore store = new(settingsPath);
        // Write directly to exercise deserialization of damaged, structurally valid JSON.
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(settingsPath, System.Text.Json.JsonSerializer.Serialize(
            ApplicationPreferences.Default with
            {
                Volume = 0.4,
                PlayQueue = [null!, new(Guid.Empty, track), new(Guid.NewGuid(), null!),
                    new(Guid.NewGuid(), track with { Location = null! }),
                    valid, valid, repeated]
            }));

        ApplicationPreferences actual = await store.LoadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(actual.Volume, Is.EqualTo(0.4));
            Assert.That(actual.PlayQueue, Is.EqualTo(new[] { valid, repeated }));
        });
    }

    [Test]
    public async Task SaveAndLoadAsync_Language_RoundTripsAndUnknownOrBlankMeansSystem()
    {
        using JsonApplicationSettingsStore store = new(settingsPath);
        await store.SaveAsync(ApplicationPreferences.Default with { Language = " pt-BR " });
        Assert.That((await store.LoadAsync()).Language, Is.EqualTo("pt-BR"));
        await store.SaveAsync(ApplicationPreferences.Default with { Language = "not a language!" });
        Assert.That((await store.LoadAsync()).Language, Is.Null);
        await store.SaveAsync(ApplicationPreferences.Default with { Language = "  " });
        Assert.That((await store.LoadAsync()).Language, Is.Null);
    }

    [Test]
    public async Task SaveAndLoadAsync_SoundFontPath_RoundTripsAndRelativeOrBlankMeansNone()
    {
        string bank = Path.Combine(Path.GetTempPath(), "bank.sf2");
        using JsonApplicationSettingsStore store = new(settingsPath);
        await store.SaveAsync(ApplicationPreferences.Default with { SoundFontPath = bank });
        Assert.That((await store.LoadAsync()).SoundFontPath, Is.EqualTo(bank));
        await store.SaveAsync(ApplicationPreferences.Default with { SoundFontPath = "relative/bank.sf2" });
        Assert.That((await store.LoadAsync()).SoundFontPath, Is.Null);
        await store.SaveAsync(ApplicationPreferences.Default with { SoundFontPath = "  " });
        Assert.That((await store.LoadAsync()).SoundFontPath, Is.Null);
    }

    [TestCase(PlaybackEngineKind.Miniaudio)]
    [TestCase(PlaybackEngineKind.Vlc)]
    public async Task SaveAndLoadAsync_PlaybackEngine_PreservesChoice(PlaybackEngineKind engine)
    {
        using JsonApplicationSettingsStore store = new(settingsPath);
        await store.SaveAsync(ApplicationPreferences.Default with { PlaybackEngine = engine });
        Assert.That((await store.LoadAsync()).PlaybackEngine, Is.EqualTo(engine));
    }

    [Test]
    public async Task LoadAsync_OldOrUnknownPlaybackEngine_UsesThePreferredPlayer()
    {
        Directory.CreateDirectory(temporaryDirectory);
        using JsonApplicationSettingsStore store = new(settingsPath);
        await File.WriteAllTextAsync(settingsPath, "{\"Volume\":0.6}");
        Assert.That((await store.LoadAsync()).PlaybackEngine, Is.EqualTo(PlaybackEngineDefaults.Preferred), "settings from before the choice existed");
        await File.WriteAllTextAsync(settingsPath, "{\"Volume\":0.6,\"PlaybackEngine\":99}");
        Assert.That((await store.LoadAsync()).PlaybackEngine, Is.EqualTo(PlaybackEngineDefaults.Preferred));
    }

    [TestCase(ApplicationTheme.System)]
    [TestCase(ApplicationTheme.Light)]
    [TestCase(ApplicationTheme.Dark)]
    public async Task SaveAndLoadAsync_Appearance_PreservesChoice(ApplicationTheme theme)
    {
        using JsonApplicationSettingsStore store = new(settingsPath);
        await store.SaveAsync(ApplicationPreferences.Default with { Theme = theme });
        Assert.That((await store.LoadAsync()).Theme, Is.EqualTo(theme));
    }

    [Test]
    public async Task LoadAsync_OldOrUnknownAppearance_UsesSystemTheme()
    {
        Directory.CreateDirectory(temporaryDirectory);
        using JsonApplicationSettingsStore store = new(settingsPath);
        await File.WriteAllTextAsync(settingsPath, "{\"Volume\":0.6,\"Theme\":99}");
        Assert.That((await store.LoadAsync()).Theme, Is.EqualTo(ApplicationTheme.System));
        await File.WriteAllTextAsync(settingsPath, "{\"Volume\":0.6}");
        Assert.That((await store.LoadAsync()).Theme, Is.EqualTo(ApplicationTheme.System));
        Assert.That((await store.LoadAsync()).Volume, Is.EqualTo(0.6));
    }

    [Test]
    public async Task PodcastFolders_RoundTripUnrelatedSaveAndCancellation_KeepChoiceAndHistory()
    {
        using JsonApplicationSettingsStore store = new(settingsPath);
        string current = Path.Combine(Path.GetTempPath(), "podcast-current");
        string previous = Path.Combine(Path.GetTempPath(), "podcast-old");
        await store.SaveAsync(ApplicationPreferences.Default with
        { PodcastDownloadFolder = current, PodcastDownloadFolderHistory = [previous, previous, "relative", "bad\0path"] });
        ApplicationPreferences preferences = await store.LoadAsync();
        await store.SaveAsync(preferences with { Volume = 0.4 });
        preferences = await store.LoadAsync();
        Assert.That(preferences.PodcastDownloadFolder, Is.EqualTo(current));
        Assert.That(preferences.PodcastDownloadFolderHistory, Is.EqualTo(new[] { previous }));
        byte[] prior = await File.ReadAllBytesAsync(settingsPath);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.That(async () => await store.SaveAsync(preferences with { PodcastDownloadFolder = previous }, canceled.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await File.ReadAllBytesAsync(settingsPath), Is.EqualTo(prior));
        Assert.That(Directory.GetFiles(temporaryDirectory), Has.Length.EqualTo(1));
    }

    private string temporaryDirectory = null!;
    private string settingsPath = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aibell-settings-{Guid.NewGuid():N}");
        settingsPath = Path.Combine(temporaryDirectory, "settings.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task SaveAndLoadAsync_RoundTripsPlaybackPreferences()
    {
        using JsonApplicationSettingsStore store = new(settingsPath);
        ApplicationPreferences expected = new(
            0.35,
            true,
            true,
            RepeatMode.One,
            [
                new TrackColumnPreference("Artist", true),
                new TrackColumnPreference("Location", false)
            ]);

        await store.SaveAsync(expected);
        ApplicationPreferences actual = await store.LoadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(actual with { TrackColumns = null }, Is.EqualTo(expected with { TrackColumns = null }));
            Assert.That(actual.TrackColumns, Is.EqualTo(expected.TrackColumns));
        });
    }

    [Test]
    public async Task SaveAndLoadAsync_RoundTripsQueuePanelLayout()
    {
        using JsonApplicationSettingsStore store = new(settingsPath);
        await store.SaveAsync(ApplicationPreferences.Default with
        {
            IsPlayQueueVisible = false,
            PlayQueueWidth = 340
        });

        ApplicationPreferences actual = await store.LoadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(actual.IsPlayQueueVisible, Is.False);
            Assert.That(actual.PlayQueueWidth, Is.EqualTo(340));
        });
    }

    [Test]
    public async Task LoadAsync_OldSettings_UsesDefaultQueueLayout()
    {
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(settingsPath,
            """{"Volume":0.8,"IsMuted":false,"ShuffleEnabled":false,"RepeatMode":0}""");
        using JsonApplicationSettingsStore store = new(settingsPath);

        ApplicationPreferences actual = await store.LoadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(actual.IsPlayQueueVisible, Is.True);
            Assert.That(actual.PlayQueueWidth, Is.EqualTo(260));
            Assert.That(actual.PlayQueue, Is.Null);
            Assert.That(actual.ShowDesktopNotifications, Is.False);
        });
    }

    [TestCase(50, 200)]
    [TestCase(900, 480)]
    public async Task SaveAsync_OutOfRangeQueueWidth_ClampsToUsableRange(double width, double expected)
    {
        using JsonApplicationSettingsStore store = new(settingsPath);
        await store.SaveAsync(ApplicationPreferences.Default with { PlayQueueWidth = width });

        Assert.That((await store.LoadAsync()).PlayQueueWidth, Is.EqualTo(expected));
    }

    [Test]
    public async Task LoadAsync_MalformedFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(settingsPath, "{not-json");
        using JsonApplicationSettingsStore store = new(settingsPath);

        ApplicationPreferences actual = await store.LoadAsync();

        Assert.That(actual, Is.EqualTo(ApplicationPreferences.Default));
    }

    [Test]
    public async Task SaveAsync_OutOfRangeVolume_NormalizesValue()
    {
        using JsonApplicationSettingsStore store = new(settingsPath);

        await store.SaveAsync(new ApplicationPreferences(4, false, false, RepeatMode.All));
        ApplicationPreferences actual = await store.LoadAsync();

        Assert.That(actual.Volume, Is.EqualTo(1));
    }

    [Test]
    public async Task SaveAsync_FractionalVolume_UsesPlaybackEnginePrecision()
    {
        using JsonApplicationSettingsStore store = new(settingsPath);

        await store.SaveAsync(new ApplicationPreferences(0.426, false, false, RepeatMode.Off));
        ApplicationPreferences actual = await store.LoadAsync();

        Assert.That(actual.Volume, Is.EqualTo(0.43));
    }
}
