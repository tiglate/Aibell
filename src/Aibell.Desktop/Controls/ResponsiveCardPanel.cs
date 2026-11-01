using Avalonia;
using Avalonia.Controls;

namespace Aibell.Desktop.Controls;

/// <summary>
/// Lays cards out in equal-width columns: as many as fit at <see cref="MinCardWidth"/> each, stretched to fill the row. A row is
/// as tall as its tallest card. Four columns fit at about a thousand pixels, one at the smallest window.
/// </summary>
public sealed class ResponsiveCardPanel : Panel
{
    public static readonly StyledProperty<double> MinCardWidthProperty =
        AvaloniaProperty.Register<ResponsiveCardPanel, double>(nameof(MinCardWidth), 230);
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ResponsiveCardPanel, double>(nameof(Spacing), 12);

    static ResponsiveCardPanel()
    {
        AffectsMeasure<ResponsiveCardPanel>(MinCardWidthProperty, SpacingProperty);
    }

    public double MinCardWidth { get => GetValue(MinCardWidthProperty); set => SetValue(MinCardWidthProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>The number of columns for a given width (always at least one).</summary>
    public static int ColumnsFor(double width, double minCardWidth, double spacing) =>
        double.IsFinite(width) && minCardWidth > 0 ? Math.Max(1, (int)Math.Floor((width + spacing) / (minCardWidth + spacing))) : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinCardWidth;
        int columns = ColumnsFor(width, MinCardWidth, Spacing);
        double cardWidth = Math.Max(0, (width - Spacing * (columns - 1)) / columns);
        double height = 0, rowHeight = 0;
        int index = 0;
        foreach (Control child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(cardWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++index % columns == 0) { height += rowHeight + Spacing; rowHeight = 0; }
        }
        if (index % columns != 0) height += rowHeight + Spacing;
        return new Size(width, Math.Max(0, height - Spacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = ColumnsFor(finalSize.Width, MinCardWidth, Spacing);
        double cardWidth = Math.Max(0, (finalSize.Width - Spacing * (columns - 1)) / columns);
        List<Control> visible = [.. Children.Where(child => child.IsVisible)];
        double y = 0;
        for (int start = 0; start < visible.Count; start += columns)
        {
            int count = Math.Min(columns, visible.Count - start);
            double rowHeight = 0;
            for (int offset = 0; offset < count; offset++) rowHeight = Math.Max(rowHeight, visible[start + offset].DesiredSize.Height);
            for (int offset = 0; offset < count; offset++)
                visible[start + offset].Arrange(new Rect(offset * (cardWidth + Spacing), y, cardWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
