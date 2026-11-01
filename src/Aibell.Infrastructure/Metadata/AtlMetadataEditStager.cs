using System.Security.Cryptography;
using System.Text.Json;
using Track = ATL.Track;
using Aibell.Application.Library;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Metadata;

public sealed class AtlMetadataEditStager : IMetadataEditStager
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".flac", ".m4a", ".m4b", ".ogg", ".opus", ".mp3", ".wav" };
    private readonly Action<string, string, MetadataEdit, CancellationToken> write;

    public AtlMetadataEditStager() : this(WriteAndValidate) { }

    internal AtlMetadataEditStager(Action<string, string, MetadataEdit, CancellationToken> write) => this.write = write;

    public async Task<IStagedMetadataEdit> StageAsync(Uri location, MetadataEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(edit);
        edit.Validate();
        if (edit.Artwork?.ImageData is { } image) edit = edit with { Artwork = edit.Artwork with { ImageData = image.ToArray() } };
        if (!location.IsAbsoluteUri || !location.IsFile) throw new ArgumentException("Metadata editing requires a local file URI.", nameof(location));
        string source = Path.GetFullPath(location.LocalPath);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
            throw new NotSupportedException("This format has not passed the staged metadata editing gate.");
        TrackFileEdit.ValidateFormat(edit, extension);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new NotSupportedException("Metadata edit staging does not accept symbolic links.");
        cancellationToken.ThrowIfCancellationRequested();
        string staged = Path.Combine(Path.GetDirectoryName(source)!, $".aibell-edit-{Guid.NewGuid():N}.stage");
        bool created = false;
        bool journalCreated = false;
        FileStream? lease = null;
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            lease = MetadataEditJournal.AcquireLease(staged);
            string initialHash = await HashAsync(source, cancellationToken).ConfigureAwait(false);
            MetadataEditJournal journal = new(1, Path.GetFileName(source), initialHash, null, false, edit);
            journal.Save(staged, first: true);
            journalCreated = true;
            string sourceHash;
            await using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (FileStream output = new(staged, options))
            {
                created = true;
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Position = 0;
                sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false));
            }
            if (sourceHash != initialHash) throw new IOException("The source changed while its preview was being copied.");

            await Task.Run(() =>
            {
                write(staged, extension, edit, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            string stageHash = await HashAsync(staged, cancellationToken).ConfigureAwait(false);
            StagedEdit result = new(new Uri(source), new Uri(staged), sourceHash, stageHash, edit, lease);
            await result.VerifyUnchangedAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            (journal with { StageHash = stageHash, Ready = true }).Save(staged, first: false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            try
            {
                if (created) File.Delete(staged);
                if (journalCreated) File.Delete(MetadataEditJournal.JournalPath(staged));
            }
            finally
            {
                if (lease is not null)
                {
                    lease.Dispose();
                    File.Delete(MetadataEditJournal.LeasePath(staged));
                }
            }
            throw;
        }
    }

    private static void WriteAndValidate(string path, string extension, MetadataEdit edit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        MpegAudioData? originalAudio = extension == ".mp3" ? MpegAudioData.Read(stream, cancellationToken) : null;
        RiffWaveInfo? originalWave = extension == ".wav" ? RiffWaveInfo.Read(stream, cancellationToken) : null;
        stream.Position = 0;
        Track track = new(stream, extension);
        if (track.DurationMs <= 0) throw new InvalidDataException("The source contains no readable audio.");
        if (track.MetadataFormats.Count > 1 && !IsId3v2WithId3v1(track.MetadataFormats.Select(format => format.Name), originalAudio))
            throw new NotSupportedException("Editing files with multiple tag formats has not been validated.");
        if (AtlTagFields.UnsafeVendorRestriction(track.AdditionalFields) is { } unsafeVendor) throw new NotSupportedException(unsafeVendor);
        Dictionary<string, string> fields = new(track.AdditionalFields);
        string lyrics = LyricsText(track);
        string chapters = JsonSerializer.Serialize(track.Chapters);
        string chapterDescription = track.ChaptersTableDescription;
        List<string> pictures = track.EmbeddedPictures.Select(picture => Convert.ToHexString(SHA256.HashData(picture.PictureData))).ToList();
        if (edit.Artwork is { } artwork)
        {
            if (artwork.PictureIndex is { } index)
            {
                if (index >= pictures.Count || pictures[index] != artwork.OriginalHash)
                    throw new IOException("The selected embedded picture changed. Reopen track properties.");
                if (artwork.ImageData is null) { track.EmbeddedPictures.RemoveAt(index); pictures.RemoveAt(index); }
                else
                {
                    ATL.PictureInfo replacement = ATL.PictureInfo.fromBinaryData(artwork.ImageData, track.EmbeddedPictures[index].PicType);
                    replacement.Description = track.EmbeddedPictures[index].Description;
                    track.EmbeddedPictures[index] = replacement;
                    pictures[index] = Convert.ToHexString(SHA256.HashData(artwork.ImageData));
                }
            }
            else
            {
                track.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(artwork.ImageData!, ATL.PictureInfo.PIC_TYPE.Front));
                pictures.Add(Convert.ToHexString(SHA256.HashData(artwork.ImageData!)));
            }
        }
        double duration = originalAudio?.Duration.TotalMilliseconds ?? track.DurationMs;
        double sampleRate = track.SampleRate;
        int channels = track.ChannelsArrangement.NbChannels;
        string originalDescription = track.Description, originalLongDescription = track.LongDescription;
        string originalSeriesTitle = track.SeriesTitle, originalSeriesPart = track.SeriesPart;
        var originalAlbumArtist = track.AlbumArtist;
        var originalComposer = track.Composer;
        var originalConductor = track.Conductor;
        var originalGrouping = track.Group;
        var originalCopyright = track.Copyright;
        var originalSortTitle = track.SortTitle;
        var originalSortArtist = track.SortArtist;
        var originalSortAlbumArtist = track.SortAlbumArtist;
        var originalSortAlbum = track.SortAlbum;
        var originalTrackTotal = track.TrackTotal;
        var originalDiscNumber = track.DiscNumber;
        var originalDiscTotal = track.DiscTotal;
        var originalBeatsPerMinute = track.BPM;
        string title = track.Title, artist = track.Artist, album = track.Album, genre = track.Genre, comment = track.Comment;
        int? year = track.Year, trackNumber = track.TrackNumber;
        if (edit.AlbumArtist is not null) track.AlbumArtist = edit.AlbumArtist;
        if (edit.Composer is not null) track.Composer = edit.Composer;
        if (edit.Conductor is not null) track.Conductor = edit.Conductor;
        if (edit.Grouping is not null) track.Group = edit.Grouping;
        if (edit.Copyright is not null) track.Copyright = edit.Copyright;
        if (edit.SortTitle is not null) track.SortTitle = edit.SortTitle;
        if (edit.SortArtist is not null) track.SortArtist = edit.SortArtist;
        if (edit.SortAlbumArtist is not null) track.SortAlbumArtist = edit.SortAlbumArtist;
        if (edit.SortAlbum is not null) track.SortAlbum = edit.SortAlbum;
        if (edit.TrackTotal is not null) track.TrackTotal = edit.TrackTotal;
        if (edit.DiscNumber is not null) track.DiscNumber = edit.DiscNumber;
        if (edit.DiscTotal is not null) track.DiscTotal = edit.DiscTotal;
        if (edit.BeatsPerMinute is not null) track.BPM = edit.BeatsPerMinute;
        if (edit.Description is not null) track.Description = edit.Description;
        if (edit.Keywords is not null) AtlTagFields.WriteText(track.AdditionalFields, edit.Keywords, AtlTagFields.KeywordNames);
        if (edit.LicenseUri is not null) AtlTagFields.WriteText(track.AdditionalFields, edit.LicenseUri, AtlTagFields.LicenseNames);
        if (edit.ShowName is not null)
        {
            AtlTagFields.WriteText(track.AdditionalFields, edit.ShowName, AtlTagFields.ShowNames);
        }
        if (edit.SeasonNumber is not null)
            AtlTagFields.WriteText(track.AdditionalFields, AtlTagFields.NumberText(edit.SeasonNumber)!, AtlTagFields.SeasonNames);
        if (edit.EpisodeNumber is not null)
        {
            AtlTagFields.WriteText(track.AdditionalFields, AtlTagFields.NumberText(edit.EpisodeNumber)!, AtlTagFields.EpisodeNames);
        }
        if (edit.Title is not null) track.Title = edit.Title;
        if (edit.Artist is not null) track.Artist = edit.Artist;
        if (edit.Album is not null) track.Album = edit.Album;
        if (edit.Genre is not null) track.Genre = edit.Genre;
        if (edit.Comment is not null) track.Comment = edit.Comment;
        if (edit.Year is not null) track.Year = edit.Year;
        if (edit.TrackNumber is not null) track.TrackNumber = edit.TrackNumber;
        if (track.TrackTotal is > 0 && track.TrackNumber > track.TrackTotal ||
            track.DiscTotal is > 0 && track.DiscNumber > track.DiscTotal)
            throw new ArgumentException("A track or disc number cannot exceed its total.");
        cancellationToken.ThrowIfCancellationRequested();
        if (originalWave is not null) originalWave.Write(stream, edit, cancellationToken);
        else if (!track.Save()) throw new IOException("ATL could not save the staged metadata edit.");
        cancellationToken.ThrowIfCancellationRequested();
        stream.Flush(flushToDisk: true);
        stream.Dispose();
        // Reopen through an independent handle for validation.
        using FileStream verification = File.OpenRead(path);
        MpegAudioData? savedAudio = extension == ".mp3" ? MpegAudioData.Read(verification, cancellationToken) : null;
        verification.Position = 0;
        Track read = new(verification, extension);
        Require(originalAudio?.Hash == savedAudio?.Hash, "MPEG audio frame bytes");
        // The stream constructor exposes raw titles without filename fallback.
        Require(read.AlbumArtist == (edit.AlbumArtist ?? originalAlbumArtist), "AlbumArtist");
        Require(read.Composer == (edit.Composer ?? originalComposer), "Composer");
        Require(read.Conductor == (edit.Conductor ?? originalConductor), "Conductor");
        Require(read.Group == (edit.Grouping ?? originalGrouping), "Grouping");
        Require(read.Copyright == (edit.Copyright ?? originalCopyright), "Copyright");
        Require(read.SortTitle == (edit.SortTitle ?? originalSortTitle), "SortTitle");
        Require(read.SortArtist == (edit.SortArtist ?? originalSortArtist), "SortArtist");
        Require(read.SortAlbumArtist == (edit.SortAlbumArtist ?? originalSortAlbumArtist), "SortAlbumArtist");
        Require(read.SortAlbum == (edit.SortAlbum ?? originalSortAlbum), "SortAlbum");
        Require(read.TrackTotal == (edit.TrackTotal ?? originalTrackTotal), "TrackTotal");
        Require(read.DiscNumber == (edit.DiscNumber ?? originalDiscNumber), "DiscNumber");
        Require(read.DiscTotal == (edit.DiscTotal ?? originalDiscTotal), "DiscTotal");
        Require(read.BPM == (edit.BeatsPerMinute ?? originalBeatsPerMinute), "BeatsPerMinute");
        Require(read.Description == (edit.Description ?? originalDescription), "description");
        Require(read.LongDescription == originalLongDescription, "long description");
        Require(read.SeriesTitle == originalSeriesTitle, "movement name");
        Require(read.SeriesPart == originalSeriesPart, "movement index");
        Require(read.Title == (edit.Title ?? title), "title");
        Require(read.Artist == (edit.Artist ?? artist), "artist");
        Require(read.Album == (edit.Album ?? album), "album");
        Require(read.Genre == (edit.Genre ?? genre), "genre");
        Require(read.Comment == (edit.Comment ?? comment), "comment");
        Require(read.Year.GetValueOrDefault() == (edit.Year ?? year).GetValueOrDefault(), "year");
        Require(read.TrackNumber.GetValueOrDefault() == (edit.TrackNumber ?? trackNumber).GetValueOrDefault(), "track number");
        Require(Math.Abs((savedAudio?.Duration.TotalMilliseconds ?? read.DurationMs) - duration) <= 1, "duration");
        Require(read.SampleRate == sampleRate, "sample rate");
        Require(read.ChannelsArrangement.NbChannels == channels, "channels");
        Require(LyricsText(read) == lyrics, "lyrics");
        Require(originalAudio?.HasId3v1 == savedAudio?.HasId3v1, "ID3v1 footer");
        Require(JsonSerializer.Serialize(read.Chapters) == chapters && read.ChaptersTableDescription == chapterDescription, "chapters");
        Require(fields.Where(field => !field.Key.Equals("USLT", StringComparison.OrdinalIgnoreCase) && !(originalWave is not null && RiffWaveInfo.IsEditedKey(field.Key, edit)) &&
                !(edit.Keywords is not null && AtlTagFields.MatchesAny(field.Key, AtlTagFields.KeywordNames)) &&
                !(edit.LicenseUri is not null && AtlTagFields.MatchesAny(field.Key, AtlTagFields.LicenseNames)) &&
                !(edit.ShowName is not null && AtlTagFields.MatchesAny(field.Key, AtlTagFields.ShowNames)) &&
                !(edit.SeasonNumber is not null && AtlTagFields.MatchesAny(field.Key, AtlTagFields.SeasonNames)) &&
                !(edit.EpisodeNumber is not null && AtlTagFields.MatchesAny(field.Key, AtlTagFields.EpisodeNames)))
            .All(field => read.AdditionalFields.TryGetValue(field.Key, out string? value) && value == field.Value), "additional fields");
        VerifyText(edit.Keywords, AtlTagFields.KeywordNames, "keywords");
        VerifyText(edit.LicenseUri, AtlTagFields.LicenseNames, "license URI");
        VerifyText(edit.ShowName, AtlTagFields.ShowNames, "show name");
        VerifyText(AtlTagFields.NumberText(edit.SeasonNumber), AtlTagFields.SeasonNames, "season number");
        VerifyText(AtlTagFields.NumberText(edit.EpisodeNumber), AtlTagFields.EpisodeNames, "episode number");

        void VerifyText(string? requested, string[] names, string label)
        {
            if (requested is null) return;
            Require((AtlTagFields.ReadText(read.AdditionalFields, names) ?? "") == requested.Trim() &&
                read.AdditionalFields.Where(field => AtlTagFields.MatchesAny(field.Key, names))
                    .All(field => field.Value.Trim() == requested.Trim()), label);
        }
        Require(read.EmbeddedPictures.Select(picture => Convert.ToHexString(SHA256.HashData(picture.PictureData))).SequenceEqual(pictures), "artwork");

        static void Require(bool matches, string field)
        {
            if (!matches) throw new InvalidDataException($"The staged metadata failed validation for {field}.");
        }
    }

    // An ID3v2 tag with the classic 128-byte ID3v1 footer: ATL keeps the footer in step with the ID3v2 text, and the footer is
    // outside the audio fingerprint, so the pair is as safe to rewrite as a lone ID3v2 tag.
    internal static bool IsId3v2WithId3v1(IEnumerable<string> formats, MpegAudioData? audio)
    {
        string[] names = [.. formats];
        return audio is { HasId3v1: true } && names.Length == 2 &&
            names.Any(name => name.StartsWith("ID3v2", StringComparison.Ordinal)) && names.Any(name => name.StartsWith("ID3v1", StringComparison.Ordinal));
    }

    // Lyrics as plain text. ATL first exposes an ID3 USLT frame as an additional field and, once it has rewritten the tag, as a
    // structured lyrics entry; the text is the same, so compare the text rather than the representation.
    private static string LyricsText(Track track)
    {
        List<string> texts = [.. track.Lyrics.Select(lyrics => lyrics.UnsynchronizedLyrics ?? ""), .. track.Lyrics.SelectMany(lyrics => lyrics.SynchronizedLyrics.Select(phrase => $"{phrase.TimestampStart}|{phrase.Text}"))];
        foreach (KeyValuePair<string, string> field in track.AdditionalFields)
            if (field.Key.Equals("USLT", StringComparison.OrdinalIgnoreCase)) texts.Add(field.Value);
        return JsonSerializer.Serialize(texts.Where(text => text.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private sealed class StagedEdit(Uri source, Uri stagedFile, string sourceHash, string stagedHash, MetadataEdit edit, FileStream lease) : IStagedMetadataEdit
    {
        private bool disposed;
        public Uri Source { get; } = source;
        public Uri StagedFile { get; } = stagedFile;
        public string SourceHash { get; } = sourceHash;
        public string StagedHash { get; } = stagedHash;
        public MetadataEdit Edit { get; } = edit;

        public async Task VerifyUnchangedAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (await HashAsync(Source.LocalPath, cancellationToken).ConfigureAwait(false) != SourceHash ||
                await HashAsync(StagedFile.LocalPath, cancellationToken).ConfigureAwait(false) != StagedHash)
                throw new IOException("The source or staged file changed; prepare the edit again.");
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                try
                {
                    File.Delete(StagedFile.LocalPath);
                    File.Delete(MetadataEditJournal.JournalPath(StagedFile.LocalPath));
                }
                finally
                {
                    lease.Dispose();
                    File.Delete(MetadataEditJournal.LeasePath(StagedFile.LocalPath));
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
