using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;
using TotalGit.Core.Avatars;
using TotalGit.Core.Git;
using TotalGit.Core.Graph;

namespace TotalGit.App.Views;

/// <summary>
/// GitKraken-style commit graph: ref pills, coloured lanes, avatar nodes and tinted row bands.
/// Draws only the rows inside the viewport, so it stays fast on large histories.
/// </summary>
public sealed class CommitGraphView : Control
{
    public static readonly StyledProperty<GraphData?> DataProperty =
        AvaloniaProperty.Register<CommitGraphView, GraphData?>(nameof(Data));

    public static readonly StyledProperty<string?> SelectedShaProperty =
        AvaloniaProperty.Register<CommitGraphView, string?>(nameof(SelectedSha), defaultBindingMode: BindingMode.TwoWay);

    private const double HeaderHeight = 26;
    private const double RowHeight = 28;
    private const double LaneWidth = 28;
    private const double GraphPadding = 10;
    private const double NodeRadius = 13; // fills the 26px row band
    private const double MergeDotRadius = 6;
    private const double SplitterGrab = 4;
    private const double MinColumnWidth = 50;
    private const double CornerRadius = 8;

    private static readonly Color[] LanePalette =
    [
        Color.Parse("#1FB6DC"), // cyan
        Color.Parse("#2D7BF4"), // blue
        Color.Parse("#9B3FE0"), // purple
        Color.Parse("#D02DC4"), // magenta
        Color.Parse("#E8246F"), // pink
        Color.Parse("#E5392F"), // red
        Color.Parse("#F07F1F"), // orange
        Color.Parse("#F2C618"), // yellow
        Color.Parse("#A3D82C"), // lime
        Color.Parse("#2FBF71"), // green
    ];

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#1C1F24"));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#23272D"));
    private static readonly IBrush HeaderTextBrush = new SolidColorBrush(Color.Parse("#7D848E"));
    private static readonly IBrush PrimaryTextBrush = new SolidColorBrush(Color.Parse("#E6E8EB"));
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#8A9099"));
    private static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    private static readonly IPen SeparatorPen = new Pen(new SolidColorBrush(Color.Parse("#30353C")), 1);

    private static readonly Geometry CheckIcon = Geometry.Parse("M21,7L9,19L3.5,13.5L4.91,12.09L9,16.17L19.59,5.59L21,7Z");
    private static readonly Geometry LaptopIcon = Geometry.Parse(
        "M4,6H20V16H4M20,18A2,2 0 0,0 22,16V6C22,4.89 21.1,4 20,4H4C2.89,4 2,4.89 2,6V16A2,2 0 0,0 4,18H0V20H24V18H20Z");
    private static readonly Geometry GitHubIcon = Geometry.Parse(
        "M12,2A10,10 0 0,0 2,12C2,16.42 4.87,20.17 8.84,21.5C9.34,21.58 9.5,21.27 9.5,21C9.5,20.77 9.5,20.14 9.5,19.31C6.73,19.91 6.14,17.97 6.14,17.97C5.68,16.81 5.03,16.5 5.03,16.5C4.12,15.88 5.1,15.9 5.1,15.9C6.1,15.97 6.63,16.93 6.63,16.93C7.5,18.45 8.97,18 9.54,17.76C9.63,17.11 9.89,16.67 10.17,16.42C7.95,16.17 5.62,15.31 5.62,11.5C5.62,10.39 6,9.5 6.65,8.79C6.55,8.54 6.2,7.5 6.75,6.15C6.75,6.15 7.59,5.88 9.5,7.17C10.29,6.95 11.15,6.84 12,6.84C12.85,6.84 13.71,6.95 14.5,7.17C16.41,5.88 17.25,6.15 17.25,6.15C17.8,7.5 17.45,8.54 17.35,8.79C18,9.5 18.38,10.39 18.38,11.5C18.38,15.32 16.04,16.16 13.81,16.41C14.17,16.72 14.5,17.33 14.5,18.26C14.5,19.6 14.5,20.68 14.5,21C14.5,21.27 14.66,21.58 15.17,21.5C19.14,20.16 22,16.42 22,12A10,10 0 0,0 12,2Z");
    private static readonly Geometry CloudIcon = Geometry.Parse(
        "M19.35,10.04C18.67,6.59 15.64,4 12,4C9.11,4 6.6,5.64 5.35,8.04C2.34,8.36 0,10.91 0,14A6,6 0 0,0 6,20H19A5,5 0 0,0 24,15C24,12.36 21.95,10.22 19.35,10.04Z");
    private static readonly Geometry PencilIcon = Geometry.Parse(
        "M20.71,7.04C21.1,6.65 21.1,6 20.71,5.63L18.37,3.29C18,2.9 17.35,2.9 16.96,3.29L15.12,5.12L18.87,8.87M3,17.25V21H6.75L17.81,9.93L14.06,6.18L3,17.25Z");
    private static readonly Geometry WorktreeIcon = Geometry.Parse(
        "M3,3H9V7H3V3M15,10H21V14H15V10M15,17H21V21H15V17M13,13H7V18H13V20H7L5,20V9H7V11H13V13Z");
    private static readonly Geometry TagIcon = Geometry.Parse(
        "M5.5,7A1.5,1.5 0 0,1 4,5.5A1.5,1.5 0 0,1 5.5,4A1.5,1.5 0 0,1 7,5.5A1.5,1.5 0 0,1 5.5,7M21.41,11.58L12.41,2.58C12.05,2.22 11.55,2 11,2H4C2.89,2 2,2.89 2,4V11C2,11.55 2.22,12.05 2.59,12.41L11.58,21.41C11.95,21.77 12.45,22 13,22C13.55,22 14.05,21.77 14.41,21.41L21.41,14.41C21.78,14.05 22,13.55 22,13C22,12.45 21.77,11.94 21.41,11.58Z");

