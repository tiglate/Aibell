using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Aibell.Application.Playback;

namespace Aibell.Desktop.Controls;

/// <summary>
/// The classic media-player visualizer: a dot-matrix spectrum drawn faintly behind the progress bar, with peak caps that hang and then fall.
/// It runs a timer only while it is switched on and on screen, and it tells the engine to analyse only for that time.
/// </summary>
public sealed class SpectrumVisualizer : Control
{
    public static readonly StyledProperty<ISpectrumSource?> SourceProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, ISpectrumSource?>(nameof(Source));

    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, bool>(nameof(IsRunning), true);

    private const double SlotWidth = 6;
    private const double Segment = 3;
    private const double SegmentGap = 1.5;
    private const double Fall = 0.07;
    private const double PeakFall = 0.012;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    private readonly DispatcherTimer timer = new() { Interval = Frame };
    private float[] levels = [];
    private float[] peaks = [];
    private float[] fresh = [];
    private ISpectrumSource? analysing;
    private bool attached;
    private bool hasVisibleBars;

    public SpectrumVisualizer()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        timer.Tick += (_, _) => Tick();
    }

    public ISpectrumSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Refresh();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsRunningProperty || change.Property == IsVisibleProperty ||
            change.Property.Name == nameof(IsEffectivelyVisible))
        {
            Refresh();
        }
    }

    // Starts or stops the timer and the engine's analysis together so a hidden or switched-off visualizer costs nothing.
    private void Refresh()
    {
        ISpectrumSource? wanted = attached && IsRunning && IsEffectivelyVisible ? Source : null;
        if (!ReferenceEquals(wanted, analysing))
        {
            analysing?.SetSpectrumEnabled(false);
            analysing = wanted;
            analysing?.SetSpectrumEnabled(true);
        }
        if (analysing is not null)
        {
            timer.Start();
        }
        else
        {
            timer.Stop();
            Array.Clear(levels);
            Array.Clear(peaks);
            hasVisibleBars = false;
            InvalidateVisual();
        }
    }

    private void Tick()
    {
        if (analysing is not { } source) return;
        int count = Math.Max(0, (int)(Bounds.Width / SlotWidth));
        if (count == 0) return;
        if (levels.Length != count)
        {
            levels = new float[count];
            peaks = new float[count];
            fresh = new float[count];
        }
        bool live = source.IsSpectrumAvailable && source.TryReadSpectrum(fresh);
        if (!live) Array.Clear(fresh);
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            // Rise at once, fall smoothly; the cap holds the highest level and sinks slowly.
            levels[i] = fresh[i] >= levels[i] ? fresh[i] : (float)Math.Max(fresh[i], levels[i] - Fall);
            peaks[i] = levels[i] >= peaks[i] ? levels[i] : (float)Math.Max(levels[i], peaks[i] - PeakFall);
            any |= peaks[i] > 0.001f;
        }
        // Once everything has settled to silence there is nothing new to paint.
        if (any || hasVisibleBars) InvalidateVisual();
        hasVisibleBars = any;
    }

    // Cool at the bottom, warm at the top: a violet-to-accent ramp (the Winamp green-yellow-red meter, restyled for this player).
    private static readonly Color Low = Color.Parse("#5B6CFF");
    private static readonly Color High = Color.Parse("#FF3D8B");
    private static readonly IBrush CapBrush = new SolidColorBrush(Color.Parse("#F4F5F6"));

    private static IBrush Ramp(double t)
    {
        byte Mix(byte a, byte b) => (byte)(a + ((b - a) * t));
        return new SolidColorBrush(Color.FromRgb(Mix(Low.R, High.R), Mix(Low.G, High.G), Mix(Low.B, High.B)));
    }

    private IBrush[] ramp = [];

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (levels.Length == 0 || Bounds.Height < Segment) return;
        double height = Bounds.Height;
        double pitch = Segment + SegmentGap;
        int rows = Math.Max(1, (int)((height + SegmentGap) / pitch));
        if (ramp.Length != rows) ramp = [.. Enumerable.Range(0, rows).Select(row => Ramp(rows == 1 ? 0 : (double)row / (rows - 1)))];
        double barWidth = SlotWidth - 2;
        double originX = ((Bounds.Width - (levels.Length * SlotWidth)) / 2) + 1;
        using (context.PushOpacity(0.5))
        {
            for (int i = 0; i < levels.Length; i++)
            {
                double x = originX + (i * SlotWidth);
                int lit = (int)Math.Round(levels[i] * rows);
                for (int row = 0; row < lit; row++)
                {
                    double y = height - ((row + 1) * pitch) + SegmentGap;
                    context.DrawRectangle(ramp[row], null, new Rect(x, y, barWidth, Segment), 1, 1);
                }
            }
        }
        using (context.PushOpacity(0.8))
        {
            for (int i = 0; i < peaks.Length; i++)
            {
                int row = (int)Math.Round(peaks[i] * rows);
                if (row <= 0) continue;
                double y = height - (row * pitch) + SegmentGap;
                context.DrawRectangle(CapBrush, null, new Rect(originX + (i * SlotWidth), y, barWidth, 1.5), 0.75, 0.75);
            }
        }
    }
}
