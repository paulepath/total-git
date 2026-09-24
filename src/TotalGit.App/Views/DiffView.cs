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

    private readonly Typeface _mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
    private double _offset;
    private double _hOffset;
    private double _charWidth = 7.5;
    private ScrollBar? _scrollBar;
    private bool _syncing;
    private IReadOnlyList<SplitRow> _splitRows = [];
    private IntraLineHighlights _highlights = IntraLineHighlights.None;

    static DiffView()
    {
        AffectsRender<DiffView>(DiffProperty, ModeProperty);
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
    private double MaxOffset => Math.Max(0, LineCount * LineHeight - Bounds.Height);

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
            _highlights = diff is not null ? IntraLineDiff.Compute(diff.Lines) : IntraLineHighlights.None;
            var oldPath = change.GetOldValue<FileDiff?>()?.Path;
            var newPath = change.GetNewValue<FileDiff?>()?.Path;
            if (oldPath != newPath)
            {
                _hOffset = 0;
                ClearSelection();
            }
            SetOffset(oldPath == newPath ? _offset : 0);
        }
        else if (change.Property == ModeProperty)
        {
            ClearSelection();
            SetOffset(0);
        }
        else if (change.Property == BoundsProperty)
        {
            SetOffset(_offset);
        }
    }

    private void SetOffset(double value)
    {
        _offset = Math.Clamp(value, 0, MaxOffset);
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
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
        {
            var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            _hOffset = Math.Max(0, _hOffset - delta * _charWidth * 6);
            InvalidateVisual();
        }
        else
        {
            SetOffset(_offset - e.Delta.Y * LineHeight * 3);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
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
        Focus();
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
        var row = (int)((p.Y + _offset) / LineHeight);
        DiffLine? line;
        double textLeft;
        if (Mode == DiffViewMode.Split)
        {
            if (row < 0 || row >= _splitRows.Count) return (null, null);
            var half = Math.Floor(Bounds.Width / 2);
            var r = _splitRows[row];
            // The right side is the new file; a left-only (removed) line maps to where it was.
            line = p.X < half ? r.Left ?? r.Right : r.Right ?? r.Left;
            textLeft = (p.X < half ? 0 : half + 1) + GutterWidth + 20;
        }
        else
        {
            if (row < 0 || row >= diff.Lines.Count) return (null, null);
            line = diff.Lines[row];
            textLeft = GutterWidth * 2 + 20;
        }
        if (line is not { } l) return (null, null);

        var index = Mode == DiffViewMode.Split ? IndexOf(diff.Lines, l) : row;
        var target = DiffLineMap.TargetLine(diff.Lines, index);
        // A column only means something on a line that is in the new file.
        int? column = l.NewLine is not null ? ColumnAt(l.Text, (p.X - textLeft + _hOffset) / _charWidth) : null;
        return (target, column);
    }

    private static int IndexOf(IReadOnlyList<DiffLine> lines, DiffLine line)
    {
        for (var i = 0; i < lines.Count; i++)
            if (lines[i] == line) return i;
        return -1;
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

        _charWidth = Text("M", TextBrush).WidthIncludingTrailingWhitespace;
        if (Mode == DiffViewMode.Split)
        {
            RenderSplit(ctx, diff);
            return;
        }

        var textLeft = GutterWidth * 2 + 20;
        ctx.FillRectangle(GutterBrush, new Rect(0, 0, GutterWidth * 2, Bounds.Height));

        var first = Math.Max(0, (int)(_offset / LineHeight));
        var last = Math.Min(LineCount - 1, (int)((_offset + Bounds.Height) / LineHeight));
        for (var i = first; i <= last; i++)
        {
            var y = i * LineHeight - _offset;
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

            using (ctx.PushClip(new Rect(GutterWidth * 2, y, width - GutterWidth * 2, LineHeight)))
            {
                if (sign.Length > 0) ctx.DrawText(Text(sign, signBrush), new Point(GutterWidth * 2 + 6, y + 2));
                var brush = line.Kind is DiffLineKind.Hunk or DiffLineKind.NoNewline ? HunkTextBrush : TextBrush;
                DrawLineText(ctx, line, brush, new Point(textLeft - _hOffset, y + 2), i, 0);
            }
        }
    }

    private void RenderSplit(DrawingContext ctx, FileDiff diff)
    {
        var width = Bounds.Width;
        var half = Math.Floor(width / 2);
        var first = Math.Max(0, (int)(_offset / LineHeight));
        var last = Math.Min(LineCount - 1, (int)((_offset + Bounds.Height) / LineHeight));

        ctx.FillRectangle(GutterBrush, new Rect(0, 0, GutterWidth, Bounds.Height));
        ctx.FillRectangle(GutterBrush, new Rect(half + 1, 0, GutterWidth, Bounds.Height));

        for (var i = first; i <= last; i++)
        {
            var y = i * LineHeight - _offset;
            if (i >= _splitRows.Count)
            {
                ctx.DrawText(Text($"Diff truncated after {diff.Lines.Count:N0} lines.", NoticeBrush), new Point(GutterWidth + 20, y + 2));
                continue;
            }

            var row = _splitRows[i];
            if (row.IsHunk)
            {
                ctx.FillRectangle(HunkBrush, new Rect(0, y, width, LineHeight));
                using (ctx.PushClip(new Rect(0, y, width, LineHeight)))
                    ctx.DrawText(Text(row.Left!.Value.Text, HunkTextBrush), new Point(GutterWidth + 20 - _hOffset, y + 2));
                continue;
            }

            DrawSide(ctx, row.Left, 0, half, y, i, left: true);
            DrawSide(ctx, row.Right, half + 1, width - half - 1, y, i, left: false);
        }

        ctx.FillRectangle(DividerBrush, new Rect(half, 0, 1, Bounds.Height));
    }

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

        using (ctx.PushClip(new Rect(x + GutterWidth, y, w - GutterWidth, LineHeight)))
        {
            if (sign.Length > 0) ctx.DrawText(Text(sign, signBrush), new Point(x + GutterWidth + 6, y + 2));
            var brush = l.Kind == DiffLineKind.NoNewline ? HunkTextBrush : TextBrush;
            DrawLineText(ctx, l, brush, new Point(x + GutterWidth + 20 - _hOffset, y + 2), row, left ? 0 : 1);
        }
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