    private readonly Typeface _typeface = new("Inter");
    private readonly Typeface _boldTypeface = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private readonly Pen[] _lanePens = LanePalette.Select(c => new Pen(new SolidColorBrush(c), 2, lineCap: PenLineCap.Round)).ToArray();
    private readonly Pen[] _connectorPens = LanePalette.Select(c => new Pen(new SolidColorBrush(c, 0.6), 1)).ToArray();
    private readonly IBrush[] _laneBrushes = LanePalette.Select(c => (IBrush)new SolidColorBrush(c)).ToArray();
    private readonly IBrush[] _bandBrushes = LanePalette.Select(c => (IBrush)new SolidColorBrush(c, 0.13)).ToArray();
    private readonly IBrush[] _strongBandBrushes = LanePalette.Select(c => (IBrush)new SolidColorBrush(c, 0.55)).ToArray();
    private readonly IBrush[] _pillBrushes = LanePalette.Select(c => (IBrush)new SolidColorBrush(c, 0.35)).ToArray();

    private static readonly IPen WipPen = new Pen(new SolidColorBrush(Color.Parse("#A0A7B0")), 1.5, new DashStyle([2, 2], 0));

    private AvatarCache? _avatarCache;
    private Dictionary<string, List<RefBadge>> _badgesBySha = [];
    private Dictionary<string, int> _rowBySha = [];
    private string? _headSha;
    private double _offset;
    private int _hoverRow = -1;
    private ScrollBar? _scrollBar;
    private bool _syncingScrollBar;

    // Column widths; a null graph width means "fit the lanes".
    private double _refWidth = 170;
    private double? _graphWidth;
    private double _authorWidth = 160;
    private double _dateWidth = 140;
    private Splitter _drag;
    private double _dragStartX;
    private GraphColumns _dragStart;

    private enum Splitter { None, Ref, Graph, Author, Date }

    static CommitGraphView()
    {
        AffectsRender<CommitGraphView>(DataProperty, SelectedShaProperty);
        FocusableProperty.OverrideDefaultValue<CommitGraphView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CommitGraphView>(true);
    }

    public GraphData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public string? SelectedSha
    {
        get => GetValue(SelectedShaProperty);
        set => SetValue(SelectedShaProperty, value);
    }

    /// <summary>Raised when the viewport nears the last loaded row, so more history can be loaded.</summary>
    public event Action? NearEnd;

    /// <summary>Raised on right-click with the commit under the pointer.</summary>
    public event Action<CommitInfo, Point>? CommitContextRequested;

    /// <summary>Raised when the user finishes resizing a column.</summary>
    public event Action? ColumnsChanged;

