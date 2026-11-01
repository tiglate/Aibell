namespace Aibell.Domain.Audiobooks;

/// <summary>A local audiobook on the shelf. Identity is the canonical file location; the saved position is user-saved only.</summary>
public sealed record AudiobookShelfEntry(Uri Location, string Title, string Author, TimeSpan Duration, TimeSpan SavedPosition,
    DateTimeOffset Added, DateTimeOffset? PositionSaved = null)
{
    public const int MaximumTitleLength = 500;
    public const int MaximumAuthorLength = 500;

    public bool HasSavedPosition => PositionSaved is not null && SavedPosition > TimeSpan.Zero;

    public void Validate()
    {
        if (!AudiobookLocation.IsSupported(Location)) throw new ArgumentException("An audiobook must be a local audio file location.");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > MaximumTitleLength)
            throw new ArgumentException($"An audiobook title needs 1–{MaximumTitleLength} characters.");
        if (Author is null || Author.Length > MaximumAuthorLength) throw new ArgumentException("The audiobook author is too long.");
        if (Duration < TimeSpan.Zero || Duration > ResumeRules.MaximumDuration) throw new ArgumentException("The audiobook duration is out of range.");
        if (SavedPosition < TimeSpan.Zero || SavedPosition > Duration) throw new ArgumentException("A saved position must lie within the duration.");
    }
}
