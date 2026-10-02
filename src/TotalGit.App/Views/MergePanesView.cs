using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TotalGit.App.ViewModels;
using TotalGit.Core.Git;

namespace TotalGit.App.Views;

/// <summary>
/// The scroll position the merge tool's panes share: they are all laid out on the same rows
/// (<see cref="MergeLayout"/>), so one offset keeps line N level in every pane.
/// </summary>
public sealed class MergeScroll
{
    public const double LineHeight = 19;

    private readonly Dictionary<object, double> _viewports = [];

    public double Offset { get; private set; }

    /// <summary>How far the three source panes (base, ours, theirs) are scrolled sideways: they move together.</summary>
    public double HOffset { get; private set; }

    /// <summary>How far the result pane is scrolled sideways: it has its own width, so it scrolls on its own.</summary>
    public double ResultHOffset { get; private set; }

    /// <summary>The furthest each can scroll sideways (set by the view from the longest line and the pane widths).</summary>
    public double MaxHOffset { get; set; } = double.MaxValue;
    public double MaxResultHOffset { get; set; } = double.MaxValue;
    public int RowCount { get; private set; }

    public double TotalHeight => RowCount * LineHeight;

    /// <summary>Scrolling stops when the last row reaches the bottom of the shortest pane.</summary>
    public double MaxOffset => Math.Max(0, TotalHeight - (_viewports.Count == 0 ? 0 : _viewports.Values.Min()));

    public double Viewport => _viewports.Count == 0 ? 0 : _viewports.Values.Min();

    public event Action? Changed;

    public void SetRowCount(int rows)
    {
        RowCount = rows;
        SetOffset(Offset);
    }

    public void SetViewport(object pane, double height)
    {
        if (height <= 0) _viewports.Remove(pane);
        else _viewports[pane] = height;
        SetOffset(Offset);
    }

    public void SetOffset(double value)
    {
        Offset = Math.Clamp(value, 0, MaxOffset);
        Changed?.Invoke();
    }

    public void SetHOffset(double value)
    {
        HOffset = Math.Clamp(value, 0, Math.Max(0, MaxHOffset));
        Changed?.Invoke();
    }

    public void SetResultHOffset(double value)
    {
        ResultHOffset = Math.Clamp(value, 0, Math.Max(0, MaxResultHOffset));
        Changed?.Invoke();
    }

    /// <summary>Scrolls a pane sideways: the result on its own, any source pane with the other two.</summary>
    public void ScrollSideways(MergePane pane, double value)
    {
        if (pane == MergePane.Result) SetResultHOffset(value);
        else SetHOffset(value);
    }

    /// <summary>Brings a run of rows into view with a few rows of context above.</summary>
    public void Reveal(int firstRow, int rowCount)
    {
        var top = firstRow * LineHeight;
        var bottom = (firstRow + rowCount) * LineHeight;
        var viewport = Viewport;
        if (top >= Offset + LineHeight && bottom <= Offset + viewport - LineHeight) return;
        SetOffset(top - Math.Min(3 * LineHeight, Math.Max(0, (viewport - (bottom - top)) / 2)));
    }
}

/// <summary>
/// One pane of the merge tool (base, ours, theirs or the result), drawn row by row on the shared layout. Clicking
/// a conflict's line picks or drops that line; clicking its gutter picks or drops the whole side.
/// </summary>
public sealed class MergePaneView : Control
{
    public static readonly StyledProperty<MergePane> PaneProperty =
        AvaloniaProperty.Register<MergePaneView, MergePane>(nameof(Pane));

    public static readonly StyledProperty<MergeToolViewModel?> ModelProperty =
        AvaloniaProperty.Register<MergePaneView, MergeToolViewModel?>(nameof(Model));

