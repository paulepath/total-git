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
/// and they can scroll together. Shared text is one row per line; inside a conflict, the lines of base, ours and
/// theirs that match (or are one line edited) are put side by side, and the rest get rows of their own.
/// </summary>
public sealed class MergeLayout
{
    // Beyond this (lines of ours × lines of theirs) a conflict isn't aligned inside, just stacked.
    private const long MaxAlignCells = 2_000_000;

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
    /// The rows of one conflict as indexes within it: ours, theirs and base aligned on matching (or similar) lines,
    /// and each result line level with the line it came from where the order allows.
    /// </summary>
    internal static List<(int Base, int Ours, int Theirs, int Result)> LayOutConflict(ConflictHunk hunk, ConflictResolution resolution)
    {
        var aligned = AlignBase(hunk.Base ?? [], hunk.Ours, hunk.Theirs, Align(hunk.Ours, hunk.Theirs));
        var rowOfBase = new int[hunk.Base?.Count ?? 0];
        var rowOfOurs = new int[hunk.Ours.Count];
        var rowOfTheirs = new int[hunk.Theirs.Count];
        for (var row = 0; row < aligned.Count; row++)
        {
            if (aligned[row].Base >= 0) rowOfBase[aligned[row].Base] = row;
            if (aligned[row].Ours >= 0) rowOfOurs[aligned[row].Ours] = row;
            if (aligned[row].Theirs >= 0) rowOfTheirs[aligned[row].Theirs] = row;
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
                ResultSource.Base when line.SourceLine < rowOfBase.Length => rowOfBase[line.SourceLine],
                _ => next,
            };
            resultRow[i] = Math.Max(wanted, next);
            next = resultRow[i] + 1;
        }

