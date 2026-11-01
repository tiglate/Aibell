using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Plugins;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class PluginsPanelTests
{
    private static readonly PluginEntry Bundled = new("Aibell.Metadata.MusicBrainz", "MusicBrainz", "Finds album details.", "1.2.0", false, true, null, true, false);
    private static readonly PluginEntry Mine = new("Mine", "My plugin", "", "0.3", true, true, null, true, false);
    private static readonly PluginEntry Broken = new("Broken", "Broken", "", "", true, false, "bad image", true, false);
    private readonly List<Window> windows = [];

    [TearDown]
    public void CloseWindows()
    {
        foreach (Window window in windows) window.Close();
        windows.Clear();
    }

    [AvaloniaTest]
    public void Panel_ListsEachPluginWithVersionDescriptionAndState()
    {
        Window window = Host(new FakeAdmin(Bundled, Mine, Broken), out _);

        Assert.That(Texts(window), Has.Member("MusicBrainz  1.2.0").And.Member("Finds album details."));
        Assert.That(Status(window, Bundled), Is.EqualTo("Included with Aibell. Active."));
        Assert.That(Status(window, Mine), Is.EqualTo("Installed by you. Active."));
        Assert.That(Status(window, Broken), Does.Contain("Could not be loaded: bad image"));
        Assert.That(Find<CheckBox>(window, "PluginEnabled_Broken").IsEnabled, Is.False, "a plugin that failed to load cannot be switched on");
    }

    [AvaloniaTest]
    public void Panel_FollowsTheUiLanguage()
    {
        System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
            Window window = Host(new FakeAdmin(Bundled, Mine), out _);

            Assert.That(Status(window, Bundled), Is.EqualTo("Incluído no Aibell. Ativo."));
            Assert.That(Find<Button>(window, "InstallPluginFolder").Content, Is.EqualTo("Instalar de uma pasta…"));
            Assert.That(Find<CheckBox>(window, "PluginEnabled_Mine").Content, Is.EqualTo("Ativado"));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = saved; }
    }

    [AvaloniaTest]
    public void Panel_NoPlugins_SaysSo()
    {
        Window window = Host(new FakeAdmin(), out _);
        Assert.That(Find<TextBlock>(window, "NoPlugins").Text, Does.Contain("No plugins"));
    }

    [AvaloniaTest]
    public void Panel_UncheckingEnabled_PersistsTheChoiceAndSaysItNeedsARestart()
    {
        FakeAdmin admin = new(Bundled);
        Window window = Host(admin, out _);

        Find<CheckBox>(window, "PluginEnabled_" + Bundled.Folder).IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.That(admin.EnabledCalls, Is.EqualTo(Items((Bundled.Folder, false))));
        Assert.That(Find<TextBlock>(window, "PluginsMessage").Text, Does.Contain("restarts"));
        Assert.That(Status(window, Bundled), Does.Contain("switched off when Aibell restarts"));
    }

    [AvaloniaTest]
    public void Panel_OnlyUserInstalledPluginsCanBeUninstalled()
    {
        Window window = Host(new FakeAdmin(Bundled, Mine), out _);

        Assert.That(window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "PluginUninstall_Mine"), Is.True);
        Assert.That(window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "PluginUninstall_" + Bundled.Folder), Is.False);
    }

    [AvaloniaTest]
    public async Task Panel_Uninstall_AsksFirstThenSchedulesRemoval()
    {
        FakeAdmin admin = new(Mine);
        Window window = Host(admin, out _);

        Click(window, "PluginUninstall_Mine");
        Window confirm = await DialogAsync(window);
        Click(confirm, "ConfirmUninstallPlugin");
        await UntilAsync(() => admin.Uninstalled.Count == 1);

        Assert.That(admin.Uninstalled, Is.EqualTo(Items("Mine")));
        Assert.That(Status(window, admin.List()[0]), Does.Contain("removed when Aibell restarts"));
        Assert.That(window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "PluginUninstall_Mine"), Is.False);
    }

    [AvaloniaTest]
    public async Task Panel_Install_WarnsAboutTrustAndOnlyInstallsAfterConfirmation()
    {
        FakeAdmin admin = new(Bundled);
        Window window = Host(admin, out _, folder: "/plugins/Sample");

        Click(window, "InstallPluginFolder");
        Window confirm = await DialogAsync(window);
        Assert.That(Texts(confirm), Has.Some.Contain("Install only plugins from sources you trust"));
        Click(confirm, "ConfirmInstallPlugin");
        await UntilAsync(() => admin.Installed.Count == 1);

        Assert.That(admin.Installed, Is.EqualTo(Items("/plugins/Sample")));
        await UntilAsync(() => Find<TextBlock>(window, "PluginsMessage").Text?.Contains("was installed", StringComparison.Ordinal) == true);
        Assert.That(Status(window, admin.List().Single(entry => entry.Folder == "Sample")), Does.Contain("Will start when Aibell restarts"));
    }

    [AvaloniaTest]
    public async Task Panel_Install_Declined_ChangesNothing()
    {
        FakeAdmin admin = new(Bundled);
        Window window = Host(admin, out _, folder: "/plugins/Sample");

        Click(window, "InstallPluginFolder");
        Window confirm = await DialogAsync(window);
        confirm.Close(false);
        await UntilAsync(() => window.OwnedWindows.Count == 0);

        Assert.That(admin.Installed, Is.Empty);
    }

    [AvaloniaTest]
    public async Task Panel_Install_Rejected_ShowsTheReasonAndKeepsTheList()
    {
        FakeAdmin admin = new(Bundled) { InstallError = new InvalidOperationException("Sample is already installed.") };
        Window window = Host(admin, out _, folder: "/plugins/Sample");

        Click(window, "InstallPluginFolder");
        Click(await DialogAsync(window), "ConfirmInstallPlugin");
        await UntilAsync(() => Find<TextBlock>(window, "PluginsMessage").Text?.Contains("not installed", StringComparison.Ordinal) == true);

        Assert.That(Find<TextBlock>(window, "PluginsMessage").Text, Does.Contain("already installed"));
        Assert.That(admin.List(), Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public void Panel_WithoutPickers_DisablesInstall()
    {
        Window window = Host(new FakeAdmin(Bundled), out _, pickers: false);
        Assert.That(Find<Button>(window, "InstallPluginFolder").IsEnabled, Is.False);
        Assert.That(Find<Button>(window, "InstallPluginZip").IsEnabled, Is.False);
    }

    [TestCase(false, false, "Switched off.")]
    [TestCase(true, true, "Active.")]
    [TestCase(false, true, "Will start when Aibell restarts.")]
    public void StatusOf_DescribesThisSessionAndTheNextStart(bool running, bool enabledNext, string expected) =>
        Assert.That(PluginsPanel.StatusOf(Mine with { IsRunning = running, EnabledNext = enabledNext }), Is.EqualTo(expected));

    private Window Host(FakeAdmin admin, out PluginsPanel panel, string? folder = null, bool pickers = true)
    {
        panel = new PluginsPanel(admin, pickers ? () => Task.FromResult(folder) : null, pickers ? () => Task.FromResult(folder) : null);
        Window window = new() { Width = 640, Height = 420, Content = panel };
        windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    private static string Status(Window window, PluginEntry entry) => Find<TextBlock>(window, "PluginStatus_" + entry.Folder).Text ?? "";

    private static string[] Texts(Window window) => [.. window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "")];

    private static void Click(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = Find<Button>(window, name);
        Assert.That(button.IsEnabled, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task<Window> DialogAsync(Window owner)
    {
        await UntilAsync(() => owner.OwnedWindows.Count == 1);
        Dispatcher.UIThread.RunJobs();
        return owner.OwnedWindows[0];
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static T[] Items<T>(params T[] items) => items;

    private sealed class FakeAdmin(params PluginEntry[] initial) : IPluginAdministration
    {
        private readonly List<PluginEntry> entries = [.. initial];
        public string UserPluginsDirectory => "/user/plugins";
        public List<(string Folder, bool Enabled)> EnabledCalls { get; } = [];
        public List<string> Uninstalled { get; } = [];
        public List<string> Installed { get; } = [];
        public Exception? InstallError { get; set; }

        public IReadOnlyList<PluginEntry> List() => [.. entries];

        public void SetEnabled(string folder, bool enabled)
        {
            EnabledCalls.Add((folder, enabled));
            int index = entries.FindIndex(entry => entry.Folder == folder);
            entries[index] = entries[index] with { EnabledNext = enabled };
        }

        public Task<string> InstallAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            if (InstallError is not null) return Task.FromException<string>(InstallError);
            Installed.Add(sourcePath);
            string name = Path.GetFileName(sourcePath);
            entries.Add(new PluginEntry(name, name, "", "", true, false, null, true, false));
            return Task.FromResult(name);
        }

        public void Uninstall(string folder)
        {
            Uninstalled.Add(folder);
            int index = entries.FindIndex(entry => entry.Folder == folder);
            entries[index] = entries[index] with { RemovalPending = true };
        }
    }
}