    /// <summary>Current column widths (graph null = sized to the lanes), for saving and restoring.</summary>
    public GraphColumns Columns
    {
        get => new(_refWidth, _graphWidth, _authorWidth, _dateWidth);
        set
        {
            _refWidth = Math.Max(MinColumnWidth, value.Ref);
            _graphWidth = value.Graph is { } g ? Math.Max(MinColumnWidth, g) : null;
            _authorWidth = Math.Max(MinColumnWidth, value.Author);
            _dateWidth = Math.Max(MinColumnWidth, value.Date);
            InvalidateVisual();
        }
    }

    private IReadOnlyList<GraphRow> Rows => Data?.Layout.Rows ?? [];
    private double BodyHeight => Math.Max(0, Bounds.Height - HeaderHeight);
    private double MaxOffset => Math.Max(0, Rows.Count * RowHeight - BodyHeight);
    private double GraphColumnWidth => _graphWidth ?? Math.Clamp((Data?.Layout.LaneCount ?? 1) * LaneWidth + GraphPadding * 2, 80, 420);
    private double RefColumnWidth => _refWidth;
    private double AuthorColumnWidth => _authorWidth;
    private double DateColumnWidth => _dateWidth;
    private double GraphLeft => RefColumnWidth;
    private double MessageLeft => GraphLeft + GraphColumnWidth + 10;
    private bool ShowMetaColumns => Bounds.Width - MessageLeft - AuthorColumnWidth - DateColumnWidth > 120;
    private double AuthorLeft => Bounds.Width - DateColumnWidth - AuthorColumnWidth;
    private double DateLeft => Bounds.Width - DateColumnWidth;