        var height = Math.Max(1, Math.Max(aligned.Count, next));
        var rows = new List<(int, int, int, int)>(height);
        for (var row = 0; row < height; row++)
        {
            var (b, o, t) = row < aligned.Count ? aligned[row] : (-1, -1, -1);
            rows.Add((b, o, t, -1));
        }
        for (var i = 0; i < resultRow.Length; i++)
        {
            var (b, o, t, _) = rows[resultRow[i]];
            rows[resultRow[i]] = (b, o, t, i);
        }
        return rows;
    }

    // Beyond this many cells, lines are only matched when equal (working out how alike lines are costs more).
    private const long MaxSimilarCells = 250_000;

    /// <summary>How alike two lines must be (share of their words) to go side by side as one line changed.</summary>
    internal const double SimilarEnough = 0.5;

    /// <summary>
    /// Pairs up the lines of two lists: equal lines, and lines alike enough to be one line edited (so a line changed
    /// on one side sits next to its other version even when lines were added around it). Every other line gets a
    /// row of its own, with a gap on the other side.
    /// </summary>
    internal static List<(int Ours, int Theirs)> Align(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        if ((long)n * m > MaxAlignCells || n == 0 || m == 0)
        {
            var stacked = new List<(int, int)>();
            for (var i = 0; i < Math.Max(n, m); i++) stacked.Add((i < n ? i : -1, i < m ? i : -1));
            return stacked;
        }
        var similar = (long)n * m <= MaxSimilarCells;
        var wa = a.Select(Words).ToArray();
        var wb = b.Select(Words).ToArray();
        return Interleave(n, m, Match(n, m, (i, j) => Weight(a[i], b[j], wa[i], wb[j], similar)));
    }

    /// <summary>
    /// Adds the base lines to rows of ours and theirs: each next to the row whose line it matches (or is most like),
    /// the rest on rows of their own where they come in order.
    /// </summary>
    internal static List<(int Base, int Ours, int Theirs)> AlignBase(IReadOnlyList<string> baseLines, IReadOnlyList<string> ours,
        IReadOnlyList<string> theirs, List<(int Ours, int Theirs)> rows)
    {
        if (baseLines.Count == 0) return rows.Select(r => (-1, r.Ours, r.Theirs)).ToList();
        var n = baseLines.Count;
        var m = rows.Count;
        if ((long)n * m > MaxAlignCells)
        {
            var stacked = new List<(int, int, int)>();
            for (var i = 0; i < Math.Max(n, m); i++) stacked.Add((i < n ? i : -1, i < m ? rows[i].Ours : -1, i < m ? rows[i].Theirs : -1));
            return stacked;
        }
        var similar = (long)n * m <= MaxSimilarCells;
        var wBase = baseLines.Select(Words).ToArray();
        var wOurs = ours.Select(Words).ToArray();
        var wTheirs = theirs.Select(Words).ToArray();
        var pairs = Match(n, m, (i, j) =>
        {
            var (o, t) = rows[j];
            return Math.Max(o >= 0 ? Weight(baseLines[i], ours[o], wBase[i], wOurs[o], similar) : 0,
                t >= 0 ? Weight(baseLines[i], theirs[t], wBase[i], wTheirs[t], similar) : 0);
        });
        return Interleave(n, m, pairs).Select(p =>
            (p.A, p.B >= 0 ? rows[p.B].Ours : -1, p.B >= 0 ? rows[p.B].Theirs : -1)).ToList();
    }

    /// <summary>
    /// The pairs (i, j) with the most weight in total that keep both lists in order (a weighted longest common
    /// subsequence); <paramref name="weight"/> is 0 for lines that mustn't pair.
    /// </summary>
    private static List<(int, int)> Match(int n, int m, Func<int, int, double> weight)
    {
        var best = new double[n + 1, m + 1];
        var w = new double[n, m];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                w[i, j] = weight(i, j);
                var skip = Math.Max(best[i + 1, j], best[i, j + 1]);
                best[i, j] = w[i, j] > 0 ? Math.Max(skip, w[i, j] + best[i + 1, j + 1]) : skip;
            }
        }
        var pairs = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (w[x, y] > 0 && best[x, y] == w[x, y] + best[x + 1, y + 1]) pairs.Add((x++, y++));
            else if (best[x + 1, y] >= best[x, y + 1]) x++;
            else y++;
        }
        return pairs;
    }

    /// <summary>
    /// Rows for two lists given their pairs: a pair shares a row; between pairs, the first list's lines, then the
    /// second's, each on a row of its own.
    /// </summary>
    private static List<(int A, int B)> Interleave(int n, int m, List<(int, int)> pairs)
    {
        var rows = new List<(int, int)>(n + m);
        int x = 0, y = 0;
        foreach (var (i, j) in pairs.Append((n, m)))
        {
            while (x < i) rows.Add((x++, -1));
            while (y < j) rows.Add((-1, y++));
            if (i < n && j < m) rows.Add((x++, y++));
        }
        return rows;
    }

    /// <summary>
    /// How strongly two lines should pair: equal lines most (blank ones only a little, so they don't pull other lines
    /// out of line), lines alike enough by how alike, others not at all.
    /// </summary>
    private static double Weight(string a, string b, Dictionary<string, int> wordsA, Dictionary<string, int> wordsB, bool similar)
    {
        if (a == b) return string.IsNullOrWhiteSpace(a) ? 0.3 : 2;
        if (!similar) return 0;
        var s = Similarity(wordsA, wordsB);
        return s >= SimilarEnough ? s : 0;
    }

    /// <summary>How alike two lines are, 0 to 1: the share of their words (letters and digits) that they have in common.</summary>
    public static double Similarity(string a, string b) => Similarity(Words(a), Words(b));

    private static double Similarity(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        var total = a.Values.Sum() + b.Values.Sum();
        if (total == 0) return 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var common = small.Sum(kv => Math.Min(kv.Value, large.GetValueOrDefault(kv.Key)));
        return 2.0 * common / total;
    }

    /// <summary>A line's words (runs of letters and digits), counted.</summary>
    private static Dictionary<string, int> Words(string line)
    {
        var words = new Dictionary<string, int>(StringComparer.Ordinal);
        var start = -1;
        for (var i = 0; i <= line.Length; i++)
        {
            var word = i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_');
            if (word && start < 0) start = i;
            else if (!word && start >= 0)
            {
                var w = line[start..i];
                words[w] = words.GetValueOrDefault(w) + 1;
                start = -1;
            }
        }
        return words;
    }

    /// <summary>
    /// Whether a conflict row's lines are all the same text (no side changed it): shown plainly, so the eye goes to
    /// the rows that differ. Rows of shared text count too.
    /// </summary>
    public bool IsSameOnEverySide(int row)
    {
        var r = Rows[row];
        string? text = null;
        foreach (var (index, lines) in new[] { (r.Base, BaseLines), (r.Ours, OursLines), (r.Theirs, TheirsLines) })
        {
            if (index < 0) continue;
            if (text is null) text = lines[index];
            else if (lines[index] != text) return false;
        }
        // A line only one side has is a change, not shared text.
        return r.Conflict < 0 || (r.Ours >= 0 && r.Theirs >= 0);
    }
}
