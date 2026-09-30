namespace TotalGit.Core.Git;

public enum MergePane
{
    Base,
    Ours,
    Theirs,
    Result,
}

/// <summary>
/// One row of the merge tool: the line each pane shows on it (an index into that pane's lines, or -1 for a
/// filler), and the segment and conflict it belongs to.
/// </summary>
/// <param name="Conflict">Index of the conflict, or -1 on shared text.</param>
public readonly record struct MergeRow(int Segment, int Conflict, int Base, int Ours, int Theirs, int Result)
{
    public int Line(MergePane pane) => pane switch
    {
        MergePane.Base => Base,
        MergePane.Ours => Ours,
        MergePane.Theirs => Theirs,
        _ => Result,
    };
}

/// <summary>
/// Lays every pane of the merge tool out on one grid of rows, so line N of the file is on the same row in each
/// and they can scroll together. Shared text is one row per line; a conflict is as tall as its tallest pane,
/// with the others padded, and inside it matching lines of ours and theirs are put side by side.
/// </summary>
public sealed class MergeLayout
{
    // Beyond this (lines of ours × lines of theirs) a conflict isn't aligned inside, just stacked.
    private const long MaxAlignCells = 4_000_000;

    private MergeLayout(
        List<string> baseLines, List<string> ours, List<string> theirs, List<ResultLine> result,
        List<MergeRow> rows, List<(int, int)> conflictRows, List<(int, int)> segmentRows)
    {
        BaseLines = baseLines;
        OursLines = ours;
        TheirsLines = theirs;
        ResultLines = result;
        Rows = rows;
        ConflictRows = conflictRows;
        SegmentRows = segmentRows;
    }

    /// <summary>The whole common ancestor (only meaningful when the document has a base).</summary>
    public IReadOnlyList<string> BaseLines { get; }
    public IReadOnlyList<string> OursLines { get; }
    public IReadOnlyList<string> TheirsLines { get; }
    public IReadOnlyList<ResultLine> ResultLines { get; }
    public IReadOnlyList<MergeRow> Rows { get; }

    /// <summary>For each conflict, its first row and number of rows.</summary>
    public IReadOnlyList<(int FirstRow, int RowCount)> ConflictRows { get; }

    /// <summary>For each segment of the file, its first row and number of rows.</summary>
    public IReadOnlyList<(int FirstRow, int RowCount)> SegmentRows { get; }

    public IReadOnlyList<string> Lines(MergePane pane) => pane switch
    {
        MergePane.Base => BaseLines,
        MergePane.Ours => OursLines,
        MergePane.Theirs => TheirsLines,
        _ => ResultLines.Select(l => l.Text).ToList(),
    };

    public static MergeLayout Build(MergeDocument doc)
    {
        var baseLines = new List<string>();
        var ours = new List<string>();
        var theirs = new List<string>();
        var result = new List<ResultLine>();
        var rows = new List<MergeRow>();
        var conflictRows = new List<(int, int)>();
        var segmentRows = new List<(int, int)>();
        var file = doc.File;
        var conflict = 0;

        for (var s = 0; s < file.Segments.Count; s++)
        {
            var first = rows.Count;
            if (file.Segments[s] is CommonSegment common)
            {
                var edit = doc.CommonEdit(s);
                var shown = edit ?? common.Lines;
                var height = Math.Max(common.Lines.Count, shown.Count);
                for (var i = 0; i < height; i++)
                {
                    var has = i < common.Lines.Count;
                    var r = i < shown.Count ? result.Count + i : -1;
                    rows.Add(new MergeRow(s, -1,
                        has ? baseLines.Count + i : -1, has ? ours.Count + i : -1, has ? theirs.Count + i : -1, r));
                }
                baseLines.AddRange(common.Lines);
                ours.AddRange(common.Lines);
                theirs.AddRange(common.Lines);
                result.AddRange(shown.Select(l => new ResultLine(l, edit is null ? ResultSource.Common : ResultSource.Custom)));
            }
            else
            {
                var hunk = (ConflictHunk)file.Segments[s];
                var resolution = doc.Resolutions[conflict];
                var block = LayOutConflict(hunk, resolution);
                foreach (var (b, o, t, r) in block)
                {
                    rows.Add(new MergeRow(s, conflict,
                        b >= 0 ? baseLines.Count + b : -1,
                        o >= 0 ? ours.Count + o : -1,
                        t >= 0 ? theirs.Count + t : -1,
                        r >= 0 ? result.Count + r : -1));
                }
                baseLines.AddRange(hunk.Base ?? []);
                ours.AddRange(hunk.Ours);
                theirs.AddRange(hunk.Theirs);
                result.AddRange(resolution.IsResolved ? resolution.Lines(hunk) : []);
                conflictRows.Add((first, rows.Count - first));
                conflict++;
            }
            segmentRows.Add((first, rows.Count - first));
        }
        return new MergeLayout(baseLines, ours, theirs, result, rows, conflictRows, segmentRows);
    }

