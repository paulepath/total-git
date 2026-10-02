using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TotalGit.Core.Hosting;

namespace TotalGit.App.Views;

/// <summary>
/// A file's review box: empty when not reviewed, ticked (green) when reviewed, and half filled (amber) when the file
/// changed since it was reviewed.
/// </summary>
public sealed class ReviewBox : Control
{
    public static readonly StyledProperty<FileReviewState> StateProperty =
        AvaloniaProperty.Register<ReviewBox, FileReviewState>(nameof(State));

    private static readonly IPen EmptyPen = new Pen(new SolidColorBrush(Color.Parse("#6B727C")), 1.3);
    private static readonly IBrush ReviewedBrush = new SolidColorBrush(Color.Parse("#2FA36B"));
    private static readonly IBrush ChangedBrush = new SolidColorBrush(Color.Parse("#E8B339"));
    private static readonly IPen ChangedPen = new Pen(ChangedBrush, 1.3);
    private static readonly IPen TickPen = new Pen(Brushes.White, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    static ReviewBox() => AffectsRender<ReviewBox>(StateProperty);

    public ReviewBox()
    {
        Width = 14;
        Height = 14;
    }

    public FileReviewState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        var box = new Rect(0.75, 0.75, Bounds.Width - 1.5, Bounds.Height - 1.5);
        switch (State)
        {
            case FileReviewState.Reviewed:
                ctx.DrawRectangle(ReviewedBrush, null, box, 3, 3);
                var tick = new StreamGeometry();
                using (var g = tick.Open())
                {
                    g.BeginFigure(new Point(box.Left + box.Width * 0.22, box.Top + box.Height * 0.52), false);
                    g.LineTo(new Point(box.Left + box.Width * 0.43, box.Top + box.Height * 0.73));
                    g.LineTo(new Point(box.Left + box.Width * 0.78, box.Top + box.Height * 0.3));
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, TickPen, tick);
                break;
            case FileReviewState.ChangedSinceReview:
                // Part ticked: outlined, with the lower-left half filled.
                ctx.DrawRectangle(null, ChangedPen, box, 3, 3);
                var half = new StreamGeometry();
                using (var g = half.Open())
                {
                    g.BeginFigure(new Point(box.Left + 2.5, box.Top + 2.5), true);
                    g.LineTo(new Point(box.Left + 2.5, box.Bottom - 2.5));
                    g.LineTo(new Point(box.Right - 2.5, box.Bottom - 2.5));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(ChangedBrush, null, half);
                break;
            default:
                ctx.DrawRectangle(null, EmptyPen, box, 3, 3);
                break;
        }
    }
}

/// <summary>Small converters for the review window.</summary>
public static class ReviewConverters
{
    public static readonly IValueConverter MessageBackground = new FuncValueConverter<bool, IBrush>(isError =>
        new SolidColorBrush(Color.Parse(isError ? "#5A2427" : "#1F3A2C")));

    public static readonly IValueConverter GeneratedOpacity = new FuncValueConverter<bool, double>(generated => generated ? 0.55 : 1);

    public static readonly IValueConverter Upper = new FuncValueConverter<string?, string>(s => s?.ToUpper(CultureInfo.CurrentCulture) ?? "");
}
