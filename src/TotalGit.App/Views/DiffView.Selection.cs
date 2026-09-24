using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using TotalGit.App.ViewModels;
using TotalGit.Core.Git;

namespace TotalGit.App.Views;

// Text selection and copy. A selection stays on one side of a side-by-side diff, so it copies one version of
// the code; hunk headers, fillers and the +/- signs and line numbers are never part of it.
public sealed partial class DiffView
{
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#3B82F6"), 0.38);

    /// <summary>A place in the text: a row of the view and a character offset in that line's (raw) text.</summary>
    private readonly record struct TextPos(int Row, int Col)
    {
        public static bool operator <(TextPos a, TextPos b) => a.Row < b.Row || (a.Row == b.Row && a.Col < b.Col);
        public static bool operator >(TextPos a, TextPos b) => b < a;
    }

    private TextPos? _anchor;
    private TextPos? _caret;
    private int _selectionSide; // 0 = inline or left, 1 = right
    private bool _selecting;

    public DiffView()
    {
        Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    public bool HasSelection => _anchor is { } a && _caret is { } c && a != c;

    /// <summary>The selected text, without diff signs or line numbers (null when nothing is selected).</summary>
    public string? SelectedText
    {
        get
        {
            if (Ordered() is not var (start, end)) return null;
            var lines = new List<string>();
            for (var row = start.Row; row <= end.Row; row++)
            {
                if (RowText(row, _selectionSide) is not { } text) continue;
                if (row == end.Row && row > start.Row && end.Col == 0) continue;
                var from = row == start.Row ? Math.Min(start.Col, text.Length) : 0;
                var to = row == end.Row ? Math.Min(end.Col, text.Length) : text.Length;
                lines.Add(text[from..Math.Max(from, to)]);
            }
            var result = string.Join(Environment.NewLine, lines);
            // A selection that ends at the start of a line (e.g. whole lines) includes the last line break.
            return end.Col == 0 && end.Row > start.Row ? result + Environment.NewLine : result;
        }
    }

    public async Task CopySelectionAsync()
    {
        if (SelectedText is { Length: > 0 } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>Selects everything on the side last clicked (the new version when nothing was clicked yet).</summary>
    public void SelectAll()
    {
        if (Diff is null || RowCount == 0) return;
        if (_anchor is null) _selectionSide = Mode == DiffViewMode.Split ? 1 : 0;
        _anchor = new TextPos(0, 0);
        var last = RowCount - 1;
        _caret = new TextPos(last, RowText(last, _selectionSide)?.Length ?? 0);
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _anchor = _caret = null;
        _selecting = false;
        InvalidateVisual();
    }

    private (TextPos Start, TextPos End)? Ordered() =>
        _anchor is { } a && _caret is { } c && a != c ? (a < c ? (a, c) : (c, a)) : null;

    /// <summary>The text of a code line on a side, or null for hunk headers, fillers and markers.</summary>
    private string? RowText(int row, int side)
    {
        if (Diff is not { } diff || row < 0) return null;
        DiffLine? line;
        if (Mode == DiffViewMode.Split)
        {
            if (row >= _splitRows.Count || _splitRows[row].IsHunk) return null;
            line = side == 0 ? _splitRows[row].Left : _splitRows[row].Right;
        }
        else
        {
            line = row < diff.Lines.Count ? diff.Lines[row] : null;
        }
        return line is { Kind: DiffLineKind.Context or DiffLineKind.Added or DiffLineKind.Removed } l ? l.Text : null;
    }

    private double Half => Math.Floor(Bounds.Width / 2);

    /// <summary>Where a side's text starts (the gutter and sign column are to its left).</summary>
    private double GutterRight(int side) => Mode == DiffViewMode.Split ? (side == 0 ? 0 : Half + 1) + GutterWidth + 20 : GutterWidth * 2 + 20;

    private int SideAt(double x) => Mode == DiffViewMode.Split && x > Half ? 1 : 0;

    private int RowAt(double y) => Math.Clamp((int)((y + _offset) / LineHeight), 0, Math.Max(0, RowCount - 1));

    private TextPos HitTest(Point p, int side)
    {
        var row = RowAt(p.Y);
        var text = RowText(row, side) ?? "";
        var column = (int)Math.Round((p.X - GutterRight(side) + _hOffset) / _charWidth);
        return new TextPos(row, RawIndex(text, Math.Max(0, column)));
    }

    /// <summary>The raw offset for a display column (tabs are drawn four wide).</summary>
    private static int RawIndex(string text, int column)
    {
        var width = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var w = text[i] == '\t' ? 4 : 1;
            // A click in the first half of a tab lands before it.
            if (column <= width + (w - 1) / 2) return i;
            width += w;
        }
        return text.Length;
    }

    /// <summary>Left button: start a selection (double-click a word, triple-click or a line number the line).</summary>
    private bool OnSelectionPressed(PointerPressedEventArgs e)
    {
        if (Diff is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        var p = e.GetPosition(this);
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _anchor is not null;
        if (!extend) _selectionSide = SideAt(p.X);

        var pos = HitTest(p, _selectionSide);
        var inGutter = p.X < GutterRight(_selectionSide) - 20;
        if (extend)
        {
            _caret = pos;
        }
        else if (e.ClickCount >= 3 || inGutter)
        {
            _anchor = new TextPos(pos.Row, 0);
            _caret = new TextPos(pos.Row + 1, 0);
        }
        else if (e.ClickCount == 2 && RowText(pos.Row, _selectionSide) is { } text)
        {
            var (from, to) = WordAt(text, pos.Col);
            _anchor = new TextPos(pos.Row, from);
            _caret = new TextPos(pos.Row, to);
        }
        else
        {
            _anchor = _caret = pos;
        }

        _selecting = e.ClickCount == 1 && !inGutter;
        if (_selecting) e.Pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    /// <summary>The word, run of spaces, or single punctuation character at a position.</summary>
    private static (int From, int To) WordAt(string text, int col)
    {
        if (text.Length == 0) return (0, 0);
        col = Math.Clamp(col, 0, text.Length - 1);
        static int Kind(char c) => char.IsLetterOrDigit(c) || c == '_' ? 1 : char.IsWhiteSpace(c) ? 0 : 2;
        var kind = Kind(text[col]);
        if (kind == 2) return (col, col + 1);
        int from = col, to = col + 1;
        while (from > 0 && Kind(text[from - 1]) == kind) from--;
        while (to < text.Length && Kind(text[to]) == kind) to++;
        return (from, to);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        var p = e.GetPosition(this);
        // Dragging past the top or bottom scrolls.
        if (p.Y < 0) SetOffset(_offset - LineHeight);
        else if (p.Y > Bounds.Height) SetOffset(_offset + LineHeight);
        _caret = HitTest(new Point(p.X, Math.Clamp(p.Y, 0, Math.Max(0, Bounds.Height - 1))), _selectionSide);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
    }

    /// <summary>Ctrl+C, Ctrl+A, and Escape to clear a selection (otherwise Escape closes the diff).</summary>
    private bool OnSelectionKey(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.C when ctrl:
            case Key.Insert when ctrl:
                _ = CopySelectionAsync();
                return true;
            case Key.A when ctrl:
                SelectAll();
                return true;
            case Key.Escape when HasSelection:
                ClearSelection();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Paints the selected part of a line (drawn behind the text).</summary>
    private void DrawSelection(DrawingContext ctx, DiffLine line, int row, int side, Point origin)
    {
        if (side != _selectionSide || Ordered() is not var (start, end) || row < start.Row || row > end.Row) return;
        if (line.Kind is not (DiffLineKind.Context or DiffLineKind.Added or DiffLineKind.Removed)) return;
        if (row == end.Row && row > start.Row && end.Col == 0) return;

        var text = line.Text;
        var from = row == start.Row ? Math.Min(start.Col, text.Length) : 0;
        var to = row == end.Row ? Math.Min(end.Col, text.Length) : text.Length;
        var x = origin.X + DisplayIndex(text, from) * _charWidth;
        var width = (DisplayIndex(text, to) - DisplayIndex(text, from)) * _charWidth;
        // The line break is selected too when the selection carries on to the next line.
        if (row < end.Row) width += _charWidth * 0.6;
        if (width > 0) ctx.FillRectangle(SelectionBrush, new Rect(x, origin.Y - 2, width, LineHeight));
    }
}
