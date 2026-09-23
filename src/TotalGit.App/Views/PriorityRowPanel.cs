using Avalonia;
using Avalonia.Controls;

namespace TotalGit.App.Views;

/// <summary>
/// Lays children out left to right, giving the first child (the label) priority: it gets its full
/// width, and each following child is shown only if it fits completely in the space left over,
/// otherwise it is hidden. When even the label doesn't fit it is measured to the available width,
/// so a TextBlock with TextTrimming shows an ellipsis. Secondary details should also be in a tooltip.
/// </summary>
public sealed class PriorityRowPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<PriorityRowPanel, double>(nameof(Spacing), 6);

    private readonly HashSet<Control> _hidden = [];

    static PriorityRowPanel()
    {
        AffectsMeasure<PriorityRowPanel>(SpacingProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _hidden.Clear();
        var visible = Children.Where(c => c.IsVisible).ToList();
        if (visible.Count == 0) return default;

        foreach (var child in visible) child.Measure(Size.Infinity);
        var label = visible[0];
        var height = visible.Max(c => c.DesiredSize.Height);
        var width = label.DesiredSize.Width;

        foreach (var extra in visible.Skip(1))
        {
            var needed = width + Spacing + extra.DesiredSize.Width;
            if (_hidden.Count == 0 && needed <= availableSize.Width) width = needed;
            else _hidden.Add(extra); // once one extra doesn't fit, later ones are hidden too
        }

        if (label.DesiredSize.Width > availableSize.Width)
            label.Measure(new Size(availableSize.Width, availableSize.Height));

        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        var first = true;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
            if (_hidden.Contains(child))
            {
                // Children with a fixed Width/Height still draw at their own size when given a
                // zero rect, so park them outside the (clipped) panel instead.
                child.Arrange(new Rect(finalSize.Width + 1000, 0, child.DesiredSize.Width, child.DesiredSize.Height));
                continue;
            }

            if (!first) x += Spacing;
            var w = first ? Math.Min(child.DesiredSize.Width, finalSize.Width) : child.DesiredSize.Width;
            var y = (finalSize.Height - child.DesiredSize.Height) / 2;
            child.Arrange(new Rect(x, y, w, child.DesiredSize.Height));
            x += w;
            first = false;
        }
        return finalSize;
    }
}
