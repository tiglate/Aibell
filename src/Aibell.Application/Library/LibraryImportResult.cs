namespace Aibell.Application.Library;

public sealed record LibraryImportIssue(Uri Location, string Message);

public sealed record LibraryImportResult(
    int Discovered,
    int Imported,
    int Removed,
    IReadOnlyList<LibraryImportIssue> Issues);
