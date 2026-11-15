using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Mpd.Config;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class MpdConfigWindowTests
{
    private const string Sample = "# my config\nmusic_directory \"~/Music\"\nport \"6600\"\n\naudio_output {\n    type \"alsa\"\n    name \"One\"\n}\n\naudio_output {\n    type \"null\"\n    name \"Two\"\n}\n";
    private string directory = "";
    private string path = "";
    private MpdConfigWindow? window;

    [SetUp]
    public void CreateFiles()
    {
        directory = Directory.CreateTempSubdirectory("aibell-ui-mpdconf-").FullName;
        path = Path.Combine(directory, "mpd.conf");
        File.WriteAllText(path, Sample);
    }

    [TearDown]
    public void Cleanup()
    {
        window?.Close();
        Directory.Delete(directory, recursive: true);
    }

    private async Task<MpdConfigWindow> OpenAsync(bool serviceAvailable = false)
    {
        MpdServiceController service = new((_, _) => Task.FromResult(new MpdProcessResult(serviceAvailable ? 0 : 1, "")));
        window = new MpdConfigWindow(null, service, path, _ => Task.FromResult<MpdVersionInfo?>(MpdVersionInfo.Parse("Music Player Daemon 0.23.14\n\nOutput plugins:\n alsa null pulse\n")));
        window.Show();
        await UntilAsync(() => Text("MpdConfigPath").Contains(path, StringComparison.Ordinal));
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private T Find<T>(string name) where T : Control => window!.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    private string Text(string name) => window!.GetVisualDescendants().OfType<TextBlock>().First(block => block.Name == name).Text ?? "";

    private void Select(int tab)
    {
        Find<TabControl>("MpdConfigTabs").SelectedIndex = tab;
        Dispatcher.UIThread.RunJobs();
    }

    private void Click(string name)
    {
        Dispatcher.UIThread.RunJobs();
        Button button = Find<Button>(name);
        Assert.That(button.IsEnabled && button.IsVisible, Is.True, name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }

    [AvaloniaTest]
    public async Task Open_ShowsTheFileAndItsValues()
    {
        await OpenAsync();
        Assert.That(Text("MpdConfigPath"), Is.EqualTo(path));
        Assert.That(Find<TextBox>("MpdValue_music_directory").Text, Is.EqualTo("~/Music"));
        Assert.That(Find<Button>("SaveMpdConfig").IsEnabled, Is.False, "nothing changed yet");
        Assert.That(Text("MpdConfigNotice"), Does.Contain("restart MPD").And.Contain("0.23.14"));
        Select(1);
        Assert.That(Find<TextBox>("MpdValue_port").Text, Is.EqualTo("6600"));
        Select(2);
        Assert.That(Find<ComboBox>("MpdValue_replaygain").SelectedIndex, Is.Zero, "not set");
    }

    [AvaloniaTest]
    public async Task Editing_MarksTheFileChangedAndSaveWritesItWithABackup()
    {
        await OpenAsync();
        Select(1);
        Find<TextBox>("MpdValue_port").Text = "6601";
        Dispatcher.UIThread.RunJobs();
        Assert.That(Text("MpdConfigPath"), Does.EndWith("*"));
        Assert.That(Find<Button>("SaveMpdConfig").IsEnabled, Is.True);

        Click("SaveMpdConfig");
        await UntilAsync(() => Text("MpdConfigStatus").StartsWith("Saved", StringComparison.Ordinal));
        string saved = File.ReadAllText(path);
        Assert.That(saved, Does.Contain("port \"6601\"").And.Contain("# my config"), "comments survive an edit");
        Assert.That(saved.Replace("6601", "6600", StringComparison.Ordinal), Is.EqualTo(Sample));
        Assert.That(Directory.GetFiles(directory, "mpd.conf.bak-*"), Has.Length.EqualTo(1));
        Assert.That(File.ReadAllText(Directory.GetFiles(directory, "mpd.conf.bak-*")[0]), Is.EqualTo(Sample));
        Assert.That(Text("MpdConfigPath"), Is.EqualTo(path));
    }

    [AvaloniaTest]
    public async Task InvalidValues_ShowAnErrorAndBlockSaving()
    {
        await OpenAsync();
        Select(1);
        Find<TextBox>("MpdValue_port").Text = "abc";
        Dispatcher.UIThread.RunJobs();
        Assert.That(Find<Button>("SaveMpdConfig").IsEnabled, Is.False);
        Assert.That(window!.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "port must be a number."), Is.True);
        Assert.That(Find<ListBox>("MpdConfigProblems").IsVisible, Is.True);
        Find<TextBox>("MpdValue_port").Text = "6602";
        Dispatcher.UIThread.RunJobs();
        Assert.That(Find<Button>("SaveMpdConfig").IsEnabled, Is.True);
        Assert.That(Find<ListBox>("MpdConfigProblems").IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task Outputs_ListEditMoveAndRemove()
    {
        await OpenAsync();
        Select(3);
        ListBox list = Find<ListBox>("MpdBlockList_audio_output");
        Assert.That(list.ItemsSource!.Cast<string>(), Is.EqualTo(new[] { "One (alsa)", "Two (null)" }));
        Assert.That(Find<ComboBox>("MpdPlugin_audio_output").SelectedItem, Is.EqualTo("alsa"));
        Assert.That(Find<Button>("MpdBlockUp_audio_output").IsEnabled, Is.False);

        Find<TextBox>("MpdValue_name").Text = "Speakers";
        Dispatcher.UIThread.RunJobs();
        Assert.That(list.ItemsSource!.Cast<string>().First(), Is.EqualTo("Speakers (alsa)"));

        list.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Click("MpdBlockUp_audio_output");
        Assert.That(list.ItemsSource!.Cast<string>(), Is.EqualTo(new[] { "Two (null)", "Speakers (alsa)" }));
        Assert.That(list.SelectedIndex, Is.Zero);

        Click("MpdRemoveBlock_audio_output");
        Assert.That(list.ItemsSource!.Cast<string>(), Is.EqualTo(new[] { "Speakers (alsa)" }));
        Click("SaveMpdConfig");
        await UntilAsync(() => Text("MpdConfigStatus").StartsWith("Saved", StringComparison.Ordinal));
        MpdConfigDocument saved = MpdConfigDocument.Parse(File.ReadAllText(path));
        Assert.That(saved.BlocksNamed("audio_output").Single().Get("name"), Is.EqualTo("Speakers"));
    }

    [AvaloniaTest]
    public async Task OtherSettings_AreKeptAndCanBeAdded()
    {
        File.WriteAllText(path, "future_option \"1\"\nport \"6600\"\n");
        await OpenAsync();
        Select(5);
        Assert.That(window!.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "future_option"), Is.True);
        Find<TextBox>("MpdNewSettingName").Text = "another_one";
        Find<TextBox>("MpdNewSettingValue").Text = "two";
        Click("MpdAddSetting");
        Click("SaveMpdConfig");
        await UntilAsync(() => Text("MpdConfigStatus").StartsWith("Saved", StringComparison.Ordinal));
        string saved = File.ReadAllText(path);
        Assert.That(saved, Does.Contain("future_option \"1\"").And.Contain("another_one \"two\""));
    }

    [AvaloniaTest]
    public async Task TextTab_EditsApplyOnSave()
    {
        await OpenAsync();
        Select(6);
        TextBox raw = Find<TextBox>("MpdConfigText");
        Assert.That(raw.Text, Is.EqualTo(Sample));
        raw.Text = Sample + "user \"me\"\n";
        raw.Focus();
        Click("SaveMpdConfig");
        await UntilAsync(() => Text("MpdConfigStatus").StartsWith("Saved", StringComparison.Ordinal));
        Assert.That(File.ReadAllText(path), Does.EndWith("user \"me\"\n"));
    }

    [AvaloniaTest]
    public async Task RestartButton_AppearsOnlyForAUserService()
    {
        await OpenAsync(serviceAvailable: false);
        Assert.That(Find<Button>("RestartMpd").IsVisible, Is.False);
        window!.Close();
        await OpenAsync(serviceAvailable: true);
        await UntilAsync(() => Find<Button>("RestartMpd").IsVisible);
        Assert.That(Find<Button>("RestartMpd").IsEnabled, Is.True);
    }

    [AvaloniaTest]
    public async Task MissingFile_OffersToStartANewOne()
    {
        File.Delete(path);
        window = new MpdConfigWindow(null, new MpdServiceController((_, _) => Task.FromResult(new MpdProcessResult(1, ""))), Path.Combine(directory, "nope.conf"),
            _ => Task.FromResult<MpdVersionInfo?>(null));
        window.Show();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.That(Find<Button>("NewMpdConfig").IsEnabled, Is.True);
        Assert.That(Text("MpdConfigPath"), Is.EqualTo(Path.Combine(directory, "nope.conf")));
    }
}
