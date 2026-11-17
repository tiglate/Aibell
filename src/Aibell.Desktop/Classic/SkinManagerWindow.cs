using Aibell.Desktop.Localization;
using Aibell.Desktop.Services;
using Aibell.Skins.Classic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Aibell.Desktop.Classic;

/// <summary>
/// Installs, chooses and removes skins, and browses the online skin museum (skins.webamp.org) when online features are allowed.
/// </summary>
internal sealed class SkinManagerWindow : Window, IDisposable
{
    private const int PageSize = 24;

    private readonly SkinLibrary library;
    private readonly SkinGalleryClient? gallery;
    private readonly IDesktopFilePicker picker;
    private readonly ListBox installed = new() { Name = "ClassicSkinList" };
    private readonly Image preview = new() { Name = "ClassicSkinPreview", Stretch = Stretch.Uniform, Width = 275 * 2, Height = 116 * 2 };
    private readonly TextBlock status = new() { Name = "ClassicSkinStatus", TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel results = new() { Name = "ClassicSkinResults", Orientation = Orientation.Horizontal };
    private readonly TextBox search = new() { Name = "ClassicSkinSearch", Width = 320 };
    private readonly Button more = new() { Name = "ClassicSkinMore", Height = 30, IsVisible = false };
    private readonly CancellationTokenSource lifetime = new();
    private string currentId;
    private string? query;
    private int offset;
    private bool busy;

    public SkinManagerWindow(SkinLibrary library, SkinGalleryClient? gallery, IDesktopFilePicker picker, string currentId)
    {
        this.library = library;
        this.gallery = gallery;
        this.picker = picker;
        this.currentId = currentId;
        Name = "ClassicSkinManager";
        Title = Loc.Get("Classic.SkinsTitle");
        Width = 900;
        Height = 640;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        RenderOptions.SetBitmapInterpolationMode(preview, BitmapInterpolationMode.None);
        AutomationProperties.SetName(installed, Loc.Get("Classic.InstalledSkins"));
        AutomationProperties.SetName(search, Loc.Get("Classic.SearchSkins"));

        TabControl tabs = new() { Name = "ClassicSkinTabs", Margin = new Thickness(12, 8, 12, 12) };
        tabs.Items.Add(new TabItem { Header = Loc.Get("Classic.InstalledSkins"), Content = InstalledPage() });
        if (gallery is not null)
        {
            tabs.Items.Add(new TabItem { Header = Loc.Get("Classic.OnlineSkins"), Content = OnlinePage() });
            // The museum is only contacted once its tab is opened.
            tabs.SelectionChanged += async (_, _) =>
            {
                if (tabs.SelectedIndex == 1 && offset == 0 && !busy && results.Children.Count == 0) await LoadAsync(null, reset: true);
            };
        }
        Content = DialogChrome.Wrap(this, tabs);
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        };
        Closed += (_, _) => Dispose();
        Refresh(currentId);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <summary>The user chose a skin to use.</summary>
    public event EventHandler<InstalledSkin>? SkinChosen;

    /// <summary>The user removed a skin.</summary>
    public event EventHandler<InstalledSkin>? SkinRemoved;

    private Grid InstalledPage()
    {
        installed.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<InstalledSkin>((skin, _) =>
            new TextBlock { Text = skin is null ? "" : skin.IsBuiltIn ? Loc.Get("Classic.BuiltInSkin") : skin.Name, Margin = new Thickness(4, 2) });
        installed.SelectionChanged += (_, _) => ShowPreview();
        installed.DoubleTapped += (_, _) => UseSelected();
        Button use = new() { Name = "ClassicSkinUse", Content = Loc.Get("Classic.UseSkin"), Height = 30, Classes = { "dialogAccept" } };
        Button install = new() { Name = "ClassicSkinInstall", Content = Loc.Get("Classic.InstallSkin"), Height = 30 };
        Button remove = new() { Name = "ClassicSkinRemove", Content = Loc.Get("Classic.RemoveSkin"), Height = 30 };
        use.Click += (_, _) => UseSelected();
        install.Click += async (_, _) => await InstallFromFileAsync();
        remove.Click += async (_, _) => await RemoveSelectedAsync();
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children = { use, install, remove } };
        StackPanel details = new()
        {
            Spacing = 12,
            Margin = new Thickness(12, 0, 0, 0),
            Children = { new Border { Child = preview, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left }, buttons, status },
        };
        Grid page = new() { ColumnDefinitions = new ColumnDefinitions("240,*"), Margin = new Thickness(0, 12, 0, 0) };
        page.Children.Add(installed);
        Grid.SetColumn(details, 1);
        page.Children.Add(details);
        return page;
    }

