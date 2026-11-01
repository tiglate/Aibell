using Aibell.Desktop.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Aibell.Domain.Radio;

namespace Aibell.Desktop;

public sealed class RadioStationDialog : Window
{
    private static string[] Ratings => [Loc.Get("Station.NoRating"), "★", "★★", "★★★", "★★★★", "★★★★★"];

    public RadioStationDialog(RadioStation? station, Func<RadioStation, Task> save)
    {
        Name = "RadioStationDialog"; Title = station is null ? Loc.Get("Station.AddTitle") : Loc.Get("Station.EditTitle");
        Width = 620; Height = 550; MinWidth = 520; MinHeight = 510;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        TextBox name = Input("RadioStationName", Loc.Get("Station.Name"), station?.Name, 200);
        TextBox stream = Input("RadioStationUrl", "https://…/live", station?.Stream.AbsoluteUri, 4096);
        TextBox genre = Input("RadioStationGenre", Loc.Get("Station.Genre"), station?.Genre, 100);
        TextBox creator = Input("RadioStationCreator", Loc.Get("Station.Creator"), station?.Creator, 200);
        TextBox description = Input("RadioStationDescription", Loc.Get("Station.Description"), station?.Description, 2000);
        ComboBox rating = new()
        {
            Name = "RadioStationRating",
            ItemsSource = Ratings,
            SelectedIndex = station?.Rating ?? 0,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(rating, Loc.Get("Station.RatingAutomation"));
        bool busy = false;
        TextBlock status = new() { Name = "RadioStationStatus", TextWrapping = TextWrapping.Wrap };
        Button accept = new() { Name = "SaveRadioStation", Content = "Save", Width = 92, Height = 30, Classes = { "dialogAccept" } };
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 };
        Grid details = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        details.Children.Add(Field("GENRE", genre));
        Control creatorField = Field("CREATOR", creator); Grid.SetColumn(creatorField, 1); details.Children.Add(creatorField);
        StackPanel form = new()
        {
            Spacing = 10,
            Margin = new Thickness(18),
            Children =
        {
            Field("STATION NAME", name), Field("DIRECT STREAM URL", stream),
            details, Field("DESCRIPTION", description), Field("RATING", rating),
            new TextBlock { Text = Loc.Get("Station.Note"), TextWrapping = TextWrapping.Wrap }, status
        }
        };
        if (station is not null) form.Children.Insert(form.Children.Count - 1, new TextBlock
        {
            Text = Loc.Format("Station.PlayInfo", station.PlayCount, station.LastPlayed?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? Loc.Get("Station.Never")),
            TextWrapping = TextWrapping.Wrap,
            [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush")
        });
        RadioStation Read()
        {
            if (!Uri.TryCreate(stream.Text?.Trim(), UriKind.Absolute, out Uri? uri)) throw new ArgumentException("Enter a complete HTTP or HTTPS stream URL.");
            RadioStation value = new(station?.Id ?? Guid.NewGuid(), name.Text?.Trim() ?? "", uri,
                genre.Text?.Trim() ?? "", creator.Text?.Trim() ?? "", description.Text?.Trim() ?? "", rating.SelectedIndex,
                Country: station?.Country, CountryCode: station?.CountryCode, LogoUri: station?.LogoUri, CatalogId: station?.CatalogId);
            value.Validate(); return value;
        }
        void Validate()
        {
            try { Read(); status.Text = ""; accept.IsEnabled = !busy; }
            catch (ArgumentException exception) { status.Text = exception.Message; accept.IsEnabled = false; }
        }
        async Task SaveAsync()
        {
            Validate(); if (!accept.IsEnabled) return;
            RadioStation value = Read(); busy = true; form.IsEnabled = false; accept.IsEnabled = cancel.IsEnabled = false;
            try { await save(value); busy = false; Close(true); }
            catch (Exception exception) { busy = false; form.IsEnabled = cancel.IsEnabled = true; Validate(); status.Text = exception.Message; }
        }
        foreach (TextBox field in new[] { name, stream, genre, creator, description }) field.TextChanged += (_, _) => Validate();
        rating.SelectionChanged += (_, _) => Validate();
        accept.Click += async (_, _) => await SaveAsync(); cancel.Click += (_, _) => Close(false);
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, accept }
        };
        Border footer = new() { Child = buttons, Padding = new Thickness(14, 10), [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush") };
        Grid content = new() { RowDefinitions = new RowDefinitions("*,51") };
        content.Children.Add(new ScrollViewer { Content = form }); Grid.SetRow(footer, 1); content.Children.Add(footer);
        Content = DialogChrome.Wrap(this, content, () => !busy);
        Closing += (_, args) => args.Cancel = busy;
        Opened += (_, _) => { Validate(); name.Focus(); };
        KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape && !busy) { args.Handled = true; Close(false); }
            else if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control)) { args.Handled = true; await SaveAsync(); }
        };
    }

    private static TextBox Input(string name, string placeholder, string? value, int maximum)
    {
        TextBox input = new()
        {
            Name = name,
            PlaceholderText = placeholder,
            Text = value ?? "",
            MaxLength = maximum,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0)
        };
        AutomationProperties.SetName(input, placeholder); return input;
    }
    private static StackPanel Field(string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        return new()
        {
            Spacing = 4,
            Children =
            { new TextBlock { Text = label, FontSize = 11, [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush") }, control }
        };
    }
}
