using Aibell.Desktop.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Aibell.Desktop;

/// <summary>
/// The About window: the logo, version and author on the first tab, what this installation can do on the second, and the credit
/// to Songbird and Banshee on the third. It is built from an <see cref="AboutInfo"/> so tests can show any combination.
/// </summary>
public sealed class AboutWindow : Window
{
    private readonly Bitmap? logo;

    public AboutWindow(AboutInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Title = Loc.Get("Menu.About");
        Width = 560;
        Height = 690;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("DialogBrush");

        logo = LoadLogo();
        Button close = new() { Name = "CloseAbout", Content = Loc.Get("Common.Close"), Width = 92, Height = 30, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(false);
        TabControl tabs = new()
        {
            Name = "AboutTabs",
            Items =
            {
                new TabItem { Name = "AboutTab", Header = Loc.Get("About.Tab.About"), Content = AboutTab(info) },
                new TabItem { Name = "FeaturesTab", Header = Loc.Get("About.Tab.Features"), Content = FeaturesTab(info) },
                new TabItem { Name = "CreditsTab", Header = Loc.Get("About.Tab.Credits"), Content = CreditsTab() }
            }
        };
        Grid layout = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        layout.Children.Add(Header(info));
        Grid.SetRow(tabs, 1); layout.Children.Add(tabs);
        Grid.SetRow(close, 2); layout.Children.Add(close);
        Content = DialogChrome.Wrap(this, layout);
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(false); } };
        Closed += (_, _) => logo?.Dispose();
    }

    private static Bitmap? LoadLogo()
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://Aibell.Desktop/Assets/about-logo.png"));
            return Bitmap.DecodeToWidth(stream, 640);
        }
        catch (Exception exception) when (exception is IOException or FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    // The logo is dark metal on white, so it sits on a white plate in both themes; it is never mirrored in right-to-left languages.
    private Border Header(AboutInfo info)
    {
        Image image = new()
        {
            Name = "AboutLogo",
            Source = logo,
            Height = 150,
            Stretch = Stretch.Uniform,
            FlowDirection = FlowDirection.LeftToRight
        };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        AutomationProperties.SetName(image, "Aibell");
        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = image
        };
    }

    private static TextBlock Text(string text, double size = 12, FontWeight weight = FontWeight.Normal, bool secondary = false, string? name = null)
    {
        TextBlock block = new() { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Name = name };
        if (secondary) block.Classes.Add("secondary");
        return block;
    }

    private static StackPanel AboutTab(AboutInfo info)
    {
        Grid details = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 6, ColumnSpacing = 16, Margin = new Thickness(0, 14, 0, 0) };
        (string Label, string Value, string Name)[] rows =
        [
            (Loc.Get("About.Author"), info.Author, "AboutAuthor"),
            (Loc.Get("About.License"), info.License, "AboutLicense"),
            (Loc.Get("About.Website"), AboutInfo.ProjectUri.AbsoluteUri, "AboutWebsite")
        ];
        for (int index = 0; index < rows.Length; index++)
        {
            details.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock label = Text(rows[index].Label, secondary: true);
            TextBlock value = Text(rows[index].Value, name: rows[index].Name);
            Grid.SetRow(label, index); details.Children.Add(label);
            Grid.SetRow(value, index); Grid.SetColumn(value, 1); details.Children.Add(value);
        }
        return new StackPanel
        {
            Margin = new Thickness(8, 14),
            Spacing = 4,
            Children =
            {
                Text(info.Name, 22, FontWeight.SemiBold, name: "AboutName"),
                Text(Loc.Format("About.Version", info.Version), 13, name: "AboutVersion"),
                Text(Loc.Get("About.Tagline"), name: "AboutTagline"),
                details,
                Text(Loc.Get("About.Independent"), secondary: true).WithMargin(0, 14, 0, 0),
                Text(info.Copyright, 11, secondary: true, name: "AboutCopyright").WithMargin(0, 10, 0, 0)
            }
        };
    }

    private static ScrollViewer FeaturesTab(AboutInfo info)
    {
        StackPanel sections = new() { Margin = new Thickness(8, 8, 24, 8), Spacing = 14 };
        foreach (AboutSection section in info.Features)
        {
            Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("14,*,Auto"), RowSpacing = 4 };
            for (int index = 0; index < section.Rows.Count; index++)
            {
                AboutRow row = section.Rows[index];
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Ellipse dot = new()
                {
                    Width = 8,
                    Height = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(Color.Parse(row.State == AboutState.Available ? "#3FA34D" : "#8A9096"))
                };
                TextBlock label = Text(row.Label);
                label.Margin = new Thickness(6, 0, 12, 0);
                TextBlock value = Text(row.Value, secondary: true);
                value.TextAlignment = TextAlignment.End;
                value.MaxWidth = 300;
                Grid.SetRow(dot, index); grid.Children.Add(dot);
                Grid.SetRow(label, index); Grid.SetColumn(label, 1); grid.Children.Add(label);
                Grid.SetRow(value, index); Grid.SetColumn(value, 2); grid.Children.Add(value);
            }
            sections.Children.Add(new StackPanel { Spacing = 6, Children = { Text(section.Title, 13, FontWeight.SemiBold), grid } });
        }
        return new ScrollViewer { Name = "AboutFeatures", Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static ScrollViewer CreditsTab() => new ScrollViewer
    {
        Name = "AboutCredits",
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = new StackPanel
        {
            Margin = new Thickness(8, 8, 24, 8),
            Spacing = 8,
            Children =
            {
                Text(Loc.Get("About.Credits.Intro")),
                Text("Songbird", 14, FontWeight.SemiBold).WithMargin(0, 8, 0, 0),
                Text(Loc.Get("About.Credits.Songbird"), name: "CreditSongbird"),
                Text("Banshee", 14, FontWeight.SemiBold).WithMargin(0, 8, 0, 0),
                Text(Loc.Get("About.Credits.Banshee"), name: "CreditBanshee"),
                Text(Loc.Get("About.Credits.Thanks")).WithMargin(0, 8, 0, 0),
                Text(Loc.Get("About.Credits.Libraries"), 11, secondary: true).WithMargin(0, 8, 0, 0)
            }
        }
    };
}

internal static class AboutLayoutExtensions
{
    public static T WithMargin<T>(this T control, double left, double top, double right, double bottom) where T : Control
    {
        control.Margin = new Thickness(left, top, right, bottom);
        return control;
    }
}
