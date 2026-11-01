namespace Aibell.Desktop.Services;

/// <summary>
/// One problem as the user sees it: a short <see cref="Title"/>, a plain-language <see cref="Message"/> and, behind the Details button,
/// the technical <see cref="Details"/> that were also written to the log.
/// </summary>
/// <param name="Source">Where it came from: <c>ui</c>, <c>task</c>, <c>appdomain</c>, <c>fatal</c>, <c>playback</c> or <c>previous-session</c>.</param>
/// <param name="ShowDialog">False for background noise that only deserves a log line and a status message.</param>
public sealed record ErrorReport(string Title, string Message, string Details, string Source, bool ShowDialog = true);
