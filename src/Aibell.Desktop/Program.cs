using Avalonia;
using Aibell.Application.Audiobooks;
using Aibell.Infrastructure.Audiobooks;
using Aibell.Application.Podcasts;
using Aibell.Infrastructure.Podcasts;
using Aibell.Application.Devices;
using Aibell.Application.Discs;
using Aibell.Application.Enrichment;
using Aibell.Application.Plugins;
using Aibell.Application.Library;
using Aibell.Application.Radio;
using Aibell.Application.Playback;
using Aibell.Application.Playlists;
using Aibell.Application.Settings;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using Aibell.Desktop.Services;
using Aibell.Infrastructure.Devices;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Playlists;
using Aibell.Playback.Alsa;
using Aibell.Playback.PipeWire;
using Aibell.Infrastructure.Metadata;
using Aibell.Infrastructure.Plugins;
using Aibell.Infrastructure.Storage;
using Aibell.Infrastructure.Settings;
using Aibell.Infrastructure.Radio;
using Aibell.Playback.LibVLC;
using Aibell.Playback.Miniaudio;
using Aibell.Platform.Linux;
using Aibell.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aibell.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Ahead of everything else: a second launch hands its files to the running window and exits without starting a host.
        string instanceDirectory = ResolveDataDirectory(create: false);
        using SingleInstance? instance = SingleInstance.TryBecomePrimary(instanceDirectory);
        if (instance is null && SingleInstance.TrySend(instanceDirectory,
                StartupFiles.Resolve(args, Environment.CurrentDirectory).Select(file => file.LocalPath), TimeSpan.FromSeconds(3)))
            return;
        App.Instance = instance;
        // Before any window exists, so the shell labels the media card and taskbar entry "Aibell" instead of the .exe name.
        if (OperatingSystem.IsWindows()) WindowsAppIdentity.Register("Aibell.Desktop", "Aibell");
        // AIBELL_LANGUAGE wins; otherwise the language chosen in Preferences; otherwise the operating system's.
        string? environmentLanguage = Environment.GetEnvironmentVariable("AIBELL_LANGUAGE");
        Aibell.Desktop.Localization.Loc.ApplyLanguageOverride(string.IsNullOrWhiteSpace(environmentLanguage) ? ReadSavedLanguage() : environmentLanguage);
        using IHost host = CreateHost(args);
        host.Start();
        App.Services = host.Services;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        host.StopAsync().GetAwaiter().GetResult();
        // Reaching this line is a clean shutdown; a crash or kill leaves the marker for the next start to report.
        host.Services.GetRequiredService<SessionMonitor>().End();
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Menus and drop-downs are drawn inside the window. Native popup windows are placed from the window's screen position,
            // which Wayland compositors (through XWayland) do not report, so the main menu opened over its own bar.
            .With(new X11PlatformOptions { OverlayPopups = true })
            .LogToTrace();
    }

    // Startup only normalizes the saved folder (no probing, nothing created), so an unplugged drive cannot block launch.
    private static PodcastDownloadStore CreatePodcastDownloadStore(string settingsPath, string defaultRoot)
    {
        try
        {
            using JsonApplicationSettingsStore settings = new(settingsPath);
            ApplicationPreferences saved = settings.LoadAsync().GetAwaiter().GetResult();
            return new PodcastDownloadStore(saved.PodcastDownloadFolder ?? defaultRoot, saved.PodcastDownloadFolderHistory);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new PodcastDownloadStore(defaultRoot);
        }
    }

    private static string ResolveDataDirectory(bool create)
    {
        string? configured = Environment.GetEnvironmentVariable("AIBELL_DATA_DIRECTORY");
        string local = string.IsNullOrWhiteSpace(configured)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                create ? Environment.SpecialFolderOption.Create : Environment.SpecialFolderOption.DoNotVerify) : "";
        return ApplicationPaths.ResolveDataDirectory(configured, local);
    }

    private static string? ReadSavedLanguage()
    {
        try
        {
            using JsonApplicationSettingsStore settings = new(Path.Combine(ResolveDataDirectory(create: false), "settings.json"));
            return settings.LoadAsync().GetAwaiter().GetResult().Language;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static IHost CreateHost(string[] args)
    {
        // The content root defaults to the working directory, and the host watches it recursively for appsettings changes.
        // Launched from a home folder, that meant watching every directory in it and took many seconds. Pin it to the install folder.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        string dataDirectory = ResolveDataDirectory(create: true);
        Directory.CreateDirectory(dataDirectory);
        string databasePath = Path.Combine(dataDirectory, "library.db");

        builder.Services.AddSingleton(new ApplicationErrorReporter(Path.Combine(dataDirectory, "logs")));
        // Started here so a crash from the very first track is noticed; ended on a clean shutdown (see Main).
        SessionMonitor sessionMonitor = new(Path.Combine(dataDirectory, "logs"));
        sessionMonitor.Begin();
        builder.Services.AddSingleton(sessionMonitor);
        builder.Services.AddSingleton<IMediaFileDiscovery, FileSystemMediaDiscovery>();
        builder.Services.AddSingleton<IMediaMetadataReader, AtlMediaMetadataReader>();
        builder.Services.AddSingleton<ITrackFileDetailsReader, AtlTrackFileDetailsReader>();
        builder.Services.AddSingleton<IMetadataEditStager, AtlMetadataEditStager>();
        builder.Services.AddSingleton<IMetadataFileWriter, MetadataFileWriter>();
        builder.Services.AddSingleton<IMetadataEditRecovery, MetadataEditRecovery>();
        builder.Services.AddSingleton<IArtworkProvider>(
            new LocalArtworkCache(Path.Combine(dataDirectory, "artwork")));
        // Online metadata comes only from plugins found in the plugins folder next to the application. Without them nothing
        // here can reach the network, and with them nothing is sent until the user allows lookups and presses Look up.
        string bundledPlugins = Path.Combine(AppContext.BaseDirectory, "plugins");
        string userPlugins = Path.Combine(dataDirectory, "plugins");
        PluginStateStore pluginState = new(Path.Combine(dataDirectory, "plugin-state.json"));
        pluginState.ApplyPendingRemovals(userPlugins);
        PluginLoadReport plugins = PluginLoader.LoadAll([new PluginSource(bundledPlugins, IsUserInstalled: false), new PluginSource(userPlugins, IsUserInstalled: true)],
            new PluginContext(dataDirectory), pluginState.Disabled);
        builder.Services.AddSingleton<IPluginAdministration>(new PluginAdministration(bundledPlugins, userPlugins, pluginState, plugins));
        foreach (LoadedPlugin plugin in plugins.Plugins)
        {
            foreach (IEnrichmentProvider provider in plugin.Contributions.EnrichmentProviders) builder.Services.AddSingleton(provider);
            foreach (IReleaseLookupProvider provider in plugin.Contributions.ReleaseProviders) builder.Services.AddSingleton(provider);
            foreach (IArtistInfoProvider provider in plugin.Contributions.ArtistProviders) builder.Services.AddSingleton(provider);
        }
        builder.Services.AddSingleton(plugins);
        builder.Services.AddSingleton<IEnrichmentCoordinator, EnrichmentCoordinator>();
        builder.Services.AddSingleton<IMetadataLookupService>(services => new MetadataLookupService(
            services.GetServices<IReleaseLookupProvider>(), services.GetServices<IArtistInfoProvider>(),
            () => services.GetRequiredService<MainWindowViewModel>().OnlineLookupAllowed));
        builder.Services.AddSingleton<IOpticalDriveService>(_ => OperatingSystem.IsWindows() ? new WindowsOpticalDriveService() : new LinuxOpticalDriveService());
        builder.Services.AddSingleton<Func<IDiscPlaybackHandoff, DiscViewModel>>(services => handoff =>
            new DiscViewModel(services.GetRequiredService<IOpticalDriveService>(), services.GetRequiredService<IMetadataLookupService>(),
                handoff, services.GetRequiredService<CoverImageCache>()));
        builder.Services.AddSingleton<ICoverArtDownloader>(_ => new CoverArtDownloader(new HttpClient(new SocketsHttpHandler { MaxAutomaticRedirections = 5 })));
        builder.Services.AddSingleton<IAlbumCoverWriter, AlbumCoverFolderWriter>();
        builder.Services.AddSingleton<Func<Track, ArtworkWindow>>(services => track =>
            new ArtworkWindow(new ArtworkLookupViewModel(track, services.GetRequiredService<IMetadataLookupService>(),
                services.GetRequiredService<ICoverArtDownloader>(), services.GetRequiredService<IAlbumCoverWriter>(), services.GetRequiredService<CoverImageCache>())));
        builder.Services.AddSingleton<IRemovableVolumeService>(_ => OperatingSystem.IsWindows() ? new WindowsRemovableVolumeService() : new LinuxRemovableVolumeService());
        builder.Services.AddSingleton<IDeviceSyncService, DeviceSyncService>();
        builder.Services.AddSingleton<Func<IDeviceTrackSource, DeviceSyncViewModel>>(services => source =>
            new DeviceSyncViewModel(services.GetRequiredService<IRemovableVolumeService>(), services.GetRequiredService<IDeviceSyncService>(), source));
        builder.Services.AddSingleton(_ => new SqliteTrackRepository(databasePath));
        builder.Services.AddSingleton<ITrackRepository>(services =>
            services.GetRequiredService<SqliteTrackRepository>());
        builder.Services.AddSingleton<ILibraryBrowser>(services =>
            services.GetRequiredService<SqliteTrackRepository>());
        builder.Services.AddSingleton<ILibrarySourceRepository>(services =>
            services.GetRequiredService<SqliteTrackRepository>());
        builder.Services.AddSingleton<IPlaylistRepository>(services =>
            services.GetRequiredService<SqliteTrackRepository>());
        builder.Services.AddSingleton<IApplicationSettingsStore>(
            new JsonApplicationSettingsStore(Path.Combine(dataDirectory, "settings.json")));
        builder.Services.AddSingleton<LibraryImportService>();
        builder.Services.AddSingleton<IPlaylistFileStore, PlaylistFileStore>();
        builder.Services.AddSingleton<IRadioStationRepository>(services => services.GetRequiredService<SqliteTrackRepository>());
        builder.Services.AddSingleton<IRadioPlaylistFileStore, RadioPlaylistFileStore>();
        builder.Services.AddSingleton<RadioPlaylistImportService>();
        builder.Services.AddSingleton<ICuratedStationCatalog, EmbeddedCuratedStationCatalog>();
        builder.Services.AddSingleton<IPlaylistImportDestination>(services => services.GetRequiredService<SqliteTrackRepository>());
        builder.Services.AddSingleton<PlaylistFileService>();
        builder.Services.AddSingleton<IPodcastRepository>(services => services.GetRequiredService<SqliteTrackRepository>());
        builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        })
        { Timeout = Timeout.InfiniteTimeSpan });
        builder.Services.AddSingleton<IPodcastFeedReader>(services => new HttpPodcastFeedReader(services.GetRequiredService<HttpClient>()));
        builder.Services.AddSingleton<IPodcastEpisodeDownloader>(services => new HttpPodcastEpisodeDownloader(services.GetRequiredService<HttpClient>()));
        string defaultPodcastRoot = Path.Combine(dataDirectory, "podcasts");
        builder.Services.AddSingleton(_ => CreatePodcastDownloadStore(Path.Combine(dataDirectory, "settings.json"), defaultPodcastRoot));
        builder.Services.AddSingleton(services => new PodcastService(services.GetRequiredService<IPodcastRepository>(),
            services.GetRequiredService<IPodcastFeedReader>(), services.GetRequiredService<IPodcastEpisodeDownloader>(),
            services.GetRequiredService<PodcastDownloadStore>(),
            positions: services.GetRequiredService<SqliteTrackRepository>(), metadata: services.GetRequiredService<IMediaMetadataReader>()));
        builder.Services.AddSingleton(services => new CoverImageCache(services.GetRequiredService<HttpClient>(), Path.Combine(dataDirectory, "podcast-covers")));
        builder.Services.AddSingleton<ICoverImageLoader>(services => services.GetRequiredService<CoverImageCache>());
        builder.Services.AddSingleton<Func<IPodcastPlaybackHandoff, PodcastsViewModel>>(services => handoff =>
            new PodcastsViewModel(services.GetRequiredService<PodcastService>(), handoff, services.GetRequiredService<CoverImageCache>()));
        builder.Services.AddSingleton<IAudiobookShelfRepository>(services => services.GetRequiredService<SqliteTrackRepository>());
        builder.Services.AddSingleton<IAudiobookFiles, AudiobookFiles>();
        builder.Services.AddSingleton<IAudiobookChapterReader, AtlAudiobookChapterReader>();
        builder.Services.AddSingleton<AudiobookShelfService>();
        builder.Services.AddSingleton<Func<IAudiobookPlaybackHandoff, AudiobooksViewModel>>(services => handoff =>
            new AudiobooksViewModel(services.GetRequiredService<AudiobookShelfService>(), handoff));
        PlaybackEngineKind engineKind = PlaybackEngineChooser.Choose(Path.Combine(dataDirectory, "settings.json"), dataDirectory, MiniaudioRuntime.IsAvailable);
        builder.Services.AddSingleton(new ActivePlaybackEngine(engineKind));
        string soundFontPath = PlaybackEngineChooser.SavedSoundFont(Path.Combine(dataDirectory, "settings.json")) ?? "";
        if (engineKind is PlaybackEngineKind.Miniaudio or PlaybackEngineKind.Managed)
            builder.Services.AddSingleton<IPlaybackEngine>(services => new MiniaudioPlaybackEngine(services.GetRequiredService<HttpClient>(), engineKind == PlaybackEngineKind.Managed,
                soundFontPath,
                services.GetRequiredService<IOpticalDriveService>()));
        else if (engineKind == PlaybackEngineKind.PipeWire)
            builder.Services.AddSingleton<IPlaybackEngine>(services => new PipeWirePlaybackEngine(services.GetRequiredService<HttpClient>(),
                soundFontPath,
                services.GetRequiredService<IOpticalDriveService>()));
        else if (engineKind == PlaybackEngineKind.Alsa)
            builder.Services.AddSingleton<IPlaybackEngine>(services => new AlsaPlaybackEngine(services.GetRequiredService<HttpClient>(),
                soundFontPath,
                services.GetRequiredService<IOpticalDriveService>()));
        else
            builder.Services.AddSingleton<IPlaybackEngine, LibVlcPlaybackEngine>();
        if (OperatingSystem.IsLinux())
        {
            builder.Services.AddSingleton<IMediaSession>(_ => new MprisMediaSession());
            builder.Services.AddSingleton<IDesktopNotifications>(_ => new LinuxDesktopNotifications());
        }
        else if (OperatingSystem.IsWindows() && WindowsMediaSession.IsSupported)
        {
            builder.Services.AddSingleton<IMediaSession>(_ => new WindowsMediaSession());
        }
        if (OperatingSystem.IsWindows() && WindowsDesktopNotifications.IsSupported)
        {
            builder.Services.AddSingleton<IDesktopNotifications>(_ => new WindowsDesktopNotifications());
        }
        builder.Services.AddSingleton<LibraryMonitoringService>();
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<LibraryMonitoringService>());
        builder.Services.AddSingleton(services =>
        {
            MainWindowViewModel model = ActivatorUtilities.CreateInstance<MainWindowViewModel>(services);
            services.GetRequiredService<ApplicationErrorReporter>().Reported += model.ShowUnexpectedError;
            model.ConfigureErrorReporting(services.GetRequiredService<ApplicationErrorReporter>(), services.GetRequiredService<SessionMonitor>());
            model.ConfigurePlaybackEngine(services.GetRequiredService<ActivePlaybackEngine>().Kind);
            model.ConfigurePodcastDownloads(services.GetRequiredService<PodcastDownloadStore>(), defaultPodcastRoot);
            model.ConfigurePodcastProgress(services.GetRequiredService<PodcastService>());
            model.ConfigurePodcastAutoRefresh(services.GetRequiredService<PodcastService>());
            model.ConfigurePlugins(services.GetRequiredService<IPluginAdministration>());
            return model;
        });
        builder.Services.AddSingleton(services => new MainWindow(services.GetRequiredService<MainWindowViewModel>(),
            services.GetService<IMediaSession>(), podcastViewModelFactory: services.GetRequiredService<Func<IPodcastPlaybackHandoff, PodcastsViewModel>>(),
            audiobooksViewModelFactory: services.GetRequiredService<Func<IAudiobookPlaybackHandoff, AudiobooksViewModel>>(),
            discViewModelFactory: services.GetRequiredService<Func<IDiscPlaybackHandoff, DiscViewModel>>(),
            deviceSyncViewModelFactory: services.GetRequiredService<Func<IDeviceTrackSource, DeviceSyncViewModel>>(),
            artworkWindowFactory: services.GetRequiredService<Func<Track, ArtworkWindow>>()));
        return builder.Build();
    }
}