    /// <summary>
    /// The rows of one conflict as indexes within it: ours and theirs aligned on matching lines, base from the
    /// top, and each result line level with the line it came from where the order allows.
    /// </summary>
    internal static List<(int Base, int Ours, int Theirs, int Result)> LayOutConflict(ConflictHunk hunk, ConflictResolution resolution)
    {
        var pairs = Align(hunk.Ours, hunk.Theirs);
        var rowOfOurs = new int[hunk.Ours.Count];
        var rowOfTheirs = new int[hunk.Theirs.Count];
        for (var row = 0; row < pairs.Count; row++)
        {
            if (pairs[row].Ours >= 0) rowOfOurs[pairs[row].Ours] = row;
            if (pairs[row].Theirs >= 0) rowOfTheirs[pairs[row].Theirs] = row;
        }

        var resultLines = resolution.IsResolved ? resolution.Lines(hunk) : [];
        var resultRow = new int[resultLines.Count];
        var next = 0;
        for (var i = 0; i < resultLines.Count; i++)
        {
            var line = resultLines[i];
            var wanted = line.Source switch
            {
                ResultSource.Ours when line.SourceLine < rowOfOurs.Length => rowOfOurs[line.SourceLine],
                ResultSource.Theirs when line.SourceLine < rowOfTheirs.Length => rowOfTheirs[line.SourceLine],
                ResultSource.Base => line.SourceLine,
                _ => next,
            };
            resultRow[i] = Math.Max(wanted, next);
            next = resultRow[i] + 1;
        }

        var baseCount = hunk.Base?.Count ?? 0;
        var height = Math.Max(1, Math.Max(pairs.Count, Math.Max(baseCount, next)));
        var rows = new List<(int, int, int, int)>(height);
        for (var row = 0; row < height; row++)
        {
            var (o, t) = row < pairs.Count ? pairs[row] : (-1, -1);
            rows.Add((row < baseCount ? row : -1, o, t, -1));
        }
        for (var i = 0; i < resultRow.Length; i++)
        {
            var (b, o, t, _) = rows[resultRow[i]];
            rows[resultRow[i]] = (b, o, t, i);
        }
        return rows;
    }

    /// <summary>Pairs up equal lines of two lists (longest common subsequence); the rest get a row each.</summary>
    internal static List<(int Ours, int Theirs)> Align(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var rows = new List<(int, int)>();
        var n = a.Count;
        var m = b.Count;
        if ((long)n * m > MaxAlignCells || n == 0 || m == 0)
        {
            for (var i = 0; i < Math.Max(n, m); i++) rows.Add((i < n ? i : -1, i < m ? i : -1));
            return rows;
        }

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        // Between two matches, the lines only one side has are put side by side (like a changed line in a diff).
        var onlyA = new List<int>();
        var onlyB = new List<int>();
        void Flush()
        {
            for (var k = 0; k < Math.Max(onlyA.Count, onlyB.Count); k++)
                rows.Add((k < onlyA.Count ? onlyA[k] : -1, k < onlyB.Count ? onlyB[k] : -1));
            onlyA.Clear();
            onlyB.Clear();
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                Flush();
                rows.Add((x++, y++));
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) onlyA.Add(x++);
            else onlyB.Add(y++);
        }
        while (x < n) onlyA.Add(x++);
        while (y < m) onlyB.Add(y++);
        Flush();
        return rows;
    }
}
