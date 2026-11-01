using Aibell.Desktop.Localization;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aibell.Desktop;

public sealed partial class MainWindow
{
    private const double SeekSmallStepSeconds = 5.0;
    private const double SeekLargeStepSeconds = 15.0;
    private const double VolumeSmallStep = 0.05;
    private const double VolumeLargeStep = 0.10;
    private static string SeekSurfaceHelpText => Loc.Get("Transport.KeysSeek");
    private static string VolumeSurfaceHelpText => Loc.Get("Transport.KeysVolume");

    private readonly HashSet<Task> transportKeyboardOperations = [];
    private readonly object transportKeyboardGate = new();
    private readonly SemaphoreSlim keyboardSeekGate = new(1, 1);
    private readonly SemaphoreSlim keyboardVolumeGate = new(1, 1);
    private Border? seekFocusRing;
    private Border? volumeFocusRing;
    private bool seekDisabledByUnsafeState;
    private bool volumeDisabledByUnsafeState;
    private bool isApplyingTransportVolume;
    private bool keyboardInitialized;
    private bool keyboardDisposed;

    private void InitializeKeyboard()
    {
        if (keyboardInitialized) return;
        keyboardInitialized = true;

        PositionSeekSurface.Focusable = true;
        PositionSeekSurface.FocusAdorner = null;
        PositionSeekSurface.MinHeight = 14;
        ToolTip.SetTip(PositionSeekSurface, Loc.Get("Main.SeekTip"));
        AutomationProperties.SetHelpText(PositionSeekSurface, SeekSurfaceHelpText);
        AutomationProperties.SetName(PositionProgress, Loc.Get("Transport.PositionAutomation"));
        seekFocusRing = CreateTransportFocusRing("PositionSeekFocusRing", "#26D52268");
        PositionSeekSurface.Children.Insert(0, seekFocusRing);
        PositionSeekSurface.GotFocus += OnSeekSurfaceFocusChanged;
        PositionSeekSurface.LostFocus += OnSeekSurfaceFocusChanged;
        PositionSeekSurface.AddHandler(
            PointerPressedEvent,
            OnTransportSurfacePointerChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        PositionSeekSurface.AddHandler(
            PointerReleasedEvent,
            OnTransportSurfacePointerChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        PositionSeekSurface.ConfigureRange(RequestSeekFromAutomation);

        VolumeSurface.Focusable = true;
        VolumeSurface.FocusAdorner = null;
        VolumeSurface.ClipToBounds = false;
        ToolTip.SetTip(VolumeSurface, Loc.Get("Main.VolumeTip"));
        AutomationProperties.SetHelpText(VolumeSurface, VolumeSurfaceHelpText);
        AutomationProperties.SetName(VolumeProgress, Loc.Get("Transport.VolumeAutomation"));
        volumeFocusRing = CreateTransportFocusRing("VolumeFocusRing", "#28FFFFFF");
        VolumeSurface.Children.Insert(0, volumeFocusRing);
        VolumeSurface.GotFocus += OnVolumeSurfaceFocusChanged;
        VolumeSurface.LostFocus += OnVolumeSurfaceFocusChanged;
        VolumeSurface.AddHandler(
            PointerPressedEvent,
            OnTransportSurfacePointerChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        VolumeSurface.AddHandler(
            PointerReleasedEvent,
            OnTransportSurfacePointerChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        VolumeSurface.ConfigureRange(RequestVolumeFromAutomation);

        viewModel.PropertyChanged += OnKeyboardViewModelPropertyChanged;
        UpdateTransportAutomationProperties();
        UpdateTransportFocusRings();
    }

    private void DisposeKeyboard()
    {
        if (Interlocked.Exchange(ref keyboardDisposed, true)) return;
        PositionSeekSurface.DisposeRange();
        VolumeSurface.DisposeRange();
        viewModel.PropertyChanged -= OnKeyboardViewModelPropertyChanged;
        viewModel.RadioFilterChanged -= RadioFilterChanged;
        PositionSeekSurface.GotFocus -= OnSeekSurfaceFocusChanged;
        PositionSeekSurface.LostFocus -= OnSeekSurfaceFocusChanged;
        VolumeSurface.GotFocus -= OnVolumeSurfaceFocusChanged;
        VolumeSurface.LostFocus -= OnVolumeSurfaceFocusChanged;
    }

    private static Border CreateTransportFocusRing(string name, string backgroundHex) => new()
    {
        Name = name,
        Height = 12,
        Margin = new Thickness(-3, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.Parse("#F4F5F6")),
        Background = new SolidColorBrush(Color.Parse(backgroundHex)),
        IsHitTestVisible = false,
        IsVisible = false
    };

    private void OnSeekSurfaceFocusChanged(object? sender, RoutedEventArgs eventArgs) =>
        UpdateTransportFocusRings();

    private void OnVolumeSurfaceFocusChanged(object? sender, RoutedEventArgs eventArgs) =>
        UpdateTransportFocusRings();

    private void OnTransportSurfacePointerChanged(object? sender, PointerEventArgs eventArgs) =>
        UpdateTransportFocusRings();

    private void UpdateTransportFocusRings()
    {
        if (seekFocusRing is not null)
        {
            seekFocusRing.IsVisible =
                PositionSeekSurface.IsKeyboardFocusWithin &&
                PositionSeekSurface.IsEnabled &&
                PositionSeekSurface.IsVisible &&
                viewModel.CanSeekPlayback &&
                !isSeekingFromPointer;
        }

        if (volumeFocusRing is not null)
        {
            volumeFocusRing.IsVisible =
                VolumeSurface.IsKeyboardFocusWithin &&
                VolumeSurface.IsEnabled &&
                VolumeSurface.IsVisible &&
                !isChangingVolumeFromPointer;
        }
    }

    private void OnKeyboardViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is
            nameof(viewModel.PositionSeconds) or
            nameof(viewModel.PositionText) or
            nameof(viewModel.DurationSeconds) or
            nameof(viewModel.DurationText) or
            nameof(viewModel.CanSeekPlayback) or
            nameof(viewModel.ShowSeekBar) or
            nameof(viewModel.IsRadioPlayback) or
            nameof(viewModel.Volume) or
            nameof(viewModel.VolumePercentText) or
            nameof(viewModel.MuteButtonText) or
            nameof(viewModel.CanMute))
        {
            UpdateTransportAutomationProperties();
            EnsureUnavailableSeekReleasesFocus();
            UpdateTransportFocusRings();
        }
    }

    private void UpdateTransportAutomationProperties()
    {
        if (Volatile.Read(ref keyboardDisposed)) return;

        if (viewModel.IsRadioPlayback)
        {
            if (seekDisabledByUnsafeState)
            {
                seekDisabledByUnsafeState = false;
                PositionSeekSurface.IsEnabled = viewModel.CanSeekPlayback;
            }

            PositionSeekSurface.UpdateRangeState(
                minimum: 0,
                maximum: 0,
                value: 0,
                smallChange: SeekSmallStepSeconds,
                largeChange: SeekLargeStepSeconds,
                isInteractive: false);
            AutomationProperties.SetName(PositionSeekSurface, Loc.Get("Transport.SeekLive"));
        }
        else if (TryGetValidSeekRange(out double maxSeconds, out double currentSeconds))
        {
            if (seekDisabledByUnsafeState)
            {
                seekDisabledByUnsafeState = false;
                PositionSeekSurface.IsEnabled = viewModel.CanSeekPlayback;
            }

            if (!isSeekingFromPointer)
            {
                PositionProgress.Value = currentSeconds;
            }

            PositionSeekSurface.UpdateRangeState(
                minimum: 0,
                maximum: maxSeconds,
                value: currentSeconds,
                smallChange: SeekSmallStepSeconds,
                largeChange: SeekLargeStepSeconds,
                isInteractive: CanRunUserAction && viewModel.ShowSeekBar,
                identity: viewModel.GetMediaSessionState().Playback.Track?.Id,
                generation: viewModel.PlaybackRequestGeneration);
            AutomationProperties.SetName(
                PositionSeekSurface,
                Loc.Format("Transport.SeekAt", viewModel.PositionText, viewModel.DurationText));
        }
        else
        {
            if (viewModel.CanSeekPlayback && PositionSeekSurface.IsEnabled)
            {
                seekDisabledByUnsafeState = true;
                PositionSeekSurface.IsEnabled = false;
            }

            PositionSeekSurface.UpdateRangeState(
                minimum: 0,
                maximum: 0,
                value: 0,
                smallChange: SeekSmallStepSeconds,
                largeChange: SeekLargeStepSeconds,
                isInteractive: false);
            AutomationProperties.SetName(PositionSeekSurface, Loc.Get("Main.SeekUnavailable"));
        }

        if (!isApplyingTransportVolume)
        {
            double rawVolume = viewModel.Volume;
            if (!double.IsFinite(rawVolume) || rawVolume < 0.0 || rawVolume > 1.0)
            {
                if (VolumeSurface.IsEnabled)
                {
                    volumeDisabledByUnsafeState = true;
                    VolumeSurface.IsEnabled = false;
                }

                VolumeSurface.UpdateRangeState(
                    minimum: 0,
                    maximum: 1,
                    value: 0,
                    smallChange: VolumeSmallStep,
                    largeChange: VolumeLargeStep,
                    isInteractive: false);
                AutomationProperties.SetName(VolumeSurface, Loc.Get("Transport.VolumeUnavailable"));
            }
            else
            {
                if (volumeDisabledByUnsafeState)
                {
                    volumeDisabledByUnsafeState = false;
                    VolumeSurface.IsEnabled = true;
                }

                double committedVolume = Math.Round(rawVolume, 2, MidpointRounding.AwayFromZero);
                if (!isChangingVolumeFromPointer)
                {
                    VolumeProgress.Value = committedVolume;
                }

                VolumeSurface.UpdateRangeState(
                    minimum: 0,
                    maximum: 1,
                    value: committedVolume,
                    smallChange: VolumeSmallStep,
                    largeChange: VolumeLargeStep,
                    isInteractive: CanRunUserAction);
                int volumePercent = (int)Math.Round(committedVolume * 100, MidpointRounding.AwayFromZero);
                bool isMuted = string.Equals(viewModel.MuteButtonText, Loc.Get("Playback.Unmute"), StringComparison.Ordinal);
                string volumeName = isMuted
                    ? Loc.Format("Transport.VolumeMuted", volumePercent)
                    : Loc.Format("Transport.VolumeAt", volumePercent);
                AutomationProperties.SetName(VolumeSurface, volumeName);
            }
        }
    }

    private bool TryGetValidSeekRange(out double maxSeconds, out double currentSeconds)
    {
        maxSeconds = 0;
        currentSeconds = 0;
        if (!viewModel.CanSeekPlayback || viewModel.IsRadioPlayback)
        {
            return false;
        }

        var playback = viewModel.GetMediaSessionState().Playback;
        if (playback.Track is not { } track || track.Duration <= TimeSpan.Zero)
        {
            return false;
        }

        double trackMax = track.Duration.TotalSeconds;
        double vmDuration = viewModel.DurationSeconds;
        double vmPosition = viewModel.PositionSeconds;
        double enginePosition = playback.Position.TotalSeconds;
        if (!double.IsFinite(trackMax) || trackMax <= 0 ||
            !double.IsFinite(vmDuration) || vmDuration <= 0 ||
            !double.IsFinite(vmPosition) || vmPosition < 0 || vmPosition > trackMax ||
            !double.IsFinite(enginePosition) || enginePosition < 0 || enginePosition > trackMax)
        {
            return false;
        }

        maxSeconds = trackMax;
        currentSeconds = vmPosition;
        return true;
    }

    private void EnsureUnavailableSeekReleasesFocus()
    {
        if (!CanRunUserAction || !PositionSeekSurface.IsKeyboardFocusWithin) return;
        if (viewModel.CanSeekPlayback && viewModel.ShowSeekBar && PositionSeekSurface.IsEnabled && PositionSeekSurface.IsVisible)
        {
            return;
        }

        Button? playButton = this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("play") && button.IsEnabled && button.IsVisible);
        if (playButton is not null)
        {
            playButton.Focus(NavigationMethod.Tab);
        }
        else if (VolumeSurface.IsEnabled && VolumeSurface.IsVisible)
        {
            VolumeSurface.Focus(NavigationMethod.Tab);
        }
    }

    private async void LibraryKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled || !CanRunUserAction) return;
        if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.F)
        {
            args.Handled = true;
            FocusSearch();
            return;
        }
        if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.Q)
        {
            args.Handled = true; Close();
            return;
        }
        if (args.KeyModifiers == KeyModifiers.None && args.Key == Key.Escape && (SearchBox.IsKeyboardFocusWithin || RadioSearchBox.IsKeyboardFocusWithin))
        {
            if (!string.IsNullOrEmpty(SearchBox.Text) || !string.IsNullOrEmpty(RadioSearchBox.Text)) { args.Handled = true; SearchBox.Clear(); RadioSearchBox.Clear(); }
            return;
        }
        if (PositionSeekSurface.IsKeyboardFocusWithin)
        {
            if (args.KeyModifiers == KeyModifiers.None && TryGetSeekTarget(args.Key, out _))
            {
                args.Handled = true;
                if (viewModel.CanSeekPlayback && PositionSeekSurface.IsEnabled && PositionSeekSurface.IsVisible && !isSeekingFromPointer)
                {
                    await RunTransportKeyboardActionAsync(() => ApplyKeyboardSeekAsync(args.Key));
                }
            }
            return;
        }
        if (VolumeSurface.IsKeyboardFocusWithin)
        {
            if (args.KeyModifiers == KeyModifiers.None && TryGetVolumeTarget(args.Key, out _))
            {
                args.Handled = true;
                if (VolumeSurface.IsEnabled && VolumeSurface.IsVisible && !isChangingVolumeFromPointer)
                {
                    await RunTransportKeyboardActionAsync(() => ApplyKeyboardVolumeAsync(args.Key));
                }
            }
            return;
        }
        // Keep native text, selector, button and range-control key handling intact.
        if (FocusManager?.GetFocusedElement() is Control focused &&
            focused.GetVisualAncestors().Prepend(focused).Any(control =>
                control is TextBox or ComboBox or Button or NumericUpDown or Slider or MenuItem)) return;
        ListBox? list = TrackList.IsKeyboardFocusWithin ? TrackList
            : PlayQueueList.IsKeyboardFocusWithin ? PlayQueueList
            : RadioStationList.IsKeyboardFocusWithin ? RadioStationList : null;
        if (args.KeyModifiers == KeyModifiers.Alt && args.Key is Key.Left or Key.Right)
        {
            args.Handled = true;
            await RunListPlaybackActionAsync(list, () => args.Key == Key.Left
                ? viewModel.MovePreviousAsync(lifetimeCancellation.Token)
                : viewModel.MoveNextAsync(lifetimeCancellation.Token));
        }
        else if (args.KeyModifiers == KeyModifiers.None && args.Key == Key.Space &&
            list is not null)
        {
            args.Handled = true;
            await RunListPlaybackActionAsync(list, () => viewModel.TogglePlaybackAsync(lifetimeCancellation.Token));
        }
    }

    private bool TryGetSeekTarget(Key key, out double targetSeconds)
    {
        if (!TryGetValidSeekRange(out double maxSeconds, out _))
        {
            targetSeconds = 0;
            return false;
        }

        double currentSeconds = Math.Clamp(viewModel.GetMediaSessionState().Playback.Position.TotalSeconds, 0, maxSeconds);
        switch (key)
        {
            case Key.Left:
            case Key.Down:
                targetSeconds = Math.Clamp(currentSeconds - SeekSmallStepSeconds, 0, maxSeconds);
                return true;
            case Key.Right:
            case Key.Up:
                targetSeconds = Math.Clamp(currentSeconds + SeekSmallStepSeconds, 0, maxSeconds);
                return true;
            case Key.PageDown:
                targetSeconds = Math.Clamp(currentSeconds - SeekLargeStepSeconds, 0, maxSeconds);
                return true;
            case Key.PageUp:
                targetSeconds = Math.Clamp(currentSeconds + SeekLargeStepSeconds, 0, maxSeconds);
                return true;
            case Key.Home:
                targetSeconds = 0;
                return true;
            case Key.End:
                targetSeconds = maxSeconds;
                return true;
            default:
                targetSeconds = currentSeconds;
                return false;
        }
    }

    private bool TryGetVolumeTarget(Key key, out double targetVolume)
    {
        if (!double.IsFinite(viewModel.Volume) || viewModel.Volume < 0.0 || viewModel.Volume > 1.0)
        {
            targetVolume = 0;
            return false;
        }

        double currentVolume = Math.Clamp(viewModel.Volume, 0, 1);
        double rawTarget;
        switch (key)
        {
            case Key.Left:
            case Key.Down:
                rawTarget = currentVolume - VolumeSmallStep;
                break;
            case Key.Right:
            case Key.Up:
                rawTarget = currentVolume + VolumeSmallStep;
                break;
            case Key.PageDown:
                rawTarget = currentVolume - VolumeLargeStep;
                break;
            case Key.PageUp:
                rawTarget = currentVolume + VolumeLargeStep;
                break;
            case Key.Home:
                rawTarget = 0;
                break;
            case Key.End:
                rawTarget = 1;
                break;
            default:
                targetVolume = currentVolume;
                return false;
        }

        targetVolume = Math.Round(Math.Clamp(rawTarget, 0, 1), 2, MidpointRounding.AwayFromZero);
        return true;
    }

    private void RequestSeekFromAutomation(double targetSeconds, Guid? requestedTrackId, long requestedGeneration)
    {
        if (!double.IsFinite(targetSeconds) || requestedTrackId is null) return;
        _ = RunTransportRangeActionAsync(() => ApplyAutomationSeekAsync(targetSeconds, requestedTrackId.Value, requestedGeneration));
    }

    private void RequestVolumeFromAutomation(double targetVolume, Guid? requestedTrackId, long requestedGeneration)
    {
        if (!double.IsFinite(targetVolume)) return;
        _ = RunTransportRangeActionAsync(() => ApplyAutomationVolumeAsync(targetVolume));
    }

    private async Task ApplyKeyboardSeekAsync(Key key)
    {
        long requestedGeneration = viewModel.PlaybackRequestGeneration;
        Guid? trackId = viewModel.GetMediaSessionState().Playback.Track?.Id;
        if (trackId is null) return;
        await keyboardSeekGate.WaitAsync(lifetimeCancellation.Token);
        try
        {
            if (!CanRunUserAction || Volatile.Read(ref keyboardDisposed) || !viewModel.CanSeekPlayback ||
                PositionSeekSurface.GetSnapshot().IsReadOnly || isSeekingFromPointer ||
                viewModel.PlaybackRequestGeneration != requestedGeneration ||
                viewModel.GetMediaSessionState().Playback.Track?.Id != trackId) return;
            // Compute each increment after the previous seek completes; a slow engine must not collapse multiple increments.
            if (!TryGetSeekTarget(key, out double targetSeconds)) return;
            await ExecuteCommittedSeekAsync(trackId.Value, targetSeconds);
        }
        finally { keyboardSeekGate.Release(); }
    }

    private async Task ApplyAutomationSeekAsync(double targetSeconds, Guid requestedTrackId, long requestedGeneration)
    {
        if (!CanRunUserAction || Volatile.Read(ref keyboardDisposed) || !viewModel.CanSeekPlayback ||
            !viewModel.ShowSeekBar || viewModel.IsRadioPlayback ||
            PositionSeekSurface.GetSnapshot().IsReadOnly || isSeekingFromPointer ||
            viewModel.PlaybackRequestGeneration != requestedGeneration ||
            !TryGetValidSeekRange(out double maxSeconds, out _) ||
            !double.IsFinite(targetSeconds) || targetSeconds < 0 || targetSeconds > maxSeconds)
        {
            return;
        }

        // The track was captured when the provider accepted the request; a later track must never receive it.
        Guid? trackId = requestedTrackId;
        if (viewModel.GetMediaSessionState().Playback.Track?.Id != trackId) return;
        await keyboardSeekGate.WaitAsync(lifetimeCancellation.Token);
        try
        {
            if (!CanRunUserAction || Volatile.Read(ref keyboardDisposed) || !viewModel.CanSeekPlayback ||
                !viewModel.ShowSeekBar || viewModel.IsRadioPlayback ||
                PositionSeekSurface.GetSnapshot().IsReadOnly || isSeekingFromPointer ||
                viewModel.PlaybackRequestGeneration != requestedGeneration ||
                viewModel.GetMediaSessionState().Playback.Track?.Id != trackId ||
                !TryGetValidSeekRange(out maxSeconds, out _) ||
                targetSeconds < 0 || targetSeconds > maxSeconds)
            {
                return;
            }

            await ExecuteCommittedSeekAsync(trackId.Value, targetSeconds);
        }
        finally { keyboardSeekGate.Release(); }
    }

    private async Task ExecuteCommittedSeekAsync(Guid trackId, double targetSeconds)
    {
        await viewModel.SeekAsync(targetSeconds, lifetimeCancellation.Token);
        if (!CanRunUserAction || Volatile.Read(ref keyboardDisposed)) return;
        var playback = viewModel.GetMediaSessionState().Playback;
        if (playback.Track?.Id == trackId &&
            double.IsFinite(playback.Position.TotalSeconds) &&
            playback.Position >= TimeSpan.Zero &&
            playback.Position <= playback.Track.Duration &&
            !isSeekingFromPointer)
        {
            double completedSeconds = playback.Position.TotalSeconds;
            viewModel.PositionSeconds = completedSeconds;
            PositionProgress.Value = completedSeconds;
        }

        UpdateTransportAutomationProperties();
    }

    private async Task ApplyKeyboardVolumeAsync(Key key)
    {
        await keyboardVolumeGate.WaitAsync(lifetimeCancellation.Token);
        try
        {
            if (!CanRunUserAction || Volatile.Read(ref keyboardDisposed) ||
                VolumeSurface.GetSnapshot().IsReadOnly || isChangingVolumeFromPointer)
            {
                return;
            }

            if (!TryGetVolumeTarget(key, out double targetVolume)) return;
            await ExecuteCommittedVolumeAsync(targetVolume);
        }
        finally { keyboardVolumeGate.Release(); }
    }

    private async Task ApplyAutomationVolumeAsync(double targetVolume)
    {
        if (!CanRunUserAction || Volatile.Read(ref keyboardDisposed) ||
            VolumeSurface.GetSnapshot().IsReadOnly || isChangingVolumeFromPointer ||
            !double.IsFinite(targetVolume) || targetVolume < 0.0 || targetVolume > 1.0)
        {
            return;
        }

        await keyboardVolumeGate.WaitAsync(lifetimeCancellation.Token);
        try
        {
            if (!CanRunUserAction || Volatile.Read(ref keyboardDisposed) ||
                VolumeSurface.GetSnapshot().IsReadOnly || isChangingVolumeFromPointer ||
                !double.IsFinite(targetVolume) || targetVolume < 0.0 || targetVolume > 1.0)
            {
                return;
            }

            double normalizedTarget = Math.Round(targetVolume, 2, MidpointRounding.AwayFromZero);
            await ExecuteCommittedVolumeAsync(normalizedTarget);
        }
        finally { keyboardVolumeGate.Release(); }
    }

    private async Task ExecuteCommittedVolumeAsync(double targetVolume)
    {
        var initialPlayback = viewModel.GetMediaSessionState().Playback;
        bool hadLoadedTrack = initialPlayback.Track is not null;
        double fallbackVolume = hadLoadedTrack && double.IsFinite(initialPlayback.Volume)
            ? Math.Clamp(initialPlayback.Volume, 0, 1)
            : double.IsFinite(viewModel.Volume) ? Math.Clamp(viewModel.Volume, 0, 1) : 0.8;
        isApplyingTransportVolume = hadLoadedTrack;
        if (hadLoadedTrack && !isChangingVolumeFromPointer)
        {
            VolumeProgress.Value = fallbackVolume;
        }

        try
        {
            await viewModel.ChangeVolumeAsync(targetVolume, lifetimeCancellation.Token);
        }
        catch
        {
            if (CanRunUserAction && !Volatile.Read(ref keyboardDisposed))
            {
                viewModel.Volume = fallbackVolume;
                if (!isChangingVolumeFromPointer) VolumeProgress.Value = fallbackVolume;
            }

            throw;
        }
        finally
        {
            isApplyingTransportVolume = false;
            if (CanRunUserAction && !Volatile.Read(ref keyboardDisposed))
            {
                var afterPlayback = viewModel.GetMediaSessionState().Playback;
                if (afterPlayback.Track is not null && double.IsFinite(afterPlayback.Volume))
                {
                    double engineVolume = Math.Round(
                        Math.Clamp(afterPlayback.Volume, 0, 1),
                        2,
                        MidpointRounding.AwayFromZero);
                    if (Math.Abs(engineVolume - viewModel.Volume) > 0.0001)
                    {
                        viewModel.Volume = engineVolume;
                    }
                }

                UpdateTransportAutomationProperties();
            }
        }
    }

    private Task RunTransportRangeActionAsync(Func<Task> action)
    {
        if (Volatile.Read(ref keyboardDisposed) || !CanRunUserAction) return Task.CompletedTask;
        if (Dispatcher.UIThread.CheckAccess())
        {
            return RunTransportKeyboardActionAsync(action);
        }

        // Track the accepted request before it is posted so window close can drain it.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (transportKeyboardGate)
        {
            if (Volatile.Read(ref keyboardDisposed) || !CanRunUserAction) return Task.CompletedTask;
            transportKeyboardOperations.Add(completion.Task);
        }

        _ = ObserveTransportKeyboardActionAsync(completion.Task);
        _ = PostTransportRangeActionAsync(action, completion);
        return completion.Task;
    }

    private async Task PostTransportRangeActionAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Volatile.Read(ref keyboardDisposed) || !CanRunUserAction) return;
                await RunUserActionAsync(action);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"Transport range automation action failed: {exception}");
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private Task RunTransportKeyboardActionAsync(Func<Task> action)
    {
        if (Volatile.Read(ref keyboardDisposed) || !CanRunUserAction) return Task.CompletedTask;
        Task task = RunUserActionAsync(action);
        lock (transportKeyboardGate)
        {
            transportKeyboardOperations.Add(task);
        }

        _ = ObserveTransportKeyboardActionAsync(task);
        return task;
    }

    private async Task ObserveTransportKeyboardActionAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            lock (transportKeyboardGate)
            {
                transportKeyboardOperations.Remove(task);
            }
        }
    }

    private async Task WaitForTransportKeyboardAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (transportKeyboardGate)
            {
                pending = transportKeyboardOperations.Where(task => !task.IsCompleted).ToArray();
            }

            if (pending.Length == 0) return;
            try
            {
                await Task.WhenAll(pending);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine($"Transport keyboard shutdown drain: {exception.Message}");
            }
        }
    }

    private Task RunListPlaybackActionAsync(ListBox? list, Func<Task> action) => RunUserActionAsync(async () =>
    {
        await action();
        if (!CanRunUserAction || list is null || !list.IsVisible || list.SelectedItem is null) return;
        // Statistics can replace the focused row. Restore it only if focus was
        // detached; never move focus away from a control the user chose meanwhile.
        if (FocusManager?.GetFocusedElement() is Control focused &&
            TopLevel.GetTopLevel(focused) == this && !ReferenceEquals(focused, this)) return;
        list.UpdateLayout();
        list.ContainerFromItem(list.SelectedItem)?.Focus(NavigationMethod.Directional);
    });
}
