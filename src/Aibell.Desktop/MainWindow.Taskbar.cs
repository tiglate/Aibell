using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Aibell.Application.Playback;
using Aibell.Desktop.Localization;
using Aibell.Platform.Windows;

namespace Aibell.Desktop;

/// <summary>Windows taskbar button: playback progress and Previous / Play-Pause / Next in the hover thumbnail.</summary>
public sealed partial class MainWindow
{
    private WindowsTaskbar? taskbar;
    private bool taskbarUpdatePending;

    private void InitializeTaskbar()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The shell announces the taskbar button with a message after the window is first shown, so the hook goes in now.
        Win32Properties.AddWndProcHookCallback(this, new Win32Properties.CustomWndProcHookCallback(TaskbarWindowProc));
        viewModel.PropertyChanged += TaskbarSourceChanged;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= TaskbarSourceChanged;
            taskbar?.Dispose();
            taskbar = null;
        };
    }

    private nint TaskbarWindowProc(nint window, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (taskbar is null && message == WindowsTaskbar.ButtonCreatedMessage)
        {
            taskbar = new WindowsTaskbar(window);
            taskbar.ButtonClicked += TaskbarButtonClicked;
            taskbar.Apply(CurrentTaskbarState());
        }
        if (taskbar?.HandleMessage(message, wParam) == true) handled = true;
        return 0;
    }

    private void TaskbarSourceChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (taskbarUpdatePending || taskbar is null || eventArgs.PropertyName is not
            ("PlaybackState" or "PositionSeconds" or "DurationSeconds" or "IsRadioPlayback" or "CanPlay" or "CanGoPrevious" or "CanGoNext" or "PlaybackButtonText")) return;
        taskbarUpdatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            taskbarUpdatePending = false;
            taskbar?.Apply(CurrentTaskbarState());
        });
    }

    private TaskbarState CurrentTaskbarState()
    {
        // The same state the system media controls publish, so both surfaces enable the same commands.
        MediaSessionState state = viewModel.GetMediaSessionState();
        // Whole seconds: the shell bar does not need finer steps and this avoids a shell call per tick.
        TimeSpan position = TimeSpan.FromSeconds(Math.Floor(state.Playback.Position.TotalSeconds));
        TimeSpan duration = state.Playback.Track?.Duration ?? TimeSpan.Zero;
        (TaskbarProgressMode mode, ulong completed, ulong total) = TaskbarState.ProgressFor(state.Playback.Status, position, duration, viewModel.IsRadioPlayback);
        return new(mode, completed, total, state.CanGoPrevious, state.CanPlay, state.CanGoNext, state.Playback.Status == PlaybackStatus.Playing,
            Loc.Get("Main.Previous"), viewModel.PlaybackButtonText, Loc.Get("Main.Next"));
    }

    private async void TaskbarButtonClicked(TaskbarButton button)
    {
        MediaSessionAction action = button switch
        {
            TaskbarButton.Previous => MediaSessionAction.Previous,
            TaskbarButton.Next => MediaSessionAction.Next,
            _ => MediaSessionAction.PlayPause
        };
        await RunUserActionAsync(() => viewModel.ExecuteMediaSessionCommandAsync(new MediaSessionCommand(action), lifetimeCancellation.Token));
    }
}