    private const double LineHeight = MergeScroll.LineHeight;
    private const double PickWidth = 18;
    private const double NumberWidth = 40;
    private const double GutterWidth = PickWidth + NumberWidth;
    private const double TextPad = 8;
    private const double FontSize = 12.5;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1C1F24"));
    private static readonly IBrush GutterBrush = new SolidColorBrush(Color.Parse("#20242A"));
    private static readonly IBrush FillerBrush = new SolidColorBrush(Color.Parse("#15171B"));
    private static readonly IPen FillerHatch = new Pen(new SolidColorBrush(Color.Parse("#23272D")), 1);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#DCDFE4"));
    private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.Parse("#6B727C"));
    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#6B727C"));
    private static readonly IBrush NoticeBrush = new SolidColorBrush(Color.Parse("#E8A0A0"));
    private static readonly Color OursColor = Color.Parse("#2D7BF4");
    private static readonly Color TheirsColor = Color.Parse("#2FBF71");
    private static readonly Color BaseColor = Color.Parse("#E5A33B");
    private static readonly Color CustomColor = Color.Parse("#A371F7");
    private static readonly Color UnresolvedColor = Color.Parse("#E5392F");
    private static readonly IPen CurrentPen = new Pen(new SolidColorBrush(Color.Parse("#F5C26B")), 1);

    private readonly Typeface _mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
    private readonly Dictionary<int, (List<TextRange> Ours, List<TextRange> Theirs)?> _wordDiffs = [];
    private MergeScroll? _scroll;
    private MergeLayout? _layout;
    private double _charWidth = 7.5;

    static MergePaneView()
    {
        AffectsRender<MergePaneView>(PaneProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MergePaneView>(true);
        FocusableProperty.OverrideDefaultValue<MergePaneView>(true);
    }

    public MergePane Pane
    {
        get => GetValue(PaneProperty);
        set => SetValue(PaneProperty, value);
    }

    public MergeToolViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Double-click in the result: type over a block (the segment of the file under the pointer).</summary>
    public event Action<int>? SegmentEditRequested;

    public void AttachScroll(MergeScroll scroll)
    {
        _scroll = scroll;
        scroll.Changed += InvalidateVisual;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            if (change.GetOldValue<MergeToolViewModel?>() is { } old) old.PropertyChanged -= OnModelChanged;
            if (change.GetNewValue<MergeToolViewModel?>() is { } model) model.PropertyChanged += OnModelChanged;
            SetLayout(change.GetNewValue<MergeToolViewModel?>()?.Layout);
        }
        else if (change.Property == BoundsProperty)
        {
            _scroll?.SetViewport(this, IsEffectivelyVisible ? Bounds.Height : 0);
        }
        else if (change.Property == IsVisibleProperty)
        {
            _scroll?.SetViewport(this, change.GetNewValue<bool>() ? Bounds.Height : 0);
        }
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MergeToolViewModel.Layout)) SetLayout(Model?.Layout);
        else if (e.PropertyName == nameof(MergeToolViewModel.CurrentIndex)) InvalidateVisual();
    }

    private void SetLayout(MergeLayout? layout)
    {
        _layout = layout;
        _wordDiffs.Clear();
        InvalidateVisual();
    }

    private double Offset => _scroll?.Offset ?? 0;
    private double HOffset => Pane == MergePane.Result ? _scroll?.ResultHOffset ?? 0 : _scroll?.HOffset ?? 0;

    // ------------------------------------------------------------------ input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_scroll is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
            _scroll.ScrollSideways(Pane, HOffset - (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * _charWidth * 6);
        else
            _scroll.SetOffset(Offset - e.Delta.Y * LineHeight * 3);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_scroll is null || e.KeyModifiers != KeyModifiers.None) return;
        var page = Math.Max(LineHeight, _scroll.Viewport - LineHeight);
        double? target = e.Key switch
        {
            Key.Down => Offset + LineHeight,
            Key.Up => Offset - LineHeight,
            Key.PageDown => Offset + page,
            Key.PageUp => Offset - page,
            Key.Home => 0,
            Key.End => _scroll.MaxOffset,
            _ => null,
        };
        if (target is { } t)
        {
            _scroll.SetOffset(t);
            e.Handled = true;
        }
    }

    private int? RowAt(Point p)
    {
        if (_layout is null) return null;
        var row = (int)Math.Floor((p.Y + Offset) / LineHeight);
        return row >= 0 && row < _layout.Rows.Count ? row : null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var clickable = Pane != MergePane.Result && RowAt(e.GetPosition(this)) is { } row && _layout!.Rows[row].Conflict >= 0;
        Cursor = clickable ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || Model is not { } model || _layout is null || RowAt(point.Position) is not { } row) return;
        var r = _layout.Rows[row];

        if (Pane == MergePane.Result)
        {
            if (e.ClickCount == 2)
            {
                SegmentEditRequested?.Invoke(r.Segment);
                e.Handled = true;
            }
            else if (r.Conflict >= 0 && r.Conflict != model.CurrentIndex)
            {
                model.CurrentIndex = r.Conflict;
            }
            return;
        }

        if (r.Conflict < 0) return;
        var side = Pane switch { MergePane.Base => MergeSide.Base, MergePane.Ours => MergeSide.Ours, _ => MergeSide.Theirs };
        if (point.Position.X < GutterWidth)
        {
            // The gutter: the whole side.
            model.ToggleConflictSide(r.Conflict, side);
        }
        else if (r.Line(Pane) is var line and >= 0)
        {
            var first = LineIndexAtConflictStart(r.Conflict);
            model.ToggleConflictLine(r.Conflict, side, line - first);
        }
        else
        {
            model.CurrentIndex = r.Conflict;
        }
        e.Handled = true;
    }

    /// <summary>This pane's line index where a conflict's lines start (so a line can be numbered within the conflict).</summary>
    private int LineIndexAtConflictStart(int conflict)
    {
        var (first, count) = _layout!.ConflictRows[conflict];
        var min = int.MaxValue;
        for (var i = first; i < first + count; i++)
            if (_layout.Rows[i].Line(Pane) is var l and >= 0) min = Math.Min(min, l);
        return min == int.MaxValue ? 0 : min;
    }

    // ------------------------------------------------------------------ drawing

    public override void Render(DrawingContext ctx)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        ctx.FillRectangle(GutterBrush, new Rect(0, 0, GutterWidth, height));
        if (_layout is not { } layout || Model is not { } model) return;

        _charWidth = Text("M", TextBrush).WidthIncludingTrailingWhitespace;
        var lines = Pane == MergePane.Result ? null : layout.Lines(Pane);
        var first = Math.Max(0, (int)Math.Floor(Offset / LineHeight));
        var textLeft = GutterWidth + TextPad - HOffset;

        for (var row = first; row < layout.Rows.Count; row++)
        {
            var y = row * LineHeight - Offset;
            if (y >= height) break;
            var r = layout.Rows[row];
            var rowRect = new Rect(GutterWidth, y, width - GutterWidth, LineHeight);
            var line = r.Line(Pane);
            var isCurrent = r.Conflict >= 0 && r.Conflict == model.CurrentIndex;

            if (Pane == MergePane.Result)
            {
                DrawResultRow(ctx, layout, model, r, row, y, rowRect, textLeft, isCurrent);
            }
            else if (line < 0)
            {
                DrawFiller(ctx, rowRect);
            }
            else
            {
                var resolution = r.Conflict >= 0 ? model.Document!.Resolutions[r.Conflict] : null;
                var side = Pane switch { MergePane.Base => MergeSide.Base, MergePane.Ours => MergeSide.Ours, _ => MergeSide.Theirs };
                var picked = false;
                if (resolution is not null)
                {
                    var inConflict = line - LineIndexAtConflictStart(r.Conflict);
                    picked = !resolution.IsCustom && resolution.Has(side, inConflict);
                    var color = SideColor(side);
                    ctx.FillRectangle(new SolidColorBrush(color, picked ? 0.30 : isCurrent ? 0.17 : 0.10), rowRect);
                    ctx.FillRectangle(new SolidColorBrush(color, picked ? 0.9 : 0.25), new Rect(0, y, 4, LineHeight));
                    // With more than one side picked, the margin shows the order they go into the result.
                    if (picked) DrawPickMark(ctx, y, color, resolution.PickCount > 1 ? resolution.OrderOf(side).ToString(CultureInfo.InvariantCulture) : "✓");
                }
                DrawNumber(ctx, line + 1, y);
                using (ctx.PushClip(rowRect))
                {
                    // Lines of a resolved conflict that aren't in the result are drawn dim.
                    var dim = resolution is { IsResolved: true } && !picked;
                    DrawWordHighlights(ctx, layout, r, row, lines![line], new Point(textLeft, y));
                    ctx.DrawText(Text(Expand(lines[line]), dim ? DimTextBrush : TextBrush), new Point(textLeft, y + 2));
                }
            }

            if (r.Conflict >= 0) DrawConflictEdges(ctx, layout, r.Conflict, row, y, width, isCurrent);
        }
    }

    private void DrawResultRow(DrawingContext ctx, MergeLayout layout, MergeToolViewModel model, MergeRow r, int row, double y,
        Rect rowRect, double textLeft, bool isCurrent)
    {
        var resolution = r.Conflict >= 0 ? model.Document!.Resolutions[r.Conflict] : null;
        if (resolution is { IsResolved: false })
        {
            ctx.FillRectangle(new SolidColorBrush(UnresolvedColor, isCurrent ? 0.16 : 0.10), rowRect);
            ctx.FillRectangle(new SolidColorBrush(UnresolvedColor, 0.6), new Rect(0, y, 4, LineHeight));
            if (row == layout.ConflictRows[r.Conflict].FirstRow)
            {
                using (ctx.PushClip(rowRect))
                    ctx.DrawText(Text("Unresolved: click lines or a side above (Ctrl+1/2/3), or double-click here to type", NoticeBrush),
                        new Point(GutterWidth + TextPad, y + 2));
            }
            return;
        }
        if (r.Result < 0)
        {
            // A resolved block shorter than the others, or deliberately empty.
            DrawFiller(ctx, rowRect);
            if (resolution is { IsEmpty: true } && row == layout.ConflictRows[r.Conflict].FirstRow)
                using (ctx.PushClip(rowRect))
                    ctx.DrawText(Text("(nothing)", DimTextBrush), new Point(GutterWidth + TextPad, y + 2));
            return;
        }

        var line = layout.ResultLines[r.Result];
        if (SourceColor(line.Source) is { } color)
        {
            ctx.FillRectangle(new SolidColorBrush(color, isCurrent ? 0.22 : 0.14), rowRect);
            ctx.FillRectangle(new SolidColorBrush(color, 0.8), new Rect(0, y, 4, LineHeight));
        }
        DrawNumber(ctx, r.Result + 1, y);
        using (ctx.PushClip(rowRect))
            ctx.DrawText(Text(Expand(line.Text), TextBrush), new Point(textLeft, y + 2));
    }

    /// <summary>A line across the top and bottom of each conflict; the current one is drawn brighter.</summary>
    private static void DrawConflictEdges(DrawingContext ctx, MergeLayout layout, int conflict, int row, double y,
        double width, bool isCurrent)
    {
        var (first, count) = layout.ConflictRows[conflict];
        var pen = isCurrent ? CurrentPen : new Pen(new SolidColorBrush(Colors.White, 0.08), 1);
        if (row == first) ctx.DrawLine(pen, new Point(0, y + 0.5), new Point(width, y + 0.5));
        if (row == first + count - 1) ctx.DrawLine(pen, new Point(0, y + LineHeight - 0.5), new Point(width, y + LineHeight - 0.5));
    }

    private static void DrawFiller(DrawingContext ctx, Rect rect)
    {
        ctx.FillRectangle(FillerBrush, rect);
        using (ctx.PushClip(rect))
            for (var x = rect.X - rect.Height; x < rect.Right; x += 8)
                ctx.DrawLine(FillerHatch, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Y));
    }

    private void DrawPickMark(DrawingContext ctx, double y, Color color, string mark)
    {
        ctx.DrawText(Text(mark, new SolidColorBrush(color)), new Point(6, y + 2));
    }

    /// <summary>The words that differ between ours and theirs on the same row of a conflict.</summary>
    private void DrawWordHighlights(DrawingContext ctx, MergeLayout layout, MergeRow r, int row, string text, Point origin)
    {
        if (r.Conflict < 0 || Pane == MergePane.Base || r.Ours < 0 || r.Theirs < 0) return;
        if (!_wordDiffs.TryGetValue(row, out var diff))
        {
            var ours = layout.OursLines[r.Ours];
            var theirs = layout.TheirsLines[r.Theirs];
            diff = ours == theirs ? null : IntraLineDiff.Compare(ours, theirs);
            _wordDiffs[row] = diff;
        }
        if (diff is not { } d) return;
        var ranges = Pane == MergePane.Ours ? d.Ours : d.Theirs;
        if (ranges.Count == 0) return;
        var ft = Text(Expand(text), TextBrush);
        var brush = new SolidColorBrush(Pane == MergePane.Ours ? OursColor : TheirsColor, 0.45);
        foreach (var range in ranges)
        {
            var start = DisplayIndex(text, range.Start);
            var end = DisplayIndex(text, range.Start + range.Length);
            if (ft.BuildHighlightGeometry(new Point(origin.X, origin.Y), start, end - start) is { } box)
                ctx.DrawRectangle(brush, null, new Rect(box.Bounds.X, origin.Y, box.Bounds.Width, LineHeight), 2, 2);
        }
    }

    private static Color SideColor(MergeSide side) => side switch
    {
        MergeSide.Base => BaseColor,
        MergeSide.Ours => OursColor,
        _ => TheirsColor,
    };

    private static Color? SourceColor(ResultSource source) => source switch
    {
        ResultSource.Base => BaseColor,
        ResultSource.Ours => OursColor,
        ResultSource.Theirs => TheirsColor,
        ResultSource.Custom => CustomColor,
        _ => null,
    };

    private void DrawNumber(DrawingContext ctx, int number, double y)
    {
        var ft = Text(number.ToString(CultureInfo.InvariantCulture), NumberBrush);
        ctx.DrawText(ft, new Point(GutterWidth - 6 - ft.Width, y + 2));
    }

    private static string Expand(string text) => text.Replace("\t", "    ");

    private static int DisplayIndex(string text, int index)
    {
        var tabs = 0;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\t') tabs++;
        return index + tabs * 3;
    }

    private FormattedText Text(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _mono, FontSize, brush);
}