    public void AttachScrollBar(ScrollBar scrollBar)
    {
        _scrollBar = scrollBar;
        scrollBar.SmallChange = RowHeight;
        scrollBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncingScrollBar)
                SetOffset(scrollBar.Value);
        };
        SyncScrollBar();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty)
        {
            var old = change.GetOldValue<GraphData?>();
            var data = change.GetNewValue<GraphData?>();
            if (!ReferenceEquals(_avatarCache, data?.Avatars))
            {
                if (_avatarCache is not null) _avatarCache.Updated -= InvalidateVisual;
                _avatarCache = data?.Avatars;
                if (_avatarCache is not null) _avatarCache.Updated += InvalidateVisual;
            }

            RebuildIndexes();
            _hoverRow = -1;
            // Refreshes of the same worktree keep the scroll position; switching repos goes to the top.
            var sameView = old is not null && data is not null && old.CurrentWorktreePath == data.CurrentWorktreePath;
            if (!sameView && SelectedSha is not null && !_rowBySha.ContainsKey(SelectedSha)) SelectedSha = null;
            SetOffset(sameView ? _offset : 0);
        }
        else if (change.Property == BoundsProperty)
        {
            SetOffset(_offset);
        }
    }

    private void RebuildIndexes()
    {
        var data = Data;
        _rowBySha = [];
        _badgesBySha = [];
        _headSha = null;
        if (data is null) return;

        for (var i = 0; i < data.Layout.Rows.Count; i++) _rowBySha[data.Layout.Rows[i].Commit.Sha] = i;
        _badgesBySha = RefBadge.Build(data);
        _headSha = data.Refs.FirstOrDefault(r => r.IsCurrent)?.TargetSha;
    }

    private void SetOffset(double offset)
    {
        _offset = Math.Clamp(offset, 0, MaxOffset);
        SyncScrollBar();
        InvalidateVisual();

        var lastVisible = (_offset + BodyHeight) / RowHeight;
        if (Rows.Count > 0 && lastVisible > Rows.Count - 200) NearEnd?.Invoke();
    }

    /// <summary>Scrolls so the given commit is visible (centred when it was off-screen).</summary>
    public void ScrollToSha(string sha)
    {
        if (!_rowBySha.TryGetValue(sha, out var row)) return;
        var top = row * RowHeight;
        if (top < _offset || top + RowHeight > _offset + BodyHeight)
            SetOffset(top - BodyHeight / 2 + RowHeight / 2);
    }

    private void SyncScrollBar()
    {
        if (_scrollBar is null) return;
        _syncingScrollBar = true;
        _scrollBar.Maximum = MaxOffset;
        _scrollBar.ViewportSize = BodyHeight;
        _scrollBar.LargeChange = Math.Max(RowHeight, BodyHeight - RowHeight);
        _scrollBar.Value = _offset;
        _scrollBar.IsVisible = MaxOffset > 0;
        _syncingScrollBar = false;
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        SetOffset(_offset - e.Delta.Y * RowHeight * 3);
        UpdateHover(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag != Splitter.None)
        {
            DragSplitter(p.X - _dragStartX);
            return;
        }
        Cursor = SplitterAt(p) != Splitter.None ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        UpdateHover(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Splitter.None) return;
        _drag = Splitter.None;
        e.Pointer.Capture(null);
        ColumnsChanged?.Invoke();
    }

    /// <summary>The column edge under the pointer; only the header row is a resize handle.</summary>
    private Splitter SplitterAt(Point p)
    {
        if (p.Y < 0 || p.Y >= HeaderHeight) return Splitter.None;
        if (Math.Abs(p.X - GraphLeft) <= SplitterGrab) return Splitter.Ref;
        if (Math.Abs(p.X - (MessageLeft - 10)) <= SplitterGrab) return Splitter.Graph;
        if (ShowMetaColumns)
        {
            if (Math.Abs(p.X - (AuthorLeft - 8)) <= SplitterGrab) return Splitter.Author;
            if (Math.Abs(p.X - (DateLeft - 8)) <= SplitterGrab) return Splitter.Date;
        }
        return Splitter.None;
    }

    private void DragSplitter(double dx)
    {
        var start = _dragStart;
        switch (_drag)
        {
            case Splitter.Ref:
                _refWidth = Math.Clamp(start.Ref + dx, MinColumnWidth, 600);
                break;
            case Splitter.Graph:
                _graphWidth = Math.Clamp(start.Graph!.Value + dx, MinColumnWidth, 1200);
                break;
            case Splitter.Author:
                _authorWidth = Math.Clamp(start.Author - dx, MinColumnWidth, 600);
                break;
            case Splitter.Date:
                // Moves only the author/date edge; the message/author edge stays put.
                var d = Math.Clamp(dx, MinColumnWidth - start.Author, start.Date - MinColumnWidth);
                _authorWidth = start.Author + d;
                _dateWidth = start.Date - d;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverRow = -1;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        var splitter = SplitterAt(point.Position);
        if (splitter != Splitter.None && point.Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2 && splitter == Splitter.Graph)
            {
                // Double-click the graph edge to fit it to the lanes again.
                _graphWidth = null;
                InvalidateVisual();
                ColumnsChanged?.Invoke();
            }
            else
            {
                _drag = splitter;
                _dragStartX = point.Position.X;
                _dragStart = new GraphColumns(_refWidth, GraphColumnWidth, _authorWidth, _dateWidth);
                e.Pointer.Capture(this);
            }
            e.Handled = true;
            return;
        }
        var row = RowAt(point.Position.Y);
        if (row >= 0)
        {
            SelectedSha = Rows[row].Commit.Sha;
            if (point.Properties.IsRightButtonPressed) CommitContextRequested?.Invoke(Rows[row].Commit, point.Position);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Rows.Count == 0) return;

        var current = SelectedSha is not null && _rowBySha.TryGetValue(SelectedSha, out var r) ? r : -1;
        var page = Math.Max(1, (int)(BodyHeight / RowHeight) - 1);
        int? target = e.Key switch
        {
            Key.Down => current + 1,
            Key.Up => current <= 0 ? 0 : current - 1,
            Key.PageDown => current + page,
            Key.PageUp => current - page,
            Key.Home => 0,
            Key.End => Rows.Count - 1,
            _ => null,
        };
        if (target is not { } t) return;

        t = Math.Clamp(t, 0, Rows.Count - 1);
        SelectedSha = Rows[t].Commit.Sha;
        var top = t * RowHeight;
        if (top < _offset) SetOffset(top);
        else if (top + RowHeight > _offset + BodyHeight) SetOffset(top + RowHeight - BodyHeight);
        e.Handled = true;
    }

    private int RowAt(double y)
    {
        if (y < HeaderHeight) return -1;
        var row = (int)((y - HeaderHeight + _offset) / RowHeight);
        return row >= 0 && row < Rows.Count ? row : -1;
    }

    private void UpdateHover(Point p)
    {
        var row = RowAt(p.Y);
        if (row != _hoverRow)
        {
            _hoverRow = row;
            InvalidateVisual();
        }

        // Tooltip only when the pointer is over the commit's node.
        var overNode = row >= 0 && Math.Abs(p.X - LaneX(Rows[row].Lane)) <= NodeRadius
                                && Math.Abs(p.Y - (RowTop(row) + RowHeight / 2)) <= NodeRadius;
        if (overNode)
        {
            var c = Rows[row].Commit;
            var tip = $"{c.Sha}\n{c.AuthorName} <{c.AuthorEmail}>\n{c.AuthorDate.LocalDateTime:f}\n\n{c.MessageShort}";
            if (!Equals(ToolTip.GetTip(this), tip))
            {
                ToolTip.SetIsOpen(this, false);
                ToolTip.SetTip(this, tip);
            }
            ToolTip.SetIsOpen(this, true);
        }
        else
        {
            ToolTip.SetIsOpen(this, false);
        }
    }

    // ---------------------------------------------------------------- rendering

    private double LaneX(int lane) => GraphLeft + GraphPadding + lane * LaneWidth + LaneWidth / 2;
    private double RowTop(int row) => HeaderHeight + row * RowHeight - _offset;

    public override void Render(DrawingContext ctx)
    {
        var width = Bounds.Width;
        ctx.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        var rows = Rows;
        if (rows.Count > 0)
        {
            var first = Math.Max(0, (int)(_offset / RowHeight));
            var last = Math.Min(rows.Count - 1, (int)((_offset + BodyHeight) / RowHeight));

            using (ctx.PushClip(new Rect(0, HeaderHeight, width, BodyHeight)))
            {
                for (var i = first; i <= last; i++) DrawRowBackground(ctx, i, width);
                for (var i = first; i <= last; i++) DrawConnector(ctx, i);
                using (ctx.PushClip(new Rect(GraphLeft, HeaderHeight, GraphColumnWidth, BodyHeight)))
                {
                    for (var i = first; i <= last; i++) DrawSegments(ctx, i);
                    for (var i = first; i <= last; i++) DrawNode(ctx, i);
                }
                for (var i = first; i <= last; i++) DrawRowText(ctx, i, width);
                using (ctx.PushClip(new Rect(0, HeaderHeight, RefColumnWidth, BodyHeight)))
                {
                    for (var i = first; i <= last; i++) DrawBadges(ctx, i);
                }
            }
        }

        DrawHeader(ctx, width);
    }

    private void DrawHeader(DrawingContext ctx, double width)
    {
        ctx.FillRectangle(HeaderBrush, new Rect(0, 0, width, HeaderHeight));
        ctx.DrawLine(SeparatorPen, new Point(0, HeaderHeight - 0.5), new Point(width, HeaderHeight - 0.5));

        void Title(string text, double x)
        {
            var ft = Text(text, 10, HeaderTextBrush, _boldTypeface);
            ctx.DrawText(ft, new Point(x, (HeaderHeight - ft.Height) / 2));
        }

        Title("BRANCH / TAG", 10);
        ctx.DrawLine(SeparatorPen, new Point(GraphLeft, 4), new Point(GraphLeft, HeaderHeight - 4));
        Title("GRAPH", GraphLeft + 8);
        ctx.DrawLine(SeparatorPen, new Point(MessageLeft - 10, 4), new Point(MessageLeft - 10, HeaderHeight - 4));
        Title("COMMIT MESSAGE", MessageLeft);
        if (ShowMetaColumns)
        {
            ctx.DrawLine(SeparatorPen, new Point(AuthorLeft - 8, 4), new Point(AuthorLeft - 8, HeaderHeight - 4));
            Title("AUTHOR", AuthorLeft);
            ctx.DrawLine(SeparatorPen, new Point(DateLeft - 8, 4), new Point(DateLeft - 8, HeaderHeight - 4));
            Title("DATE", DateLeft);
        }
    }

    private void DrawRowBackground(DrawingContext ctx, int i, double width)
    {
        var row = Rows[i];
        var top = RowTop(i);
        var color = row.ColorIndex;
        var sha = row.Commit.Sha;

        if (i == _hoverRow) ctx.FillRectangle(HoverBrush, new Rect(0, top, width, RowHeight));

        var strong = sha == SelectedSha || sha == _headSha;
        // The band starts with a rounded end centred on the node, so it wraps around the circle.
        var bandTop = top + 1;
        var bandHeight = RowHeight - 2;
        var nodeX = LaneX(row.Lane);
        var band = new StreamGeometry();
        using (var g = band.Open())
        {
            g.BeginFigure(new Point(nodeX, bandTop), true);
            g.LineTo(new Point(width, bandTop));
            g.LineTo(new Point(width, bandTop + bandHeight));
            g.LineTo(new Point(nodeX, bandTop + bandHeight));
            g.ArcTo(new Point(nodeX, bandTop), new Size(bandHeight / 2, bandHeight / 2), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(strong ? _strongBandBrushes[color] : _bandBrushes[color], null, band);

        // Right-edge accent stripe, as in GitKraken.
        ctx.FillRectangle(_laneBrushes[color], new Rect(width - 3, top + 1, 3, RowHeight - 2));
    }

    private void DrawConnector(DrawingContext ctx, int i)
    {
        var row = Rows[i];
        if (!_badgesBySha.TryGetValue(row.Commit.Sha, out var badges)) return;
        var y = RowTop(i) + RowHeight / 2;
        // Start after the pill so the line doesn't show through its translucent fill.
        var start = Math.Min(BadgeRight(badges), RefColumnWidth);
        var end = LaneX(row.Lane);
        if (end > start) ctx.DrawLine(_connectorPens[row.ColorIndex], new Point(start, y), new Point(end, y));
    }

    private void DrawSegments(DrawingContext ctx, int i)
    {
        var top = RowTop(i);
        foreach (var s in Rows[i].Segments)
        {
            var pen = _lanePens[s.ColorIndex];
            var x1 = LaneX(s.FromLane);
            var x2 = LaneX(s.ToLane);
            var y1 = AnchorY(top, s.From);
            var y2 = AnchorY(top, s.To);

            if (s.FromLane == s.ToLane)
            {
                ctx.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
                continue;
            }

            var dir = Math.Sign(x2 - x1);
            var r = Math.Min(CornerRadius, Math.Min(Math.Abs(x2 - x1), Math.Abs(y2 - y1)));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(x1, y1), false);
                if (s.From == RowAnchor.Top)
                {
                    // Come down the source lane, then turn across into the node.
                    g.LineTo(new Point(x1, y2 - r));
                    g.QuadraticBezierTo(new Point(x1, y2), new Point(x1 + dir * r, y2));
                    g.LineTo(new Point(x2, y2));
                }
                else
                {
                    // Leave the node sideways, then turn down into the target lane.
                    g.LineTo(new Point(x2 - dir * r, y1));
                    g.QuadraticBezierTo(new Point(x2, y1), new Point(x2, y1 + r));
                    g.LineTo(new Point(x2, y2));
                }
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geometry);
        }
    }

    private static double AnchorY(double top, RowAnchor anchor) => anchor switch
    {
        RowAnchor.Top => top,
        RowAnchor.Middle => top + RowHeight / 2,
        _ => top + RowHeight,
    };

    private void DrawNode(DrawingContext ctx, int i)
    {
        var row = Rows[i];
        var center = new Point(LaneX(row.Lane), RowTop(i) + RowHeight / 2);
        var brush = _laneBrushes[row.ColorIndex];

        if (row.Commit.IsWorkingTree)
        {
            ctx.DrawEllipse(BackgroundBrush, WipPen, center, NodeRadius - 1, NodeRadius - 1);
            DrawIcon(ctx, PencilIcon, center.X - 6, center.Y - 6, 12, MutedTextBrush);
            return;
        }

        if (row.Commit.IsMerge)
        {
            ctx.DrawEllipse(brush, null, center, MergeDotRadius, MergeDotRadius);
            return;
        }

        ctx.DrawEllipse(brush, null, center, NodeRadius, NodeRadius);
        var inner = NodeRadius - 2;
        var rect = new Rect(center.X - inner, center.Y - inner, inner * 2, inner * 2);

        if (_avatarCache?.TryGet(row.Commit.AuthorEmail, Data?.GitHubRepo, row.Commit.Sha) is { } bitmap)
        {
            // Black behind the avatar so transparent logos don't pick up the lane colour.
            ctx.DrawEllipse(Brushes.Black, null, center, inner, inner);
            using (ctx.PushGeometryClip(new EllipseGeometry(rect)))
                ctx.DrawImage(bitmap, rect);
        }
        else
        {
            var hue = AvatarIdentity.Hue(row.Commit.AuthorEmail);
            ctx.DrawEllipse(new SolidColorBrush(HslColor.FromHsl(hue, 0.45, 0.42).ToRgb()), null, center, inner, inner);
            var ft = Text(AvatarIdentity.Initials(row.Commit.AuthorName), 9.5, Brushes.White, _boldTypeface);
            ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
        }
    }

    private void DrawRowText(DrawingContext ctx, int i, double width)
    {
        var c = Rows[i].Commit;
        var top = RowTop(i);
        var messageWidth = Math.Max(0, (ShowMetaColumns ? AuthorLeft : width) - MessageLeft - 12);

        if (c.IsWorkingTree)
        {
            var count = Data?.WipCount ?? 0;
            var wip = Text($"// WIP    {count} {(count == 1 ? "file" : "files")} changed", 13, MutedTextBrush, _typeface, messageWidth);
            ctx.DrawText(wip, new Point(MessageLeft, top + (RowHeight - wip.Height) / 2));
            return;
        }

        var msg = Text(c.MessageShort, 13, PrimaryTextBrush, _typeface, messageWidth);
        ctx.DrawText(msg, new Point(MessageLeft, top + (RowHeight - msg.Height) / 2));

        if (!ShowMetaColumns) return;
        var author = Text(c.AuthorName, 12, MutedTextBrush, _typeface, AuthorColumnWidth - 12);
        ctx.DrawText(author, new Point(AuthorLeft, top + (RowHeight - author.Height) / 2));
        var date = Text(FormatDate(c.AuthorDate), 12, MutedTextBrush, _typeface, DateColumnWidth - 12);
        ctx.DrawText(date, new Point(DateLeft, top + (RowHeight - date.Height) / 2));
    }

    private const double PillHeight = 20;
    private const double PillIconSize = 12;
    private const double PillGap = 4;

    /// <summary>Lays out a row's first ref pill (at y = 0): its icons, trimmed name, rectangle and "+N".</summary>
    private (List<Geometry> Icons, FormattedText Name, Rect Pill, FormattedText? More) LayoutBadges(List<RefBadge> badges)
    {
        var badge = badges[0];
        var more = badges.Count > 1 ? Text($"+{badges.Count - 1}", 11, MutedTextBrush, _typeface) : null;
        var maxWidth = RefColumnWidth - 12 - (more is not null ? more.Width + 8 : 0);

        var icons = new List<Geometry>();
        if (badge.IsTag) icons.Add(TagIcon);
        if (badge.HasLocal) icons.Add(LaptopIcon);
        if (badge.HasRemote) icons.Add(Data?.GitHubRepo is not null ? GitHubIcon : CloudIcon);
        if (badge.HasWorktree) icons.Add(WorktreeIcon);
        var leading = badge.IsCurrent ? PillIconSize + PillGap : 0;
        var trailing = icons.Count * (PillIconSize + PillGap);

        var name = Text(badge.Name, 12, Brushes.White, badge.IsCurrent ? _boldTypeface : _typeface,
            Math.Max(10, maxWidth - 12 - leading - trailing));
        var pillWidth = Math.Max(0, Math.Min(maxWidth, 12 + leading + name.Width + trailing));
        return (icons, name, new Rect(6, 0, pillWidth, PillHeight), more);
    }

    /// <summary>Where a row's pill (and its "+N") ends, so the connector can start there.</summary>
    private double BadgeRight(List<RefBadge> badges)
    {
        var (_, _, pill, more) = LayoutBadges(badges);
        return pill.Right + (more is not null ? 4 + more.Width : 0) + 4;
    }

    private void DrawBadges(DrawingContext ctx, int i)
    {
        var row = Rows[i];
        if (!_badgesBySha.TryGetValue(row.Commit.Sha, out var badges)) return;

        const double pillHeight = PillHeight;
        const double iconSize = PillIconSize;
        const double gap = PillGap;
        var badge = badges[0];
        var top = RowTop(i) + (RowHeight - pillHeight) / 2;
        var (icons, name, layoutPill, moreText) = LayoutBadges(badges);
        var pill = layoutPill.WithY(top);

        ctx.DrawRectangle(badge.IsCurrent ? _laneBrushes[row.ColorIndex] : _pillBrushes[row.ColorIndex],
            null, pill, 3, 3);

        var x = pill.X + 6;
        if (badge.IsCurrent)
        {
            DrawIcon(ctx, CheckIcon, x, top + (pillHeight - iconSize) / 2, iconSize);
            x += iconSize + gap;
        }
        ctx.DrawText(name, new Point(x, top + (pillHeight - name.Height) / 2));
        x += name.Width + gap;
        foreach (var icon in icons)
        {
            DrawIcon(ctx, icon, x, top + (pillHeight - iconSize) / 2, iconSize);
            x += iconSize + gap;
        }

        if (moreText is not null)
            ctx.DrawText(moreText, new Point(pill.Right + 4, top + (pillHeight - moreText.Height) / 2));
    }

    private static void DrawIcon(DrawingContext ctx, Geometry icon, double x, double y, double size, IBrush? brush = null)
    {
        var scale = size / 24;
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y)))
            ctx.DrawGeometry(brush ?? Brushes.White, null, icon);
    }

    private static FormattedText Text(string text, double size, IBrush brush, Typeface typeface, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush);
        if (!double.IsPositiveInfinity(maxWidth))
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    private static string FormatDate(DateTimeOffset when)
    {
        var age = DateTimeOffset.Now - when;
        return age.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)age.TotalMinutes} min ago",
            < 60 * 24 => $"{(int)age.TotalHours} hours ago",
            < 60 * 24 * 7 => $"{(int)age.TotalDays} days ago",
            _ => when.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>One pill in the branch/tag column; local and remote branches with the same name share a pill.</summary>
    private sealed record RefBadge(string Name, bool IsCurrent, bool HasLocal, bool HasRemote, bool IsTag, bool HasWorktree = false)
    {
        public static Dictionary<string, List<RefBadge>> Build(GraphData data)
        {
            var refs = data.Refs;
            var result = new Dictionary<string, List<RefBadge>>();
            foreach (var group in refs.GroupBy(r => r.TargetSha))
            {
                var badges = new List<RefBadge>();
                var remotes = group.Where(r => r.Kind == RefKind.RemoteBranch).ToList();

                foreach (var local in group.Where(r => r.Kind is RefKind.LocalBranch or RefKind.DetachedHead))
                {
                    var match = remotes.FirstOrDefault(r => ShortRemoteName(r.Name) == local.Name);
                    if (match is not null) remotes.Remove(match);
                    // Worktree icon: the branch is checked out in a worktree other than the one being viewed.
                    var inOtherWorktree = data.WorktreesByBranch.TryGetValue(local.Name, out var wt)
                        && !string.Equals(wt.Path, data.CurrentWorktreePath, StringComparison.OrdinalIgnoreCase);
                    badges.Add(new RefBadge(local.Name, local.IsCurrent, true, match is not null, false, inOtherWorktree));
                }
                badges.AddRange(remotes.Select(r => new RefBadge(ShortRemoteName(r.Name), false, false, true, false)));
                badges.AddRange(group.Where(r => r.Kind == RefKind.Tag).Select(r => new RefBadge(r.Name, false, false, false, true)));

                result[group.Key] = badges
                    .OrderByDescending(b => b.IsCurrent)
                    .ThenByDescending(b => b.HasLocal)
                    .ThenByDescending(b => b.HasRemote)
                    .ToList();
            }
            return result;
        }

        private static string ShortRemoteName(string name)
        {
            var slash = name.IndexOf('/');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }
    }
}

/// <summary>Saved commit-graph column widths; a null graph width means sized to the lanes.</summary>
public readonly record struct GraphColumns(double Ref, double? Graph, double Author, double Date);
