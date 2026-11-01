using System.Reflection;
using System.Runtime.InteropServices;
using Aibell.Application.Plugins;
using Aibell.Application.Settings;
using Aibell.Desktop.Localization;
using Aibell.Desktop.ViewModels;
using Aibell.Playback.Alsa;
using Aibell.Playback.Miniaudio;
using Aibell.Playback.PipeWire;

namespace Aibell.Desktop;

/// <summary>How a row on the About window's Features tab is drawn: a green dot for something that works, a grey one for something that does not.</summary>
public enum AboutState { Available, Unavailable }

/// <summary>One line of the Features tab: a capability and its state in this run.</summary>
public sealed record AboutRow(string Label, string Value, AboutState State);

public sealed record AboutSection(string Title, IReadOnlyList<AboutRow> Rows);

/// <summary>
/// Everything the About window shows, gathered once when it opens so the window itself stays a plain view (and testable with
/// made-up data). The version, author and copyright come from the assembly, which gets them from <c>Directory.Build.props</c>.
/// </summary>
public sealed record AboutInfo(string Name, string Version, string Author, string Copyright, string License, IReadOnlyList<AboutSection> Features)
{
    public static readonly Uri ProjectUri = new("https://github.com/tiglate/Aibell");

    /// <param name="hasMediaSession">Whether MPRIS (Linux) or the system media controls (Windows) are connected.</param>
    /// <param name="hasDisc">Whether the Audio CD view is wired up in this host.</param>
    /// <param name="hasDevices">Whether the Devices view is wired up in this host.</param>
    /// <param name="hasPodcasts">Whether the Podcasts view is wired up in this host.</param>
    /// <param name="hasAudiobooks">Whether the Audiobooks view is wired up in this host.</param>
    public static AboutInfo Collect(MainWindowViewModel viewModel, bool hasMediaSession, bool hasDisc = true, bool hasDevices = true, bool hasPodcasts = true, bool hasAudiobooks = true)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Assembly assembly = typeof(AboutInfo).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "";
        int build = version.IndexOf('+', StringComparison.Ordinal);
        if (build >= 0) version = version[..build];
        return new AboutInfo(
            "Aibell",
            version,
            assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Tiglate Pileser III",
            assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "",
            "MIT",
            [
                PlaybackSection(viewModel.ActivePlaybackEngine, viewModel.CanUseEqualizer),
                DesktopSection(hasMediaSession, viewModel.CanShowDesktopNotifications),
                ContentSection(viewModel, hasDisc, hasDevices, hasPodcasts, hasAudiobooks),
                SystemSection()
            ]);
    }

    private static AboutRow Row(string label, bool available) =>
        Row(label, available, Loc.Get("About.Status.Available"), Loc.Get("About.Status.Unavailable"));

    private static AboutRow Row(string label, bool available, string whenAvailable, string whenNot) =>
        new(label, available ? whenAvailable : whenNot, available ? AboutState.Available : AboutState.Unavailable);

    private static AboutSection PlaybackSection(PlaybackEngineKind active, bool equalizer)
    {
        string engine = active switch
        {
            PlaybackEngineKind.Vlc => Loc.Get("About.Engine.Vlc"),
            PlaybackEngineKind.Managed => Loc.Get("About.Engine.Managed"),
            PlaybackEngineKind.PipeWire => Loc.Get("About.Engine.PipeWire"),
            PlaybackEngineKind.Alsa => Loc.Get("About.Engine.Alsa"),
            _ => Loc.Get("About.Engine.Miniaudio")
        };
        bool builtIn = MiniaudioRuntime.IsAvailable(out _);
        AboutRow builtInRow = active is PlaybackEngineKind.Miniaudio or PlaybackEngineKind.Managed && builtIn
            ? new(Loc.Get("About.Engine.Miniaudio"), Loc.Get("About.Status.InUse"), AboutState.Available)
            : Row(Loc.Get("About.Engine.Miniaudio"), builtIn);
        AboutRow vlcRow = active == PlaybackEngineKind.Vlc
            ? new(Loc.Get("About.Engine.Vlc"), Loc.Get("About.Status.InUse"), AboutState.Available)
            : Row(Loc.Get("About.Engine.Vlc"), VlcInstalled(), Loc.Get("About.Status.VlcFound"), Loc.Get("About.Status.VlcMissing"));
        bool pipeWireAvailable = PipeWireRuntime.IsAvailable(out _);
        AboutRow pipeWireRow = active == PlaybackEngineKind.PipeWire
            ? new(Loc.Get("About.Engine.PipeWire"), Loc.Get("About.Status.InUse"), AboutState.Available)
            : Row(Loc.Get("About.Engine.PipeWire"), pipeWireAvailable, Loc.Get("About.Status.Available"), Loc.Get("About.Status.Unavailable"));
        bool alsaAvailable = AlsaRuntime.IsAvailable(out _);
        AboutRow alsaRow = active == PlaybackEngineKind.Alsa
            ? new(Loc.Get("About.Engine.Alsa"), Loc.Get("About.Status.InUse"), AboutState.Available)
            : Row(Loc.Get("About.Engine.Alsa"), alsaAvailable, Loc.Get("About.Status.Available"), Loc.Get("About.Status.Unavailable"));
        return new AboutSection(Loc.Get("About.Section.Playback"),
        [
            new AboutRow(Loc.Get("About.Engine"), engine, AboutState.Available),
            builtInRow,
            pipeWireRow,
            alsaRow,
            vlcRow,
            Row(Loc.Get("Equalizer.Title"), equalizer)
        ]);
    }

    private static AboutSection DesktopSection(bool mediaSession, bool notifications) => new(Loc.Get("About.Section.Desktop"),
    [
        Row(Loc.Get("About.Feature.MediaControls"), mediaSession),
        Row(Loc.Get("About.Feature.Notifications"), notifications)
    ]);

    private static AboutSection ContentSection(MainWindowViewModel viewModel, bool disc, bool devices, bool podcasts, bool audiobooks)
    {
        List<AboutRow> rows =
        [
            Row(Loc.Get("Sidebar.Radio"), viewModel.CanUseRadio),
            Row(Loc.Get("About.Feature.Catalog"), viewModel.CanUseRadioCatalog),
            Row(Loc.Get("Sidebar.Podcasts"), podcasts),
            Row(Loc.Get("Sidebar.Audiobooks"), audiobooks),
            Row(Loc.Get("Sidebar.AudioCd"), disc),
            Row(Loc.Get("Sidebar.Devices"), devices),
            Row(Loc.Get("About.Feature.OnlineLookup"), viewModel.OnlineLookupAllowed, Loc.Get("About.Status.On"), Loc.Get("About.Status.Off"))
        ];
        IReadOnlyList<PluginEntry> plugins = viewModel.PluginAdministration?.List() ?? [];
        string[] running = [.. plugins.Where(plugin => plugin.IsRunning).Select(plugin => plugin.Name)];
        rows.Add(running.Length > 0
            ? new AboutRow(Loc.Get("About.Feature.Plugins"), string.Join(", ", running), AboutState.Available)
            : new AboutRow(Loc.Get("About.Feature.Plugins"), Loc.Get("About.Status.None"), AboutState.Unavailable));
        return new AboutSection(Loc.Get("About.Section.Content"), rows);
    }

    private static AboutSection SystemSection() => new(Loc.Get("About.Section.System"),
    [
        new AboutRow(".NET", RuntimeInformation.FrameworkDescription.Replace(".NET ", "", StringComparison.Ordinal), AboutState.Available),
        new AboutRow("Avalonia", typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3) ?? "", AboutState.Available),
        new AboutRow(Loc.Get("About.System.Os"), RuntimeInformation.OSDescription, AboutState.Available),
        new AboutRow(Loc.Get("About.System.Language"), System.Globalization.CultureInfo.CurrentUICulture.NativeName, AboutState.Available)
    ]);

    // Looks for the library without starting it: VLC is only initialized when the user chooses it as the player.
    private static bool VlcInstalled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string? configured = Environment.GetEnvironmentVariable("AIBELL_LIBVLC_PATH");
                string folder = !string.IsNullOrWhiteSpace(configured) ? configured
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC");
                return File.Exists(Path.Combine(folder, "libvlc.dll"));
            }
            foreach (string name in new[] { "libvlc.so.5", "libvlc" })
            {
                if (!NativeLibrary.TryLoad(name, out nint handle)) continue;
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return false;
    }
}