/// <summary>A thin map of the whole file beside the panes: where the conflicts are, and what's in view. Click to jump.</summary>
public sealed class MergeOverview : Control
{
    public static readonly StyledProperty<MergeToolViewModel?> ModelProperty =
        AvaloniaProperty.Register<MergeOverview, MergeToolViewModel?>(nameof(Model));

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#181A1E"));
    private static readonly IBrush UnresolvedBrush = new SolidColorBrush(Color.Parse("#E5392F"));
    private static readonly IBrush ResolvedBrush = new SolidColorBrush(Color.Parse("#5C636D"));
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.Parse("#F5C26B"));
    private static readonly IBrush ViewportBrush = new SolidColorBrush(Colors.White, 0.10);

    private MergeScroll? _scroll;

    public MergeToolViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public void AttachScroll(MergeScroll scroll)
    {
        _scroll = scroll;
        scroll.Changed += InvalidateVisual;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ModelProperty) return;
        if (change.GetOldValue<MergeToolViewModel?>() is { } old) old.PropertyChanged -= OnModelChanged;
        if (change.GetNewValue<MergeToolViewModel?>() is { } model) model.PropertyChanged += OnModelChanged;
        InvalidateVisual();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        if (Model is not { Layout: { } layout, Document: { } doc } model || layout.Rows.Count == 0) return;
        var scale = Bounds.Height / layout.Rows.Count;
        for (var c = 0; c < layout.ConflictRows.Count; c++)
        {
            var (first, count) = layout.ConflictRows[c];
            var brush = c == model.CurrentIndex ? CurrentBrush : doc.Resolutions[c].IsResolved ? ResolvedBrush : UnresolvedBrush;
            ctx.FillRectangle(brush, new Rect(2, first * scale, Bounds.Width - 4, Math.Max(3, count * scale)));
        }
        if (_scroll is not null && _scroll.Viewport > 0)
        {
            var top = _scroll.Offset / MergeScroll.LineHeight * scale;
            var height = _scroll.Viewport / MergeScroll.LineHeight * scale;
            ctx.FillRectangle(ViewportBrush, new Rect(0, top, Bounds.Width, Math.Max(4, height)));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Model is not { Layout: { } layout } model || _scroll is null || layout.Rows.Count == 0) return;
        var row = (int)(e.GetPosition(this).Y / Bounds.Height * layout.Rows.Count);
        // A click on (or near) a conflict makes it the current one; elsewhere it just scrolls there.
        var slack = Math.Max(1, (int)(4 / (Bounds.Height / layout.Rows.Count)));
        for (var c = 0; c < layout.ConflictRows.Count; c++)
        {
            var (first, count) = layout.ConflictRows[c];
            if (row >= first - slack && row < first + count + slack)
            {
                model.GoTo(c);
                e.Handled = true;
                return;
            }
        }
        _scroll.SetOffset(row * MergeScroll.LineHeight - _scroll.Viewport / 2);
        e.Handled = true;
    }
}
