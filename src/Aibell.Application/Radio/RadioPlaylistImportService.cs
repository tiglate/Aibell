namespace Aibell.Application.Radio;

public sealed class RadioPlaylistImportService(IRadioPlaylistFileStore files, IRadioStationRepository stations)
{
    public async Task<RadioPlaylistImportPreview> PrepareAsync(Uri source, CancellationToken cancellationToken = default)
    {
        RadioPlaylistContent content = await files.ReadAsync(source, cancellationToken).ConfigureAwait(false);
        var existing = await stations.ListStationsAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> streams = new(existing.Select(station => station.Stream.AbsoluteUri), StringComparer.Ordinal);
        var unique = content.Stations.Where(station => streams.Add(station.Stream.AbsoluteUri)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(source, unique, content.Issues, content.Stations.Count - unique.Length);
    }

    public Task<RadioStationImportResult> ImportAsync(RadioPlaylistImportPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return stations.ImportStationsAsync(preview.Stations, cancellationToken);
    }
}
