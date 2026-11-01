using Aibell.Desktop.Localization;

namespace Aibell.Desktop.ViewModels;

// The shuffle, repeat and mute buttons are icons; these flags say which look to show. The labels stay the accessible names.
public sealed partial class MainWindowViewModel
{
    public bool IsShuffleOn => ShuffleButtonText == Loc.Get("Playback.ShuffleOn");

    public bool IsRepeatActive => RepeatButtonText != Loc.Get("Playback.RepeatOff");

    public bool IsRepeatOne => RepeatButtonText == Loc.Get("Playback.RepeatOne");

    public bool IsMutedState => MuteButtonText == Loc.Get("Playback.Unmute");

    public bool IsNotMutedState => !IsMutedState;

    partial void OnShuffleButtonTextChanged(string value) => OnPropertyChanged(nameof(IsShuffleOn));

    partial void OnRepeatButtonTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsRepeatActive));
        OnPropertyChanged(nameof(IsRepeatOne));
    }

    partial void OnMuteButtonTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsMutedState));
        OnPropertyChanged(nameof(IsNotMutedState));
    }
}
