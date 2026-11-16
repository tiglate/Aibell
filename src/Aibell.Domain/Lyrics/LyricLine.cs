namespace Aibell.Domain.Lyrics;

/// <summary>One line of lyrics. <see cref="Start"/> is null for unsynchronized lines.</summary>
public sealed record LyricLine(TimeSpan? Start, string Text);
