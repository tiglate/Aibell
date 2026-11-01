using Aibell.Application.Library;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Application.Tests.Library;

[TestFixture]
public sealed class TrackFileEditTests
{
    [Test]
    public void Between_ChangedAndClearedFields_ProducesOnlyRequestedPatch()
    {
        TrackMetadata original = new(Title: "Title", Composer: "Composer", TrackNumber: 1, TrackTotal: 10);
        MetadataEdit? patch = TrackFileEdit.Between(original, original with { Title = "", Composer = "New composer", DiscTotal = 2 });
        Assert.That(patch, Is.EqualTo(new MetadataEdit(Title: "", Composer: "New composer", DiscTotal: 2)));
    }
    [Test]
    public void Between_RatingOnly_DoesNotWriteTags()
    {
        Assert.That(TrackFileEdit.Between(new(), new(Rating: 5)), Is.Null);
    }
    [Test]
    public void Between_KeywordsAndLicense_ProducesChangedOnlyPatch()
    {
        TrackMetadata original = new(Keywords: "Old keywords", LicenseUri: "https://example.test/license");
        Assert.That(TrackFileEdit.Between(original, original with { Keywords = "", LicenseUri = "https://example.test/new" }),
            Is.EqualTo(new MetadataEdit(Keywords: "", LicenseUri: "https://example.test/new")));
        Assert.That(TrackFileEdit.Between(original, original), Is.Null);
        Assert.That(() => new MetadataEdit(Keywords: "bad\0keywords").Validate(), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => new MetadataEdit(LicenseUri: "bad\0license").Validate(), Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Between_PodcastFields_MapsChangesAndRejectsInvalidValues()
    {
        TrackMetadata original = new(ShowName: "Old show", SeasonNumber: 2, EpisodeNumber: 7);
        Assert.That(TrackFileEdit.Between(original, original with { ShowName = "", EpisodeNumber = 0 }),
            Is.EqualTo(new MetadataEdit(ShowName: "", EpisodeNumber: 0)));
        Assert.That(() => new MetadataEdit(ShowName: "bad\0show").Validate(), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => new MetadataEdit(SeasonNumber: -1).Validate(), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => new MetadataEdit(EpisodeNumber: -1).Validate(), Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Between_UnrepresentableBpm_RequiresLibraryDestination()
    {
        Assert.That(() => TrackFileEdit.Between(new(), new(BeatsPerMinute: double.MaxValue)), Throws.TypeOf<NotSupportedException>());
    }
    [Test]
    public void ValidateFormat_Wave_RejectsEveryExtendedFieldAndAllowsBasicPatch()
    {
        TrackFileEdit.ValidateFormat(new(Title: "", Artist: "A", Album: "B", Genre: "G", Comment: "C", Year: 0, TrackNumber: 0), ".WAV");
        foreach (System.Reflection.PropertyInfo property in typeof(MetadataEdit).GetProperties())
        {
            if (new[] { "Title", "Artist", "Album", "Genre", "Comment", "Year", "TrackNumber" }.Contains(property.Name)) continue;
            MetadataEdit edit = new(Title: "Title");
            object value = property.PropertyType == typeof(string) ? "Value" :
                property.PropertyType == typeof(int?) ? 1 :
                property.PropertyType == typeof(float?) ? 1f : new ArtworkChange();
            property.SetValue(edit, value);
            Assert.That(() => TrackFileEdit.ValidateFormat(edit, ".wav"), Throws.TypeOf<NotSupportedException>(), property.Name);
        }
    }

    [Test]
    public void ReconcileRestored_Wave_RestoresOnlyWritableFieldsAndKeepsLocalDetails()
    {
        TrackMetadata library = new(Title: "Edited", Composer: "Local composer", AlbumArtist: "Local artist", Description: "Local description",
            TrackTotal: 12, DiscNumber: 2, Rating: 5, SortTitle: "Local sort", Keywords: "Local keywords", ShowName: "Local show");
        TrackMetadata restored = new(Title: "Original", Artist: "Artist", Album: "Album", Genre: "Genre", Year: 2026, TrackNumber: 1, Comment: "Comment");
        TrackMetadata result = TrackFileEdit.ReconcileRestored(library, restored, ".WAV");
        Assert.That(result, Is.EqualTo(library with
        {
            Title = restored.Title,
            Artist = restored.Artist,
            Album = restored.Album,
            Genre = restored.Genre,
            Year = restored.Year,
            TrackNumber = restored.TrackNumber,
            Comment = restored.Comment
        }));
    }

    [TestCase(".flac")]
    [TestCase(".m4a")]
    public void ReconcileRestored_OtherFormats_KeepsRatingAndOnlyUnsupportedSortFields(string extension)
    {
        TrackMetadata library = new(Rating: 4, SortTitle: "Local", Grouping: "Local group");
        TrackMetadata restored = new(Title: "Original", SortTitle: "File", Grouping: "File group", Composer: "File composer");
        TrackMetadata result = TrackFileEdit.ReconcileRestored(library, restored, extension);
        Assert.That(result, Is.EqualTo(extension == ".m4a" ? restored with { Rating = 4 } : restored with { Rating = 4, SortTitle = library.SortTitle, Grouping = library.Grouping }));
    }
}
