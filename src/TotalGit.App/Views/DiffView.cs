using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using TotalGit.App.ViewModels;
using TotalGit.Core.Git;

namespace TotalGit.App.Views;

/// <summary>Diff viewer (inline or side by side) with line numbers; draws only the visible lines.</summary>
public sealed partial class DiffView : Control
{
    public static readonly StyledProperty<FileDiff?> DiffProperty =
        AvaloniaProperty.Register<DiffView, FileDiff?>(nameof(Diff));

    public static readonly StyledProperty<DiffViewMode> ModeProperty =
        AvaloniaProperty.Register<DiffView, DiffViewMode>(nameof(Mode));

    private const double LineHeight = 19;
    private const double GutterWidth = 46;
    private const double FontSize = 12.5;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1C1F24"));
    private static readonly IBrush GutterBrush = new SolidColorBrush(Color.Parse("#20242A"));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#2FBF71"), 0.10);
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.Parse("#E5392F"), 0.11);
    // The words that changed within a changed line.
    private static readonly IBrush AddedWordBrush = new SolidColorBrush(Color.Parse("#2FBF71"), 0.48);
    private static readonly IBrush RemovedWordBrush = new SolidColorBrush(Color.Parse("#E5392F"), 0.52);
    private static readonly IBrush AddedGutterBrush = new SolidColorBrush(Color.Parse("#2FBF71"), 0.28);
    private static readonly IBrush RemovedGutterBrush = new SolidColorBrush(Color.Parse("#E5392F"), 0.3);
    private static readonly IBrush HunkBrush = new SolidColorBrush(Color.Parse("#2D7BF4"), 0.14);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#DCDFE4"));
    private static readonly IBrush HunkTextBrush = new SolidColorBrush(Color.Parse("#7FA8E8"));
    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#6B727C"));
    private static readonly IBrush AddedSignBrush = new SolidColorBrush(Color.Parse("#4CC38A"));
    private static readonly IBrush RemovedSignBrush = new SolidColorBrush(Color.Parse("#F26B6B"));
    private static readonly IBrush NoticeBrush = new SolidColorBrush(Color.Parse("#8A9099"));
    private static readonly IBrush FillerBrush = new SolidColorBrush(Color.Parse("#131518"));
    private static readonly IBrush DividerBrush = new SolidColorBrush(Color.Parse("#30353C"));
    private static readonly IBrush SinceReviewBrush = new SolidColorBrush(Color.Parse("#A371F7"));
    private static readonly IBrush SinceReviewTint = new SolidColorBrush(Color.FromArgb(0x30, 0xA3, 0x71, 0xF7));

    public static readonly StyledProperty<TotalGit.Core.Hosting.ReviewDelta?> SinceReviewProperty =
        AvaloniaProperty.Register<DiffView, TotalGit.Core.Hosting.ReviewDelta?>(nameof(SinceReview));

    /// <summary>Lines changed since the file was reviewed (new-side numbers): marked in violet over the usual colours.</summary>
    public TotalGit.Core.Hosting.ReviewDelta? SinceReview
    {
        get => GetValue(SinceReviewProperty);
        set => SetValue(SinceReviewProperty, value);
    }

    private readonly Typeface _mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
    private double _offset;
    private double _hOffset;
    private double _charWidth = 7.5;
    private ScrollBar? _scrollBar;
    private ScrollBar? _hScrollBar;
    private bool _syncing;
    // The longest line in characters (tabs drawn as four spaces), for how far the text can scroll sideways.
    private int _longestLine;
    private IReadOnlyList<SplitRow> _splitRows = [];
    private SplitRowLines[] _splitLines = [];
    // For each diff line, the side-by-side row it is on.
    private int[] _splitRowOf = [];
    private DiffRowLayout _layout = new(0, LineHeight);
    private IntraLineHighlights _highlights = IntraLineHighlights.None;

    static DiffView()
    {
        AffectsRender<DiffView>(DiffProperty, ModeProperty, CanAddCommentsProperty, SinceReviewProperty);
        ClipToBoundsProperty.OverrideDefaultValue<DiffView>(true);
        FocusableProperty.OverrideDefaultValue<DiffView>(true);
    }

    public FileDiff? Diff
    {
        get => GetValue(DiffProperty);
        set => SetValue(DiffProperty, value);
    }

    public DiffViewMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private int RowCount => Mode == DiffViewMode.Split ? _splitRows.Count : Diff?.Lines.Count ?? 0;
    private int LineCount => RowCount + (Diff?.Truncated == true ? 1 : 0);
    private double MaxOffset => Math.Max(0, _layout.TotalHeight - Bounds.Height);

    /// <summary>The sideways scroll bar: both sides of a side-by-side diff move together.</summary>
    public void AttachHorizontalScrollBar(ScrollBar scrollBar)
    {
        _hScrollBar = scrollBar;
        scrollBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing) SetHOffset(scrollBar.Value);
        };
    }

    /// <summary>
    /// How far the text can scroll sideways: until the longest line ends a little inside the text area (in a
    /// side-by-side diff, the narrower side's).
    /// </summary>
    private double MaxHOffset
    {
        get
        {
            var textArea = Mode == DiffViewMode.Split
                ? Math.Floor(Bounds.Width / 2) - GutterWidth - 20
                : Bounds.Width - GutterWidth * 2 - 20;
            return Math.Max(0, _longestLine * _charWidth + 24 - textArea);
        }
    }

    private void SetHOffset(double value)
    {
        _hOffset = Math.Clamp(value, 0, MaxHOffset);
        SyncHorizontalScrollBar();
        InvalidateVisual();
    }

    private void SyncHorizontalScrollBar()
    {
        if (_hScrollBar is null) return;
        var max = MaxHOffset;
        _syncing = true;
        _hScrollBar.Maximum = max;
        _hScrollBar.ViewportSize = Math.Max(1, Bounds.Width);
        _hScrollBar.SmallChange = _charWidth * 4;
        _hScrollBar.LargeChange = Math.Max(_charWidth, Bounds.Width / 3);
        _hScrollBar.Value = Math.Min(_hOffset, max);
        _hScrollBar.IsVisible = max > 0 && Diff is { IsBinary: false };
        _syncing = false;
    }

    public void AttachScrollBar(ScrollBar scrollBar)
    {
        _scrollBar = scrollBar;
        scrollBar.SmallChange = LineHeight;
        scrollBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing) SetOffset(scrollBar.Value);
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DiffProperty)
        {
            var diff = change.GetNewValue<FileDiff?>();
            _splitRows = diff is not null ? SplitDiff.Build(diff.Lines) : [];
            _splitLines = diff is not null ? SplitRowIndex.Build(diff.Lines, _splitRows) : [];
            _splitRowOf = new int[diff?.Lines.Count ?? 0];
            for (var row = 0; row < _splitLines.Length; row++)
            {
                if (_splitLines[row].Left >= 0) _splitRowOf[_splitLines[row].Left] = row;
                if (_splitLines[row].Right >= 0) _splitRowOf[_splitLines[row].Right] = row;
            }
            ResetLayout();
            _highlights = diff is not null ? IntraLineDiff.Compute(diff.Lines) : IntraLineHighlights.None;
            _longestLine = diff?.Lines.Select(l => l.Text.Length + l.Text.Count(c => c == '\t') * 3).DefaultIfEmpty(0).Max() ?? 0;
            var oldPath = change.GetOldValue<FileDiff?>()?.Path;
            var newPath = change.GetNewValue<FileDiff?>()?.Path;
            if (oldPath != newPath)
            {
                _hOffset = 0;
                ClearSelection();
            }
            var switchedView = oldPath == newPath && change.GetOldValue<FileDiff?>()?.IsWholeFile != diff?.IsWholeFile;
            if (switchedView) ScrollToFirstChange();
            else SetOffset(oldPath == newPath ? _offset : 0);
            SetHOffset(_hOffset);
        }
        else if (change.Property == ModeProperty)
        {
            ClearSelection();
            ResetLayout();
            SetOffset(0);
            SetHOffset(_hOffset);
        }
        else if (change.Property == AnnotationsProperty)
        {
            OnAnnotationsChanged(change.GetNewValue<IReadOnlyList<DiffAnnotation>?>());
        }
        else if (change.Property == CanAddCommentsProperty)
        {
            _hover = null;
        }
        else if (change.Property == BoundsProperty)
        {
            SetOffset(_offset);
            SetHOffset(_hOffset);
        }
    }

    /// <summary>Scrolls so the first added or removed line sits a few lines below the top.</summary>
    private void ScrollToFirstChange()
    {
        var lines = Diff?.Lines;
        var first = lines?.ToList().FindIndex(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed) ?? -1;
        if (first < 0)
        {
            SetOffset(0);
            return;
        }
        var row = Mode == DiffViewMode.Split ? _splitRowOf[first] : first;
        SetOffset(_layout.TopOf(Math.Max(0, row - 3)));
    }

    private void SetOffset(double value)
    {
        var offset = Math.Clamp(value, 0, MaxOffset);
        // The hovered line has moved away from the pointer.
        if (offset != _offset) _hover = null;
        _offset = offset;
        if (_scrollBar is not null)
        {
            _syncing = true;
            _scrollBar.Maximum = MaxOffset;
            _scrollBar.ViewportSize = Bounds.Height;
            _scrollBar.LargeChange = Math.Max(LineHeight, Bounds.Height - LineHeight);
            _scrollBar.Value = _offset;
            _scrollBar.IsVisible = MaxOffset > 0;
            _syncing = false;
        }
        // The comment threads move with the text.
        if (_annotations.Count > 0) InvalidateArrange();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
        {
            var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            SetHOffset(_hOffset - delta * _charWidth * 6);
        }
        else
        {
            SetOffset(_offset - e.Delta.Y * LineHeight * 3);
            if (!_selecting) UpdateHover(e);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Keys typed into a comment box are the box's.
        if (IsInAnnotation(e.Source)) return;
        if (OnSelectionKey(e))
        {
            e.Handled = true;
            return;
        }
        var page = Math.Max(LineHeight, Bounds.Height - LineHeight);
        double? target = e.Key switch
        {
            Key.Down => _offset + LineHeight,
            Key.Up => _offset - LineHeight,
            Key.PageDown => _offset + page,
            Key.PageUp => _offset - page,
            Key.Home => 0,
            Key.End => MaxOffset,
            _ => null,
        };
        if (target is { } t)
        {
            SetOffset(t);
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // A click on a comment thread is the thread's (and must not take the focus from its text box).
        if (IsInAnnotation(e.Source)) return;
        Focus();
        if (OnAddCommentPressed(e) || _layout.RowAt(e.GetPosition(this).Y + _offset).InGap)
        {
            e.Handled = true;
            return;
        }
        if (OnSelectionPressed(e))
        {
            e.Handled = true;
            return;
        }
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed && Diff is { IsBinary: false } diff)
        {
            var (line, column) = LineAt(diff, point.Position);
            LineContextRequested?.Invoke(diff, line, column);
            e.Handled = true;
        }
    }

    /// <summary>Right-click: the file, and the line (in the new version) and column under the pointer.</summary>
    public event Action<FileDiff, int?, int?>? LineContextRequested;

    private (int? Line, int? Column) LineAt(FileDiff diff, Point p)
    {
        if (RowUnder(p) is not { } row) return (null, null);
        int index;
        double textLeft;
        if (Mode == DiffViewMode.Split)
        {
            if (row >= _splitLines.Length) return (null, null);
            var half = Math.Floor(Bounds.Width / 2);
            var (left, right) = _splitLines[row];
            // The right side is the new file; a left-only (removed) line maps to where it was.
            index = p.X < half ? (left >= 0 ? left : right) : (right >= 0 ? right : left);
            textLeft = (p.X < half ? 0 : half + 1) + GutterWidth + 20;
        }
        else
        {
            if (row >= diff.Lines.Count) return (null, null);
            index = row;
            textLeft = GutterWidth * 2 + 20;
        }
        if (index < 0) return (null, null);

        var l = diff.Lines[index];
        var target = DiffLineMap.TargetLine(diff.Lines, index);
        // A column only means something on a line that is in the new file.
        int? column = l.NewLine is not null ? ColumnAt(l.Text, (p.X - textLeft + _hOffset) / _charWidth) : null;
        return (target, column);
    }

    /// <summary>The row under a point, or null over the space below a row or past the end.</summary>
    private int? RowUnder(Point p)
    {
        var y = p.Y + _offset;
        if (y < 0 || y >= _layout.TotalHeight) return null;
        var (row, inGap) = _layout.RowAt(y);
        return inGap ? null : row;
    }

    /// <summary>
    /// The index in <see cref="FileDiff.Lines"/> of the line under a point (on the side under it in a
    /// side-by-side diff), or null over a filler, a comment thread, or past the end.
    /// </summary>
    public int? LineIndexAt(Point p) => RowUnder(p) is { } row ? LineIndexOf(row, SideAt(p.X)) : null;

    /// <summary>The diff line a row shows on a side (inline, the side is ignored).</summary>
    private int? LineIndexOf(int row, int side)
    {
        if (Mode == DiffViewMode.Split)
        {
            if (row < 0 || row >= _splitLines.Length) return null;
            var index = side == 0 ? _splitLines[row].Left : _splitLines[row].Right;
            return index >= 0 ? index : null;
        }
        return row >= 0 && row < (Diff?.Lines.Count ?? 0) ? row : null;
    }

    /// <summary>The row a diff line is on, or -1.</summary>
    private int RowOfLine(int index)
    {
        if (index < 0 || index >= (Diff?.Lines.Count ?? 0)) return -1;
        return Mode == DiffViewMode.Split ? _splitRowOf[index] : index;
    }

    /// <summary>Starts the layout over for a new diff or mode (keeping the space for comment threads).</summary>
    private void ResetLayout()
    {
        _hover = null;
        _layout = new DiffRowLayout(LineCount, LineHeight);
        UpdateExtras();
        InvalidateMeasure();
    }

    /// <summary>1-based column in the raw text for a display position (tabs are drawn as four spaces).</summary>
    private static int ColumnAt(string text, double displayColumn)
    {
        var display = 0;
        for (var i = 0; i < text.Length; i++)
        {
            display += text[i] == '\t' ? 4 : 1;
            if (display > displayColumn) return i + 1;
        }
        return text.Length + 1;
    }

    public override void Render(DrawingContext ctx)
    {
        var width = Bounds.Width;
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        var diff = Diff;
        if (diff is null) return;

        if (diff.IsBinary || diff.Lines.Count == 0)
        {
            var notice = Text(diff.IsBinary ? "Binary file — no text diff." : "No changes to show.", NoticeBrush);
            ctx.DrawText(notice, new Point(16, 16));
            return;
        }

        var charWidth = Text("M", TextBrush).WidthIncludingTrailingWhitespace;
        if (charWidth != _charWidth)
        {
            _charWidth = charWidth;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SetHOffset(_hOffset));
        }
        if (Mode == DiffViewMode.Split)
        {
            RenderSplit(ctx, diff);
            return;
        }

        var textLeft = GutterWidth * 2 + 20;
        ctx.FillRectangle(GutterBrush, new Rect(0, 0, GutterWidth * 2, Bounds.Height));

        for (var i = _layout.RowAt(_offset).Row; i < LineCount && _layout.TopOf(i) < _offset + Bounds.Height; i++)
        {
            var y = _layout.TopOf(i) - _offset;
            DrawGap(ctx, i, GutterWidth * 2);
            if (i >= diff.Lines.Count)
            {
                ctx.DrawText(Text($"Diff truncated after {diff.Lines.Count:N0} lines.", NoticeBrush), new Point(textLeft, y + 2));
                continue;
            }

            var line = diff.Lines[i];
            var (bg, gutterBg, sign, signBrush) = line.Kind switch
            {
                DiffLineKind.Added => (AddedBrush, AddedGutterBrush, "+", AddedSignBrush),
                DiffLineKind.Removed => (RemovedBrush, RemovedGutterBrush, "-", RemovedSignBrush),
                DiffLineKind.Hunk => (HunkBrush, HunkBrush, "", TextBrush),
                _ => ((IBrush?)null, (IBrush?)null, "", TextBrush),
            };
            if (bg is not null) ctx.FillRectangle(bg, new Rect(GutterWidth * 2, y, width - GutterWidth * 2, LineHeight));
            if (gutterBg is not null) ctx.FillRectangle(gutterBg, new Rect(0, y, GutterWidth * 2, LineHeight));

            if (line.OldLine is { } o) DrawNumber(ctx, o, 0, y);
            if (line.NewLine is { } n) DrawNumber(ctx, n, GutterWidth, y);
            DrawSinceReview(ctx, line, GutterWidth * 2, width - GutterWidth * 2, y);

            using (ctx.PushClip(new Rect(GutterWidth * 2, y, width - GutterWidth * 2, LineHeight)))
            {
                if (sign.Length > 0) ctx.DrawText(Text(sign, signBrush), new Point(GutterWidth * 2 + 6, y + 2));
                var brush = line.Kind is DiffLineKind.Hunk or DiffLineKind.NoNewline ? HunkTextBrush : TextBrush;
                // Text scrolled sideways disappears before the +/- column instead of running under it.
                using (ctx.PushClip(new Rect(textLeft - TextMargin, y, Math.Max(0, width - textLeft + TextMargin), LineHeight)))
                    DrawLineText(ctx, line, brush, new Point(textLeft - _hOffset, y + 2), i, 0);
            }
        }
        DrawAddCommentButton(ctx);
    }

    private void RenderSplit(DrawingContext ctx, FileDiff diff)
    {
        var width = Bounds.Width;
        var half = Math.Floor(width / 2);
        var first = _layout.RowAt(_offset).Row;

        ctx.FillRectangle(GutterBrush, new Rect(0, 0, GutterWidth, Bounds.Height));
        ctx.FillRectangle(GutterBrush, new Rect(half + 1, 0, GutterWidth, Bounds.Height));

        for (var i = first; i < LineCount && _layout.TopOf(i) < _offset + Bounds.Height; i++)
        {
            var y = _layout.TopOf(i) - _offset;
            if (i >= _splitRows.Count)
            {
                ctx.DrawText(Text($"Diff truncated after {diff.Lines.Count:N0} lines.", NoticeBrush), new Point(GutterWidth + 20, y + 2));
                continue;
            }

            var row = _splitRows[i];
            if (row.IsHunk)
            {
                ctx.FillRectangle(HunkBrush, new Rect(0, y, width, LineHeight));
                using (ctx.PushClip(new Rect(GutterWidth + 20 - TextMargin, y, Math.Max(0, width - GutterWidth - 20 + TextMargin), LineHeight)))
                    ctx.DrawText(Text(row.Left!.Value.Text, HunkTextBrush), new Point(GutterWidth + 20 - _hOffset, y + 2));
                continue;
            }

            DrawSide(ctx, row.Left, 0, half, y, i, left: true);
            DrawSide(ctx, row.Right, half + 1, width - half - 1, y, i, left: false);
        }

        ctx.FillRectangle(DividerBrush, new Rect(half, 0, 1, Bounds.Height));
        // Comment threads span both sides, so their space goes over the divider.
        for (var i = first; i < LineCount && _layout.TopOf(i) < _offset + Bounds.Height; i++)
            DrawGap(ctx, i, GutterWidth);
        DrawAddCommentButton(ctx);
    }

    /// <summary>How far left of the text column scrolled text still shows (just clear of the +/- sign).</summary>
    private const double TextMargin = 4;

    private void DrawSide(DrawingContext ctx, DiffLine? line, double x, double w, double y, int row, bool left)
    {
        if (line is not { } l)
        {
            ctx.FillRectangle(FillerBrush, new Rect(x, y, w, LineHeight));
            return;
        }

        var (bg, gutterBg, sign, signBrush) = l.Kind switch
        {
            DiffLineKind.Added => (AddedBrush, AddedGutterBrush, "+", AddedSignBrush),
            DiffLineKind.Removed => (RemovedBrush, RemovedGutterBrush, "-", RemovedSignBrush),
            _ => ((IBrush?)null, (IBrush?)null, "", TextBrush),
        };
        if (bg is not null) ctx.FillRectangle(bg, new Rect(x + GutterWidth, y, w - GutterWidth, LineHeight));
        if (gutterBg is not null) ctx.FillRectangle(gutterBg, new Rect(x, y, GutterWidth, LineHeight));

        if ((left ? l.OldLine : l.NewLine) is { } n) DrawNumber(ctx, n, x, y);
        if (!left) DrawSinceReview(ctx, l, x + GutterWidth, w - GutterWidth, y);

        using (ctx.PushClip(new Rect(x + GutterWidth, y, w - GutterWidth, LineHeight)))
        {
            if (sign.Length > 0) ctx.DrawText(Text(sign, signBrush), new Point(x + GutterWidth + 6, y + 2));
            var brush = l.Kind == DiffLineKind.NoNewline ? HunkTextBrush : TextBrush;
            var textLeft = x + GutterWidth + 20;
            using (ctx.PushClip(new Rect(textLeft - TextMargin, y, Math.Max(0, w - GutterWidth - 20 + TextMargin), LineHeight)))
                DrawLineText(ctx, l, brush, new Point(textLeft - _hOffset, y + 2), row, left ? 0 : 1);
        }
    }

    /// <summary>
    /// The violet marks for changes since the file was reviewed: a bar and tint on lines added since, and a thin
    /// line above where lines were removed since.
    /// </summary>
    private void DrawSinceReview(DrawingContext ctx, DiffLine line, double x, double w, double y)
    {
        if (SinceReview is not { } delta || line.Kind == DiffLineKind.Removed || line.NewLine is not { } n) return;
        if (delta.NewLines.Contains(n))
        {
            ctx.FillRectangle(SinceReviewTint, new Rect(x, y, w, LineHeight));
            ctx.FillRectangle(SinceReviewBrush, new Rect(x, y, 3, LineHeight));
        }
        if (delta.RemovedBefore.Contains(n)) ctx.FillRectangle(SinceReviewBrush, new Rect(x, y - 1, Math.Min(w, 220), 2));
    }

    /// <summary>
    /// Scrolls to the next (or previous) block of changes below (above) the top of the view; false when there's none.
    /// </summary>
    public bool ScrollToNextChange(int direction)
    {
        if (Diff is not { } diff) return false;
        // The first line of each run of added/removed lines, as display rows.
        var starts = new List<int>();
        for (var i = 0; i < diff.Lines.Count; i++)
        {
            var changed = diff.Lines[i].Kind is DiffLineKind.Added or DiffLineKind.Removed;
            var before = i > 0 && diff.Lines[i - 1].Kind is DiffLineKind.Added or DiffLineKind.Removed;
            if (changed && !before) starts.Add(Mode == DiffViewMode.Split ? _splitRowOf[i] : i);
        }
        var current = _layout.RowAt(_offset + LineHeight * 3).Row;
        int? target = direction > 0
            ? starts.Where(r => r > current).Select(r => (int?)r).FirstOrDefault()
            : starts.Where(r => r < current).Select(r => (int?)r).LastOrDefault();
        if (target is not { } row) return false;
        SetOffset(_layout.TopOf(Math.Max(0, row - 3)));
        return true;
    }

    /// <summary>The diff line index nearest the middle of the view (for commenting from the keyboard).</summary>
    public int? LineNearCentre()
    {
        if (Diff is not { } diff || diff.Lines.Count == 0) return null;
        var row = _layout.RowAt(_offset + Bounds.Height / 2).Row;
        if (Mode != DiffViewMode.Split) return Math.Clamp(row, 0, diff.Lines.Count - 1);
        if (row < 0 || row >= _splitLines.Length) return null;
        var lines = _splitLines[row];
        return lines.Right >= 0 ? lines.Right : lines.Left >= 0 ? lines.Left : null;
    }

    /// <summary>Draws a line's text, over a stronger highlight on the words that changed.</summary>
    private void DrawLineText(DrawingContext ctx, DiffLine line, IBrush brush, Point origin, int row, int side)
    {
        DrawSelection(ctx, line, row, side, origin);
        var text = Text(line.Text.Replace("\t", "    "), brush);
        var ranges = _highlights.For(line);
        if (ranges.Count > 0)
        {
            var wordBrush = line.Kind == DiffLineKind.Added ? AddedWordBrush : RemovedWordBrush;
            // Fill the row height, not just the glyph box (the text sits 2px below the row top).
            var top = new Point(origin.X, origin.Y - 2);
            foreach (var r in ranges)
            {
                // Tabs are drawn as four spaces, so offsets after a tab move along.
                var start = DisplayIndex(line.Text, r.Start);
                var end = DisplayIndex(line.Text, r.Start + r.Length);
                if (text.BuildHighlightGeometry(top, start, end - start) is { } box)
                    ctx.DrawRectangle(wordBrush, null, new Rect(box.Bounds.X, top.Y, box.Bounds.Width, LineHeight), 2, 2);
            }
        }
        ctx.DrawText(text, origin);
    }

    private static int DisplayIndex(string text, int index)
    {
        var tabs = 0;
        for (var i = 0; i < index; i++)
            if (text[i] == '\t') tabs++;
        return index + tabs * 3;
    }

    private void DrawNumber(DrawingContext ctx, int number, double left, double y)
    {
        var ft = Text(number.ToString(CultureInfo.InvariantCulture), NumberBrush);
        ctx.DrawText(ft, new Point(left + GutterWidth - 6 - ft.Width, y + 2));
    }

    private FormattedText Text(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _mono, FontSize, brush);
}
