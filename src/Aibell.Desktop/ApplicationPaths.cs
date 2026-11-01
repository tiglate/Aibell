namespace Aibell.Desktop;

internal static class ApplicationPaths
{
    public static string ResolveDataDirectory(string? configuredPath, string localApplicationData)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath))
                throw new ArgumentException("AIBELL_DATA_DIRECTORY must be an absolute folder path.", nameof(configuredPath));
            return Path.GetFullPath(configuredPath);
        }
        if (string.IsNullOrWhiteSpace(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
            throw new InvalidOperationException("The application data folder is unavailable. Set AIBELL_DATA_DIRECTORY to an absolute folder path.");
        return Path.Combine(localApplicationData, "Aibell");
    }
}
