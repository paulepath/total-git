using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using TotalGit.App.Services;
using TotalGit.Core.Git;

namespace TotalGit.App.Views;

/// <summary>
/// Where the changes are in a diff, drawn down its scroll bar (which sits on top, see-through): removed lines in a
/// red lane on the left, added lines in a green lane on the right, and in a review the comment threads (blue dots)
/// and the lines changed since the file was reviewed (violet). Clicking a mark scrolls to it.
/// </summary>
public sealed class DiffScrollMarks : Control
{
    // Marks within this many pixels of a click are what it means.
    private const double ClickSlack = 4;
    private const double MinMarkHeight = 3;
    private const double DotSize = 7;

    // The scroll bar's own track colour, which it no longer paints (so the marks show through).
    private static readonly IBrush TrackBrush = Palette.ScrollTrack;
    private static readonly IBrush RemovedBrush = Palette.MarkRemoved;
    private static readonly IBrush AddedBrush = Palette.MarkAdded;
    private static readonly IBrush SinceReviewBrush = Palette.Purple;
    private static readonly IBrush CommentBrush = Palette.Blue;
    private static readonly IPen CommentRing = new Pen(Palette.CommentRing, 1.5);

    private DiffView? _view;
    private ScrollBar? _bar;
    private Track? _track;
    private readonly Cursor _hand = new(StandardCursorType.Hand);

    public DiffScrollMarks()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Draws <paramref name="view"/>'s marks under <paramref name="bar"/>, which must be laid over this control.</summary>
    internal void Attach(DiffView view, ScrollBar bar)
    {
        _view = view;
        _bar = bar;
        bar.TemplateApplied += (_, _) => FindTrack();
        bar.AddHandler(PointerPressedEvent, OnBarPressed, RoutingStrategies.Tunnel);
        bar.AddHandler(PointerMovedEvent, OnBarMoved, RoutingStrategies.Tunnel);
        bar.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) IsVisible = bar.IsVisible;
        };
        IsVisible = bar.IsVisible;
        FindTrack();
    }

    private void FindTrack()
    {
        if (_track is not null) _track.PropertyChanged -= OnTrackChanged;
        _track = _bar?.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        if (_track is not null) _track.PropertyChanged += OnTrackChanged;
        InvalidateVisual();
    }

    private void OnTrackChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty) InvalidateVisual();
    }

    /// <summary>The part of this control the scroll bar's thumb travels over (between its arrow buttons).</summary>
    private (double Top, double Height) TrackSpan()
    {
        if (_track is { } track && track.TranslatePoint(default, this) is { } origin && track.Bounds.Height > 0)
            return (origin.Y, track.Bounds.Height);
        return (0, Bounds.Height);
    }

    /// <summary>Each mark's span down the track, the row it scrolls to, and its kind (null for a comment).</summary>
    private IEnumerable<(double Top, double Height, int Row, DiffMarkKind? Kind)> Spans()
    {
        if (_view is not { } view) yield break;
        var layout = view.RowLayout;
        var total = layout.TotalHeight;
        if (total <= 0) yield break;
        var (top, height) = TrackSpan();
        var scale = height / total;
        foreach (var m in view.Marks)
        {
            var y = top + layout.TopOf(m.FirstRow) * scale;
            var bottom = top + (layout.TopOf(m.FirstRow + m.RowCount - 1) + layout.RowHeight) * scale;
            yield return (y, Math.Max(MinMarkHeight, bottom - y), m.FirstRow, m.Kind);
        }
        foreach (var row in view.CommentRows)
            yield return (top + (layout.TopOf(row) + layout.RowHeight / 2) * scale - DotSize / 2, DotSize, row, null);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(TrackBrush, new Rect(Bounds.Size));
        if (_view is null) return;
        var width = Bounds.Width;
        var lane = Math.Max(2, Math.Floor((width - 5) / 2));
        foreach (var (y, h, _, kind) in Spans())
        {
            switch (kind)
            {
                case DiffMarkKind.Removed:
                    ctx.FillRectangle(RemovedBrush, new Rect(2, y, lane, h));
                    break;
                case DiffMarkKind.Added:
                    ctx.FillRectangle(AddedBrush, new Rect(width - 2 - lane, y, lane, h));
                    break;
                case DiffMarkKind.SinceReview:
                    ctx.FillRectangle(SinceReviewBrush, new Rect(Math.Floor(width / 2) - 1, y, 2, h));
                    break;
            }
        }
        // Comment dots on top of the lanes.
        foreach (var (y, h, _, kind) in Spans())
            if (kind is null)
                ctx.DrawEllipse(CommentBrush, CommentRing, new Rect(width / 2 - h / 2, y, h, h));
    }

    /// <summary>The row of the mark under a point on the scroll bar's track (not on its thumb or buttons), if any.</summary>
    private int? RowAt(PointerEventArgs e)
    {
        if (_track is null || _view is null) return null;
        if (_track.Thumb is { } thumb && thumb.IsPointerOver) return null;
        var p = e.GetPosition(this);
        var (top, height) = TrackSpan();
        if (p.Y < top || p.Y > top + height) return null;
        int? best = null;
        var bestDistance = double.MaxValue;
        foreach (var (y, h, row, _) in Spans())
        {
            var distance = p.Y < y ? y - p.Y : p.Y > y + h ? p.Y - (y + h) : 0;
            if (distance > ClickSlack || distance >= bestDistance) continue;
            best = row;
            bestDistance = distance;
        }
        return best;
    }

    private void OnBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || RowAt(e) is not { } row) return;
        _view!.ScrollToRow(row);
        e.Handled = true;
    }

    private void OnBarMoved(object? sender, PointerEventArgs e)
    {
        if (_bar is null) return;
        var cursor = RowAt(e) is null ? null : _hand;
        if (_bar.Cursor != cursor) _bar.Cursor = cursor;
    }
}
