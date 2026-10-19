using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.App.Views;

/// <summary>
/// A control shown in the diff below a line, such as a review comment thread. <see cref="LineIndex"/> is an
/// index into <see cref="FileDiff.Lines"/>; side by side, the control goes below that line's row on
/// <see cref="Side"/> (a comment on the new code under the right side, one on removed code under the left).
/// </summary>
public sealed record DiffAnnotation(int LineIndex, Control Content, DiffSide Side = DiffSide.Right);

// Annotations: real controls hosted between the drawn rows, with the rows below moved down to make room,
// and a "+" on the hovered line to start a new comment.
public sealed partial class DiffView
{
    /// <summary>Controls to show between lines. Several on one line are stacked in list order.</summary>
    public static readonly StyledProperty<IReadOnlyList<DiffAnnotation>?> AnnotationsProperty =
        AvaloniaProperty.Register<DiffView, IReadOnlyList<DiffAnnotation>?>(nameof(Annotations));

    /// <summary>Whether hovering a code line offers a "+" button that raises <see cref="AddCommentRequested"/>.</summary>
    public static readonly StyledProperty<bool> CanAddCommentsProperty =
        AvaloniaProperty.Register<DiffView, bool>(nameof(CanAddComments));

    private const double GapPadding = 6;
    private const double GapSpacing = 6;
    private const double GapRightMargin = 12;
    private const double AddButtonSize = 16;

    private static readonly IBrush GapBrush = Palette.DiffGap;
    private static readonly IBrush GapBorderBrush = Palette.DiffGapBorder;
    private static readonly IBrush AddButtonBrush = Palette.AccentStrong;
    private static readonly IPen AddButtonPen = new Pen(Brushes.White, 1.6);

    /// <summary>A code line that can be commented on: its row, side, and index in the diff's lines.</summary>
    private readonly record struct CommentTarget(int Row, int Side, int Line);

    private IReadOnlyList<DiffAnnotation> _annotations = [];
    private HashSet<Control> _annotationContents = new(ReferenceEqualityComparer.Instance);
    // Where the next thread goes in each row's space (side by side, on each side), while arranging.
    private readonly Dictionary<(int Row, int Column), double> _stackTops = [];
    private CommentTarget? _hover;
    private readonly Cursor _textCursor = new(StandardCursorType.Ibeam);
    private readonly Cursor _arrowCursor = new(StandardCursorType.Arrow);
    private readonly Cursor _handCursor = new(StandardCursorType.Hand);

    public IReadOnlyList<DiffAnnotation>? Annotations
    {
        get => GetValue(AnnotationsProperty);
        set => SetValue(AnnotationsProperty, value);
    }

    public bool CanAddComments
    {
        get => GetValue(CanAddCommentsProperty);
        set => SetValue(CanAddCommentsProperty, value);
    }

    /// <summary>The "+" on a line was clicked: the file, and the index of the line in its <see cref="FileDiff.Lines"/>.</summary>
    public event Action<FileDiff, int>? AddCommentRequested;

