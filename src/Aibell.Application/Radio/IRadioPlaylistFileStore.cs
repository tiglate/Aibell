using Aibell.Domain.Radio;

namespace Aibell.Application.Radio;

public sealed record RadioPlaylistIssue(int Entry, string Message);
public sealed record RadioPlaylistContent(IReadOnlyList<RadioStation> Stations, IReadOnlyList<RadioPlaylistIssue> Issues);
public sealed record RadioPlaylistImportPreview(Uri Source, IReadOnlyList<RadioStation> Stations,
    IReadOnlyList<RadioPlaylistIssue> Issues, int Duplicates);
public sealed record RadioStationImportResult(int Imported, int Duplicates);

public interface IRadioPlaylistFileStore
{
    Task<RadioPlaylistContent> ReadAsync(Uri source, CancellationToken cancellationToken = default);
}
