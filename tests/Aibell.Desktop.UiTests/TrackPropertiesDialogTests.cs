using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aibell.Application.Library;
using Aibell.Desktop.ViewModels;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class TrackPropertiesDialogTests
{
    private static readonly string[] TabNames = ["Basic", "Extra", "Sorting", "Properties"];
    private Window owner = null!;
    private TrackPropertiesDialog dialog = null!;

    [TearDown]
    public void TearDown() { dialog?.Close(); owner?.Close(); }

    [AvaloniaTest]
    public async Task Dialog_ChangesAcrossTabs_SavesAllFieldsWithKeyboard()
    {
        TrackMetadata? saved = null;
        Task<bool> result = Open((metadata, _) => { saved = metadata; return Task.CompletedTask; });
        Assert.That(Save().IsEnabled, Is.False);
        Input("Title").Text = "Edited 日本語";
        Input("Year").Text = "2019";
        Tabs().SelectedIndex = 1; Pump();
        Input("Composer").Text = "Jimmy Bain, Ronnie James Dio";
        Input("Conductor").Text = "A conductor";
        Input("Copyright").Text = "© 2026";
        Tabs().SelectedIndex = 2; Pump();
        Input("SortArtist").Text = "Alternate artist";
        Assert.That(Save().IsEnabled, Is.True);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(saved!.Title, Is.EqualTo("Edited 日本語"));
            Assert.That(saved.Year, Is.EqualTo(2019));
            Assert.That(saved.Composer, Is.EqualTo("Jimmy Bain, Ronnie James Dio"));
            Assert.That(saved.Conductor, Is.EqualTo("A conductor"));
            Assert.That(saved.SortArtist, Is.EqualTo("Alternate artist"));
            Assert.That(saved.TrackTotal, Is.EqualTo(9));
        });
    }

    [AvaloniaTest]
    public async Task Dialog_InvalidNumbersAndCancel_DoNotSave()
    {
        int calls = 0;
        Task<bool> result = Open((_, _) => { calls++; return Task.CompletedTask; });
        Input("Year").Text = "10000";
        Pump();
        Assert.That(Save().IsEnabled, Is.False);
        Input("Year").Text = "1983";
        Input("TrackNumber").Text = "10";
        Pump();
        Assert.That(Save().IsEnabled, Is.False);
        Input("TrackTotal").Text = "12";
        Pump();
        Assert.That(Save().IsEnabled, Is.True);
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(calls, Is.Zero);
    }

    [AvaloniaTest]
    public void Dialog_SaveFailure_RetainsEditsAndAllowsRetry()
    {
        Open((_, _) => Task.FromException(new IOException("Test storage failure")));
        Input("Title").Text = "Retain this edit";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Pump();
        Assert.That(dialog.IsVisible, Is.True);
        Assert.That(Input("Title").Text, Is.EqualTo("Retain this edit"));
        Assert.That(Save().IsEnabled, Is.True);
        Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "MetadataValidation").Text,
            Does.Contain("Test storage failure"));
    }

    [AvaloniaTest]
    public async Task Dialog_SaveInProgress_PreventsDiscardingUnsavedInput()
    {
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> result = Open((_, _) => completed.Task);
        Input("Title").Text = "Pending save"; Pump();
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        try
        {
            Assert.That(Tabs().IsEnabled, Is.False);
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.That(result.IsCompleted, Is.False);
        }
        finally { completed.TrySetResult(); }
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
    }

    [AvaloniaTest]
    public void Dialog_BothThemesAndAllTabs_CentersInputsAndCloseIconAtMinimumSize()
    {
        Open((_, _) => Task.CompletedTask);
        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
        Assert.That(Tabs().Items.OfType<TabItem>().Select(tab => tab.Header), Is.EqualTo(TabNames));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Avalonia.Application.Current!.RequestedThemeVariant = theme;
            for (int index = 0; index < 4; index++)
            {
                Tabs().SelectedIndex = index; Pump();
                foreach (TextBox input in dialog.GetVisualDescendants().OfType<TextBox>())
                    Assert.That(input.VerticalContentAlignment, Is.EqualTo(VerticalAlignment.Center));
                foreach (ComboBox input in dialog.GetVisualDescendants().OfType<ComboBox>())
                    Assert.That(input.VerticalContentAlignment, Is.EqualTo(VerticalAlignment.Center));
            }
            Button close = dialog.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Close track properties");
            PathIcon icon = (PathIcon)close.Content!;
            Point origin = icon.TranslatePoint(default, close)!.Value;
            Assert.That(origin.X + icon.Bounds.Width / 2, Is.EqualTo(close.Bounds.Width / 2).Within(0.5));
            Assert.That(origin.Y + icon.Bounds.Height / 2, Is.EqualTo(close.Bounds.Height / 2).Within(0.5));
        }
        Assert.That(dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == "FLAC Audio"), Is.True);
    }

    [AvaloniaTest]
    public async Task Dialog_FileDestination_IsExplicitAndPassedToSave()
    {
        bool? file = null;
        Task<bool> result = Open((_, toFile, _) => { file = toFile; return Task.CompletedTask; }, canWriteFile: true);
        ComboBox destination = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "MetadataSaveDestination");
        Assert.That(destination.SelectedIndex, Is.Zero);
        destination.SelectedIndex = 1;
        Input("Title").Text = "File title"; Pump();
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(file, Is.True);
    }

    [AvaloniaTest]
    public void Dialog_FileDestination_RejectsUnsupportedFieldsAndAllowsLibraryFallback()
    {
        Open((_, _, _) => Task.CompletedTask, canWriteFile: true);
        ComboBox destination = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "MetadataSaveDestination");
        destination.SelectedIndex = 1;
        Tabs().SelectedIndex = 2; Pump(); Input("SortTitle").Text = "Alternate title"; Pump();
        Assert.That(Save().IsEnabled, Is.False);
        Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "MetadataValidation").Text,
            Does.Contain("M4A"));
        destination.SelectedIndex = 0; Pump();
        Assert.That(Save().IsEnabled, Is.True);
        destination.SelectedIndex = 1; Input("SortTitle").Text = "";
        Tabs().SelectedIndex = 1; Pump(); Input("Keywords").Text = "Keywords";
        Input("LicenseUri").Text = "https://example.com/license"; Pump();
        Assert.That(Save().IsEnabled, Is.True);
        dialog.GetVisualDescendants().OfType<Expander>().Single(expander => Equals(expander.Header, "Show and episode details")).IsExpanded = true;
        Pump(); Input("ShowName").Text = "Show"; Input("SeasonNumber").Text = "2"; Input("EpisodeNumber").Text = "7"; Pump();
        Assert.That(Save().IsEnabled, Is.True);
        Input("Grouping").Text = "Group"; Pump();
        Assert.That(Save().IsEnabled, Is.False);
        destination.SelectedIndex = 0; Pump(); Assert.That(Save().IsEnabled, Is.True);
    }

    [AvaloniaTest]
    public async Task Dialog_ArtworkOnlyEdit_RequiresExplicitFileSaveAndCanCancel()
    {
        Track track = new(Guid.NewGuid(), new Uri("file:///music/track.flac"), "Title", "Artist", "Album", TimeSpan.FromSeconds(1));
        TrackMetadata metadata = TrackMetadata.FromTrack(track); ArtworkChange? captured = null;
        owner = new Window(); owner.Show();
        dialog = new(new TrackPropertiesData(track, metadata, null, null, CanWriteFile: true),
            (_, _, artwork, _) => { captured = artwork; return Task.CompletedTask; });
        Task<bool> result = dialog.ShowDialog<bool>(owner); Pump();
        byte[] image = File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "cover.png"));
        dialog.SetArtwork(image); Pump(); Assert.That(Save().IsEnabled, Is.False);
        dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "MetadataSaveDestination").SelectedIndex = 1;
        Pump(); Assert.That(Save().IsEnabled, Is.True);
        dialog.RemoveEmbeddedArtwork(); Pump(); Assert.That(Save().IsEnabled, Is.False);
        dialog.SetArtwork(image); Pump(); dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(captured!.ImageData, Is.EqualTo(image)); Assert.That(captured.PictureIndex, Is.Null);
    }

    [AvaloniaTest]
    public async Task Dialog_Wave_BasicFileSaveAndLibraryFallbackKeepExtendedInput()
    {
        Track track = new(Guid.NewGuid(), new Uri("file:///music/track.wav"), "Title", "Artist", "Album", TimeSpan.FromSeconds(1));
        TrackMetadata metadata = TrackMetadata.FromTrack(track);
        TrackMetadata? captured = null; bool? toFile = null;
        owner = new Window(); owner.Show();
        dialog = new(new TrackPropertiesData(track, metadata, null, null, CanWriteFile: true),
            (value, file, _, _) => { captured = value; toFile = file; return Task.CompletedTask; });
        Task<bool> result = dialog.ShowDialog<bool>(owner); Pump();
        Assert.That(dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "AddTrackArtwork"), Is.False);
        Assert.That(() => dialog.SetArtwork([1, 2, 3]), Throws.TypeOf<NotSupportedException>());
        ComboBox destination = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "MetadataSaveDestination");
        destination.SelectedIndex = 1; Input("Title").Text = "Wave title"; Pump();
        Assert.That(Save().IsEnabled, Is.True);
        Tabs().SelectedIndex = 1; Pump(); Input("Composer").Text = "Library composer"; Pump();
        Assert.That(Save().IsEnabled, Is.False);
        Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "MetadataValidation").Text,
            Does.Contain("WAV"));
        destination.SelectedIndex = 0; Pump(); Assert.That(Save().IsEnabled, Is.True);
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(captured!.Composer, Is.EqualTo("Library composer"));
        Assert.That(captured.Title, Is.EqualTo("Wave title"));
        Assert.That(toFile, Is.False);
    }

    private Task<bool> Open(Func<TrackMetadata, CancellationToken, Task> save) =>
        Open((metadata, _, token) => save(metadata, token));

    private Task<bool> Open(Func<TrackMetadata, bool, CancellationToken, Task> save, bool canWriteFile = false)
    {
        Track track = new(Guid.NewGuid(), new Uri("file:///music/Dio/Holy%20Diver/Stand%20Up%20and%20Shout.flac"),
            "Stand Up and Shout", "Dio", "Holy Diver", TimeSpan.FromSeconds(198), Genre: "Metal", AlbumArtist: "Dio",
            Year: 1983, TrackNumber: 1, DiscNumber: 1, FileSize: 22873958, SampleRate: 44100, Channels: 2, BitRate: 922);
        TrackMetadata metadata = TrackMetadata.FromTrack(track) with { TrackTotal = 9, DiscTotal = 1 };
        TrackFileDetails details = new(metadata, 16, "FLAC Audio", ["Xiph", "FLAC"], new Dictionary<string, string>());
        owner = new Window(); owner.Show();
        dialog = new(new TrackPropertiesData(track, metadata, details, null, CanWriteFile: canWriteFile), save);
        Task<bool> result = dialog.ShowDialog<bool>(owner); Pump(); return result;
    }
    private TextBox Input(string key) => dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "Metadata" + key);
    private TabControl Tabs() => dialog.GetVisualDescendants().OfType<TabControl>().Single();
    private Button Save() => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveTrackProperties");
    private static void Pump() => Dispatcher.UIThread.RunJobs();
}
