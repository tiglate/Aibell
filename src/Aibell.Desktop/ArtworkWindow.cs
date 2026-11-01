using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Aibell.Desktop.Localization;
using Aibell.Desktop.ViewModels;

namespace Aibell.Desktop;

/// <summary>
/// The "Get Artwork…" window: matching albums with their covers, a Save button and a status line. It opens, searches and, when
/// closed, cancels whatever is still running. Saving only ever creates a new cover file; see <see cref="ArtworkLookupViewModel"/>.
/// </summary>
public sealed class ArtworkWindow : Window
{
    public ArtworkWindow(ArtworkLookupViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        DataContext = model;
        Name = "ArtworkWindow"; Title = Loc.Get("Artwork.Title"); Width = 560; Height = 520; MinWidth = 440; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");

        TextBlock heading = new() { Name = "ArtworkHeading", Text = model.Heading, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        ListBox candidates = new()
        {
            Name = "ArtworkCandidates",
            ItemsSource = model.Candidates,
            ItemTemplate = new FuncDataTemplate<ArtworkCandidateRow>((row, _) => Row(row), supportsRecycling: false)
        };
        AutomationProperties.SetName(candidates, Loc.Get("Artwork.Matching"));
        candidates.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(ArtworkLookupViewModel.Selected)) { Mode = BindingMode.TwoWay });
        TextBlock status = new() { Name = "ArtworkStatus", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(ArtworkLookupViewModel.StatusMessage)));
        ProgressBar progress = new() { Name = "ArtworkProgress", IsIndeterminate = true, Height = 4, MinHeight = 4 };
        progress.Bind(IsVisibleProperty, new Binding(nameof(ArtworkLookupViewModel.IsBusy)));
        StackPanel statusArea = new() { Spacing = 6 };
        statusArea.Children.Add(progress); statusArea.Children.Add(status);
        Button save = new() { Name = "SaveArtwork", Content = Loc.Get("Artwork.Save"), Width = 110, Height = 30, Classes = { "dialogAccept" } };
        save.Bind(IsEnabledProperty, new Binding(nameof(ArtworkLookupViewModel.CanSave)));
        ToolTip.SetTip(save, Loc.Get("Artwork.SaveTip"));
        Button close = new() { Name = "CloseArtwork", Content = Loc.Get("Artwork.Close"), Width = 92, Height = 30 };
        save.Click += async (_, _) => await model.SaveAsync();
        close.Click += (_, _) => Close(model.SavedPath is not null);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(close); buttons.Children.Add(save);

        Grid layout = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 10 };
        layout.Children.Add(heading);
        Grid.SetRow(candidates, 1); layout.Children.Add(candidates);
        Grid.SetRow(statusArea, 2); layout.Children.Add(statusArea);
        Grid.SetRow(buttons, 3); layout.Children.Add(buttons);
        Content = DialogChrome.Wrap(this, layout);

        Opened += async (_, _) => await model.SearchAsync();
        Closing += OnClosing;
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(model.SavedPath is not null); } };
    }

    public ArtworkLookupViewModel Model { get; }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        Closing -= OnClosing;
        Model.Dispose();
    }

    private static StackPanel Row(ArtworkCandidateRow? row)
    {
        Image image = new() { Width = 64, Height = 64, Stretch = Stretch.Uniform };
        image.Bind(Image.SourceProperty, new Binding(nameof(ArtworkCandidateRow.Thumbnail)));
        Border frame = new()
        {
            Width = 64,
            Height = 64,
            Margin = new Thickness(0, 0, 10, 0),
            Child = image,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("PanelHeaderBrush")
        };
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = row?.Details, FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        StackPanel line = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        line.Children.Add(frame); line.Children.Add(text);
        return line;
    }
}
