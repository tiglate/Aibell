using System.Diagnostics;
using System.Runtime.InteropServices;
using Aibell.Application.Playback;
using Aibell.Platform.Windows.Native;

namespace Aibell.Platform.Windows;

public enum TaskbarProgressMode { None, Normal, Paused }

/// <summary>The thumbnail-toolbar buttons shown when the pointer rests on the taskbar button. Values are the button ids.</summary>
public enum TaskbarButton { Previous = 0, PlayPause = 1, Next = 2 }

/// <summary>What the taskbar button shows: a progress bar over the icon and three transport buttons in the thumbnail.</summary>
public sealed record TaskbarState(TaskbarProgressMode Progress, ulong Completed, ulong Total,
    bool PreviousEnabled, bool PlayPauseEnabled, bool NextEnabled, bool ShowPause,
    string PreviousTip, string PlayPauseTip, string NextTip)
{
    public static TaskbarState Idle { get; } = new(TaskbarProgressMode.None, 0, 0, false, false, false, false, "", "", "");

    /// <summary>
    /// Progress for the current playback. Live streams, an unknown duration, and anything that is not playing or paused
    /// (stopped, ended, faulted, nothing loaded) show no bar. Values are milliseconds, clamped to the duration.
    /// </summary>
    public static (TaskbarProgressMode Mode, ulong Completed, ulong Total) ProgressFor(PlaybackStatus status, TimeSpan position, TimeSpan duration, bool live)
    {
        TaskbarProgressMode mode = status switch
        {
            PlaybackStatus.Playing => TaskbarProgressMode.Normal,
            PlaybackStatus.Paused => TaskbarProgressMode.Paused,
            _ => TaskbarProgressMode.None
        };
        ulong total = duration > TimeSpan.Zero ? (ulong)duration.TotalMilliseconds : 0;
        if (live || mode == TaskbarProgressMode.None || total == 0) return (TaskbarProgressMode.None, 0, 0);
        double clamped = Math.Clamp(position.TotalMilliseconds, 0, total);
        return (mode, (ulong)clamped, total);
    }
}

/// <summary>
/// Windows taskbar integration for one top-level window (<c>ITaskbarList3</c>): playback progress on the taskbar button and
/// Previous / Play-Pause / Next buttons in the hover thumbnail. The shell only accepts these calls after it has created the
/// button, so the owner forwards window messages to <see cref="HandleMessage"/>; state applied earlier is kept and shown once
/// the shell announces the button (and again if Explorer restarts). Everything runs on the window's UI thread.
/// </summary>
public sealed class WindowsTaskbar : IDisposable
{
    private const uint NoProgress = 0, Normal = 2, Paused = 8;
    private static readonly Lazy<uint> ButtonCreated = new(() => NativeMethods.RegisterWindowMessage("TaskbarButtonCreated"));

    private readonly nint window;
    private readonly Dictionary<TaskbarGlyph, nint> icons = [];
    private TaskbarList? list;
    private TaskbarState desired = TaskbarState.Idle;
    private TaskbarState? applied;
    private bool disposed;

