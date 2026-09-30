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

    /// <summary>A run of commits picked with Shift+click (their SHAs), or null. Set by the view model.</summary>
    public static readonly StyledProperty<IReadOnlyCollection<string>?> SelectedRangeProperty =
        AvaloniaProperty.Register<CommitGraphView, IReadOnlyCollection<string>?>(nameof(SelectedRange), defaultBindingMode: BindingMode.TwoWay);

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
    private static readonly IBrush RangeBrush = new SolidColorBrush(Color.FromArgb(0x48, 0x3B, 0x82, 0xF6));
    private static readonly IBrush RangeAccentBrush = new SolidColorBrush(Color.Parse("#5AA9F2"));
    private static readonly IPen RangeNodePen = new Pen(new SolidColorBrush(Color.Parse("#E6E8EB")), 2);
    private static readonly IBrush FanBackgroundBrush = new SolidColorBrush(Color.Parse("#23272D"));
    private static readonly IBrush FanShadowBrush = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
    private static readonly IBrush FanHoverBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly IPen FanBorderPen = new Pen(new SolidColorBrush(Color.Parse("#4A505A")), 1);
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
    private static readonly Geometry BranchGlyph = Geometry.Parse(
        "M13,14C9.64,14 8.54,15.35 8.18,16.24C9.25,16.7 10,17.76 10,19A3,3 0 0,1 7,22A3,3 0 0,1 4,19C4,17.69 4.83,16.58 6,16.17V7.83C4.83,7.42 4,6.31 4,5A3,3 0 0,1 7,2A3,3 0 0,1 10,5C10,6.31 9.17,7.42 8,7.83V13.12C8.88,12.47 10.16,12 12,12C14.67,12 15.56,10.66 15.85,9.77C14.77,9.32 14,8.25 14,7A3,3 0 0,1 17,4A3,3 0 0,1 20,7C20,8.34 19.12,9.5 17.91,9.86C17.65,11.29 16.68,14 13,14M7,18A1,1 0 0,0 6,19A1,1 0 0,0 7,20A1,1 0 0,0 8,19A1,1 0 0,0 7,18M7,4A1,1 0 0,0 6,5A1,1 0 0,0 7,6A1,1 0 0,0 8,5A1,1 0 0,0 7,4M17,6A1,1 0 0,0 16,7A1,1 0 0,0 17,8A1,1 0 0,0 18,7A1,1 0 0,0 17,6Z");
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
    private readonly IBrush[] _stackBrushes = LanePalette.Select(c => (IBrush)new SolidColorBrush(c, 0.2)).ToArray();

    private static readonly IPen WipPen = new Pen(new SolidColorBrush(Color.Parse("#A0A7B0")), 1.5, new DashStyle([2, 2], 0));

    private AvatarCache? _avatarCache;
    private Dictionary<string, List<RefBadge>> _badgesBySha = [];
    private Dictionary<string, int> _rowBySha = [];
    private string? _headSha;
    private double _offset;
    private int _hoverRow = -1;
    private bool _tipSuppressed;

    // The row whose refs are fanned out (hovering a pill that stands for several refs), and the hovered ref in it.
    private int _fanRow = -1;
    private int _fanHover = -1;
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
        AffectsRender<CommitGraphView>(DataProperty, SelectedShaProperty, SelectedRangeProperty);
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

    public IReadOnlyCollection<string>? SelectedRange
    {
        get => GetValue(SelectedRangeProperty);
        set => SetValue(SelectedRangeProperty, value);
    }

    /// <summary>
    /// Shift+click (or Shift+Up/Down): select the commits from <c>anchor</c> (the selected commit) to <c>other</c>.
    /// The view model works out the run and sets <see cref="SelectedRange"/>.
    /// </summary>
    public event Action<string, string>? RangeSelectRequested;

    // The far end of the range, moved by Shift+Up/Down.
    private string? _rangeEnd;

    /// <summary>The selected commits were dragged onto a branch: rebase them onto it (the view model asks first).</summary>
    public event Action<string>? RangeDropped;

    // Dragging the selected commits: where the press started (on a selected row), whether it has become a drag,
    // and the row under the pointer.
    private Point? _rangePress;
    private int _rangePressRow = -1;
    private IPointer? _rangePointer;
    private bool _rangeDragging;
    private Point _rangeDragPoint;
    private int _dropRow = -1;
    private const double DragThreshold = 5;

    private static readonly IBrush DropBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xC3, 0x8A));
    private static readonly IPen DropPen = new Pen(new SolidColorBrush(Color.Parse("#4CC38A")), 2);
    private static readonly IPen NoDropPen = new Pen(new SolidColorBrush(Color.Parse("#E5484D")), 2, new DashStyle([3, 3], 0));
    private static readonly IBrush DragLabelBrush = new SolidColorBrush(Color.Parse("#2B2F36"));

    /// <summary>The branch to rebase onto when dropping on a row: its local branch, else a remote one; null for none.</summary>
    private string? DropBranch(int row)
    {
        if (row < 0 || row >= Rows.Count || InRange(Rows[row].Commit.Sha)) return null;
        if (!_badgesBySha.TryGetValue(Rows[row].Commit.Sha, out var badges)) return null;
        var branches = badges.Where(b => !b.IsTag).ToList();
        return (branches.FirstOrDefault(b => b.HasLocal) ?? branches.FirstOrDefault())?.Ref.Name;
    }

    private void EndRangeDrag(IPointer? pointer)
    {
        _rangePointer = null;
        _rangePress = null;
        _rangePressRow = -1;
        _rangeDragging = false;
        _dropRow = -1;
        pointer?.Capture(null);
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    private bool InRange(string sha) => SelectedRange?.Contains(sha) == true;

    /// <summary>Raised when the viewport nears the last loaded row, so more history can be loaded.</summary>
    public event Action? NearEnd;

    /// <summary>Raised on right-click with the commit under the pointer.</summary>
    public event Action<CommitInfo, Point>? CommitContextRequested;

    /// <summary>Raised on right-click on one ref in a fanned-out list of a commit's refs.</summary>
    public event Action<RefInfo>? RefContextRequested;

    /// <summary>Raised on double-click on one ref in a fanned-out list (check it out, as in the sidebar).</summary>
    public event Action<RefInfo>? RefActivated;

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
            _fanRow = _fanHover = -1;
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
        if (_rangePress is { } start)
        {
            if (!_rangeDragging && Math.Abs(p.Y - start.Y) + Math.Abs(p.X - start.X) < DragThreshold) return;
            _rangeDragging = true;
            _rangeDragPoint = p;
            // Scroll when dragging past the top or bottom edge, so any branch can be reached.
            if (p.Y < HeaderHeight + RowHeight / 2) SetOffset(_offset - RowHeight / 2);
            else if (p.Y > Bounds.Height - RowHeight / 2) SetOffset(_offset + RowHeight / 2);
            _dropRow = RowAt(Math.Clamp(p.Y, HeaderHeight, Bounds.Height - 1));
            Cursor = new Cursor(DropBranch(_dropRow) is null ? StandardCursorType.No : StandardCursorType.DragMove);
            InvalidateVisual();
            return;
        }
        Cursor = SplitterAt(p) != Splitter.None ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        UpdateHover(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_rangePress is not null)
        {
            var dragged = _rangeDragging;
            var target = dragged ? DropBranch(_dropRow) : null;
            var pressRow = _rangePressRow;
            EndRangeDrag(e.Pointer);
            if (target is not null) RangeDropped?.Invoke(target);
            else if (!dragged && pressRow >= 0 && pressRow < Rows.Count)
            {
                // A plain click on a selected commit: select just that one.
                SelectedRange = null;
                SelectedSha = Rows[pressRow].Commit.Sha;
            }
            return;
        }
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
        _fanRow = _fanHover = -1;
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
        // A click hides the node's tooltip (it would cover the context menu) until the pointer leaves the node.
        ToolTip.SetIsOpen(this, false);
        _tipSuppressed = true;

        // In an open fan, a click belongs to the ref under the pointer, not to the row underneath.
        if (Fan() is { } fan && fan.Panel.Contains(point.Position))
        {
            SelectedSha = Rows[_fanRow].Commit.Sha;
            if (FanIndexAt(fan, point.Position) is var k and >= 0)
            {
                var r = fan.Items[k].Badge.Ref;
                if (point.Properties.IsRightButtonPressed) RefContextRequested?.Invoke(r);
                else if (e.ClickCount == 2) RefActivated?.Invoke(r);
            }
            e.Handled = true;
            return;
        }

        var row = RowAt(point.Position.Y);
        if (row >= 0)
        {
            var sha = Rows[row].Commit.Sha;
            if (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && SelectedSha is { } anchor)
            {
                _rangeEnd = sha;
                RangeSelectRequested?.Invoke(anchor, sha);
            }
            else if (point.Properties.IsRightButtonPressed && InRange(sha))
            {
                CommitContextRequested?.Invoke(Rows[row].Commit, point.Position);
            }
            else if (point.Properties.IsLeftButtonPressed && InRange(sha))
            {
                _rangePress = point.Position;
                _rangePressRow = row;
                _rangePointer = e.Pointer;
                e.Pointer.Capture(this);
            }
            else
            {
                SelectedRange = null;
                SelectedSha = sha;
                if (point.Properties.IsRightButtonPressed) CommitContextRequested?.Invoke(Rows[row].Commit, point.Position);
            }
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Rows.Count == 0) return;

        if (e.Key == Key.Escape && _rangeDragging)
        {
            EndRangeDrag(_rangePointer);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SelectedRange is not null)
        {
            SelectedRange = null;
            e.Handled = true;
            return;
        }

        // Shift+Up/Down moves the far end of the range.
        if (e.KeyModifiers == KeyModifiers.Shift && e.Key is Key.Up or Key.Down && SelectedSha is { } anchor)
        {
            var end = _rangeEnd is not null && SelectedRange is not null && _rowBySha.TryGetValue(_rangeEnd, out var er) ? er
                : _rowBySha.TryGetValue(anchor, out var ar) ? ar : -1;
            if (end < 0) return;
            var next = Math.Clamp(end + (e.Key == Key.Down ? 1 : -1), 0, Rows.Count - 1);
            _rangeEnd = Rows[next].Commit.Sha;
            RangeSelectRequested?.Invoke(anchor, _rangeEnd);
            ScrollRowIntoView(next);
            e.Handled = true;
            return;
        }

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
        SelectedRange = null;
        SelectedSha = Rows[t].Commit.Sha;
        ScrollRowIntoView(t);
        e.Handled = true;
    }

    private void ScrollRowIntoView(int row)
    {
        var top = row * RowHeight;
        if (top < _offset) SetOffset(top);
        else if (top + RowHeight > _offset + BodyHeight) SetOffset(top + RowHeight - BodyHeight);
    }

    private int RowAt(double y)
    {
        if (y < HeaderHeight) return -1;
        var row = (int)((y - HeaderHeight + _offset) / RowHeight);
        return row >= 0 && row < Rows.Count ? row : -1;
    }

    private void UpdateHover(Point p)
    {
        // An open fan stays open while the pointer is inside it; it covers the rows (and nodes) beneath.
        if (Fan() is { } fan && fan.Panel.Contains(p))
        {
            var k = FanIndexAt(fan, p);
            if (k != _fanHover)
            {
                _fanHover = k;
                InvalidateVisual();
            }
            ToolTip.SetIsOpen(this, false);
            return;
        }
        var fanRow = FanTriggerRow(p);
        if (fanRow != _fanRow)
        {
            _fanRow = fanRow;
            _fanHover = fanRow >= 0 ? 0 : -1;
            InvalidateVisual();
        }

        var row = RowAt(p.Y);
        if (row != _hoverRow)
        {
            _hoverRow = row;
            InvalidateVisual();
        }

        // Tooltip only when the pointer is over the commit's node.
        var overNode = row >= 0 && Math.Abs(p.X - LaneX(Rows[row].Lane)) <= NodeRadius
                                && Math.Abs(p.Y - (RowTop(row) + RowHeight / 2)) <= NodeRadius;
        if (!overNode) _tipSuppressed = false;
        if (overNode && !_tipSuppressed)
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
                    for (var i = first; i <= last; i++)
                    {
                        if (InRange(rows[i].Commit.Sha))
                            ctx.DrawEllipse(null, RangeNodePen, new Point(LaneX(rows[i].Lane), RowTop(i) + RowHeight / 2), NodeRadius + 2, NodeRadius + 2);
                    }
                }
                for (var i = first; i <= last; i++) DrawRowText(ctx, i, width);
                using (ctx.PushClip(new Rect(0, HeaderHeight, RefColumnWidth, BodyHeight)))
                {
                    for (var i = first; i <= last; i++) DrawBadges(ctx, i);
                }
                // Over everything else, and free to extend past the ref column.
                DrawFan(ctx);
                DrawRangeDrag(ctx, width);
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

        if (SelectedRange is { Count: > 0 } range)
        {
            var label = Text(range.Count == 1 ? "1 commit selected · Esc to clear" : $"{range.Count} commits selected · Esc to clear", 10, Brushes.White, _boldTypeface);
            var pill = new Rect(width - label.Width - 30, 4, label.Width + 16, HeaderHeight - 8);
            ctx.DrawRectangle(RangeAccentBrush, null, new RoundedRect(pill, pill.Height / 2));
            ctx.DrawText(label, new Point(pill.X + 8, pill.Y + (pill.Height - label.Height) / 2));
        }
    }

    private void DrawRowBackground(DrawingContext ctx, int i, double width)
    {
        var row = Rows[i];
        var top = RowTop(i);
        var color = row.ColorIndex;
        var sha = row.Commit.Sha;

        if (i == _hoverRow) ctx.FillRectangle(HoverBrush, new Rect(0, top, width, RowHeight));
        var inRange = InRange(sha);
        if (inRange)
        {
            ctx.FillRectangle(RangeBrush, new Rect(0, top, width, RowHeight));
            ctx.FillRectangle(RangeAccentBrush, new Rect(0, top, 4, RowHeight));
            ctx.FillRectangle(RangeAccentBrush, new Rect(MessageLeft - 8, top + 3, 3, RowHeight - 6));
        }

        var strong = sha == SelectedSha || sha == _headSha || inRange;
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

    private void DrawRangeDrag(DrawingContext ctx, double width)
    {
        if (!_rangeDragging || SelectedRange is not { Count: > 0 } range) return;
        var target = DropBranch(_dropRow);
        if (_dropRow >= 0 && _dropRow < Rows.Count && !InRange(Rows[_dropRow].Commit.Sha))
        {
            var rect = new Rect(1, RowTop(_dropRow) + 1, width - 2, RowHeight - 2);
            if (target is not null) ctx.FillRectangle(DropBrush, rect);
            ctx.DrawRectangle(null, target is null ? NoDropPen : DropPen, rect, 4, 4);
        }

        var what = range.Count == 1 ? "1 commit" : $"{range.Count} commits";
        var text = target is not null ? $"Rebase {what} onto {target}" : $"Drop {what} on a branch to rebase onto it";
        var label = Text(text, 12, target is null ? MutedTextBrush : PrimaryTextBrush, _boldTypeface);
        var box = new Rect(_rangeDragPoint.X + 14, _rangeDragPoint.Y + 10, label.Width + 20, label.Height + 10);
        if (box.Right > width - 4) box = box.WithX(Math.Max(4, width - 4 - box.Width));
        ctx.DrawRectangle(DragLabelBrush, target is null ? NoDropPen : DropPen, box, 6, 6);
        ctx.DrawText(label, new Point(box.X + 10, box.Y + 5));
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
            var info = Data?.Wip.GetValueOrDefault(c.Sha);
            var count = info?.Count ?? 0;
            var files = $"{count} {(count == 1 ? "file" : "files")}";
            // Another worktree's row names the worktree (kept short: the message column can be narrow).
            var label = info?.WorktreeName is { } name ? $"// WIP {name} · {files}" : $"// WIP    {files} changed";
            var wip = Text(label, 13, MutedTextBrush, _typeface, messageWidth);
            ctx.DrawText(wip, new Point(MessageLeft, top + (RowHeight - wip.Height) / 2));
            return;
        }

        var msg = Text(c.MessageShort, 13, PrimaryTextBrush, _typeface, messageWidth);
        ctx.DrawText(msg, new Point(MessageLeft, top + (RowHeight - msg.Height) / 2));

        if (!ShowMetaColumns) return;
        var author = Text(c.AuthorName, 12, MutedTextBrush, _typeface, AuthorColumnWidth - 12);
        ctx.DrawText(author, new Point(AuthorLeft, top + (RowHeight - author.Height) / 2));
        var date = Text(DateText.Relative(c.AuthorDate), 12, MutedTextBrush, _typeface, DateColumnWidth - 12);
        ctx.DrawText(date, new Point(DateLeft, top + (RowHeight - date.Height) / 2));
    }

    private const double PillHeight = 20;
    private const double PillIconSize = 12;
    private const double PillGap = 4;
    private const double KindIconSize = 14;
    private const double BadgeCircle = 22; // icon circle at the left end of a ref label
    private const double PillLeft = 4;
    private const double StackOffset = 3; // shift of each card drawn behind a pill that stands for several refs
    private const double ChipHeight = 16;

    /// <summary>One pill's icons, (possibly trimmed) name and total width.</summary>
    private sealed record PillLayout(List<Geometry> Icons, FormattedText Name, double Width);

    private PillLayout LayoutPill(RefBadge badge, double maxWidth)
    {
        var icons = new List<Geometry>();
        if (badge.HasLocal) icons.Add(LaptopIcon);
        if (badge.HasRemote) icons.Add(Data?.GitHubRepo is not null ? GitHubIcon : CloudIcon);
        if (badge.HasWorktree) icons.Add(WorktreeIcon);
        // Circle + gap, optional check, name, trailing icons, then padding round the rounded right end.
        var leading = BadgeCircle + 5 + (badge.IsCurrent ? PillIconSize + PillGap : 0);
        var trailing = icons.Count * (PillIconSize + PillGap) + 6;

        var name = Text(badge.Name, 12, Brushes.White, badge.IsCurrent ? _boldTypeface : _typeface,
            Math.Max(10, maxWidth - leading - trailing));
        return new PillLayout(icons, name, Math.Max(0, Math.Min(maxWidth, leading + name.Width + trailing)));
    }

    private static int StackCards(List<RefBadge> badges) => Math.Min(2, badges.Count - 1);

    private static double ChipWidth(FormattedText count) => count.Width + 10;

    /// <summary>Room taken after the pill by the stacked cards and the "+N" chip.</summary>
    private static double StackExtra(List<RefBadge> badges, FormattedText? count) =>
        count is null ? 0 : StackCards(badges) * StackOffset + 5 + ChipWidth(count);

    /// <summary>A row's first pill, trimmed so it, its stack and its "+N" chip fit the column.</summary>
    private (PillLayout Pill, FormattedText? Count) LayoutRowBadges(List<RefBadge> badges)
    {
        var count = badges.Count > 1 ? Text($"+{badges.Count - 1}", 11, Brushes.White, _boldTypeface) : null;
        return (LayoutPill(badges[0], RefColumnWidth - 12 - StackExtra(badges, count)), count);
    }

    /// <summary>Where a row's pill (with its stack and "+N") ends, so the connector can start there.</summary>
    private double BadgeRight(List<RefBadge> badges)
    {
        var (pill, count) = LayoutRowBadges(badges);
        return PillLeft + pill.Width + StackExtra(badges, count) + 4;
    }

    private void DrawBadges(DrawingContext ctx, int i)
    {
        var row = Rows[i];
        if (!_badgesBySha.TryGetValue(row.Commit.Sha, out var badges)) return;
        var (pill, count) = LayoutRowBadges(badges);
        var cy = RowTop(i) + RowHeight / 2;
        var ci = row.ColorIndex;

        if (count is not null)
        {
            // Cards peeking out behind the pill: this commit has more refs than the one shown.
            for (var k = StackCards(badges); k >= 1; k--)
            {
                var card = new Rect(PillLeft + BadgeCircle / 2 + k * StackOffset, cy - PillHeight / 2 - k * StackOffset,
                    Math.Max(0, pill.Width - BadgeCircle / 2), PillHeight);
                ctx.DrawRectangle(_stackBrushes[ci], _connectorPens[ci], new RoundedRect(card, PillHeight / 2));
            }
        }

        DrawPill(ctx, badges[0], pill, PillLeft, cy, ci);

        if (count is not null)
        {
            var chip = new Rect(PillLeft + pill.Width + StackCards(badges) * StackOffset + 5, cy - ChipHeight / 2, ChipWidth(count), ChipHeight);
            ctx.DrawRectangle(_laneBrushes[ci], null, new RoundedRect(chip, ChipHeight / 2));
            ctx.DrawText(count, new Point(chip.X + 5, cy - count.Height / 2));
        }
    }

    /// <summary>Draws one ref pill with its left edge at <paramref name="left"/>, centred on <paramref name="cy"/>.</summary>
    private void DrawPill(DrawingContext ctx, RefBadge badge, PillLayout layout, double left, double cy, int colorIndex)
    {
        const double iconSize = PillIconSize;
        var top = cy - PillHeight / 2;

        // The label box starts under the circle (so its left edge is hidden and square) and has a rounded right end.
        var box = new Rect(left + BadgeCircle / 2, top, Math.Max(0, layout.Width - BadgeCircle / 2), PillHeight);
        ctx.DrawRectangle(badge.IsCurrent ? _laneBrushes[colorIndex] : _pillBrushes[colorIndex], null,
            new RoundedRect(box, new CornerRadius(0, PillHeight / 2, PillHeight / 2, 0)));

        // Black circle with a lane-coloured ring holding the branch-kind icon.
        var center = new Point(left + BadgeCircle / 2, cy);
        ctx.DrawEllipse(Brushes.Black, _lanePens[colorIndex], center, BadgeCircle / 2 - 1, BadgeCircle / 2 - 1);
        if (badge.KindIcon is { } kindIcon)
            ctx.DrawImage(kindIcon, new Rect(center.X - KindIconSize / 2, center.Y - KindIconSize / 2, KindIconSize, KindIconSize));
        else
            DrawIcon(ctx, badge.IsTag ? TagIcon : BranchGlyph, center.X - 6, center.Y - 6, 12);

        var x = left + BadgeCircle + 5;
        if (badge.IsCurrent)
        {
            DrawIcon(ctx, CheckIcon, x, cy - iconSize / 2, iconSize);
            x += iconSize + PillGap;
        }
        ctx.DrawText(layout.Name, new Point(x, cy - layout.Name.Height / 2));
        x += layout.Name.Width + PillGap;
        foreach (var icon in layout.Icons)
        {
            DrawIcon(ctx, icon, x, cy - iconSize / 2, iconSize);
            x += iconSize + PillGap;
        }
    }

    // ---------------------------------------------------------------- ref fan-out

    /// <summary>The open fan: its panel and each ref's pill, one per line.</summary>
    private sealed record FanLayout(Rect Panel, List<(RefBadge Badge, PillLayout Pill, double CenterY)> Items);

    /// <summary>
    /// Layout of the fan for <see cref="_fanRow"/>: every ref on the commit, untrimmed, one row apart, starting at
    /// the row itself and going down (or up, when there's no room below). Drawn over the rows it covers.
    /// </summary>
    private FanLayout? Fan()
    {
        if (_fanRow < 0 || _fanRow >= Rows.Count
            || !_badgesBySha.TryGetValue(Rows[_fanRow].Commit.Sha, out var badges) || badges.Count < 2)
            return null;

        var maxWidth = Math.Max(120, Bounds.Width - PillLeft - 24);
        var cy = RowTop(_fanRow) + RowHeight / 2;
        var down = cy + (badges.Count - 1) * RowHeight + RowHeight / 2 <= HeaderHeight + BodyHeight;
        var items = badges
            .Select((b, k) => (b, LayoutPill(b, maxWidth), down ? cy + k * RowHeight : cy - k * RowHeight))
            .ToList();

        var top = items.Min(it => it.Item3) - RowHeight / 2;
        var bottom = items.Max(it => it.Item3) + RowHeight / 2;
        // At least as wide as the row's pill with its stack and "+N", so none of it peeks out from under the panel.
        var width = Math.Max(PillLeft + items.Max(it => it.Item2.Width) + 8, Math.Min(BadgeRight(badges), RefColumnWidth) + 2);
        return new FanLayout(new Rect(0, top, width, bottom - top), items);
    }

    /// <summary>The fan item under <paramref name="p"/>, or -1.</summary>
    private static int FanIndexAt(FanLayout fan, Point p)
    {
        if (!fan.Panel.Contains(p)) return -1;
        return fan.Items.FindIndex(it => Math.Abs(p.Y - it.CenterY) <= RowHeight / 2);
    }

    /// <summary>The row whose pill (or its stack and "+N") is under <paramref name="p"/>, when it has several refs.</summary>
    private int FanTriggerRow(Point p)
    {
        var row = RowAt(p.Y);
        if (row < 0 || !_badgesBySha.TryGetValue(Rows[row].Commit.Sha, out var badges) || badges.Count < 2) return -1;
        return p.X >= 0 && p.X <= Math.Min(BadgeRight(badges), RefColumnWidth) ? row : -1;
    }

    private void DrawFan(DrawingContext ctx)
    {
        if (Fan() is not { } fan) return;
        var ci = Rows[_fanRow].ColorIndex;

        var panel = new RoundedRect(fan.Panel, 8);
        ctx.DrawRectangle(FanShadowBrush, null, new RoundedRect(fan.Panel.Translate(new Vector(2, 3)), 8));
        ctx.DrawRectangle(FanBackgroundBrush, FanBorderPen, panel);
        for (var k = 0; k < fan.Items.Count; k++)
        {
            var (badge, pill, cy) = fan.Items[k];
            if (k == _fanHover)
                ctx.DrawRectangle(FanHoverBrush, null, new RoundedRect(new Rect(2, cy - RowHeight / 2 + 2, fan.Panel.Width - 4, RowHeight - 4), 6));
            DrawPill(ctx, badge, pill, PillLeft, cy, ci);
        }
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

    /// <summary>One pill in the branch/tag column; local and remote branches with the same name share a pill.</summary>
    private sealed record RefBadge(string Name, bool IsCurrent, bool HasLocal, bool HasRemote, bool IsTag, bool HasWorktree = false)
    {
        /// <summary>Icon replacing a feature/, bug/ or hot-fix/ prefix; Name is then shown without it.</summary>
        public Bitmap? KindIcon { get; init; }

        /// <summary>The ref this pill stands for (the local branch when it also has a remote one).</summary>
        public required RefInfo Ref { get; init; }

        private static RefBadge Branch(RefInfo r, string fullName, bool isCurrent, bool hasLocal, bool hasRemote, bool hasWorktree = false)
        {
            var (kind, shortName) = BranchCategory.Classify(fullName);
            return new RefBadge(shortName, isCurrent, hasLocal, hasRemote, false, hasWorktree) { KindIcon = BranchIcons.For(kind), Ref = r };
        }

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
                    badges.Add(Branch(local, local.Name, local.IsCurrent, true, match is not null, inOtherWorktree));
                }
                badges.AddRange(remotes.Select(r => Branch(r, ShortRemoteName(r.Name), false, false, true)));
                badges.AddRange(group.Where(r => r.Kind == RefKind.Tag).Select(r => new RefBadge(r.Name, false, false, false, true) { Ref = r }));

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