    private Grid OnlinePage()
    {
        Button go = new() { Name = "ClassicSkinSearchRun", Content = Loc.Get("Classic.SearchSkins"), Height = 30 };
        go.Click += async (_, _) => await LoadAsync(search.Text, reset: true);
        search.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            await LoadAsync(search.Text, reset: true);
        };
        more.Content = Loc.Get("Classic.MoreSkins");
        more.Click += async (_, _) => await LoadAsync(query, reset: false);
        StackPanel bar = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { search, go, new TextBlock { Text = Loc.Format("Classic.MuseumNote", SkinGalleryClient.Website.Host), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 } },
        };
        StackPanel list = new() { Spacing = 10, Children = { results, more } };
        Grid page = new() { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        page.Children.Add(bar);
        ScrollViewer scroller = new() { Content = list };
        Grid.SetRow(scroller, 1);
        page.Children.Add(scroller);
        return page;
    }

    private void Refresh(string? select)
    {
        IReadOnlyList<InstalledSkin> skins = library.List();
        installed.ItemsSource = skins;
        installed.SelectedItem = skins.FirstOrDefault(skin => skin.Id == select) ?? skins[0];
    }

    private void ShowPreview()
    {
        if (installed.SelectedItem is not InstalledSkin skin) return;
        ClassicSkin loaded = library.Load(skin.Id);
        Bitmap? old = preview.Source as Bitmap;
        preview.Source = SkinBitmaps.ToBitmap(SkinPreview.Render(loaded));
        old?.Dispose();
        status.Text = skin.Id == currentId ? Loc.Get("Classic.SkinInUse") : "";
    }

    private void UseSelected()
    {
        if (installed.SelectedItem is not InstalledSkin skin) return;
        currentId = skin.Id;
        SkinChosen?.Invoke(this, skin);
        ShowPreview();
    }

    private async Task InstallFromFileAsync()
    {
        IReadOnlyList<Uri> files = await picker.PickFilesAsync(this, new FilePickerOpenOptions
        {
            Title = Loc.Get("Classic.InstallSkin"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Loc.Get("Classic.SkinFiles")) { Patterns = ["*.wsz", "*.zip"] }],
        });
        InstalledSkin? last = null;
        List<string> failures = [];
        foreach (Uri file in files.Where(file => file.IsFile))
        {
            try
            {
                last = library.InstallFile(file.LocalPath);
            }
            catch (Exception exception) when (exception is InvalidSkinException or IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(file.LocalPath)}: {Describe(exception)}");
            }
        }
        if (last is not null) Refresh(last.Id);
        status.Text = failures.Count > 0 ? string.Join(Environment.NewLine, failures) : last is null ? "" : Loc.Format("Classic.SkinInstalled", last.Name);
    }

    private async Task RemoveSelectedAsync()
    {
        if (installed.SelectedItem is not InstalledSkin { IsBuiltIn: false } skin) return;
        if (!await DialogChrome.ConfirmAsync(this, Loc.Get("Classic.RemoveSkin"), Loc.Format("Classic.RemoveSkinConfirm", skin.Name),
                Loc.Get("Classic.RemoveSkin"), "ClassicSkinRemoveConfirm")) return;
        try
        {
            library.Uninstall(skin);
            SkinRemoved?.Invoke(this, skin);
            if (currentId == skin.Id) currentId = string.Empty;
            Refresh(currentId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status.Text = exception.Message;
        }
    }

    private async Task LoadAsync(string? text, bool reset)
    {
        if (gallery is null || busy) return;
        busy = true;
        more.IsEnabled = false;
        if (reset)
        {
            query = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            offset = 0;
            results.Children.Clear();
        }
        try
        {
            IReadOnlyList<GallerySkin> page = await gallery.GetPageAsync(query, offset, PageSize, lifetime.Token);
            offset += PageSize;
            foreach (GallerySkin skin in page) results.Children.Add(Card(skin));
            more.IsVisible = page.Count > 0;
            if (reset && page.Count == 0) results.Children.Add(new TextBlock { Text = Loc.Get("Classic.NoSkinsFound") });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!lifetime.IsCancellationRequested) results.Children.Add(new TextBlock { Text = Loc.Format("Classic.MuseumFailed", exception.Message), TextWrapping = TextWrapping.Wrap });
        }
        finally
        {
            busy = false;
            more.IsEnabled = true;
        }
    }

    private Border Card(GallerySkin skin)
    {
        Image screenshot = new() { Width = 200, Height = 253, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(screenshot, BitmapInterpolationMode.None);
        Button install = new() { Content = Loc.Get("Classic.InstallAndUse"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(install, Loc.Format("Classic.InstallNamed", skin.Name));
        TextBlock name = new() { Text = skin.Name, TextTrimming = TextTrimming.CharacterEllipsis, Width = 200 };
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            try
            {
                InstalledSkin installedSkin = library.Install(await gallery!.DownloadAsync(skin, lifetime.Token), skin.Name);
                Refresh(installedSkin.Id);
                currentId = installedSkin.Id;
                SkinChosen?.Invoke(this, installedSkin);
                install.Content = Loc.Get("Classic.SkinInUse");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidSkinException or IOException or UnauthorizedAccessException)
            {
                install.IsEnabled = true;
                ToolTip.SetTip(install, Describe(exception));
                install.Content = Loc.Get("Classic.InstallFailed");
            }
        };
        _ = LoadScreenshotAsync(skin, screenshot);
        return new Border
        {
            Width = 216,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(6),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("CardBrush"),
            Child = new StackPanel { Spacing = 6, Children = { screenshot, name, install } },
        };
    }

    private async Task LoadScreenshotAsync(GallerySkin skin, Image target)
    {
        try
        {
            byte[] bytes = await gallery!.DownloadScreenshotAsync(skin, lifetime.Token);
            using MemoryStream stream = new(bytes);
            target.Source = new Bitmap(stream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A missing or unreadable preview leaves the card without a picture.
        }
    }

    private static string Describe(Exception exception) => exception is InvalidSkinException invalid
        ? invalid.Reason switch
        {
            InvalidSkinReason.ModernSkin => Loc.Get("Classic.ModernSkin"),
            InvalidSkinReason.TooLarge => Loc.Get("Classic.SkinTooLarge"),
            _ => Loc.Get("Classic.NotASkin"),
        }
        : exception.Message;
}
