namespace Aibell.Application.Settings;

public interface IApplicationSettingsStore
{
    Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken = default);
}