    private void OnAnnotationsChanged(IReadOnlyList<DiffAnnotation>? annotations)
    {
        _annotations = annotations ?? [];
        var next = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        foreach (var a in _annotations) next.Add(a.Content);
        // Controls in both lists stay put, so a text box being typed in keeps its focus.
        foreach (var c in _annotationContents)
        {
            if (next.Contains(c)) continue;
            LogicalChildren.Remove(c);
            VisualChildren.Remove(c);
        }
        foreach (var c in next)
        {
            if (_annotationContents.Contains(c)) continue;
            LogicalChildren.Add(c);
            VisualChildren.Add(c);
        }
        _annotationContents = next;
        UpdateExtras();
        _scrollMarks?.InvalidateVisual();
        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>Which stack an annotation joins below its row: side by side, its side's; inline, the one.</summary>
    private int ColumnOf(DiffAnnotation a) => Mode == DiffViewMode.Split && a.Side == DiffSide.Left ? 0 : 1;

    /// <summary>
    /// Where an annotation goes across a view <paramref name="width"/> wide, lined up with the code: inline, after
    /// both gutters; side by side, on its own side after that side's gutter.
    /// </summary>
    private (double Left, double Width) AnnotationSpan(DiffAnnotation a, double width)
    {
        if (Mode != DiffViewMode.Split) return (GutterWidth * 2, Math.Max(0, width - GutterWidth * 2 - GapRightMargin));
        var half = Math.Floor(width / 2);
        var left = a.Side == DiffSide.Left ? GutterWidth : half + 1 + GutterWidth;
        var right = a.Side == DiffSide.Left ? half : width;
        return (left, Math.Max(0, right - left - GapRightMargin));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Bounds.Width;
        foreach (var a in _annotations) a.Content.Measure(new Size(AnnotationSpan(a, width).Width, double.PositiveInfinity));
        UpdateExtras();
        // The view takes the space it's given; the threads don't size it.
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _stackTops.Clear();
        foreach (var a in _annotations)
        {
            var c = a.Content;
            var (left, width) = AnnotationSpan(a, finalSize.Width);
            var row = RowOfLine(a.LineIndex);
            if (row < 0 || !c.IsVisible)
            {
                // Out of sight (not hidden, which could take the focus away).
                c.Arrange(new Rect(left, -c.DesiredSize.Height - 1000, width, c.DesiredSize.Height));
                continue;
            }
            var key = (row, ColumnOf(a));
            var top = _stackTops.TryGetValue(key, out var t) ? t : _layout.GapTopOf(row) + GapPadding;
            c.Arrange(new Rect(left, top - _offset, width, c.DesiredSize.Height));
            _stackTops[key] = top + c.DesiredSize.Height + GapSpacing;
        }
        return finalSize;
    }

    /// <summary>Reserves the space below each row for its threads (as last measured): side by side, the taller side's.</summary>
    private void UpdateExtras()
    {
        var stacks = new Dictionary<(int Row, int Column), double>();
        foreach (var a in _annotations)
        {
            var row = RowOfLine(a.LineIndex);
            if (row < 0 || !a.Content.IsVisible) continue;
            var key = (row, ColumnOf(a));
            stacks[key] = (stacks.TryGetValue(key, out var h) ? h + GapSpacing : GapPadding * 2) + a.Content.DesiredSize.Height;
        }
        var extras = new Dictionary<int, double>();
        foreach (var ((row, _), height) in stacks) extras[row] = Math.Max(extras.GetValueOrDefault(row), height);
        if (!_layout.SetExtras(extras)) return;
        SetOffset(_offset);
        _scrollMarks?.InvalidateVisual();
    }

    /// <summary>Whether an event came from inside one of the annotation controls (or a popup of one).</summary>
    private bool IsInAnnotation(object? source)
    {
        if (_annotationContents.Count == 0) return false;
        for (var v = source as Visual; v is not null && v != this; v = v.GetVisualParent() ?? v.Parent as Visual)
            if (v is Control c && _annotationContents.Contains(c)) return true;
        return false;
    }

    /// <summary>Paints the space below a row, from the left edge given (the gutters to its left carry on).</summary>
    private void DrawGap(DrawingContext ctx, int row, double left)
    {
        var extra = _layout.ExtraOf(row);
        if (extra <= 0) return;
        var y = _layout.GapTopOf(row) - _offset;
        var width = Bounds.Width - left;
        ctx.FillRectangle(GapBrush, new Rect(left, y, width, extra));
        ctx.FillRectangle(GapBorderBrush, new Rect(left, y, width, 1));
        ctx.FillRectangle(GapBorderBrush, new Rect(left, y + extra - 1, width, 1));
    }

    /// <summary>The code line under a point that a comment can be added to, if comments can be added.</summary>
    private CommentTarget? CommentTargetAt(Point p)
    {
        if (!CanAddComments || Diff is not { IsBinary: false } diff || RowUnder(p) is not { } row) return null;
        var side = SideAt(p.X);
        if (LineIndexOf(row, side) is not { } index) return null;
        return Commentable(diff)[index] ? new CommentTarget(row, side, index) : null;
    }

    private FileDiff? _commentableFor;
    private bool[] _commentable = [];

    /// <summary>The lines the host takes a comment on (in the whole-file view, not those far from a change).</summary>
    private bool[] Commentable(FileDiff diff)
    {
        if (_commentableFor != diff)
        {
            _commentable = CommentableLines.Build(diff.Lines);
            _commentableFor = diff;
        }
        return _commentable;
    }

    /// <summary>The "+" button, at the start of the code column (over the +/- sign).</summary>
    private Rect AddButtonRect(CommentTarget target)
    {
        var codeLeft = Mode == DiffViewMode.Split ? (target.Side == 0 ? 0 : Half + 1) + GutterWidth : GutterWidth * 2;
        var top = _layout.TopOf(target.Row) - _offset + (LineHeight - AddButtonSize) / 2;
        return new Rect(codeLeft + 1, top, AddButtonSize, AddButtonSize);
    }

    private bool OnAddCommentPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || Diff is not { } diff) return false;
        if (CommentTargetAt(point.Position) is not { } target || !AddButtonRect(target).Inflate(2).Contains(point.Position))
            return false;
        AddCommentRequested?.Invoke(diff, target.Line);
        return true;
    }

    /// <summary>Tracks the hovered line (for the "+") and picks the cursor for what's under the pointer.</summary>
    private void UpdateHover(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var inThread = IsInAnnotation(e.Source);
        var hover = inThread ? null : CommentTargetAt(p);
        if (hover != _hover)
        {
            _hover = hover;
            InvalidateVisual();
        }
        var cursor = inThread || _layout.RowAt(p.Y + _offset).InGap ? _arrowCursor
            : hover is { } h && AddButtonRect(h).Inflate(2).Contains(p) ? _handCursor
            : _textCursor;
        if (Cursor != cursor) Cursor = cursor;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
    }

    private void DrawAddCommentButton(DrawingContext ctx)
    {
        if (_hover is not { } target || _selecting || !CanAddComments) return;
        var r = AddButtonRect(target);
        ctx.DrawRectangle(AddButtonBrush, null, r, 3, 3);
        var c = r.Center;
        ctx.DrawLine(AddButtonPen, new Point(c.X - 4, c.Y), new Point(c.X + 4, c.Y));
        ctx.DrawLine(AddButtonPen, new Point(c.X, c.Y - 4), new Point(c.X, c.Y + 4));
    }
}
