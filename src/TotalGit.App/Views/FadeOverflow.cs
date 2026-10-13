using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TotalGit.App.Views;

/// <summary>
/// Holds content that may be wider than the room it's given (a file name in a narrow list): instead of drawing past
/// its edge, over whatever sits beside it, the content is cut off there and fades out over the last few pixels.
/// Content that fits is shown as it is.
/// </summary>
public sealed class FadeOverflow : Decorator
{
    private const double FadeWidth = 18;

    public FadeOverflow()
    {
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        // The content's natural width, so it isn't squeezed (trimmed or wrapped) to fit.
        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var desired = child.DesiredSize;
        return new Size(Math.Min(desired.Width, availableSize.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child) return finalSize;
        var natural = child.DesiredSize.Width;
        child.Arrange(new Rect(0, 0, Math.Max(natural, finalSize.Width), finalSize.Height));
        OpacityMask = natural > finalSize.Width + 0.5 && finalSize.Width > 0 ? Fade(finalSize.Width) : null;
        return finalSize;
    }

    private static LinearGradientBrush Fade(double width)
    {
        var start = Math.Clamp((width - FadeWidth) / width, 0, 1);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Colors.Black, start),
                new GradientStop(Colors.Transparent, 1),
            },
        };
    }
}
