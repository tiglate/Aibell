using Aibell.Application.Settings;

namespace Aibell.Desktop.Services;

/// <summary>Which player this run started with: the saved choice, unless the built-in player could not load and VLC replaced it.</summary>
internal sealed record ActivePlaybackEngine(PlaybackEngineKind Kind);
