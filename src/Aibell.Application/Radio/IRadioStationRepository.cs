using Aibell.Domain.Radio;

namespace Aibell.Application.Radio;

public interface IRadioStationRepository
{
    Task<IReadOnlyList<RadioStation>> ListStationsAsync(CancellationToken cancellationToken = default);
    Task AddStationAsync(RadioStation station, CancellationToken cancellationToken = default);
    // Save the complete selection in one transaction; existing stream URLs and their history win.
    Task<RadioStationImportResult> ImportStationsAsync(IReadOnlyList<RadioStation> stations, CancellationToken cancellationToken = default);
    Task UpdateStationAsync(RadioStation station, CancellationToken cancellationToken = default);
    Task RemoveStationAsync(Guid id, CancellationToken cancellationToken = default);
    Task RecordStationPlayedAsync(Guid id, DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
}
