using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aibell.Desktop.Controls;

internal sealed record TransportRangeSnapshot(
    double Minimum,
    double Maximum,
    double Value,
    double SmallChange,
    double LargeChange,
    bool IsReadOnly,
    bool IsEnabled,
    bool IsDisposed,
    Guid? Identity = null,
    long Generation = 0);

public sealed class TransportRangeSurface : Grid
{
    private TransportRangeSnapshot snapshot = new(
        Minimum: 0,
        Maximum: 0,
        Value: 0,
        SmallChange: 0,
        LargeChange: 0,
        IsReadOnly: true,
        IsEnabled: false,
        IsDisposed: false);

    private readonly List<IDisposable> ancestorSubscriptions = [];
    private Action<double, Guid?, long>? requestValueChange;
    private Guid? configuredIdentity;
    private long configuredGeneration;
    private TransportRangeAutomationPeer? automationPeer;
    private double configuredMinimum;
    private double configuredMaximum;
    private double configuredValue;
    private double configuredSmallChange;
    private double configuredLargeChange;
    private bool configuredInteractive;
    private bool hasSafeNumericState;
    private bool disabledByUnsafeRangeState;
    private bool isDisposed;

    public void ConfigureRange(Action<double, Guid?, long>? onValueRequested)
    {
        if (Volatile.Read(ref isDisposed))
        {
            return;
        }

        Volatile.Write(ref requestValueChange, onValueRequested);
        if (Dispatcher.UIThread.CheckAccess())
        {
            PublishSnapshot();
        }
    }

    public void UpdateRangeState(
        double minimum,
        double maximum,
        double value,
        double smallChange,
        double largeChange,
        bool isInteractive,
        Guid? identity = null,
        long generation = 0)
    {
        if (Volatile.Read(ref isDisposed))
        {
            return;
        }

        bool validStep =
            double.IsFinite(smallChange) &&
            double.IsFinite(largeChange) &&
            smallChange > 0 &&
            largeChange >= smallChange;
        bool validBounds =
            double.IsFinite(minimum) &&
            double.IsFinite(maximum) &&
            double.IsFinite(value) &&
            maximum >= minimum &&
            value >= minimum &&
            value <= maximum;

        if (!validStep || !validBounds)
        {
            hasSafeNumericState = false;
            configuredMinimum = 0;
            configuredMaximum = 0;
            configuredValue = 0;
            configuredSmallChange = validStep ? smallChange : 0;
            configuredLargeChange = validStep ? largeChange : 0;
            configuredInteractive = false;
            configuredIdentity = identity;
            configuredGeneration = generation;
            if (IsEnabled)
            {
                disabledByUnsafeRangeState = true;
                IsEnabled = false;
            }

            PublishSnapshot();
            return;
        }

        if (disabledByUnsafeRangeState)
        {
            disabledByUnsafeRangeState = false;
            IsEnabled = true;
        }

        hasSafeNumericState = true;
        configuredMinimum = minimum;
        configuredMaximum = maximum;
        configuredValue = value;
        configuredSmallChange = smallChange;
        configuredLargeChange = largeChange;
        configuredInteractive = isInteractive;
        configuredIdentity = identity;
        configuredGeneration = generation;
        PublishSnapshot();
    }

    public void DisposeRange()
    {
        if (Interlocked.Exchange(ref isDisposed, true))
        {
            return;
        }

        Volatile.Write(ref requestValueChange, null);
        TransportRangeSnapshot current = Volatile.Read(ref snapshot);
        TransportRangeSnapshot next = current with
        {
            IsReadOnly = true,
            IsEnabled = false,
            IsDisposed = true
        };
        TransportRangeSnapshot previous = Interlocked.Exchange(ref snapshot, next);
        if (Dispatcher.UIThread.CheckAccess() && automationPeer is { } peer)
        {
            peer.RaiseSnapshotChanges(previous, next);
        }
    }

    public bool TryRequestValueChange(double value)
    {
        if (!double.IsFinite(value))
        {
            return false;
        }

        TransportRangeSnapshot current = GetSnapshot();
        if (current.IsDisposed || !current.IsEnabled || current.IsReadOnly ||
            !double.IsFinite(current.Minimum) || !double.IsFinite(current.Maximum) ||
            current.Maximum <= current.Minimum ||
            value < current.Minimum || value > current.Maximum)
        {
            return false;
        }

        Action<double, Guid?, long>? handler = Volatile.Read(ref requestValueChange);
        if (handler is null)
        {
            return false;
        }

        handler(value, current.Identity, current.Generation);
        return true;
    }

