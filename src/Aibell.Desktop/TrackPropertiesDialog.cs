using Aibell.Desktop.Localization;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Aibell.Desktop.ViewModels;
using Aibell.Application.Library;
using Aibell.Domain.Media;
using Track = Aibell.Domain.Media.Track;

namespace Aibell.Desktop;

public sealed class TrackPropertiesDialog : Window, IDisposable
{
    private readonly Dictionary<string, TextBox> inputs = [];
    private readonly TrackMetadata initial;
    private readonly Func<TrackMetadata, bool, ArtworkChange?, CancellationToken, Task> save;
    private readonly bool supportsArtworkSave;
    private ArtworkChange? artworkChange;
    private int artworkVersion;
    private readonly Image cover = new() { Name = "TrackPropertiesCover", Width = 76, Height = 76, Stretch = Stretch.Uniform };
    private readonly Button removeArtwork = new() { Name = "RemoveTrackArtwork", Content = Loc.Get("Meta.RemoveCover"), Height = 30, Width = 112 };
    private readonly TrackPropertiesData data;
    private readonly ComboBox destination = new()
    {
        Name = "MetadataSaveDestination",
        Height = 30,
        Width = 190,
        Padding = new Thickness(8, 0),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly Button accept = new() { Name = "SaveTrackProperties", Content = "Save", Width = 92, Height = 30, Classes = { "dialogAccept" } };
    private readonly Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), Width = 92, Height = 30 };
    private readonly ComboBox rating = new() { Name = "MetadataRating", Height = 30, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock validation = new() { Name = "MetadataValidation", TextWrapping = TextWrapping.Wrap };
    private readonly TabControl tabs = new() { Name = "TrackPropertiesTabs", Margin = new Thickness(18, 0, 18, 8) };
    private Button? closeButton;
    private readonly CancellationTokenSource lifetime = new();
    private Bitmap? bitmap;
    private bool saving;
    private bool disposed;

    public TrackPropertiesDialog(TrackPropertiesData data, Func<TrackMetadata, CancellationToken, Task> save)
        : this(data, (metadata, _, token) => save(metadata, token)) { }

    public TrackPropertiesDialog(TrackPropertiesData data, Func<TrackMetadata, bool, CancellationToken, Task> save,
        Func<CancellationToken, Task>? restore = null, Func<Window, CancellationToken, Task<bool>>? manageBackups = null)
        : this(data, (metadata, toFile, _, token) => save(metadata, toFile, token), restore, manageBackups, enableArtworkEditing: false) { }

    public TrackPropertiesDialog(TrackPropertiesData data, Func<TrackMetadata, bool, ArtworkChange?, CancellationToken, Task> save,
        Func<CancellationToken, Task>? restore = null, Func<Window, CancellationToken, Task<bool>>? manageBackups = null, bool enableArtworkEditing = true)
    {
        supportsArtworkSave = enableArtworkEditing && !Path.GetExtension(data.Track.Location.LocalPath).Equals(".wav", StringComparison.OrdinalIgnoreCase);
        this.data = data;
        initial = data.Metadata;
        this.save = save;
        ToolTip.SetTip(accept, Loc.Get("Meta.SaveTip"));
        Title = Loc.Get("Meta.Title");
        Width = 740; Height = 720; MinWidth = 640; MinHeight = 580;
        ShowInTaskbar = false; WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        Button close = new()
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Classes = { "toolbar" },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                Width = 14,
                Height = 14,
                Foreground = Brushes.White,
                Data = Geometry.Parse("M5,3 L12,10 L19,3 L21,5 L14,12 L21,19 L19,21 L12,14 L5,21 L3,19 L10,12 L3,5 Z")
            }
        };
        AutomationProperties.SetName(close, Loc.Get("Meta.CloseAutomation"));
        closeButton = close;
        Grid caption = new() { ColumnDefinitions = new ColumnDefinitions("*,40") };
        caption.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(close, 1); caption.Children.Add(close);
        Border titleBar = new()
        {
            Child = caption,
            Background = new SolidColorBrush(Color.Parse("#303438")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D52268")),
            BorderThickness = new Thickness(0, 0, 0, 3)
        };
        titleBar.PointerPressed += (_, args) => { if (args.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed) BeginMoveDrag(args); };

        AutomationProperties.SetName(cover, Loc.Get("Meta.CoverAutomation"));
        ToolTip.SetTip(cover, Loc.Get("Meta.CoverTip"));
        StackPanel summary = new() { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(new TextBlock { Text = initial.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        summary.Children.Add(new TextBlock { Text = initial.Artist, TextTrimming = TextTrimming.CharacterEllipsis });
        summary.Children.Add(new TextBlock
        {
            Text = initial.Album,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush")
        });
        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("76,*,Auto"), ColumnSpacing = 14, Margin = new Thickness(18, 14) };
        heading.Children.Add(cover); Grid.SetColumn(summary, 1); heading.Children.Add(summary);
        if (supportsArtworkSave && data.CanWriteFile)
        {
            StackPanel artworkActions = new() { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            Button addArtwork = new() { Name = "AddTrackArtwork", Content = Loc.Get("Meta.AddCover"), Height = 30, Width = 112 };
            ToolTip.SetTip(addArtwork, Loc.Get("Meta.AddCoverTip"));
            ToolTip.SetTip(removeArtwork, Loc.Get("Meta.RemoveCoverTip"));
            addArtwork.Click += async (_, _) =>
            {
                if (saving) return;
                try
                {
                    IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = Loc.Get("Meta.PickCover"),
                        AllowMultiple = false,
                        FileTypeFilter = [new FilePickerFileType(Loc.Get("Meta.PngJpeg")) { Patterns = ["*.png", "*.jpg", "*.jpeg"] }]
                    });
                    if (files.Count == 0 || lifetime.IsCancellationRequested) return;
                    await using Stream input = await files[0].OpenReadAsync();
                    using MemoryStream bytes = new(); byte[] buffer = new byte[65536]; int count;
                    while ((count = await input.ReadAsync(buffer, lifetime.Token)) > 0)
                    {
                        if (bytes.Length + count > ArtworkChange.MaximumImageBytes) throw new ArgumentException("Choose an image up to 16 MiB.");
                        bytes.Write(buffer, 0, count);
                    }
                    SetArtwork(bytes.ToArray());
                }
                catch (Exception exception) { validation.Text = Loc.Format("Meta.CoverLoadFailed", exception.Message); }
            };
            removeArtwork.Click += (_, _) => { if (!saving) RemoveEmbeddedArtwork(); };
            artworkActions.Children.Add(addArtwork); artworkActions.Children.Add(removeArtwork);
            Grid.SetColumn(artworkActions, 2); heading.Children.Add(artworkActions);
        }

        Grid basic = new() { ColumnDefinitions = new ColumnDefinitions("*,185"), ColumnSpacing = 18 };
        StackPanel left = new() { Spacing = 10 }, right = new() { Spacing = 10 };
        left.Children.Add(Field(Loc.Get("Meta.TrackTitle"), "Title"));
        left.Children.Add(Field(Loc.Get("Meta.TrackArtist"), "Artist"));
        left.Children.Add(Field(Loc.Get("Meta.AlbumTitle"), "Album"));
        left.Children.Add(Field(Loc.Get("Meta.AlbumArtist"), "AlbumArtist"));
        left.Children.Add(Field(Loc.Get("Meta.Genre"), "Genre"));
        left.Children.Add(Field(Loc.Get("Meta.Comment"), "Comment", multiline: true));
        right.Children.Add(Field(Loc.Get("Meta.Year"), "Year"));
        right.Children.Add(Pair(Loc.Get("Meta.TrackNumber"), "TrackNumber", "TrackTotal"));
        right.Children.Add(Pair(Loc.Get("Meta.DiscNumber"), "DiscNumber", "DiscTotal"));
        rating.ItemsSource = new[] { Loc.Get("Meta.Unrated"), "★", "★★", "★★★", "★★★★", "★★★★★" };
        rating.SelectedIndex = Math.Clamp(initial.Rating, 0, 5);
        AutomationProperties.SetName(rating, Loc.Get("Meta.Rating"));
        StackPanel ratingField = new() { Spacing = 5 };
        ratingField.Children.Add(Label(Loc.Get("Meta.Rating"))); ratingField.Children.Add(rating);
        right.Children.Add(ratingField);
        basic.Children.Add(left); Grid.SetColumn(right, 1); basic.Children.Add(right);

        StackPanel extra = new() { Spacing = 10 };
        extra.Children.Add(Two(Field(Loc.Get("Meta.Composer"), "Composer"), Field(Loc.Get("Meta.Conductor"), "Conductor")));
        extra.Children.Add(Two(Field(Loc.Get("Meta.Grouping"), "Grouping"), Field(Loc.Get("Meta.Bpm"), "BeatsPerMinute")));
        extra.Children.Add(Two(Field(Loc.Get("Meta.Copyright"), "Copyright"), Field(Loc.Get("Meta.LicenseUri"), "LicenseUri")));
        extra.Children.Add(Field(Loc.Get("Meta.Description"), "Description", multiline: true));
        extra.Children.Add(Field(Loc.Get("Meta.Keywords"), "Keywords"));
        StackPanel show = new() { Spacing = 10 };
        show.Children.Add(Field(Loc.Get("Meta.ShowName"), "ShowName"));
        show.Children.Add(Two(Field(Loc.Get("Meta.SeasonNumber"), "SeasonNumber"), Field(Loc.Get("Meta.EpisodeNumber"), "EpisodeNumber")));
        extra.Children.Add(new Expander { Header = Loc.Get("Meta.ShowDetails"), Content = show, HorizontalAlignment = HorizontalAlignment.Stretch });
        StackPanel sorting = new() { Spacing = 12 };
        sorting.Children.Add(new TextBlock { Text = Loc.Get("Meta.SortNote"), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Normal });
        sorting.Children.Add(Field(Loc.Get("Meta.SortTitle"), "SortTitle"));
        sorting.Children.Add(Field(Loc.Get("Meta.SortArtist"), "SortArtist"));
        sorting.Children.Add(Field(Loc.Get("Meta.SortAlbumArtist"), "SortAlbumArtist"));
        sorting.Children.Add(Field(Loc.Get("Meta.SortAlbum"), "SortAlbum"));
        StackPanel properties = Properties(data);
        if (data.CanManageBackups && manageBackups is not null)
        {
            Button manage = new() { Name = "ManageMetadataBackups", Content = Loc.Get("Meta.ManageBackups"), Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            manage.Click += async (_, _) =>
            {
                if (saving) return;
                try { if (await manageBackups(this, lifetime.Token)) Close(true); }
                catch (Exception exception) { validation.Text = Loc.Format("Meta.BackupsFailed", exception.Message); }
            };
            properties.Children.Insert(0, manage);
        }
        tabs.ItemsSource = new[] { Tab(Loc.Get("Meta.TabBasic"), basic), Tab(Loc.Get("Meta.TabExtra"), extra), Tab(Loc.Get("Meta.TabSorting"), sorting), Tab(Loc.Get("Meta.TabProperties"), properties) };
        tabs.SelectedIndex = 0;

        StackPanel messages = new() { Spacing = 4, Margin = new Thickness(18, 0, 18, 10) };
        messages.Children.Add(validation);
        if (data.Notice is not null) messages.Children.Add(new TextBlock { Text = data.Notice, TextWrapping = TextWrapping.Wrap });
        TextBlock destinationHint = new()
        {
            Name = "MetadataDestinationHint",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush")
        };
        void UpdateDestinationHint() => destinationHint.Text = destination.SelectedIndex == 1
            ? Path.GetExtension(data.Track.Location.LocalPath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? Loc.Get("Meta.HintWav")
                : Loc.Get("Meta.HintFile")
            : Loc.Get("Meta.HintLibrary");
        messages.Children.Add(destinationHint);
        destination.ItemsSource = new[] { new ComboBoxItem { Content = Loc.Get("Meta.LibraryOnly") },
            new ComboBoxItem { Content = Loc.Get("Meta.FileAndLibrary"), IsEnabled = data.CanWriteFile } };
        destination.SelectedIndex = 0;
        AutomationProperties.SetName(destination, Loc.Get("Meta.DestinationAutomation"));
        ToolTip.SetTip(destination, data.CanWriteFile ? Loc.Get("Meta.DestinationTip")
            : Loc.Get("Meta.DestinationUnavailable"));
        UpdateDestinationHint();
        destination.SelectionChanged += (_, _) => { UpdateDestinationHint(); ValidateInput(); };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        StackPanel saveOptions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        saveOptions.Children.Add(destination);
        if (restore is not null && data.FileBackup is not null)
        {
            Button restoreButton = new() { Name = "RestoreMetadataBackup", Content = Loc.Get("Meta.RestoreBackup"), Height = 30 };
            ToolTip.SetTip(restoreButton, Loc.Get("Meta.RestoreTip"));
            restoreButton.Click += async (_, _) =>
            {
                if (saving) return;
                if (data.CanManageBackups && manageBackups is not null)
                {
                    try { if (await manageBackups(this, lifetime.Token)) Close(true); }
                    catch (Exception exception) { validation.Text = Loc.Format("Meta.BackupsFailed", exception.Message); }
                    return;
                }
                if (!await DialogChrome.ConfirmAsync(this, Loc.Get("Backups.RestoreTitle"),
                    Loc.Get("Meta.RestorePrompt"),
                    Loc.Get("Backups.RestoreAccept"), "ConfirmRestoreMetadata")) return;
                saving = true; tabs.IsEnabled = false; accept.IsEnabled = false; cancel.IsEnabled = false; saveOptions.IsEnabled = false;
                if (closeButton is not null) closeButton.IsEnabled = false;
                try { await restore(lifetime.Token); saving = false; Close(true); }
                catch (Exception exception)
                {
                    saving = false; tabs.IsEnabled = true; cancel.IsEnabled = true; saveOptions.IsEnabled = true;
                    if (closeButton is not null) closeButton.IsEnabled = true;
                    ValidateInput(); validation.Text = Loc.Format("Meta.RestoreFailed", exception.Message);
                }
            };
            saveOptions.Children.Add(restoreButton);
        }
        Grid footerContent = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footerContent.Children.Add(saveOptions); Grid.SetColumn(buttons, 1); footerContent.Children.Add(buttons);
        Border footer = new()
        {
            Child = footerContent,
            Padding = new Thickness(14, 10),
            BorderThickness = new Thickness(0, 1, 0, 0),
            [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush"),
            [!BorderBrushProperty] = new DynamicResourceExtension("PanelBorderBrush")
        };
        Grid layout = new() { RowDefinitions = new RowDefinitions("44,104,*,Auto,51") };
        layout.Children.Add(titleBar); Grid.SetRow(heading, 1); layout.Children.Add(heading);
        Grid.SetRow(tabs, 2); layout.Children.Add(tabs); Grid.SetRow(messages, 3); layout.Children.Add(messages);
        Grid.SetRow(footer, 4); layout.Children.Add(footer);
        Content = new Border { Child = layout, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#17191B")) };
        foreach (TextBox input in inputs.Values) input.TextChanged += (_, _) => ValidateInput();
        rating.SelectionChanged += (_, _) => ValidateInput();
        accept.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => Close(false);
        close.Click += (_, _) => { if (!saving) Close(false); };
        AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.Key == Key.Escape && !saving) { Close(false); args.Handled = true; }
            else if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control) && accept.IsEnabled)
            { args.Handled = true; await SaveAsync(); }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closing += (_, args) => { if (saving) args.Cancel = true; };
        Opened += async (_, _) =>
        {
            inputs["Title"].Focus();
            inputs["Title"].SelectAll();
            if (artworkChange is not null) return;
            CancellationToken token = lifetime.Token;
            int version = artworkVersion;
            try
            {
                Bitmap loaded = await Task.Run(() =>
                {
                    using Stream stream = data.Artwork is { IsFile: true } uri ? File.OpenRead(uri.LocalPath)
                        : AssetLoader.Open(new Uri("avares://Aibell.Desktop/Assets/default-cover.png"));
                    return Bitmap.DecodeToWidth(stream, 152);
                }, token);
                if (token.IsCancellationRequested || version != artworkVersion) loaded.Dispose();
                else { bitmap = loaded; cover.Source = loaded; }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { /* The editor remains usable without artwork. */ }
        };
        Closed += (_, _) => { cover.Source = null; Dispose(); };
        ValidateInput();
    }

    private EmbeddedArtworkInfo? PrimaryArtwork => data.File?.EmbeddedArtwork?.OrderBy(picture => picture.IsFront ? 0 : 1).FirstOrDefault();

    public void SetArtwork(byte[] imageData)
    {
        if (!supportsArtworkSave || !data.CanWriteFile) throw new NotSupportedException("Artwork file editing is unavailable.");
        if (saving) throw new InvalidOperationException("Wait for the current save.");
        byte[] owned = imageData.ToArray();
        ArtworkChange next = new(PrimaryArtwork?.Index, PrimaryArtwork?.Hash, owned); next.Validate();
        using MemoryStream stream = new(owned);
        Bitmap preview = Bitmap.DecodeToWidth(stream, 152);
        artworkVersion++; artworkChange = next; cover.Source = preview; bitmap?.Dispose(); bitmap = preview;
        ValidateInput();
    }

    public void RemoveEmbeddedArtwork()
    {
        if (!supportsArtworkSave || !data.CanWriteFile) throw new NotSupportedException("Artwork file editing is unavailable.");
        if (saving) throw new InvalidOperationException("Wait for the current save.");
        artworkChange = PrimaryArtwork is { } selected ? new(selected.Index, selected.Hash) : null;
        artworkVersion++;
        Bitmap preview;
        try
        {
            using Stream original = artworkChange is null && data.Artwork is { IsFile: true } uri ? File.OpenRead(uri.LocalPath)
                : AssetLoader.Open(new Uri("avares://Aibell.Desktop/Assets/default-cover.png"));
            preview = Bitmap.DecodeToWidth(original, 152);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            using Stream placeholder = AssetLoader.Open(new Uri("avares://Aibell.Desktop/Assets/default-cover.png"));
            preview = Bitmap.DecodeToWidth(placeholder, 152);
        }
        cover.Source = preview; bitmap?.Dispose(); bitmap = preview;
        ValidateInput();
    }

    private StackPanel Field(string label, string key, bool multiline = false)
    {
        object? value = typeof(TrackMetadata).GetProperty(key)!.GetValue(initial);
        string text = value is IFormattable number ? number.ToString(null, CultureInfo.CurrentCulture) : value?.ToString() ?? "";
        if (text == "0" && value is not string) text = "";
        TextBox input = new()
        {
            Name = "Metadata" + key,
            Text = text,
            Height = multiline ? 84 : 30,
            Padding = new Thickness(8, 0),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        inputs[key] = input;
        AutomationProperties.SetName(input, label);
        StackPanel field = new() { Spacing = 5 };
        field.Children.Add(Label(label)); field.Children.Add(input);
        return field;
    }

    private Grid Pair(string label, string number, string total)
    {
        Grid pair = Two(Field(label, number), Field(Loc.Get("Meta.OfTotal"), total));
        AutomationProperties.SetName(inputs[total], Loc.Format("Meta.TotalAutomation", label));
        return pair;
    }

    private static Grid Two(Control left, Control right)
    {
        Grid pair = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        pair.Children.Add(left); Grid.SetColumn(right, 1); pair.Children.Add(right); return pair;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush")
    };
    private static TabItem Tab(string name, Control content) => new()
    {
        Header = name,
        Content = new ScrollViewer
        {
            Content = new Border { Child = content, Padding = new Thickness(2, 14, 16, 4) },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        }
    };

    private TrackMetadata Read() => initial with
    {
        Title = Text("Title"),
        Artist = Text("Artist"),
        Album = Text("Album"),
        AlbumArtist = Text("AlbumArtist"),
        Genre = Text("Genre"),
        Year = Integer("Year"),
        TrackNumber = Integer("TrackNumber"),
        TrackTotal = Integer("TrackTotal"),
        DiscNumber = Integer("DiscNumber"),
        DiscTotal = Integer("DiscTotal"),
        Rating = rating.SelectedIndex,
        BeatsPerMinute = string.IsNullOrWhiteSpace(Text("BeatsPerMinute")) ? 0 : double.Parse(Text("BeatsPerMinute"), CultureInfo.CurrentCulture),
        Composer = Text("Composer"),
        Conductor = Text("Conductor"),
        Grouping = Text("Grouping"),
        Comment = Text("Comment"),
        Copyright = Text("Copyright"),
        LicenseUri = Text("LicenseUri"),
        SortTitle = Text("SortTitle"),
        SortArtist = Text("SortArtist"),
        SortAlbumArtist = Text("SortAlbumArtist"),
        SortAlbum = Text("SortAlbum"),
        Description = Text("Description"),
        Keywords = Text("Keywords"),
        ShowName = Text("ShowName"),
        SeasonNumber = Integer("SeasonNumber"),
        EpisodeNumber = Integer("EpisodeNumber")
    };
    private string Text(string key) => inputs[key].Text ?? "";
    private int Integer(string key) => string.IsNullOrWhiteSpace(Text(key)) ? 0 : int.Parse(Text(key), CultureInfo.CurrentCulture);
    private void ValidateInput()
    {
        try
        {
            TrackMetadata value = Read(); value.Validate(); artworkChange?.Validate();
            removeArtwork.IsEnabled = !saving && (artworkChange?.ImageData is not null || PrimaryArtwork is not null && artworkChange is null);
            if (artworkChange is not null && destination.SelectedIndex != 1)
                throw new NotSupportedException("Artwork edits require File and library. Folder artwork is kept.");
            if (destination.SelectedIndex == 1)
            {
                if (!data.CanWriteFile) throw new NotSupportedException("File writing is unavailable for this track.");
                MetadataEdit? patch = TrackFileEdit.Between(initial, value);
                if (artworkChange is not null) patch = (patch ?? new MetadataEdit()) with { Artwork = artworkChange };
                if (patch is not null) TrackFileEdit.ValidateFormat(patch, Path.GetExtension(data.Track.Location.LocalPath));
            }
            validation.Text = ""; accept.IsEnabled = !saving && (value != initial || artworkChange is not null);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or NotSupportedException)
        { validation.Text = exception is FormatException or OverflowException ? Loc.Get("Meta.BadNumbers") : exception.Message; accept.IsEnabled = false; }
    }
    private async Task SaveAsync()
    {
        if (saving || !accept.IsEnabled) return;
        saving = true; accept.IsEnabled = false; cancel.IsEnabled = false;
        tabs.IsEnabled = false; destination.IsEnabled = false;
        if (closeButton is not null) closeButton.IsEnabled = false;
        validation.Text = destination.SelectedIndex == 1 ? Loc.Get("Meta.SavingFile") : Loc.Get("Meta.SavingLibrary");
        try
        {
            await save(Read(), destination.SelectedIndex == 1, artworkChange, lifetime.Token);
            saving = false;
            Close(true);
        }
        catch (Exception exception)
        {
            saving = false; cancel.IsEnabled = true; tabs.IsEnabled = true; destination.IsEnabled = true;
            if (closeButton is not null) closeButton.IsEnabled = true;
            ValidateInput();
            validation.Text = Loc.Format("Meta.SaveFailed", exception.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel(); bitmap?.Dispose(); lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private static StackPanel Properties(TrackPropertiesData data)
    {
        Track track = data.Track;
        StackPanel rows = new() { Spacing = 4 };
        void Add(string label, string? value, StackPanel? target = null)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("160,*"), ColumnSpacing = 12 };
            row.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            SelectableTextBlock text = new() { Text = string.IsNullOrEmpty(value) ? Loc.Get("Common.Unknown") : value, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Normal };
            Grid.SetColumn(text, 1); row.Children.Add(text); (target ?? rows).Children.Add(row);
        }
        string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
        string Date(DateTimeOffset? date) => date?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? Loc.Get("Common.Unknown");
        Add(Loc.Get("Meta.FileName"), track.Location.IsFile ? Path.GetFileName(track.Location.LocalPath) : track.Location.Segments.LastOrDefault());
        Add(Loc.Get("Meta.Directory"), track.Location.IsFile ? Path.GetDirectoryName(track.Location.LocalPath) : "");
        Add(Loc.Get("Meta.Location"), track.Location.IsFile ? track.Location.LocalPath : track.Location.AbsoluteUri);
        Add(Loc.Get("Meta.FileSize"), string.Create(CultureInfo.CurrentCulture, $"{track.FileSize / 1048576d:N2} MiB ({track.FileSize:N0} bytes)"));
        Add(Loc.Get("Meta.Duration"), track.Duration.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture) + " (" + Number((long)track.Duration.TotalMilliseconds) + " ms)");
        Add(Loc.Get("Meta.BitRate"), Number(track.BitRate) + " kbps"); Add(Loc.Get("Meta.SampleRate"), Number(track.SampleRate) + " Hz");
        Add(Loc.Get("Meta.Channels"), Number(track.Channels)); Add(Loc.Get("Meta.BitsPerSample"), data.File is { BitsPerSample: > 0 } ? Number(data.File.BitsPerSample) : Loc.Get("Common.Unknown"));
        Add(Loc.Get("Meta.EmbeddedPictures"), (data.File?.EmbeddedArtwork?.Count ?? 0).ToString(CultureInfo.CurrentCulture));
        Add(Loc.Get("Meta.Codec"), data.File?.Codec); Add(Loc.Get("Meta.TagFormats"), data.File is null ? "" : string.Join(", ", data.File.TagFormats));
        Add(Loc.Get("Meta.Imported"), Date(track.DateAdded)); Add(Loc.Get("Meta.FileModified"), Date(track.DateModified));
        Add(Loc.Get("Meta.LastPlayed"), Date(track.LastPlayed)); Add(Loc.Get("Meta.LastSkipped"), Date(track.LastSkipped));
        Add(Loc.Get("Meta.PlayCount"), Number(track.PlayCount)); Add(Loc.Get("Meta.SkipCount"), Number(track.SkipCount)); Add(Loc.Get("Meta.LibrarySource"), track.Source);
        if (data.File?.AdditionalFields.Count > 0)
        {
            rows.Children.Add(new Separator { Margin = new Thickness(0, 8) });
            StackPanel tags = new() { Spacing = 6 };
            foreach (var pair in data.File.AdditionalFields.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)) Add(pair.Key, pair.Value, tags);
            rows.Children.Add(new Expander
            {
                Header = Loc.Format("Meta.AdditionalTags", data.File.AdditionalFields.Count.ToString(CultureInfo.CurrentCulture)),
                Content = tags,
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
        }
        return rows;
    }
}
