using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aibell.Desktop;

public sealed partial class App : Avalonia.Application
{
    internal static IServiceProvider Services { get; set; } = null!;

    /// <summary>Receives the files a later launch passes along; null when single-instance coordination is unavailable.</summary>
    internal static Aibell.Desktop.Services.SingleInstance? Instance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (Localization.Loc.IsRightToLeft)
        {
            Style rightToLeft = new(selector => selector.OfType<Window>());
            rightToLeft.Setters.Add(new Setter(Visual.FlowDirectionProperty, FlowDirection.RightToLeft));
            Styles.Add(rightToLeft);
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Attached first so unexpected errors during window construction are logged too.
            Services.GetRequiredService<Services.ApplicationErrorReporter>().Attach();
            MainWindow window = Services.GetRequiredService<MainWindow>();
            desktop.MainWindow = window;
            window.StartupFiles.AddRange(Aibell.Desktop.Services.StartupFiles.Resolve(desktop.Args ?? [], Environment.CurrentDirectory));
            Instance?.SetHandler(lines => Dispatcher.UIThread.Post(() => window.OpenFromAnotherLaunch(
                Aibell.Desktop.Services.StartupFiles.Resolve(lines, Environment.CurrentDirectory))));
            StopWhenTheHostIsAskedTo(desktop);
            // A crash that left no trace of its own is reported once the window is up, so the message has somewhere to appear.
            if (Services.GetRequiredService<Services.SessionMonitor>().PreviousCrash is { } crash)
                window.Opened += (_, _) => Services.GetRequiredService<ViewModels.MainWindowViewModel>().ReportPreviousSession(crash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The host is started but not run: nothing waits for it, so its console lifetime (SIGTERM, Ctrl+C) would swallow the signal, log
    // "Application is shutting down" and leave the window open forever. Close the application the way the window's close button does
    // (the main window drains playback and saves state before it closes).
    private static void StopWhenTheHostIsAskedTo(IClassicDesktopStyleApplicationLifetime desktop)
    {
        bool exited = false;
        desktop.Exit += (_, _) => exited = true;
        Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!exited) desktop.TryShutdown();
            }));
    }
}
