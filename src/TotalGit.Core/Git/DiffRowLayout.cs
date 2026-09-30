namespace TotalGit.Core.Git;

/// <summary>
/// Vertical layout of a diff view: rows of a fixed height, some with extra space reserved below them
/// (for inline comment threads). Rows with extra space are few, so positions are the plain
/// <c>row * rowHeight</c> plus a prefix sum over just those rows.
/// </summary>
public sealed class DiffRowLayout
{
    private readonly Dictionary<int, double> _extras = [];
    // Rows that have extra space, sorted, and the total extra space above each of them.
    private int[] _rows = [];
    private double[] _before = [];
    private double _totalExtra;
    private bool _dirty;

    public DiffRowLayout(int rowCount, double rowHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowHeight);
        RowCount = rowCount;
        RowHeight = rowHeight;
    }

    public int RowCount { get; }
    public double RowHeight { get; }

    public double TotalHeight
    {
        get
        {
            Rebuild();
            return RowCount * RowHeight + _totalExtra;
        }
    }

    /// <summary>Sets the space below a row (zero or less removes it). Rows out of range are ignored.</summary>
    public void SetExtra(int row, double height)
    {
        if (row < 0 || row >= RowCount) return;
        if (height > 0)
        {
            if (_extras.TryGetValue(row, out var old) && old == height) return;
            _extras[row] = height;
        }
        else if (!_extras.Remove(row))
        {
            return;
        }
        _dirty = true;
    }

    /// <summary>Replaces all the extra space; returns whether anything changed.</summary>
    public bool SetExtras(IReadOnlyDictionary<int, double> extras)
    {
        var next = new Dictionary<int, double>();
        foreach (var (row, height) in extras)
            if (row >= 0 && row < RowCount && height > 0) next[row] = height;

        if (next.Count == _extras.Count && next.All(e => _extras.TryGetValue(e.Key, out var h) && h == e.Value))
            return false;
        _extras.Clear();
        foreach (var (row, height) in next) _extras[row] = height;
        _dirty = true;
        return true;
    }

    public double ExtraOf(int row) => _extras.TryGetValue(row, out var h) ? h : 0;

    /// <summary>The top of a row (a row at or past the end starts where the content ends).</summary>
    public double TopOf(int row)
    {
        Rebuild();
        if (row <= 0) return Math.Max(row, 0) * RowHeight;
        if (_rows.Length == 0) return row * RowHeight;
        // The extra space of every row above this one.
        var i = LowerBound(_rows, row);
        var extra = i < _rows.Length ? _before[i] : _totalExtra;
        return row * RowHeight + extra;
    }

    /// <summary>The top of the space reserved below a row.</summary>
    public double GapTopOf(int row) => TopOf(row) + RowHeight;

    /// <summary>
    /// The row at a vertical position, and whether the position is in that row's extra space.
    /// Positions above the first row clamp to it, and past the end to the last.
    /// </summary>
    public (int Row, bool InGap) RowAt(double y)
    {
        if (RowCount == 0 || y < 0) return (0, false);
        Rebuild();
        if (y >= TotalHeight) return (RowCount - 1, false);
        if (_rows.Length == 0) return (Math.Min((int)(y / RowHeight), RowCount - 1), false);

        // The last row whose top is at or above y.
        int lo = 0, hi = RowCount - 1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (TopOf(mid) <= y) lo = mid;
            else hi = mid - 1;
        }
        return (lo, y >= GapTopOf(lo));
    }

    private void Rebuild()
    {
        if (!_dirty) return;
        _dirty = false;
        _rows = [.. _extras.Keys.Order()];
        _before = new double[_rows.Length];
        var sum = 0.0;
        for (var i = 0; i < _rows.Length; i++)
        {
            _before[i] = sum;
            sum += _extras[_rows[i]];
        }
        _totalExtra = sum;
    }

    /// <summary>The index of the first element not less than a value.</summary>
    private static int LowerBound(int[] sorted, int value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

/// <summary>Which diff lines (indexes into the parsed lines) a side-by-side row shows; -1 for a filler.</summary>
public readonly record struct SplitRowLines(int Left, int Right);

public static class SplitRowIndex
{
    /// <summary>
    /// The line indexes of each row built by <see cref="SplitDiff.Build"/>. Each side's lines are in their
    /// original order, so one forward cursor per side finds them; the only lines that can be equal are
    /// "\ No newline" markers, and a marker always comes straight after the line before it on its side.
    /// </summary>
    public static SplitRowLines[] Build(IReadOnlyList<DiffLine> lines, IReadOnlyList<SplitRow> rows)
    {
        var result = new SplitRowLines[rows.Count];
        int left = 0, right = 0;

        int Find(DiffLine? line, ref int cursor)
        {
            if (line is not { } l) return -1;
            for (var i = cursor; i < lines.Count; i++)
            {
                if (lines[i] != l) continue;
                cursor = i + 1;
                return i;
            }
            return -1;
        }

        for (var r = 0; r < rows.Count; r++)
            result[r] = new SplitRowLines(Find(rows[r].Left, ref left), Find(rows[r].Right, ref right));
        return result;
    }
}