    internal TransportRangeSnapshot GetSnapshot()
    {
        if (Dispatcher.UIThread.CheckAccess() && !Volatile.Read(ref isDisposed))
        {
            PublishSnapshot();
        }

        return Volatile.Read(ref snapshot);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        automationPeer ??= new TransportRangeAutomationPeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (Volatile.Read(ref isDisposed))
        {
            return;
        }

        if (change.Property == IsEnabledProperty || change.Property == IsVisibleProperty ||
            change.Property == IsEffectivelyEnabledProperty)
        {
            PublishSnapshot();
        }
        else if (change.Property == AutomationProperties.NameProperty && automationPeer is { } peer)
        {
            peer.RaisePropertyChangedEvent(
                AutomationElementIdentifiers.NameProperty,
                change.GetOldValue<string?>(),
                change.GetNewValue<string?>());
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ReleaseAncestorSubscriptions();
        foreach (Visual ancestor in this.GetVisualAncestors())
        {
            ancestorSubscriptions.Add(ancestor.GetObservable(IsVisibleProperty).Subscribe(new AncestorObserver(this)));
        }

        PublishSnapshot();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseAncestorSubscriptions();
        base.OnDetachedFromVisualTree(e);
        PublishSnapshot();
    }

    private void ReleaseAncestorSubscriptions()
    {
        foreach (IDisposable subscription in ancestorSubscriptions)
        {
            subscription.Dispose();
        }

        ancestorSubscriptions.Clear();
    }

    private sealed class AncestorObserver(TransportRangeSurface owner) : IObserver<bool>
    {
        public void OnNext(bool value) => owner.PublishSnapshot();

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private void PublishSnapshot()
    {
        bool disposedNow = Volatile.Read(ref isDisposed);
        bool hasHandler = Volatile.Read(ref requestValueChange) is not null;
        bool controlActive = !disposedNow && hasSafeNumericState && IsEffectivelyEnabled && IsEffectivelyVisible;
        bool effectiveEnabled = controlActive && configuredInteractive && hasHandler;
        bool effectiveReadOnly = !effectiveEnabled || !hasHandler || configuredMaximum <= configuredMinimum;

        TransportRangeSnapshot next = new(
            configuredMinimum,
            configuredMaximum,
            configuredValue,
            configuredSmallChange,
            configuredLargeChange,
            effectiveReadOnly,
            effectiveEnabled,
            disposedNow,
            configuredIdentity,
            configuredGeneration);
        TransportRangeSnapshot previous = Interlocked.Exchange(ref snapshot, next);
        if (!disposedNow && automationPeer is { } peer)
        {
            peer.RaiseSnapshotChanges(previous, next);
        }
    }
}

public sealed class TransportRangeAutomationPeer(TransportRangeSurface owner)
    : ControlAutomationPeer(owner), IRangeValueProvider
{
    public new TransportRangeSurface Owner => (TransportRangeSurface)base.Owner;

    public bool IsReadOnly => Owner.GetSnapshot().IsReadOnly;

    public double Minimum => Owner.GetSnapshot().Minimum;

    public double Maximum => Owner.GetSnapshot().Maximum;

    public double Value => Owner.GetSnapshot().Value;

    public double LargeChange => Owner.GetSnapshot().LargeChange;

    public double SmallChange => Owner.GetSnapshot().SmallChange;

    public void SetValue(double value) => _ = Owner.TryRequestValueChange(value);

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Slider;

    protected override string GetClassNameCore() =>
        nameof(TransportRangeSurface);

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override bool IsEnabledCore() => Owner.GetSnapshot().IsEnabled;

    internal void RaiseSnapshotChanges(TransportRangeSnapshot previous, TransportRangeSnapshot next)
    {
        if (previous.Minimum != next.Minimum)
        {
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.MinimumProperty, previous.Minimum, next.Minimum);
        }

        if (previous.Maximum != next.Maximum)
        {
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.MaximumProperty, previous.Maximum, next.Maximum);
        }

        if (previous.Value != next.Value)
        {
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, previous.Value, next.Value);
        }

        if (previous.IsReadOnly != next.IsReadOnly)
        {
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.IsReadOnlyProperty, previous.IsReadOnly, next.IsReadOnly);
        }
    }
}
