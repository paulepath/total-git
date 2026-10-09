using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TotalGit.Core.Hosting;

namespace TotalGit.App.Views;

/// <summary>
/// A file's review box: empty when not reviewed, ticked (green) when approved, crossed (red) when rejected, and half
/// filled in the colour of the verdict when the file changed since.
/// </summary>
public sealed class ReviewBox : Control
{
    public static readonly StyledProperty<FileReviewState> StateProperty =
        AvaloniaProperty.Register<ReviewBox, FileReviewState>(nameof(State));

    private static readonly IPen EmptyPen = new Pen(new SolidColorBrush(Color.Parse("#6B727C")), 1.3);
    internal static readonly IBrush ReviewedBrush = new SolidColorBrush(Color.Parse("#2FA36B"));
    internal static readonly IBrush RejectedBrush = new SolidColorBrush(Color.Parse("#D9474D"));
    private static readonly IPen ReviewedPen = new Pen(ReviewedBrush, 1.3);
    private static readonly IPen RejectedPen = new Pen(RejectedBrush, 1.3);
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
            case FileReviewState.Rejected:
                ctx.DrawRectangle(RejectedBrush, null, box, 3, 3);
                var a = box.Width * 0.27;
                ctx.DrawLine(TickPen, new Point(box.Left + a, box.Top + a), new Point(box.Right - a, box.Bottom - a));
                ctx.DrawLine(TickPen, new Point(box.Right - a, box.Top + a), new Point(box.Left + a, box.Bottom - a));
                break;
            case FileReviewState.ChangedSinceReview:
                DrawHalf(ctx, box, ReviewedPen, ReviewedBrush);
                break;
            case FileReviewState.ChangedSinceRejected:
                DrawHalf(ctx, box, RejectedPen, RejectedBrush);
                break;
            default:
                ctx.DrawRectangle(null, EmptyPen, box, 3, 3);
                break;
        }
    }

    /// <summary>Part marked: outlined, with the lower-left half filled, in the colour of the verdict.</summary>
    private static void DrawHalf(DrawingContext ctx, Rect box, IPen pen, IBrush brush)
    {
        ctx.DrawRectangle(null, pen, box, 3, 3);
        var half = new StreamGeometry();
        using (var g = half.Open())
        {
            g.BeginFigure(new Point(box.Left + 2.5, box.Top + 2.5), true);
            g.LineTo(new Point(box.Left + 2.5, box.Bottom - 2.5));
            g.LineTo(new Point(box.Right - 2.5, box.Bottom - 2.5));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, half);
    }
}

/// <summary>
/// How far a review has got, as one bar: approved (green), rejected (red), changed since marked (the same, faded),
/// then what's still to review.
/// </summary>
public sealed class ReviewProgressBar : Control
{
    public static readonly StyledProperty<int> ApprovedProperty = AvaloniaProperty.Register<ReviewProgressBar, int>(nameof(Approved));
    public static readonly StyledProperty<int> RejectedProperty = AvaloniaProperty.Register<ReviewProgressBar, int>(nameof(Rejected));
    public static readonly StyledProperty<int> ChangedProperty = AvaloniaProperty.Register<ReviewProgressBar, int>(nameof(Changed));
    public static readonly StyledProperty<int> TotalProperty = AvaloniaProperty.Register<ReviewProgressBar, int>(nameof(Total));

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#30353C"));
    private static readonly IBrush ChangedBrush = new SolidColorBrush(Color.Parse("#8A9099"));

    static ReviewProgressBar() => AffectsRender<ReviewProgressBar>(ApprovedProperty, RejectedProperty, ChangedProperty, TotalProperty);

    public ReviewProgressBar() => Height = 5;

    public int Approved { get => GetValue(ApprovedProperty); set => SetValue(ApprovedProperty, value); }
    public int Rejected { get => GetValue(RejectedProperty); set => SetValue(RejectedProperty, value); }
    public int Changed { get => GetValue(ChangedProperty); set => SetValue(ChangedProperty, value); }
    public int Total { get => GetValue(TotalProperty); set => SetValue(TotalProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        var radius = bounds.Height / 2;
        using (ctx.PushGeometryClip(new RectangleGeometry(bounds, radius, radius)))
        {
            ctx.FillRectangle(TrackBrush, bounds);
            if (Total <= 0) return;
            var x = 0.0;
            foreach (var (count, brush) in new[] { (Approved, ReviewBox.ReviewedBrush), (Rejected, ReviewBox.RejectedBrush), (Changed, ChangedBrush) })
            {
                var w = bounds.Width * count / Total;
                ctx.FillRectangle(brush, new Rect(x, 0, w, bounds.Height));
                x += w;
            }
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
