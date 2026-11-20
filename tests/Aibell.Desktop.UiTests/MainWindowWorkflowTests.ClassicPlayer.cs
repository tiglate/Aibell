using System.IO.Compression;
using System.Net;
using System.Text;
using Aibell.Desktop.Classic;
using Aibell.Domain.Media;
using Aibell.Application.Playback;
using Aibell.Skins.Classic;
using Aibell.Application.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class MainWindowWorkflowTests
{
    private static void Click(Window target, double x, double y)
    {
        target.MouseMove(new Point(x, y));
        target.MouseDown(new Point(x, y), MouseButton.Left);
        target.MouseUp(new Point(x, y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTest]
    public async Task ClassicMode_TakesOverTheWindowAndDrivesTheSamePlayer()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-classic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.ArePreferencesLoaded);
        Track one = new(Guid.NewGuid(), new Uri(Path.Combine(directory, "one.flac")), "One", "Artist", "Album", TimeSpan.FromMinutes(2));
        Track two = one with { Id = Guid.NewGuid(), Location = new Uri(Path.Combine(directory, "two.flac")), Title = "Two" };

        window.EnterClassicMode();
        ClassicPlayerWindow classic = window.ClassicWindow!;
        window.ClassicModel!.Add([one, two]);
        Dispatcher.UIThread.RunJobs();
        ClassicSectionWindow equalizer = classic.EqualizerWindow!, playlist = classic.PlaylistWindow!;
        Assert.Multiple(() =>
        {
            Assert.That(window.IsVisible, Is.False);
            Assert.That(classic.IsVisible, Is.True);
            Assert.That(classic.Surface.SkinSize, Is.EqualTo(new Size(275, 116)), "the main window draws only itself");
            Assert.That(equalizer.IsVisible && playlist.IsVisible, Is.True);
            Assert.That(equalizer.Position, Is.EqualTo(classic.Position + new PixelVector(0, 116)), "the equalizer starts docked under it");
            Assert.That(playlist.Position, Is.EqualTo(equalizer.Position + new PixelVector(0, 116)));
        });

        // Dragging the main window by its background carries the docked windows in the same moment.
        PixelPoint start = classic.Position;
        classic.MouseDown(new Point(150, 6), MouseButton.Left);
        classic.MouseMove(new Point(230, 66));
        classic.MouseUp(new Point(230, 66), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(classic.Position, Is.EqualTo(start + new PixelVector(80, 60)));
            Assert.That(equalizer.Position, Is.EqualTo(classic.Position + new PixelVector(0, 116)));
            Assert.That(playlist.Position, Is.EqualTo(classic.Position + new PixelVector(0, 232)));
        });
        // Dragging the playlist pulls it away on its own.
        playlist.MouseDown(new Point(150, 5), MouseButton.Left);
        playlist.MouseMove(new Point(450, 105));
        playlist.MouseUp(new Point(450, 105), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.That(playlist.Position, Is.EqualTo(classic.Position + new PixelVector(300, 332)));
        Assert.That(viewModel.ClassicPlayer.PlaylistPosition, Is.EqualTo(new ClassicWindowPosition(playlist.Position.X, playlist.Position.Y)));

        // The grip in the playlist's corner widens it in 25-pixel steps; wide enough, its bottom bar gains a visualizer.
        Assert.That(playlist.Surface.PlaylistShowsVisualizer, Is.False);
        playlist.MouseDown(new Point(265, 164), MouseButton.Left);
        playlist.MouseMove(new Point(365, 164));
        playlist.MouseUp(new Point(365, 164), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.ClassicPlayer.PlaylistExtraColumns, Is.EqualTo(4));
            Assert.That(playlist.Surface.SkinSize, Is.EqualTo(new Size(375, 174)));
            Assert.That(playlist.Surface.PlaylistShowsVisualizer, Is.True);
            Assert.That(classic.Surface.SkinSize.Width, Is.EqualTo(275), "the main window keeps its width");
        });

        Click(classic, 50, 97);
        await UntilAsync(() => engine.Snapshot is { Status: PlaybackStatus.Playing } && engine.Snapshot.Track?.Id == one.Id);

        // The EQ button hides the equalizer window.
        Click(classic, 230, 64);
        await UntilAsync(() => !viewModel.ClassicPlayer.ShowEqualizer);
        Assert.That(equalizer.IsVisible, Is.False);

        // Rolling the main window up pulls nothing (the playlist is no longer docked); the stacked layout is still there.
        classic.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        await UntilAsync(() => viewModel.ClassicPlayer.DoubleSize);
        classic.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        await UntilAsync(() => !viewModel.ClassicPlayer.DoubleSize);
        classic.Surface.ChangeLayout(preferences => preferences with { SeparateWindows = false, ShowEqualizer = true });
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(classic.Surface.SkinSize, Is.EqualTo(new Size(375, 116 + 116 + 174)), "stacked, the window is as wide as the playlist");
            Assert.That(playlist.IsVisible, Is.False);
        });

        classic.Surface.Focus();
        classic.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        await UntilAsync(() => engine.Snapshot.Track?.Id == two.Id);
        Assert.That(window.ClassicModel.CurrentIndex, Is.EqualTo(1));

        classic.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        await UntilAsync(() => viewModel.ClassicPlayer.DoubleSize);
        Assert.That(classic.Surface.Scale, Is.EqualTo(2));

        window.ExitClassicMode();
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(window.IsVisible, Is.True);
            Assert.That(classic.IsVisible, Is.False);
            Assert.That(engine.Snapshot.Status, Is.EqualTo(PlaybackStatus.Playing), "switching modes does not stop the music");
            Assert.That(viewModel.ClassicPlayer.IsEnabled, Is.False);
        });

        await CloseAsync();
        Assert.That(classic.IsVisible, Is.False);
        await ReleaseServicesAsync();
        Directory.Delete(directory, recursive: true);
    }

    [AvaloniaTest]
    public async Task ClassicMode_AutoButtonAndTheStandardEqualizerShareAutoAndBalance()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-classic-auto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Open();
        await UntilAsync(() => !viewModel.IsBusy && viewModel.ArePreferencesLoaded);

        window.EnterClassicMode();
        ClassicPlayerWindow classic = window.ClassicWindow!;
        Dispatcher.UIThread.RunJobs();
        // AUTO sits at (40, 18) in the equalizer window.
        Click(classic.EqualizerWindow!, 56, 24);
        await UntilAsync(() => viewModel.IsEqualizerAuto);
        // This engine has no balance control, so the slider stays put.
        Click(classic, 177 + 30, 63);
        Assert.That(viewModel.Balance, Is.Zero);
        window.ExitClassicMode();
        Dispatcher.UIThread.RunJobs();

        Button open = window.FindControl<Button>("OpenEqualizerButton")!;
        Activate(open);
        EqualizerWindow eq = window.OwnedWindows.OfType<EqualizerWindow>().Single();
        Avalonia.Controls.Primitives.ToggleButton auto = eq.FindControl<Avalonia.Controls.Primitives.ToggleButton>("EqualizerAuto")!;
        Slider balance = eq.FindControl<Slider>("EqualizerBalance")!;
        Assert.Multiple(() =>
        {
            Assert.That(auto.IsChecked, Is.True, "the standard window shows AUTO as the classic one left it");
            Assert.That(balance.IsEffectivelyEnabled, Is.False, "no balance without an engine that can pan");
            Assert.That(eq.FindControl<TextBlock>("EqualizerBalanceText")!.Text, Is.EqualTo("Center"));
        });
        auto.IsChecked = false;
        await UntilAsync(() => !viewModel.IsEqualizerAuto);
        eq.Close();
        await UntilAsync(() => !eq.IsVisible);

        await CloseAsync();
        await ReleaseServicesAsync();
        Directory.Delete(directory, recursive: true);
    }

    [AvaloniaTest]
    public async Task SkinManager_InstallsFromTheMuseumAndChoosesSkins()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-skins-{Guid.NewGuid():N}");
        SkinLibrary library = new(directory);
        byte[] skin = MinimalSkin();
        MuseumHandler museum = new(skin);
        List<InstalledSkin> chosen = [];
        SkinManagerWindow manager = new(library, new SkinGalleryClient(new HttpClient(museum)), new DelayedFilePicker(new Uri(directory + "/")), "");
        manager.SkinChosen += (_, picked) => chosen.Add(picked);
        manager.Show();
        Dispatcher.UIThread.RunJobs();
        ListBox installed = manager.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "ClassicSkinList");
        Assert.That(installed.ItemCount, Is.EqualTo(1), "only the built-in skin at first");

        manager.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        await UntilAsync(() => manager.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Install and use")));
        Assert.That(museum.Requests, Has.Some.Contains("sort:MUSEUM"));
        Button install = manager.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Install and use"));
        install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => chosen.Count == 1);
        Assert.Multiple(() =>
        {
            Assert.That(chosen[0].Name, Is.EqualTo("Museum Skin"));
            Assert.That(library.List(), Has.Count.EqualTo(2));
            Assert.That(install.Content, Is.EqualTo("In use"));
        });

        manager.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        installed.SelectedIndex = 0;
        manager.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ClassicSkinUse").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(chosen[^1].IsBuiltIn, Is.True);
        Assert.That(manager.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "ClassicSkinPreview").Source, Is.Not.Null);
        manager.Close();
        Directory.Delete(directory, recursive: true);
    }

    private static byte[] MinimalSkin()
    {
        const int width = 275, height = 116, stride = width * 3 + 3 & ~3;
        byte[] bmp = new byte[54 + stride * height];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (Stream entry = zip.CreateEntry("main.bmp").Open())
            entry.Write(bmp);
        return stream.ToArray();
    }

    private sealed class MuseumHandler(byte[] skin) : HttpMessageHandler
    {
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? request.RequestUri!.AbsoluteUri : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(body);
            if (request.Method == HttpMethod.Post)
            {
                const string page = """{"data":{"skins":{"nodes":[{"md5":"m","filename":"Museum Skin.wsz","screenshot_url":"https://r2.webampskins.org/screenshots/m.png","download_url":"https://r2.webampskins.org/skins/m.wsz","nsfw":false}]}}}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page, Encoding.UTF8, "application/json") };
            }
            byte[] bytes = request.RequestUri!.AbsolutePath.EndsWith(".png", StringComparison.Ordinal) ? Png : skin;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}

public sealed partial class MainWindowWorkflowTests
{
    // Under a tiling compositor the classic player starts as one window; choosing separate windows still works and sticks.
    [AvaloniaTest]
    public async Task ClassicMode_StartsAsOneWindowWhenTheDesktopTiles()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-ui-classic-tiling-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        ClassicPlayerWindow.SeparateWindowsByDefault = false;
        try
        {
            Open();
            await UntilAsync(() => !viewModel.IsBusy && viewModel.ArePreferencesLoaded);
            Assert.That(viewModel.ClassicPlayer.SeparateWindows, Is.Null, "no layout has been chosen yet");

            window.EnterClassicMode();
            ClassicPlayerWindow classic = window.ClassicWindow!;
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(classic.Separate, Is.False);
                Assert.That(classic.EqualizerWindow, Is.Null);
                Assert.That(classic.PlaylistWindow, Is.Null);
                Assert.That(classic.Surface.SkinSize.Height, Is.GreaterThan(116), "one window draws the equalizer and playlist too");
            });

            classic.Surface.ChangeLayout(preferences => preferences with { SeparateWindows = !classic.Separate });
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(classic.Separate, Is.True);
                Assert.That(viewModel.ClassicPlayer.SeparateWindows, Is.True, "the user's choice is saved explicitly");
                Assert.That(classic.EqualizerWindow, Is.Not.Null);
            });
        }
        finally
        {
            ClassicPlayerWindow.SeparateWindowsByDefault = true;
        }
    }
}