    public WindowsTaskbar(nint window)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        this.window = window;
    }

    /// <summary>The registered message the shell posts to a window once its taskbar button exists.</summary>
    public static uint ButtonCreatedMessage => ButtonCreated.Value;

    public event Action<TaskbarButton>? ButtonClicked;

    /// <summary>True once the shell has accepted the thumbnail buttons.</summary>
    internal bool IsAttached => list is not null;

    /// <summary>Feed every window message here. Returns true when the message was a thumbnail button click.</summary>
    public bool HandleMessage(uint message, nint wParam)
    {
        if (disposed || !OperatingSystem.IsWindows()) return false;
        if (message == ButtonCreatedMessage)
        {
            Attach();
            return false;
        }
        if (message != NativeMethods.WmCommand || ((int)(wParam >> 16) & 0xFFFF) != NativeMethods.ThumbButtonClicked) return false;
        int id = (int)wParam & 0xFFFF;
        if (Enum.IsDefined((TaskbarButton)id)) ButtonClicked?.Invoke((TaskbarButton)id);
        return true;
    }

    public void Apply(TaskbarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (disposed) return;
        desired = state;
        if (list is not null) Update();
    }

    private void Attach()
    {
        list?.Dispose();
        list = null;
        applied = null;
        try
        {
            list = TaskbarList.Create();
            if (list is null) return;
            if (list.AddButtons(window, Buttons(desired)) < 0)
            {
                Trace.WriteLine("The taskbar thumbnail buttons could not be added.");
                list.Dispose();
                list = null;
                return;
            }
            // The shell shows the buttons just added; progress and later changes are written against this baseline.
            applied = desired with { Progress = TaskbarProgressMode.None, Completed = 0, Total = 0 };
            Update();
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"Taskbar integration is unavailable: {exception.Message}");
            list?.Dispose();
            list = null;
        }
    }

    private void Update()
    {
        if (list is null || applied is null || desired == applied) return;
        TaskbarState previous = applied;
        if (ButtonsDiffer(previous, desired) && list.UpdateButtons(window, Buttons(desired)) < 0)
            Trace.WriteLine("The taskbar thumbnail buttons could not be updated.");
        if (previous.Progress != desired.Progress || previous.Completed != desired.Completed || previous.Total != desired.Total)
            SetProgress(desired.Progress, desired.Completed, desired.Total, force: previous.Progress != desired.Progress);
        applied = desired;
    }

    private void SetProgress(TaskbarProgressMode mode, ulong completed, ulong total, bool force)
    {
        if (list is null) return;
        if (force) list.SetProgressState(window, mode switch { TaskbarProgressMode.Normal => Normal, TaskbarProgressMode.Paused => Paused, _ => NoProgress });
        if (mode != TaskbarProgressMode.None) list.SetProgressValue(window, completed, total);
    }

    private static bool ButtonsDiffer(TaskbarState left, TaskbarState right) =>
        left.PreviousEnabled != right.PreviousEnabled || left.PlayPauseEnabled != right.PlayPauseEnabled || left.NextEnabled != right.NextEnabled ||
        left.ShowPause != right.ShowPause || left.PreviousTip != right.PreviousTip || left.PlayPauseTip != right.PlayPauseTip || left.NextTip != right.NextTip;

    private ThumbButton[] Buttons(TaskbarState state) =>
    [
        Button(TaskbarButton.Previous, TaskbarGlyph.Previous, state.PreviousTip, state.PreviousEnabled),
        Button(TaskbarButton.PlayPause, state.ShowPause ? TaskbarGlyph.Pause : TaskbarGlyph.Play, state.PlayPauseTip, state.PlayPauseEnabled),
        Button(TaskbarButton.Next, TaskbarGlyph.Next, state.NextTip, state.NextEnabled)
    ];

    private unsafe ThumbButton Button(TaskbarButton id, TaskbarGlyph glyph, string tip, bool enabled)
    {
        ThumbButton button = new()
        {
            Mask = ThumbButton.MaskIcon | ThumbButton.MaskTooltip | ThumbButton.MaskFlags,
            Id = (uint)id,
            Icon = IconFor(glyph),
            Flags = enabled ? ThumbButton.FlagEnabled : ThumbButton.FlagDisabled
        };
        int length = Math.Min(tip.Length, 259);
        // Do not cut a surrogate pair in half.
        if (length > 0 && length < tip.Length && char.IsHighSurrogate(tip[length - 1])) length--;
        for (int index = 0; index < length; index++) button.Tip[index] = tip[index];
        return button;
    }

    private unsafe nint IconFor(TaskbarGlyph glyph)
    {
        if (icons.TryGetValue(glyph, out nint existing)) return existing;
        byte[] pixels = TaskbarGlyphs.Render(glyph);
        byte[] mask = new byte[TaskbarGlyphs.Size * TaskbarGlyphs.Size / 8];
        nint color, monochrome, icon = 0;
        fixed (byte* colorBits = pixels)
        fixed (byte* maskBits = mask)
        {
            color = NativeMethods.CreateBitmap(TaskbarGlyphs.Size, TaskbarGlyphs.Size, 1, 32, colorBits);
            monochrome = NativeMethods.CreateBitmap(TaskbarGlyphs.Size, TaskbarGlyphs.Size, 1, 1, maskBits);
        }
        if (color != 0 && monochrome != 0)
            icon = NativeMethods.CreateIconIndirect(new NativeMethods.IconInfo { IsIcon = 1, Mask = monochrome, Color = color });
        if (color != 0) NativeMethods.DeleteObject(color);
        if (monochrome != 0) NativeMethods.DeleteObject(monochrome);
        icons[glyph] = icon;
        return icon;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        list?.Dispose();
        list = null;
        foreach (nint icon in icons.Values)
            if (icon != 0) NativeMethods.DestroyIcon(icon);
        icons.Clear();
    }
}
